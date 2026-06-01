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
            // Click "Project Files" in the left navigation or folder tree
            var projectFiles = _page.GetByRole(AriaRole.Link, new() { Name = "Project Files" })
                .Or(_page.GetByText("Project Files", new() { Exact = true }).First);

            await projectFiles.WaitForAsync(new LocatorWaitForOptions
                { State = WaitForSelectorState.Visible, Timeout = 20_000 });
            await projectFiles.ClickAsync();
            await Task.Delay(1500);
        }
    }
}

