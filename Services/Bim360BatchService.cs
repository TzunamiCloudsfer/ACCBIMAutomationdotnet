using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using AutodeskAutomation.Models.Documents;
using AutodeskAutomation.Playwright.Bim360;
using Microsoft.Playwright;

namespace AutodeskAutomation.Services
{
    public class Bim360BatchService
    {
        private static readonly Bim360BatchService _instance = new Bim360BatchService();
        public static Bim360BatchService Instance => _instance;

        private bool _paused;
        private bool _stopped;
        private TaskCompletionSource<bool>? _resumeSource;
        private readonly object _pauseLock = new object();

        private Bim360BatchService() { }

        public void Pause()
        {
            lock (_pauseLock)
            {
                if (!_paused)
                {
                    _paused = true;
                    _resumeSource = new TaskCompletionSource<bool>();
                }
            }
        }

        public void Resume()
        {
            lock (_pauseLock)
            {
                if (_paused)
                {
                    _paused = false;
                    _resumeSource?.TrySetResult(true);
                    _resumeSource = null;
                }
            }
        }

        public void Stop()
        {
            _stopped = true;
            Resume();
        }

        private Task WaitIfPaused()
            => _paused && _resumeSource != null
                ? _resumeSource.Task
                : Task.CompletedTask;

        private void Reset()
        {
            _paused = false;
            _stopped = false;
            _resumeSource = null;
        }

        public async Task<BatchResult> RunBatch(List<ProjectDocument> projects, BatchOptions opts)
        {
            Reset();
            var db  = DatabaseService.Instance;
            var sse = SseService.Instance;
            var srv = ServerState.Instance;

            // Ensure Bim360Running is always cleared when we exit, no matter what
            try
            {
            return await RunBatchInternal(projects, opts, db, sse, srv);
            }
            finally
            {
                srv.Bim360Running = false;
                srv.Bim360Paused  = false;
            }
        }

        private static string Now() => DateTime.UtcNow.ToString("O");

