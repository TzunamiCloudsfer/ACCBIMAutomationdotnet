using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutodeskAutomation.Models.Documents;
using Microsoft.Playwright;

namespace AutodeskAutomation.Playwright.Bim360
{
    public class Bim360ProjectPicker
    {
        public IPage Page { get; }
        private readonly string _accountId;

        public Bim360ProjectPicker(IPage page, string accountId)
        {
            Page = page;
            _accountId = accountId;
        }

        public async Task<string?> NavigateToDataManagement(ProjectDocument project)
        {
            if (string.IsNullOrEmpty(_accountId))
                throw new Exception($"AccountId is empty for project '{project.Name}'");

            var url = $"https://admin.b360.autodesk.com/admin/{_accountId}/projects/{project.ProjectId}";
            Console.WriteLine($"[bim360] â†’ {url}");

            await Page.GotoAsync(url, new PageGotoOptions
                { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
            await Stabilize(3000);

            var landed = Page.Url;
            Console.WriteLine($"[bim360] Landed: {landed}");

            // Session expired
            if (landed.Contains("identity.autodesk") || landed.Contains("login.autodesk") ||
                landed.Contains("signin.autodesk") || landed.Contains("accounts.autodesk"))
                throw new Exception("Autodesk session expired â€” please click Login to re-authenticate.");

            if (!landed.Contains("autodesk.com"))
            {
                Console.WriteLine($"[bim360] Redirected off Autodesk â€” marking as no_dm");
                return null;
            }

            var opened = await OpenDocumentManagement(project);
            if (!opened) return null;

            // Wait for the DM folder tree (Plans or Project Files) â€” up to 60s
            try
            {
                var plansNode = Page.GetByRole(AriaRole.Treeitem, new() { Name = "Plans" })
                    .Or(Page.Locator("[title=\"Plans\"]").First)
                    .Or(Page.GetByText("Plans", new() { Exact = true }).First);
                await plansNode.First.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            }
            catch
            {
                try
                {
                    var pfNode = Page.GetByRole(AriaRole.Treeitem, new() { Name = "Project Files" })
                        .Or(Page.Locator("[title=\"Project Files\"]").First)
                        .Or(Page.GetByText("Project Files", new() { Exact = true }).First);
                    await pfNode.First.WaitForAsync(new LocatorWaitForOptions
                        { State = WaitForSelectorState.Visible, Timeout = 30_000 });
                }
                catch
                {
                    Console.WriteLine($"[bim360] DM folder tree did not load â€” marking as no_dm");
                    return null;
                }
            }

            Console.WriteLine($"[bim360] Document Management loaded for: {project.Name}");
            return project.Name;
        }

        private async Task<bool> OpenDocumentManagement(ProjectDocument project)
        {
            // â”€â”€ Strategy A: Click "Project Admin â–¼" dropdown â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            Console.WriteLine($"[bim360] Opening Project Admin dropdownâ€¦");
            try
            {
                var trigger = Page.Locator("a, button, div, span")
                    .Filter(new LocatorFilterOptions
                    {
                        Has = Page.Locator(":text-matches(\"^project admin$\", \"i\")")
                    }).First;

                await DismissPendoOverlay();
                await trigger.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 8_000 });
                await trigger.ClickAsync(new LocatorClickOptions { Force = true });
                await Stabilize(1500);

                var dmLink = Page.GetByRole(AriaRole.Link, new() { Name = "document management" })
                    .Or(Page.GetByText("Document Management", new() { Exact = true }).First);
                await dmLink.First.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                Console.WriteLine($"[bim360] Clicking Document Management (dropdown)â€¦");
                await dmLink.First.ClickAsync(new LocatorClickOptions { Force = true });
                await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 30_000 }).ConfigureAwait(false);
                await Stabilize(3000);

                var after = Page.Url;
                Console.WriteLine($"[bim360] After DM click: {after}");

                var adminBase = $"admin.b360.autodesk.com/admin/{_accountId}/projects/{project.ProjectId}";
                if (!after.Contains(adminBase)) return true;  // navigated away = success

                Console.WriteLine($"[bim360] Dropdown DM click didn't navigate â€” trying direct link");
            }
            catch
            {
                Console.WriteLine($"[bim360] Dropdown approach failed â€” trying direct link");
            }

            // â”€â”€ Strategy B: Wait for direct "Document Management" link â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            Console.WriteLine($"[bim360] Waiting for Document Management link (15s)â€¦");
            try
            {
                var direct = Page.GetByRole(AriaRole.Link, new() { Name = "document management" });
                await DismissPendoOverlay();
                await direct.First.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                Console.WriteLine($"[bim360] Clicking Document Management (direct link)â€¦");
                await direct.First.ClickAsync(new LocatorClickOptions { Force = true });
                await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 30_000 }).ConfigureAwait(false);
                await Stabilize(3000);
                Console.WriteLine($"[bim360] After DM click: {Page.Url}");
                return true;
            }
            catch { }

            Console.WriteLine($"[bim360] Document Management not found â€” marking as no_dm");
            return false;
        }

        public async Task DismissPendoOverlay()
        {
            try { await Page.Keyboard.PressAsync("Escape"); } catch { }
            try
            {
                await Page.EvaluateAsync(@"() => {
                    document.querySelectorAll(
                        '#pendo-base, [id^=""pendo-base""], ._pendo-step-container, ' +
                        '._pendo-backdrop, [class*=""pendo-backdrop""], [pendo-region]'
                    ).forEach(el => el.remove());
                }");
            }
            catch { }
            await Task.Delay(150);
        }

        private Task Stabilize(int ms) => Task.Delay(ms);
    }
}

