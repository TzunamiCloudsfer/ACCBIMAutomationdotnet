using System;
using System.Threading.Tasks;
using AutodeskAutomation.Services;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Acc
{
    public class FilesPage
    {
        private readonly IPage _page;

        public FilesPage(IPage page) => _page = page;

        public async Task OpenProjectFiles()
        {
            // Check for the "Can't view folder" access-denied state before interacting.
            // This appears when the authenticated user lacks permission to view the project.
            var bodyText = await _page.EvaluateAsync<string>(
                "document.body ? document.body.innerText : ''");
            if (!string.IsNullOrEmpty(bodyText) &&
                bodyText.Contains("It may have been deleted or you don't have permission"))
                throw new Exception("access denied");

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

                // Check again after clicking — the folder content area can also show the error
                bodyText = await _page.EvaluateAsync<string>(
                    "document.body ? document.body.innerText : ''");
                if (!string.IsNullOrEmpty(bodyText) &&
                    bodyText.Contains("You don't have access to any folders. Contact your project administrator."))
                    throw new Exception("access denied");
                throw new Exception("link not found");
            }
        }
    }
}