        private async Task<BatchResult> RunBatchInternal(
            List<ProjectDocument> projects, BatchOptions opts,
            DatabaseService db, SseService sse, ServerState srv)
        {
            var authExists = !string.IsNullOrEmpty(opts.AuthStatePath) && File.Exists(opts.AuthStatePath);

            if (string.IsNullOrEmpty(opts.AccountId))
            {
                sse.Broadcast("export-error", new { error =
                    "AccountId is not set â€” please click Discover Projects first to populate the account ID.",
                    platform = "bim360" });
                db.CompleteRun(db.CreateRun(opts.UserEmail, "bim360"), 0, 0, 0, 0, 0, 0,
                    "Stopped: AccountId missing");
                return new BatchResult();
            }

            if (!authExists)
            {
                sse.Broadcast("export-error", new { error =
                    "auth-state.json not found â€” please click Login to authenticate with Autodesk first.",
                    platform = "bim360" });
                db.CompleteRun(db.CreateRun(opts.UserEmail, "bim360"), 0, 0, 0, 0, 0, 0,
                    "Stopped: auth-state.json missing");
                return new BatchResult();
            }

            Directory.CreateDirectory(opts.ScreenshotsDir ?? ".");

            // â”€â”€ Session warm-up â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Navigate to the BIM360 account admin root before hitting individual
            // projects. This refreshes the BIM360 session and writes the
            // account-specific cookies into the context â€” preventing "session expired"
            // on the first project.
            sse.Broadcast("log", new { level = "INFO",
                message = "Warming up BIM360 admin sessionâ€¦", platform = "bim360" });
            try
            {
                using var warmupPlaywright = await Microsoft.Playwright.Playwright.CreateAsync();
                var warmupBrowser = await warmupPlaywright.Chromium.LaunchAsync(
                    AutodeskAutomation.Helpers.BrowserHelper.HeadlessOptions());
                try
                {
                    var wctx = File.Exists(opts.AuthStatePath)
                        ? await warmupBrowser.NewContextAsync(new BrowserNewContextOptions
                            { StorageStatePath = opts.AuthStatePath })
                        : await warmupBrowser.NewContextAsync();

                    var wpage = await wctx.NewPageAsync();

                    // Step 1: hit b360.autodesk.com to establish consumer session
                    await wpage.GotoAsync("https://b360.autodesk.com/",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
                    await Task.Delay(4000);
                    Console.WriteLine($"[bim360-warmup] b360: {wpage.Url}");

                    // Step 2: hit admin root to establish admin session
                    await wpage.GotoAsync(
                        $"https://admin.b360.autodesk.com/admin/{opts.AccountId}/projects",
                        new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
                    await Task.Delay(4000);
                    Console.WriteLine($"[bim360-warmup] admin: {wpage.Url}");

                    if (wpage.Url.Contains("signin.autodesk") || wpage.Url.Contains("identity.autodesk"))
                    {
                        await warmupBrowser.CloseAsync();
                        throw new Exception("Autodesk session expired â€” please click Login to re-authenticate.");
                    }

                    // Save the refreshed auth state with the new BIM360 cookies
                    await wctx.StorageStateAsync(new BrowserContextStorageStateOptions
                        { Path = opts.AuthStatePath });
                    Console.WriteLine("[bim360-warmup] Session refreshed and auth state updated.");
                }
                finally { await warmupBrowser.CloseAsync(); }
            }
            catch (Exception warmupEx)
            {
                if (warmupEx.Message.Contains("session expired"))
                {
                    sse.Broadcast("export-error", new { error = warmupEx.Message, platform = "bim360" });
                    srv.Bim360Running = false;
                    db.CompleteRun(db.CreateRun(opts.UserEmail, "bim360"), 0, 0, 0, 0, 0, 0, warmupEx.Message);
                    return new BatchResult();
                }
                Console.WriteLine($"[bim360-warmup] Non-fatal: {warmupEx.Message}");
            }

            var runId = db.CreateRun(opts.UserEmail, "bim360");

            var pending = opts.Fresh ? new List<ProjectDocument>(projects)
                : projects.FindAll(p => !db.IsCompleted(opts.UserEmail, "bim360", p)
                                     && !db.IsNoDm(opts.UserEmail, "bim360", p));

            var results = new BatchResult { Skipped = projects.Count - pending.Count };

            // â”€â”€ Broadcast export-start FIRST so A.runningPlatform is set on the client â”€â”€
            // All subsequent log events will then pass the isCurrentPlatform check.
            sse.Broadcast("export-start", new { total = pending.Count, skipped = results.Skipped, platform = "bim360" });

            // Diagnostic logs (now visible because export-start already fired)
            sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                message = $"BIM360 batch starting â€” accountId={opts.AccountId ?? "NULL"}, auth={authExists}, projects={projects.Count}, pending={pending.Count}",
                platform = "bim360" });

            srv.Bim360.Progress.Total = pending.Count;
            srv.Bim360.Progress.Completed = 0;
            srv.Bim360.ExportStatus = "running";

            for (int i = 0; i < pending.Count; i++)
            {
                if (_stopped) break;

                if (_paused)
                {
                    sse.Broadcast("export-paused", new { nextIndex = i, remaining = pending.Count - i, platform = "bim360" });
                    srv.Bim360Paused = true;
                    await WaitIfPaused();
                    srv.Bim360Paused = false;
                    if (_stopped) break;
                    sse.Broadcast("export-resumed", new { nextIndex = i, platform = "bim360" });
                }

                var project = pending[i];
                sse.Broadcast("project-start", new { index = i + 1, total = pending.Count, project = new { id = project.ProjectId, name = project.Name }, platform = "bim360" });

                string? screenshotPath = null;
                ExportResult result;

                // â”€â”€ Try API export first (faster, no browser needed) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                result = await TryApiExport(project, opts, sse);
                bool needsBrowser = result == null;

                if (needsBrowser)
                {
                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                var browser = await playwright.Chromium.LaunchAsync(BrowserHelper.HeadlessOptions());
                try
                {
                    IBrowserContext context;
                    if (!string.IsNullOrEmpty(opts.AuthStatePath) && File.Exists(opts.AuthStatePath))
                        context = await browser.NewContextAsync(new BrowserNewContextOptions
                            { StorageStatePath = opts.AuthStatePath });
                    else
                        context = await browser.NewContextAsync();

                    var page = await context.NewPageAsync();

                    var picker = new Bim360ProjectPicker(page, opts.AccountId ?? "");
                    var rootSel = new RootSelector(page);
                    var dialog = new DocumentLogDialog(page);

                    result = await ExportDocumentLog(picker, rootSel, dialog, project,
                        runId, opts.UserEmail, opts.AccountId);

                    // Capture screenshot for both failed AND no_dm â€” shows where browser ended up
                    if ((result.Status == "failed" || result.Status == "no_dm") && opts.ScreenshotsDir != null)
                    {
                        try
                        {
                            var slug = System.Text.RegularExpressions.Regex.Replace(project.Name, @"[^\w]", "_");
                            screenshotPath = Path.Combine(opts.ScreenshotsDir, $"{slug}-{result.Status}.png");
                            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = true });
                        }
                        catch { screenshotPath = null; }
                    }
                }
                catch (Exception ex)
                {
                    result = new ExportResult { Status = "failed", Error = ex.Message };
                }
                finally
                {
                    await browser.CloseAsync();
                }
                } // end if (needsBrowser)

