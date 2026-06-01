using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Acc
{
    public class AccExportDialog
    {
        private readonly IPage _page;

        public AccExportDialog(IPage page) => _page = page;

        public async Task TriggerFilesLogExport()
        {
            // Click the "Export" dropdown button in the toolbar
            var exportBtn = _page.GetByRole(AriaRole.Button, new() { Name = "Export" })
                .Or(_page.Locator("[aria-label*='export' i]").First);

            await exportBtn.WaitForAsync(new LocatorWaitForOptions
                { State = WaitForSelectorState.Visible, Timeout = 20_000 });
            await exportBtn.ClickAsync();
            await Task.Delay(500);

            // Select "Files Log" from the dropdown
            var filesLog = _page.GetByText("Files Log", new() { Exact = false })
                .Or(_page.GetByRole(AriaRole.Menuitem, new() { Name = "Files Log" }));
            await filesLog.WaitForAsync(new LocatorWaitForOptions
                { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            await filesLog.ClickAsync();
            await Task.Delay(500);

            // Confirm in the dialog if one appears
            try
            {
                var confirmBtn = _page.GetByRole(AriaRole.Button, new() { Name = "Export" })
                    .Or(_page.Locator("button").Filter(new() { HasText = "Export" }).Last);
                await confirmBtn.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                await confirmBtn.ClickAsync();
            }
            catch { /* no confirmation dialog â€” export was triggered directly */ }

            await Task.Delay(1000);
        }
    }
}

