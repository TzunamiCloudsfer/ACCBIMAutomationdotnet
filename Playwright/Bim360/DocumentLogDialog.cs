using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Bim360
{
    public class DocumentLogDialog
    {
        private readonly IPage _page;

        public DocumentLogDialog(IPage page) => _page = page;

        // Returns the UTC datetime just before the export was triggered.
        // The caller uses this to identify the newly created report row in the Reports table.
        public async Task<DateTime> OpenAndExport()
        {
            var exportTriggeredAt = DateTime.Now.AddMinutes(-1);
            await DismissPendoOverlay();

            // Detect which page we're on by waiting for the ACC or BIM360 toolbar
            // ACC docs/files: data-testid="action-toolbar-dropdown"  (Export ▼ button)
            // BIM360 admin:   .ActionBarButton                       (icon toolbar)
            var accExportBtn = _page.Locator("[data-testid=\"action-toolbar-dropdown\"]");
            var bim360Btn    = _page.Locator(".ActionBarButton");

            // Wait up to 15s for whichever toolbar appears first
            bool isAccPage = false;
            for (int w = 0; w < 15; w++)
            {
                if (await accExportBtn.CountAsync() > 0) { isAccPage = true;  break; }
                if (await bim360Btn.CountAsync()    > 0) { isAccPage = false; break; }
                await Task.Delay(1000);
            }

            Console.WriteLine($"[dialog] isAccPage={isAccPage}, url={_page.Url}");
            AutodeskAutomation.Services.SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = isAccPage ? "[Export] ACC toolbar detected -- clicking Export dropdown"
                                    : "[Export] BIM360 toolbar detected -- scanning ActionBarButtons",
                platform = "bim360"
            });

            if (isAccPage)
            {
                exportTriggeredAt = DateTime.Now.AddMinutes(-1);  // 1 min buffer so Reports table match is reliable
                await OpenAccExportDropdown(accExportBtn);
                return exportTriggeredAt;
            }

            //  Fallback: BIM360 ActionBarButton hover scan ─────────────────────

            // 1 --  find and click the Document Log toolbar button
            await ClickDocumentLogButton();

            await DismissPendoOverlay();

            // 2 --  wait for an ENABLED Export button (not the background one which is disabled)
            Console.WriteLine("[bim360] Waiting for enabled Export button--");
            await _page.WaitForFunctionAsync(@"
                () => [...document.querySelectorAll('button')]
                    .some(b => b.textContent.trim() === 'Export' && !b.disabled)",
                null, new PageWaitForFunctionOptions { Timeout = 15_000 });

            // 3 --  click the enabled Export button
            Console.WriteLine("[bim360] Clicking Export--");
            var exportBtn = _page.Locator("button:not([disabled])")
                .Filter(new LocatorFilterOptions { HasText = "Export" }).First;
            await exportBtn.ClickAsync(new LocatorClickOptions { Force = true });
            Console.WriteLine("[bim360] Export clicked.");

            // 4 --  wait for modal to close
            var modal = _page.Locator("[role=\"dialog\"]")
                .Filter(new LocatorFilterOptions { HasText = "export document log" }).Last;
            try
            {
                await modal.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
                Console.WriteLine("[bim360] Export queued --  modal closed.");
            }
            catch
            {
                Console.WriteLine("[bim360] Modal did not close --  assuming export queued.");
                await _page.Keyboard.PressAsync("Escape");
                await Task.Delay(600);
            }
            return exportTriggeredAt;
        }

        //  ACC docs/files Export dropdown ───────────────────────────────────────
        private async Task OpenAccExportDropdown(ILocator exportBtn)
        {
            Console.WriteLine("[bim360] Clicking ACC Export dropdown...");
            await exportBtn.ClickAsync(new LocatorClickOptions { Force = true });
            await Task.Delay(1500);

            // Step 1: Click "Files log" via JavaScript (plain div element, no role="menuitem")
            // Screenshot at export-dropdown-*.png confirmed the items are "Files log" and "Folder permissions"
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

            Console.WriteLine(filesLogClicked ? "[bim360] 'Files log' clicked." : "[bim360] 'Files log' not found.");
            AutodeskAutomation.Services.SseService.Instance.Broadcast("log", new
            {
                level = filesLogClicked ? "INFO" : "WARN",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = filesLogClicked ? "[Dropdown] 'Files log' clicked" : "[Dropdown] 'Files log' not found in dropdown",
                platform = "bim360"
            });

            // Step 2: Wait briefly then try to click the Export confirm button if it appears.
            // For many projects clicking "Files log" submits the export directly (no confirm dialog).
            await Task.Delay(2000);

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
                Console.WriteLine("[bim360] Export confirm button clicked.");
            else
                Console.WriteLine("[bim360] No confirm button -- export submitted directly by 'Files log' click.");

            // Either way, treat as submitted
            AutodeskAutomation.Services.SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = "[Export] Files Log export submitted -- report will be sent by email.",
                platform = "bim360"
            });
            Console.WriteLine("[bim360] ACC export complete.");
        }
        // Hover over each .ActionBarButton to find the one with "Document log" tooltip
        private async Task ClickDocumentLogButton()
        {
            var btns  = _page.Locator(".ActionBarButton");
            var count = await btns.CountAsync();
            Console.WriteLine($"[bim360] Scanning {count} ActionBarButton(s) for Document log tooltip--");

            for (int i = 0; i < count; i++)
            {
                try
                {
                    await btns.Nth(i).HoverAsync();
                    await Task.Delay(500);

                    bool hit = await _page.EvaluateAsync<bool>(@"() => {
                        for (const sel of ['[class*=""tooltip"" i]', '[role=""tooltip""]', '[class*=""Tooltip""]']) {
                            for (const el of document.querySelectorAll(sel)) {
                                if (el.offsetWidth > 0 && /document.?log/i.test(el.textContent))
                                    return true;
                            }
                        }
                        return false;
                    }");

                    if (hit)
                    {
                        Console.WriteLine($"[bim360] Document log tooltip on ActionBarButton[{i}] --  clicking.");
                        await btns.Nth(i).ClickAsync(new LocatorClickOptions { Force = true });
                        await Task.Delay(600);
                        return;
                    }
                }
                catch { /* hover failed, try next */ }
            }

            // Fallback: second-to-last ActionBarButton
            var fb = Math.Max(0, count - 2);
            Console.WriteLine($"[bim360] Tooltip not found --  falling back to ActionBarButton[{fb}].");
            await btns.Nth(fb).ClickAsync(new LocatorClickOptions { Force = true });
            await Task.Delay(600);
        }

        private async Task DismissPendoOverlay()
        {
            try { await _page.Keyboard.PressAsync("Escape"); } catch { }
            try
            {
                await _page.EvaluateAsync(@"() => {
                    document.querySelectorAll(
                        '#pendo-base, [id^=""pendo-base""], ._pendo-step-container, ' +
                        '._pendo-backdrop, [class*=""pendo-backdrop""], [pendo-region]'
                    ).forEach(el => el.remove());
                }");
            }
            catch { }
            await Task.Delay(150);
        }
    }
}

