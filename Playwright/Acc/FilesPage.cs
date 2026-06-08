using System;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Acc
{
    public class FilesPage
    {
        private readonly IPage _page;

        public FilesPage(IPage page) => _page = page;

        public async Task OpenProjectFiles()
        {
            // We're already on the files page after navigation — clicking "Project Files" in
            // the nav is optional. If it times out or isn't present, just proceed to export.
            try
            {
                var projectFiles = _page.GetByRole(AriaRole.Link, new() { Name = "Project Files" })
                    .Or(_page.GetByText("Project Files", new() { Exact = true }).First);
                await projectFiles.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 5_000 });
                await projectFiles.ClickAsync();
                await Task.Delay(2000);
            }
            catch
            {
                // Link not found or click failed — we're already on the files page, proceed
            }
        }
    }
}

