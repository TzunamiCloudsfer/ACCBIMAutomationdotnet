using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using AutodeskAutomation.Models.Documents;
using AutodeskAutomation.Playwright.Acc;
using AutodeskAutomation.Playwright.Bim360;
using Microsoft.Playwright;
using System.Linq;

namespace AutodeskAutomation.Services
{
    public class AccBatchService
    {
        private static readonly AccBatchService _instance = new AccBatchService();
        public static AccBatchService Instance => _instance;

        private bool _paused;
        private bool _stopped;
        private TaskCompletionSource<bool>? _resumeSource;
        private readonly object _pauseLock = new object();

        private AccBatchService() { }

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
            => _paused && _resumeSource != null ? _resumeSource.Task : Task.CompletedTask;

        private void Reset()
        {
            _paused = false;
            _stopped = false;
            _resumeSource = null;
        }

        public async Task<BatchResult> RunBatch(List<ProjectDocument> projects, BatchOptions opts)
        {
            Reset();
            var db = DatabaseService.Instance;
            var sse = SseService.Instance;
            var srv = ServerState.Instance;

            Directory.CreateDirectory(opts.ScreenshotsDir ?? ".");
            var runId = db.CreateRun(opts.UserEmail, "acc");

            // Filter BIM360 projects
            var bim360Projects = projects.FindAll(p =>
                string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase));
            foreach (var p in bim360Projects)
                db.MarkFilteredBim360(opts.UserEmail, "acc", p);

            // If checkpoint is empty (e.g. after reset), force Fresh so all projects are re-exported
            if (!opts.Fresh)
            {
                var cp = db.LoadCheckpoint(opts.UserEmail, "acc");
                if (cp.Completed.Count == 0 && cp.NoDm.Count == 0)
                    opts.Fresh = true;
            }

