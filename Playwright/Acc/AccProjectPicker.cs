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
            var sse = AutodeskAutomation.Services.SseService.Instance;
            var url = $"https://acc.autodesk.com/docs/files/projects/{project.ProjectId}";

            sse.Broadcast("log", new { level = "INFO",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = $"[ACC] Navigating to project: {project.Name} — {url}",
                platform = "acc" });

            await _page.GotoAsync(url, new PageGotoOptions
                { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await Task.Delay(2000);

            var currentUrl = _page.Url;
            sse.Broadcast("log", new { level = "INFO",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = $"[ACC] Landed URL for {project.Name}: {currentUrl}",
                platform = "acc" });

            if (currentUrl.Contains("identity.autodesk") || currentUrl.Contains("accounts.autodesk"))
                throw new Exception("Autodesk session expired --  please re-authenticate.");

            // If redirected away from the project files page, the project has no DM module
            if (!currentUrl.Contains("/docs/files/"))
            {
                sse.Broadcast("log", new { level = "WARN",
                    timestamp = DateTime.UtcNow.ToString("O"),
                    message = $"[ACC] {project.Name} — no /docs/files/ in URL, marking no_dm. Landed: {currentUrl}",
                    platform = "acc" });
                return null;
            }

            return project.Name;
        }
    }
}

