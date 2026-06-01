using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Bim360
{
    public class DocumentLogDialog
    {
        private readonly IPage _page;

        public DocumentLogDialog(IPage page) => _page = page;

        public async Task OpenAndExport()
        {
            await DismissPendoOverlay();

            // 1 â€” find and click the Document Log toolbar button
            await ClickDocumentLogButton();

            await DismissPendoOverlay();

            // 2 â€” wait for an ENABLED Export button (not the background one which is disabled)
            Console.WriteLine("[bim360] Waiting for enabled Export buttonâ€¦");
            await _page.WaitForFunctionAsync(@"
                () => [...document.querySelectorAll('button')]
                    .some(b => b.textContent.trim() === 'Export' && !b.disabled)",
                null, new PageWaitForFunctionOptions { Timeout = 15_000 });

            // 3 â€” click the enabled Export button
            Console.WriteLine("[bim360] Clicking Exportâ€¦");
            var exportBtn = _page.Locator("button:not([disabled])")
                .Filter(new LocatorFilterOptions { HasText = "Export" }).First;
            await exportBtn.ClickAsync(new LocatorClickOptions { Force = true });
            Console.WriteLine("[bim360] Export clicked.");

            // 4 â€” wait for modal to close
            var modal = _page.Locator("[role=\"dialog\"]")
                .Filter(new LocatorFilterOptions { HasText = "export document log" }).Last;
            try
            {
                await modal.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Hidden, Timeout = 15_000 });
                Console.WriteLine("[bim360] Export queued â€” modal closed.");
            }
            catch
            {
                Console.WriteLine("[bim360] Modal did not close â€” assuming export queued.");
                await _page.Keyboard.PressAsync("Escape");
                await Task.Delay(600);
            }
        }

        // Hover over each .ActionBarButton to find the one with "Document log" tooltip
        private async Task ClickDocumentLogButton()
        {
            var btns  = _page.Locator(".ActionBarButton");
            var count = await btns.CountAsync();
            Console.WriteLine($"[bim360] Scanning {count} ActionBarButton(s) for Document log tooltipâ€¦");

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
                        Console.WriteLine($"[bim360] Document log tooltip on ActionBarButton[{i}] â€” clicking.");
                        await btns.Nth(i).ClickAsync(new LocatorClickOptions { Force = true });
                        await Task.Delay(600);
                        return;
                    }
                }
                catch { /* hover failed, try next */ }
            }

            // Fallback: second-to-last ActionBarButton
            var fb = Math.Max(0, count - 2);
            Console.WriteLine($"[bim360] Tooltip not found â€” falling back to ActionBarButton[{fb}].");
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