            var toProcess = opts.Fresh
                ? projects.FindAll(p => !string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase))
                : projects.FindAll(p =>
                    !string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase) &&
                    !db.IsCompleted(opts.UserEmail, "acc", p) &&
                    !db.IsNoDm(opts.UserEmail, "acc", p));

            var results = new BatchResult { Skipped = projects.Count - toProcess.Count };

            // Pre-populate project statuses so SSE reconnects restore the full list
            srv.Acc.ProjectStatuses.Clear();
            foreach (var p in toProcess)
                srv.Acc.ProjectStatuses[p.ProjectId] = new ProjectStatus { Status = "pending", Name = p.Name };
            foreach (var p in projects)
            {
                if (!srv.Acc.ProjectStatuses.ContainsKey(p.ProjectId))
                {
                    var isBim360 = string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase);
                    var skipSt = isBim360 ? "skipped"
                        : db.IsCompleted(opts.UserEmail, "acc", p) ? "success"
                        : db.IsNoDm(opts.UserEmail, "acc", p) ? "no_dm"
                        : "skipped";
                    srv.Acc.ProjectStatuses[p.ProjectId] = new ProjectStatus { Status = skipSt, Name = p.Name };
                }
            }

            sse.Broadcast("export-start", new {
                total    = toProcess.Count,
                skipped  = results.Skipped,
                platform = "acc",
                projects = projects.Select(p => new {
                    id     = p.ProjectId,
                    name   = p.Name,
                    status = srv.Acc.ProjectStatuses.TryGetValue(p.ProjectId, out var ps) ? ps.Status : "pending"
                }).ToList()
            });

            srv.Acc.Progress.Total = toProcess.Count;
            srv.Acc.Progress.Completed = 0;
            srv.Acc.ExportStatus = "running";

            for (int i = 0; i < toProcess.Count; i++)
            {
                if (_stopped) break;

                if (_paused)
                {
                    sse.Broadcast("export-paused", new { nextIndex = i, platform = "acc" });
                    srv.AccPaused = true;
                    await WaitIfPaused();
                    srv.AccPaused = false;
                    if (_stopped) break;
                    sse.Broadcast("export-resumed", new { nextIndex = i, platform = "acc" });
                }

                var project = toProcess[i];
                sse.Broadcast("project-start", new { index = i + 1, total = toProcess.Count,
                    project = new { id = project.ProjectId, name = project.Name }, platform = "acc" });

                string? screenshotPath = null;
                ExportResult result;

                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                var browser = await playwright.Chromium.LaunchAsync(BrowserHelper.HeadlessOptions());
                try
                {
                    IBrowserContext context;
                    var authPath = opts.AuthStatePath ?? GetDefaultAuthPath(opts.UserEmail);
                    if (!string.IsNullOrEmpty(authPath) && File.Exists(authPath))
                        context = await browser.NewContextAsync(new BrowserNewContextOptions
                            { StorageStatePath = authPath });
                    else
                        context = await browser.NewContextAsync(new BrowserNewContextOptions
                            { ViewportSize = new ViewportSize { Width = 1920, Height = 1080 } });

                    var page = await context.NewPageAsync();
                    var picker = new AccProjectPicker(page);
                    var filesPage = new FilesPage(page);
                    var exportDialog = new AccExportDialog(page);

                    result = await ExportFilesLog(picker, filesPage, exportDialog, project);

                    if (result.Status == "failed" && opts.ScreenshotsDir != null)
                    {
                        try
                        {
                            var slug = System.Text.RegularExpressions.Regex.Replace(project.Name, @"[^\w]", "_");
                            screenshotPath = Path.Combine(opts.ScreenshotsDir, $"{slug}-fail.png");
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

                // After browser closes, open a fresh browser to download and parse the Excel report
                if (result.Status == "success" && !string.IsNullOrEmpty(result.ReportsUrl))
                {
                    try
                    {
                        using var rptPlaywright = await Microsoft.Playwright.Playwright.CreateAsync();
                        var rptBrowser = await rptPlaywright.Chromium.LaunchAsync(BrowserHelper.HeadlessOptions());
                        try
                        {
                            var authPath = opts.AuthStatePath ?? GetDefaultAuthPath(opts.UserEmail);
                            var rptCtx = !string.IsNullOrEmpty(authPath) && File.Exists(authPath)
                                ? await rptBrowser.NewContextAsync(new BrowserNewContextOptions { StorageStatePath = authPath })
                                : await rptBrowser.NewContextAsync();
                            var rptPage = await rptCtx.NewPageAsync();
                            var summary = await Bim360BatchService.NavigateToReportsAndCapture(
                                rptPage, project, result.ReportsUrl, opts.UserEmail, result.ExportTriggeredAt, "acc");
                            result.TotalFiles = summary.TotalFiles;
                            result.TotalSizeBytes = summary.TotalSizeBytes;
                            result.TotalSizeFormatted = summary.TotalSizeFormatted;
                        }
                        finally { await rptBrowser.CloseAsync(); }
                    }
                    catch (Exception rptEx)
                    {
                        Console.WriteLine($"[acc-reports] Report capture failed: {rptEx.Message}");
                    }
                }

                if (result.Status == "success")
                {
                    db.MarkCompleted(opts.UserEmail, "acc", project);
                    results.Success++;
                    results.EmailsQueued++;
                }
                else if (result.Status == "no_dm")
                {
                    db.MarkNoDm(opts.UserEmail, "acc", project);
                    results.NoDm++;
                }
                else
                {
                    results.Failed++;
                }

                srv.Acc.Progress.Completed = i + 1;
                srv.Acc.ProjectStatuses[project.ProjectId] = new ProjectStatus
                    { Status = result.Status, Name = project.Name, Error = result.Error };
                srv.Acc.Results.Success      = results.Success;
                srv.Acc.Results.Failed       = results.Failed;
                srv.Acc.Results.NoDm         = results.NoDm;
                srv.Acc.Results.Skipped      = results.Skipped;
                srv.Acc.Results.EmailsQueued = results.EmailsQueued;
                sse.Broadcast("project-done", new { project = new { id = project.ProjectId, name = project.Name },
                    status = result.Status, error = result.Error,
                    totalFiles = result.TotalFiles,
                    totalSizeFormatted = result.TotalSizeFormatted,
                    platform = "acc" });
                sse.Broadcast("progress-update", new { completed = i + 1, total = toProcess.Count,
                    results = new { results.Success, results.Failed, no_dm = results.NoDm,
                        results.Skipped, results.EmailsQueued }, platform = "acc" });
            }

            results.Stopped = _stopped;
            db.CompleteRun(runId, projects.Count, results.Success, results.NoDm,
                results.Failed, results.Skipped, results.EmailsQueued);

            srv.AccRunning = false;
            srv.AccPaused = false;
            srv.Acc.ExportStatus = "complete";
            sse.Broadcast("export-complete", new {
                results = new { results.Success, results.Failed, no_dm = results.NoDm,
                    results.Skipped, results.EmailsQueued },
                stopped = results.Stopped, platform = "acc" });

            Reset();
            return results;
        }

        private static async Task<ExportResult> ExportFilesLog(
            AccProjectPicker picker, FilesPage filesPage, AccExportDialog dialog,
            ProjectDocument project)
        {
            var start = DateTime.UtcNow;
            try
            {
                var resolved = await picker.NavigateToProject(project);
                if (resolved == null)
                    return new ExportResult { Status = "no_dm", Duration = (DateTime.UtcNow - start).TotalMilliseconds };

                await filesPage.OpenProjectFiles();
                var exportTriggeredAt = DateTime.Now.AddMinutes(-1);
                var triggered = await dialog.TriggerFilesLogExport();

                // Only set ReportsUrl if the export was actually triggered.
                // If "Files log" wasn't found, skip report capture to avoid a 5-minute wait.
                return new ExportResult { Status = "success",
                    Duration = (DateTime.UtcNow - start).TotalMilliseconds, EmailsQueued = 1,
                    ReportsUrl = triggered ? $"https://acc.autodesk.com/docs/reports/projects/{project.ProjectId}" : null,
                    ExportTriggeredAt = exportTriggeredAt };
            }
            catch (Exception ex)
            {
                return new ExportResult { Status = "failed", Error = ex.Message,
                    Duration = (DateTime.UtcNow - start).TotalMilliseconds };
            }
        }

        private static string GetDefaultAuthPath(string? userEmail)
        {
            if (string.IsNullOrEmpty(userEmail)) return string.Empty;
            var slug = Helpers.SlugHelper.EmailToSlug(userEmail);
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage", "users", slug);
            return Path.Combine(dir, "auth-state.json");
        }
    }
}
