using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using AutodeskAutomation.Services;
using Microsoft.Playwright;

namespace AutodeskAutomation.Controllers
{
    /// <summary>
    /// Temporary diagnostic controller -- reads a page with Playwright and
    /// returns its visible text + a screenshot. Used to inspect live ACC/BIM360 UI.
    /// </summary>
    [RoutePrefix("api/diag")]
    public class DiagController : ApiController
    {
        private readonly ServerState _srv = ServerState.Instance;

        // POST /api/diag/read-page
        // Body: { "url": "https://acc.autodesk.com/..." }
        [HttpPost, Route("read-page")]
        public async Task<IHttpActionResult> ReadPage([FromBody] ReadPageRequest body)
        {
            if (string.IsNullOrEmpty(body?.Url))
                return BadRequest("url is required");

            var authPath = GetAuthPath();
            if (!File.Exists(authPath))
                return BadRequest("No auth-state.json -- please login first.");

            try
            {
                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                var browser = await playwright.Chromium.LaunchAsync(BrowserHelper.HeadlessOptions());
                try
                {
                    var context = await browser.NewContextAsync(new BrowserNewContextOptions
                    {
                        StorageStatePath = authPath,
                        ViewportSize = new ViewportSize { Width = 1600, Height = 900 }
                    });
                    var page = await context.NewPageAsync();

                    await page.GotoAsync(body.Url, new PageGotoOptions
                        { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                    await Task.Delay(5000);  // let React SPA render

                    var finalUrl   = page.Url;
                    var title      = await page.TitleAsync();
                    var bodyText   = await page.EvaluateAsync<string>("() => document.body.innerText");

                    // Collect all button text
                    var btnTexts   = await page.EvaluateAsync<string[]>(@"
                        () => [...document.querySelectorAll('button, [role=""button""]')]
                            .map(b => b.innerText.trim()).filter(t => t.length > 0)");

                    // Collect table headers
                    var headers    = await page.EvaluateAsync<string[]>(@"
                        () => [...document.querySelectorAll('th, [role=""columnheader""]')]
                            .map(h => h.innerText.trim()).filter(t => t.length > 0)");

                    // Count rows
                    var rowCount   = await page.EvaluateAsync<int>(@"
                        () => document.querySelectorAll('tr:not(:first-child), [role=""row""]:not(:first-child)').length");

                    // Take screenshot
                    var shotDir  = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "storage", "screenshots");
                    Directory.CreateDirectory(shotDir);
                    var shotPath = Path.Combine(shotDir, $"page-{DateTime.UtcNow:yyyyMMdd-HHmmss}.png");
                    await page.ScreenshotAsync(new PageScreenshotOptions { Path = shotPath, FullPage = true });

                    return Ok(new
                    {
                        url           = finalUrl,
                        title,
                        rowCount,
                        buttons       = btnTexts,
                        tableHeaders  = headers,
                        screenshotPath = shotPath,
                        bodyPreview   = bodyText?.Length > 3000
                            ? bodyText.Substring(0, 3000) + "..."
                            : bodyText
                    });
                }
                finally { await browser.CloseAsync(); }
            }
            catch (Exception ex)
            {
                return Content(HttpStatusCode.InternalServerError,
                    new { error = ex.Message });
            }
        }

        private string GetAuthPath()
        {
            var slug = _srv.ActiveUserSlug ?? "rojin_bastola_tzunami_com";
            return Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                "storage", "users", slug, "auth-state.json");
        }

        public class ReadPageRequest { public string? Url { get; set; } }
    }
}
