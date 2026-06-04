using System;
using System.Threading.Tasks;
using AutodeskAutomation.Models.Documents;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Acc
{
    public class AccProjectPicker
    {
        private readonly IPage _page;

        public AccProjectPicker(IPage page) => _page = page;

        public async Task<string?> NavigateToProject(ProjectDocument project)
        {
            var url = $"https://acc.autodesk.com/build/projects/{project.ProjectId}";
            await _page.GotoAsync(url, new PageGotoOptions
                { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await Task.Delay(2000);

            var currentUrl = _page.Url;
            if (currentUrl.Contains("identity.autodesk") || currentUrl.Contains("accounts.autodesk"))
                throw new Exception("Autodesk session expired --  please re-authenticate.");

            // If redirected away from the project page it's likely no_dm
            if (!currentUrl.Contains("acc.autodesk.com"))
                return null;

            return project.Name;
        }
    }
}

