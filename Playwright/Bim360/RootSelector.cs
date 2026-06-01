using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Bim360
{
    public class RootSelector
    {
        private readonly IPage _page;

        public RootSelector(IPage page) => _page = page;

        public async Task SelectRoot(string rootName)
        {
            Console.WriteLine($"[bim360] Selecting root folder: {rootName}");

            await DismissPendoOverlay();

            ILocator locator;
            if (rootName == "Plans")
            {
                locator = _page.GetByRole(AriaRole.Treeitem, new() { Name = "Plans" })
                    .Or(_page.Locator("[title=\"Plans\"]").First)
                    .Or(_page.GetByText("Plans", new() { Exact = true }).First);
            }
            else
            {
                locator = _page.GetByRole(AriaRole.Treeitem, new() { Name = "Project Files" })
                    .Or(_page.Locator("[title=\"Project Files\"]").First)
                    .Or(_page.GetByText("Project Files", new() { Exact = true }).First);
            }

            await locator.First.WaitForAsync(new LocatorWaitForOptions
                { State = WaitForSelectorState.Visible, Timeout = 20_000 });
            await locator.First.ClickAsync(new LocatorClickOptions { Force = true });

            // Wait for the file list / grid to appear
            try
            {
                await _page.WaitForSelectorAsync("table, [role=\"grid\"], [role=\"treegrid\"]",
                    new PageWaitForSelectorOptions
                        { State = WaitForSelectorState.Visible, Timeout = 20_000 });
            }
            catch
            {
                await Task.Delay(1500);
            }

            await Task.Delay(1000);
            Console.WriteLine($"[bim360] Root \"{rootName}\" selected.");
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

