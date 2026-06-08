using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Acc
{
    public class AccExportDialog
    {
        private readonly IPage _page;

        public AccExportDialog(IPage page) => _page = page;

        public async Task<bool> TriggerFilesLogExport()
        {
            var sse = AutodeskAutomation.Services.SseService.Instance;

            // Step 1: Wait up to 15s for the Export toolbar dropdown to render.
            // DocumentLogDialog (working BIM360/ACC path) does the same poll before clicking.
            // Without this, the toolbar may not exist yet after the 2s wait in FilesPage.
            var exportBtn = _page.Locator("[data-testid=\"action-toolbar-dropdown\"]");
            bool toolbarFound = false;
            for (int t = 0; t < 15; t++)
            {
                try
                {
                    if (await exportBtn.CountAsync() > 0 && await exportBtn.IsVisibleAsync())
                    {
                        toolbarFound = true;
                        break;
                    }
                }
                catch { }
                await Task.Delay(1000);
            }

            if (!toolbarFound)
            {
                sse.Broadcast("log", new {
                    level = "WARN",
                    timestamp = DateTime.UtcNow.ToString("O"),
                    message = "[ACC Export] Toolbar dropdown not found after 15s — skipping Files log export",
                    platform = "acc"
                });
                return false;
            }

            await exportBtn.ClickAsync(new LocatorClickOptions { Force = true });
            await Task.Delay(2500);

            // Step 2: Click "Files log" via JS — plain div element, no role="menuitem"
            var filesLogClicked = await _page.EvaluateAsync<bool>(
                "(function() {" +
                "  var els = Array.from(document.querySelectorAll('div, span, li, button, a'));" +
                "  for (var i = 0; i < els.length; i++) {" +
                "    var el = els[i];" +
                "    if (el.offsetWidth > 0 && el.offsetHeight > 0 &&" +
                "        el.innerText && el.innerText.trim().toLowerCase() === 'files log') {" +
                "      el.click(); return true;" +
                "    }" +
                "  }" +
                "  return false;" +
                "})()");

            Console.WriteLine(filesLogClicked ? "[acc] 'Files log' clicked." : "[acc] 'Files log' not found.");
            AutodeskAutomation.Services.SseService.Instance.Broadcast("log", new
            {
                level = filesLogClicked ? "INFO" : "WARN",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = filesLogClicked ? "[ACC Dropdown] 'Files log' clicked" : "[ACC Dropdown] 'Files log' not found",
                platform = "acc"
            });

            // Step 3: Click confirm button if a dialog appears
            // For many projects "Files log" submits directly with no confirm dialog
            await Task.Delay(3000);

            var confirmClicked = await _page.EvaluateAsync<bool>(
                "(function() {" +
                "  var all = Array.from(document.querySelectorAll('button'));" +
                "  for (var i = 0; i < all.length; i++) {" +
                "    var b = all[i];" +
                "    if (b.className && b.className.indexOf('SaveButton') >= 0 && !b.disabled) {" +
                "      b.click(); return true;" +
                "    }" +
                "  }" +
                "  var td = document.querySelector('[data-testid=\"button\"]');" +
                "  if (td && !td.disabled) { td.click(); return true; }" +
                "  return false;" +
                "})()");

            if (confirmClicked)
                Console.WriteLine("[acc] Export confirm button clicked.");
            else
                Console.WriteLine("[acc] No confirm button -- export submitted directly.");

            AutodeskAutomation.Services.SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = "[ACC Export] Files Log export submitted.",
                platform = "acc"
            });

            return filesLogClicked;
        }
    }
}

