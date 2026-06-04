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
                    "AccountId is not set --  please click Discover Projects first to populate the account ID.",
                    platform = "bim360" });
                db.CompleteRun(db.CreateRun(opts.UserEmail, "bim360"), 0, 0, 0, 0, 0, 0,
                    "Stopped: AccountId missing");
                return new BatchResult();
            }

            if (!authExists)
            {
                sse.Broadcast("export-error", new { error =
                    "auth-state.json not found --  please click Login to authenticate with Autodesk first.",
                    platform = "bim360" });
                db.CompleteRun(db.CreateRun(opts.UserEmail, "bim360"), 0, 0, 0, 0, 0, 0,
                    "Stopped: auth-state.json missing");
                return new BatchResult();
            }

            Directory.CreateDirectory(opts.ScreenshotsDir ?? ".");

            // "" Session warm-up """""""""""""""""""""""""""""""""""""""""""""""""""
            // Navigate to the BIM360 account admin root before hitting individual
            // projects. This refreshes the BIM360 session and writes the
            // account-specific cookies into the context --  preventing "session expired"
            // on the first project.
            sse.Broadcast("log", new { level = "INFO",
                message = "Warming up BIM360 admin session--", platform = "bim360" });
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
                        throw new Exception("Autodesk session expired --  please click Login to re-authenticate.");
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
                    var errMsg = "BIM360 session expired -- your Autodesk login does not include BIM360 admin access. " +
                                 "Please click Login, sign into acc.autodesk.com, " +
                                 "then also sign into admin.b360.autodesk.com when the browser navigates there.";
                    sse.Broadcast("export-error", new { error = errMsg, platform = "bim360" });
                    sse.Broadcast("log", new { level = "ERROR", timestamp = Now(),
                        message = errMsg, platform = "bim360" });
                    srv.Bim360Running = false;
                    db.CompleteRun(db.CreateRun(opts.UserEmail, "bim360"), 0, 0, 0, 0, 0, 0, warmupEx.Message);
                    return new BatchResult();
                }
                // Non-fatal warmup error -- proceed anyway (individual projects handle their own auth)
                sse.Broadcast("log", new { level = "WARN", timestamp = Now(),
                    message = $"[Warmup] {warmupEx.Message} -- proceeding with export", platform = "bim360" });
                Console.WriteLine($"[bim360-warmup] Non-fatal: {warmupEx.Message}");
            }

            var runId = db.CreateRun(opts.UserEmail, "bim360");

            var pending = opts.Fresh ? new List<ProjectDocument>(projects)
                : projects.FindAll(p => !db.IsCompleted(opts.UserEmail, "bim360", p)
                                     && !db.IsNoDm(opts.UserEmail, "bim360", p));

            var results = new BatchResult { Skipped = projects.Count - pending.Count };

            // "" Broadcast export-start FIRST so A.runningPlatform is set on the client ""
            // All subsequent log events will then pass the isCurrentPlatform check.
            sse.Broadcast("export-start", new { total = pending.Count, skipped = results.Skipped, platform = "bim360" });

            // Diagnostic logs (now visible because export-start already fired)
            sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                message = $"BIM360 batch starting --  accountId={opts.AccountId ?? "NULL"}, auth={authExists}, projects={projects.Count}, pending={pending.Count}",
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

                // "" Try API export first (faster, no browser needed) """"""""""""""
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

                    // Capture screenshot for both failed AND no_dm --  shows where browser ended up
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
                    sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                        message = $"[{project.Name}] DONE -- marked as Completed (success={results.Success}, emails={results.EmailsQueued})",
                        platform = "bim360" });
                }
                else if (result.Status == "no_dm")
                {
                    db.MarkNoDm(opts.UserEmail, "bim360", project);
                    results.NoDm++;
                    sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                        message = $"[{project.Name}] No Data Management -- skipped (no_dm={results.NoDm})",
                        platform = "bim360" });
                }
                else
                {
                    results.Failed++;
                    sse.Broadcast("log", new { level = "ERROR", timestamp = Now(),
                        message = $"[{project.Name}] FAILED: {result.Error ?? "unknown error"} (failed={results.Failed})",
                        platform = "bim360" });
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

        // After export: navigate to Reports page, capture report rows + screenshots
        private static async Task NavigateToReportsAndCapture(
            IPage page, ProjectDocument project, string reportsUrl, string? userEmail,
            DateTime exportTriggeredAt = default)
        {
            if (exportTriggeredAt == default) exportTriggeredAt = DateTime.UtcNow.AddMinutes(-5);
            var sse = SseService.Instance;
            var downloadsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage", "downloads");
            Directory.CreateDirectory(downloadsDir);
            try
            {
                sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                    message = $"[{project.Name}] Navigating to Reports page...",
                    platform = "bim360" });

                await page.GotoAsync(reportsUrl, new PageGotoOptions
                    { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
                await Task.Delay(6000);  // give React SPA time to render
                Console.WriteLine($"[reports] URL: {page.Url}");

                // Wait for the report list table to load
                // data-testid="report-list-table"
                var table = page.Locator("[data-testid=\"report-list-table\"]");
                // Wait up to 30s for the React SPA to render the reports table
                bool tableFound = false;
                for (int t = 0; t < 6; t++)  // 6 x 5s = 30s
                {
                    if (await table.CountAsync() > 0) { tableFound = true; break; }
                    await Task.Delay(5000);
                }

                if (!tableFound)
                {
                    // Try reloading once more
                    await page.ReloadAsync(new PageReloadOptions
                        { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15_000 });
                    await Task.Delay(5000);
                    tableFound = await table.CountAsync() > 0;
                }

                if (!tableFound)
                {
                    sse.Broadcast("log", new { level = "WARN", timestamp = Now(),
                        message = $"[{project.Name}] Reports table not found after 35s -- URL: {page.Url}",
                        platform = "bim360" });
                    return;
                }

                // Poll for a report row whose "Run at" datetime is AFTER exportTriggeredAt.
                // The Reports table shows "Run at" in column 3 (index 2), newest first.
                // Autodesk generates reports asynchronously -- can take up to 3 minutes.
                sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                    message = $"[{project.Name}] Waiting for report created after {exportTriggeredAt:HH:mm:ss} UTC...",
                    platform = "bim360" });

                int targetRowIndex = -1;
                int maxWaitSec = 180;
                for (int w = 0; w < maxWaitSec / 10; w++)
                {
                    // Extract "Run at" datetime from each row using JavaScript
                    var rowDatetimes = await page.EvaluateAsync<string[]>(
                        "(function() {" +
                        "  var rows = document.querySelectorAll('[data-testid^=\"report-list-table-row-\"]');" +
                        "  return Array.from(rows).map(function(row) {" +
                        "    var cells = row.querySelectorAll('td');" +
                        "    return cells.length >= 3 ? (cells[2].innerText || '') : '';" +
                        "  });" +
                        "})()");

                    if (rowDatetimes != null && rowDatetimes.Length > 0)
                    {
                        for (int ri = 0; ri < rowDatetimes.Length; ri++)
                        {
                            // Parse "Jun 4, 2026 11:07 AM" or similar
                            var dtStr = rowDatetimes[ri].Trim();
                            if (DateTime.TryParse(dtStr, out DateTime rowDt))
                            {
                                // Convert to UTC for comparison (assume server is in local time)
                                if (rowDt.ToUniversalTime() >= exportTriggeredAt.AddSeconds(-30))
                                {
                                    targetRowIndex = ri;
                                    sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                                        message = $"[{project.Name}] Found report row {ri} with datetime: {dtStr}",
                                        platform = "bim360" });
                                    break;
                                }
                            }
                        }
                        if (targetRowIndex >= 0) break;
                    }

                    // Refresh every 30s
                    if (w > 0 && w % 3 == 0)
                    {
                        sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                            message = $"[{project.Name}] Waiting for report... ({w * 10}s elapsed)",
                            platform = "bim360" });
                        await page.ReloadAsync(new PageReloadOptions
                            { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15_000 });
                        await Task.Delay(4000);
                    }
                    else
                    {
                        await Task.Delay(10_000);
                    }
                }

                if (targetRowIndex < 0)
                {
                    sse.Broadcast("log", new { level = "WARN", timestamp = Now(),
                        message = $"[{project.Name}] No new report found after {maxWaitSec}s",
                        platform = "bim360" });
                    return;
                }

                // Use the specific row by index
                var firstRow = page.Locator($"[data-testid=\"report-list-table-row-{targetRowIndex}\"]");

                sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                    message = $"[{project.Name}] Opening report menu to download Excel...",
                    platform = "bim360" });

                // Click the three-dot menu on the first row
                // data-testid="table-row-menu" (aria-label="toggle menu")
                var menuBtn = page.Locator("[data-testid=\"table-row-menu\"]").First;
                if (await menuBtn.CountAsync() == 0)
                {
                    sse.Broadcast("log", new { level = "WARN", timestamp = Now(),
                        message = $"[{project.Name}] No menu button on report row",
                        platform = "bim360" });
                    return;
                }

                await menuBtn.ClickAsync(new LocatorClickOptions { Force = true });
                await Task.Delay(1000);

                // Screenshot the open menu so we can see what options are available
                var shotDir3 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage", "screenshots");
                Directory.CreateDirectory(shotDir3);
                var slug3 = System.Text.RegularExpressions.Regex.Replace(project.Name, @"[^\w]", "_");
                var menuShot = Path.Combine(shotDir3, $"{slug3}-report-menu.png");
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = menuShot, FullPage = false });

                // Log ALL visible menu items so we know what text to click
                var menuItemTexts = await page.EvaluateAsync<string[]>(@"
                    () => [...document.querySelectorAll('[role=""menuitem""], [role=""option""], [data-testid*=""menu""] button')]
                        .filter(el => el.offsetWidth > 0 && el.innerText.trim())
                        .map(el => el.innerText.trim())");

                var menuText = menuItemTexts?.Length > 0
                    ? string.Join(", ", menuItemTexts)
                    : "(no menu items found)";
                sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                    message = $"[{project.Name}] Menu items: {menuText}",
                    platform = "bim360" });
                Console.WriteLine($"[reports] Menu items: {menuText}");
                Console.WriteLine($"[reports] Menu screenshot: {menuShot}");

                // Try each possible menu item text for download
                IDownload? download = null;
                var downloadTexts = new[] { "Download", "Export", "Export to Excel", "Export report" };
                foreach (var text in downloadTexts)
                {
                    var candidate = page.GetByRole(AriaRole.Menuitem, new() { Name = text })
                        .Or(page.Locator("[role=\"menuitem\"]")
                            .Filter(new LocatorFilterOptions { HasText = text }));
                    if (await candidate.CountAsync() > 0)
                    {
                        try
                        {
                            var dlTask = page.WaitForDownloadAsync(
                                new PageWaitForDownloadOptions { Timeout = 20_000 });
                            await candidate.First.ClickAsync(new LocatorClickOptions { Force = true });
                            download = await dlTask;
                            break;
                        }
                        catch { }
                    }
                }

                if (download == null)
                {
                    sse.Broadcast("log", new { level = "WARN", timestamp = Now(),
                        message = $"[{project.Name}] Download did not start",
                        platform = "bim360" });
                    return;
                }

                // Save the downloaded Excel file
                var fileName = download.SuggestedFilename;
                if (string.IsNullOrEmpty(fileName)) fileName = $"{project.ProjectId}-report.xlsx";
                var filePath = Path.Combine(downloadsDir, fileName);
                await download.SaveAsAsync(filePath);

                Console.WriteLine($"[reports] Downloaded: {filePath}");
                sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                    message = $"[{project.Name}] Excel downloaded: {fileName}",
                    platform = "bim360" });

                // Read the Excel file and extract summary
                await ReadExcelSummary(filePath, project, userEmail, sse);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[reports] Navigation failed (non-fatal): {ex.Message}");
                sse.Broadcast("log", new { level = "WARN", timestamp = Now(),
                    message = $"[{project.Name}] Reports capture error: {ex.Message}",
                    platform = "bim360" });
            }
        }

        private static async Task ReadExcelSummary(
            string filePath, ProjectDocument project, string? userEmail, SseService sse)
        {
            await Task.Run(() =>
            {
                try
                {
                    using var workbook = new ClosedXML.Excel.XLWorkbook(filePath);
                    if (workbook.Worksheets.Count == 0) return;

                    // The ACC Files Log Excel has 2 sheets:
                    //   Sheet 1 "Overview"  — metadata (project name, total items, etc.)
                    //   Sheet 2 "Files"     — one row per file with "File size" in col 14
                    // Use the "Files" sheet if available, otherwise fall back to sheet 1.
                    ClosedXML.Excel.IXLWorksheet ws;
                    try   { ws = workbook.Worksheet("Files"); }
                    catch { ws = workbook.Worksheet(workbook.Worksheets.Count > 1 ? 2 : 1); }

                    var lastRow = ws.LastRowUsed();
                    var lastCol = ws.LastColumnUsed();
                    if (lastRow == null) return;

                    int rowCount = lastRow.RowNumber();
                    int colCount = lastCol?.ColumnNumber() ?? 0;

                    // Read column headers from row 1
                    var headers = new System.Collections.Generic.Dictionary<string, int>(
                        StringComparer.OrdinalIgnoreCase);
                    for (int c = 1; c <= colCount; c++)
                    {
                        var hdr = ws.Cell(1, c).GetValue<string>().Trim();
                        if (!string.IsNullOrEmpty(hdr)) headers[hdr] = c;
                    }

                    var colList = string.Join(", ", headers.Keys);
                    Console.WriteLine($"[excel] Columns found: {colList}");
                    sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                        message = $"[{project.Name}] Excel columns: {colList}", platform = "bim360" });

                    // Find the file size column -- case-insensitive, partial match
                    // ACC Files Log typically uses "File size" or "Size"
                    int sizeCol = 0;
                    foreach (var hdr in headers)
                    {
                        var lower = hdr.Key.ToLowerInvariant();
                        if (lower.IndexOf("size") >= 0 || lower.IndexOf("bytes") >= 0)
                        {
                            sizeCol = hdr.Value;
                            Console.WriteLine($"[excel] Using size column: '{hdr.Key}' (col {sizeCol})");
                            sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                                message = $"[{project.Name}] File size column: '{hdr.Key}'", platform = "bim360" });
                            break;
                        }
                    }

                    // Count total files (data rows) and sum total size
                    int totalFiles = rowCount - 1;  // exclude header row
                    long totalSizeBytes = 0;
                    int sizeParseErrors = 0;

                    if (sizeCol > 0)
                    {
                        for (int r = 2; r <= rowCount; r++)
                        {
                            // Try to get as numeric first (most reliable for byte values)
                            // ACC Files Log format: "7.3 KB", "1,002.7 KB", "3.7 MB", "385 B"
                            // Always read as string and parse the unit
                            var cellVal = ws.Cell(r, sizeCol).GetValue<string>().Trim();
                            if (string.IsNullOrEmpty(cellVal) || cellVal == "--") continue;

                            // Strip commas from numbers like "1,002.7 KB" -> "1002.7 KB"
                            var normalized = cellVal.Replace(",", "");
                            // Extract the numeric part (digits and decimal point only)
                            var cleaned = System.Text.RegularExpressions.Regex
                                .Replace(normalized, @"[^\d\.]", "");
                            if (double.TryParse(cleaned,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double sizeVal))
                            {
                                var upper = normalized.ToUpperInvariant();
                                if (upper.IndexOf(" GB") >= 0 || upper.EndsWith("GB"))
                                    totalSizeBytes += (long)(sizeVal * 1024 * 1024 * 1024);
                                else if (upper.IndexOf(" MB") >= 0 || upper.EndsWith("MB"))
                                    totalSizeBytes += (long)(sizeVal * 1024 * 1024);
                                else if (upper.IndexOf(" KB") >= 0 || upper.EndsWith("KB"))
                                    totalSizeBytes += (long)(sizeVal * 1024);
                                else  // "B" or plain number → bytes
                                    totalSizeBytes += (long)sizeVal;
                            }
                            else sizeParseErrors++;
                        }
                    }

                    // Format total size for display
                    string totalSizeStr = totalSizeBytes > 0
                        ? FormatBytes(totalSizeBytes)
                        : (sizeCol == 0 ? "(size column not found)" : "(could not parse)");

                    var summaryText =
                        $"Project: {project.Name}\n" +
                        $"File: {Path.GetFileName(filePath)}\n" +
                        $"Total Files: {totalFiles:N0}\n" +
                        $"Total File Size: {totalSizeStr}\n" +
                        $"Columns: {string.Join(", ", headers.Keys)}";

                    Console.WriteLine($"[excel] {summaryText}");
                    sse.Broadcast("log", new { level = "INFO", timestamp = Now(),
                        message = $"[{project.Name}] Files Log Summary:\n{summaryText}",
                        platform = "bim360" });

                    // Also broadcast a dedicated summary event for the UI
                    sse.Broadcast("files-log-summary", new
                    {
                        projectId   = project.ProjectId,
                        projectName = project.Name,
                        totalFiles,
                        totalSizeBytes,
                        totalSizeFormatted = totalSizeStr,
                        platform    = "bim360"
                    });

                    // Save to RavenDB -- include total files + size in the title/notes
                    var db = DatabaseService.Instance;
                    using var session = db.OpenSession();
                    var docId = $"reports/excel/{System.Text.RegularExpressions.Regex.Replace(project.ProjectId, @"[^\w]", "")}";
                    var existing = session.Load<Models.Documents.ReportDocument>(docId);
                    var doc = existing ?? new Models.Documents.ReportDocument { Id = docId };
                    doc.ProjectId   = project.ProjectId;
                    doc.UserEmail   = userEmail;
                    doc.Platform    = "bim360";
                    doc.Title       = $"{Path.GetFileName(filePath)} | {totalFiles:N0} files | {totalSizeStr}";
                    doc.Status      = "complete";
                    doc.DownloadUrl = filePath;
                    doc.ErrorMessage = $"Total files: {totalFiles:N0}, Total size: {totalSizeStr}";
                    doc.CompletedAt = DateTime.UtcNow;
                    doc.FirstSeenAt = doc.FirstSeenAt == default ? DateTime.UtcNow : doc.FirstSeenAt;
                    doc.LastSeenAt  = DateTime.UtcNow;
                    if (existing == null) session.Store(doc);
                    session.SaveChanges();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[excel] Read failed: {ex.Message}");
                    sse.Broadcast("log", new { level = "WARN", timestamp = Now(),
                        message = $"[{project.Name}] Excel read failed: {ex.Message}",
                        platform = "bim360" });
                }
            });
        }


        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:0.##} GB";
            if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:0.##} MB";
            if (bytes >= 1_024)         return $"{bytes / 1_024.0:0.##} KB";
            return $"{bytes} B";
        }
        // BIM360 Document Log has no public REST API -- always use browser automation.
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

                // Capture URL immediately -- BEFORE ExportRun.Begin which may navigate the page
                var currentUrl = picker.Page.Url;
                Console.WriteLine($"[bim360] Post-navigation URL: {currentUrl}");

                // Begin report tracker --  snapshot existing reports before triggering
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

                if (currentUrl.Contains("/docs/files/") || currentUrl.Contains("acc.autodesk.com"))
                {
                    //  ACC docs/files page: Export  -> Files Log ────────────────
                    Console.WriteLine($"[bim360] ACC docs/files page -- clicking Export  -> Files Log");
                    var exportTriggeredAt = await dialog.OpenAndExport();
                    emailsQueued++;

                    //  Navigate to Reports page -- find the row created AFTER exportTriggeredAt
                    var reportsUrl = $"https://acc.autodesk.com/docs/reports/projects/{project.ProjectId}";
                    await NavigateToReportsAndCapture(picker.Page, project, reportsUrl, userEmail, exportTriggeredAt);
                }
                else
                {
                    //  BIM360 admin page: Plans + Project Files (2 exports) ────────
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

                    await rootSel.SelectRoot("Project Files");
                    await dialog.OpenAndExport();
                    emailsQueued++;
                }

                // Finalize tracker --  poll for new reports (up to 90s)
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