                // Update checkpoint and results
                if (result.Status == "success")
                {
                    db.MarkCompleted(opts.UserEmail, "bim360", project);
                    results.Success++;
                    results.EmailsQueued += result.EmailsQueued;
                    Console.WriteLine($"[bim360] âœ“ Marked COMPLETED: {project.Name} (user={opts.UserEmail ?? "null"})");
                }
                else if (result.Status == "no_dm")
                {
                    db.MarkNoDm(opts.UserEmail, "bim360", project);
                    results.NoDm++;
                    Console.WriteLine($"[bim360] âŠ˜ Marked NO_DM: {project.Name} (user={opts.UserEmail ?? "null"})");
                }
                else
                {
                    results.Failed++;
                    db.LogError(opts.UserEmail, "bim360", runId, project, result.Error, screenshotPath);
                }

                srv.Bim360.Progress.Completed = i + 1;
                srv.Bim360.ProjectStatuses[project.ProjectId] = new ProjectStatus
                    { Status = result.Status, Name = project.Name, Error = result.Error };

                sse.Broadcast("project-done", new { project = new { id = project.ProjectId, name = project.Name },
                    status = result.Status, error = result.Error, platform = "bim360" });
                sse.Broadcast("progress-update", new { completed = i + 1, total = pending.Count,
                    results = new { results.Success, results.Failed, no_dm = results.NoDm,
                        results.Skipped, results.EmailsQueued }, platform = "bim360" });
            }

            results.Stopped = _stopped;
            var note = $"Max emails possible: {results.Success * 2} (2 per project)";
            db.CompleteRun(runId, projects.Count, results.Success, results.NoDm,
                results.Failed, results.Skipped, results.EmailsQueued, note);

            srv.Bim360.ExportStatus = "complete";
            sse.Broadcast("export-complete", new { results, stopped = results.Stopped, platform = "bim360" });

            Reset();
            return results;
        }   // end RunBatchInternal

        // BIM360 Document Log has no public REST API â€” always use browser automation.
        private static Task<ExportResult?> TryApiExport(
            ProjectDocument project, BatchOptions opts, SseService sse)
            => Task.FromResult<ExportResult?>(null);

        private static async Task<ExportResult> ExportDocumentLog(
            Bim360ProjectPicker picker, RootSelector rootSel, DocumentLogDialog dialog,
            ProjectDocument project, string? batchRunId = null, string? userEmail = null,
            string? accountId = null)
        {
            var start = DateTime.UtcNow;
            int emailsQueued = 0;

            try
            {
                var resolved = await picker.NavigateToDataManagement(project);
                if (resolved == null)
                    return new ExportResult { Status = "no_dm", Duration = (DateTime.UtcNow - start).TotalMilliseconds };

                // Begin report tracker â€” snapshot existing reports before triggering
                ExportRun? tracker = null;
                try
                {
                    tracker = await ExportRun.Begin(picker.Page, new ExportRunMeta
                    {
                        ProjectId    = project.ProjectId,
                        ProjectName  = project.Name,
                        AccountId    = accountId,
                        UserEmail    = userEmail,
                        Platform     = "bim360",
                        BatchRunId   = batchRunId,
                        ExpectedCount = 2  // Plans + Project Files
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[tracker] begin failed (non-fatal): {ex.Message}");
                }

                // Export Plans root
                try
                {
                    await rootSel.SelectRoot("Plans");
                    await dialog.OpenAndExport();
                    emailsQueued++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[bim360] Plans root failed for {project.Name}: {ex.Message}");
                    try { await picker.Page.Keyboard.PressAsync("Escape"); } catch { }
                    await Task.Delay(600);
                }

                // Export Project Files root
                await rootSel.SelectRoot("Project Files");
                await dialog.OpenAndExport();
                emailsQueued++;

                // Finalize tracker â€” poll for new reports (up to 90s)
                if (tracker != null)
                {
                    try { await tracker.FinalizeAsync(pollIntervalMs: 7000, maxWaitMs: 90_000); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[tracker] finalize failed (non-fatal): {ex.Message}");
                    }
                }

                return new ExportResult { Status = "success",
                    Duration = (DateTime.UtcNow - start).TotalMilliseconds, EmailsQueued = emailsQueued };
            }
            catch (Exception ex)
            {
                return new ExportResult { Status = "failed", Error = ex.Message,
                    Duration = (DateTime.UtcNow - start).TotalMilliseconds, EmailsQueued = emailsQueued };
            }
        }
    }

    public class ExportResult
    {
        public string Status { get; set; } = "failed";
        public string? Error { get; set; }
        public double Duration { get; set; }
        public int EmailsQueued { get; set; }
    }
}

