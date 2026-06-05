using System;
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
            Log(project.Name, "Checking for Data Management...");

            //  Strategy 1: ACC project-admin page ───────────────────────────────
            // Newer BIM360/ACC projects: acc.autodesk.com/project-admin/members/projects/{id}
            // The "Data Management" product picker item:
            //   <a data-testid="ProductPicker__product-docs" href="/docs/files/projects/{id}">
            var accAdminUrl = $"https://acc.autodesk.com/project-admin/members/projects/{project.ProjectId}";
            var result = await TryAccAdminFlow(project, accAdminUrl);
            if (result != null) return result;

            //  Strategy 2: Legacy BIM360 admin (admin.b360.autodesk.com) ────────
            if (!string.IsNullOrEmpty(_accountId))
                return await TryBim360AdminFlow(project);

            Log(project.Name, "No accountId -- no_dm");
            return null;
        }

                //  ACC docs/files direct navigation ───────────────────────────────────────
        // The Data Management link sits inside a floating-ui tooltip that only exists
        // in the DOM when the product picker trigger is hovered. Instead of waiting
        // for that, navigate directly to the well-known docs/files URL.
        private async Task<string?> TryAccAdminFlow(ProjectDocument project, string url)
        {
            var docsUrl = $"https://acc.autodesk.com/docs/files/projects/{project.ProjectId}";
            Console.WriteLine($"[bim360] ACC docs/files direct -> {docsUrl}");
            try
            {
                await Page.GotoAsync(docsUrl, new PageGotoOptions
                    { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
                await Task.Delay(4000);

                var landed = Page.Url;
                Console.WriteLine($"[bim360] ACC docs/files landed: {landed}");

                if (IsSignInPage(landed))
                    throw new Exception("Autodesk session expired -- please click Login.");

                // If redirected away from docs/files, project has no Data Management
                if (!landed.Contains("/docs/files/"))
                {
                    Log(project.Name, $"No Data Management -- redirected to {landed}");
                    return null;
                }

                Log(project.Name, $"Data Management page loaded: {landed}");
                return project.Name;
            }
            catch (Exception ex) when (ex.Message.Contains("session expired")) { throw; }
            catch (Exception ex)
            {
                Console.WriteLine($"[bim360] ACC docs/files error: {ex.Message}");
                return null;
            }
        }
        //  Legacy BIM360 admin ───────────────────────────────────────────────────
        private async Task<string?> TryBim360AdminFlow(ProjectDocument project)
        {
            var url = $"https://admin.b360.autodesk.com/admin/{_accountId}/projects/{project.ProjectId}";
            Console.WriteLine($"[bim360] BIM360 admin -> {url}");
            try
            {
                await Page.GotoAsync(url, new PageGotoOptions
                    { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
                await Task.Delay(3000);

                var landed = Page.Url;
                if (IsSignInPage(landed)) throw new Exception("Autodesk session expired -- please click Login.");
                if (!landed.Contains("autodesk.com")) return null;

                var opened = await OpenDocumentManagement(project);
                if (!opened) return null;

                try
                {
                    var plans = Page.GetByRole(AriaRole.Treeitem, new() { Name = "Plans" })
                        .Or(Page.Locator("[title=\"Plans\"]").First)
                        .Or(Page.GetByText("Plans", new() { Exact = true }).First);
                    await plans.First.WaitForAsync(new LocatorWaitForOptions
                        { State = WaitForSelectorState.Visible, Timeout = 60_000 });
                }
                catch
                {
                    try
                    {
                        var pf = Page.GetByRole(AriaRole.Treeitem, new() { Name = "Project Files" })
                            .Or(Page.Locator("[title=\"Project Files\"]").First)
                            .Or(Page.GetByText("Project Files", new() { Exact = true }).First);
                        await pf.First.WaitForAsync(new LocatorWaitForOptions
                            { State = WaitForSelectorState.Visible, Timeout = 30_000 });
                    }
                    catch { Log(project.Name, "DM folder tree did not load -- no_dm"); return null; }
                }
                return project.Name;
            }
            catch (Exception ex) when (ex.Message.Contains("session expired")) { throw; }
            catch (Exception ex) { Console.WriteLine($"[bim360] BIM360 error: {ex.Message}"); return null; }
        }

        private async Task<bool> OpenDocumentManagement(ProjectDocument project)
        {
            try
            {
                var trigger = Page.Locator("a, button, div, span")
                    .Filter(new LocatorFilterOptions
                        { Has = Page.Locator(":text-matches(\"^project admin$\", \"i\")") }).First;
                await DismissPendoOverlay();
                await trigger.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 8_000 });
                await trigger.ClickAsync(new LocatorClickOptions { Force = true });
                await Task.Delay(1500);

                var dmLink = Page.GetByRole(AriaRole.Link, new() { Name = "document management" })
                    .Or(Page.GetByText("Document Management", new() { Exact = true }).First);
                await dmLink.First.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                await dmLink.First.ClickAsync(new LocatorClickOptions { Force = true });
                await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 30_000 });
                await Task.Delay(3000);
                if (!Page.Url.Contains($"projects/{project.ProjectId}")) return true;
            }
            catch { }

            try
            {
                var direct = Page.GetByRole(AriaRole.Link, new() { Name = "document management" });
                await DismissPendoOverlay();
                await direct.First.WaitForAsync(new LocatorWaitForOptions
                    { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                await direct.First.ClickAsync(new LocatorClickOptions { Force = true });
                await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                    new PageWaitForLoadStateOptions { Timeout = 30_000 });
                await Task.Delay(3000);
                return true;
            }
            catch { }
            return false;
        }

        public async Task DismissPendoOverlay()
        {
            try { await Page.Keyboard.PressAsync("Escape"); } catch { }
            try { await Page.EvaluateAsync(@"() => { document.querySelectorAll('#pendo-base,[id^=""pendo-base""],._pendo-step-container,._pendo-backdrop,[class*=""pendo-backdrop""],[pendo-region]').forEach(el => el.remove()); }"); }
            catch { }
            await Task.Delay(150);
        }

        private static bool IsSignInPage(string url)
            => url.Contains("identity.autodesk") || url.Contains("login.autodesk")
            || url.Contains("signin.autodesk") || url.Contains("accounts.autodesk");

        private void Log(string name, string msg)
        {
            Console.WriteLine($"[bim360] [{name}] {msg}");
            AutodeskAutomation.Services.SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                timestamp = DateTime.UtcNow.ToString("O"),
                message = $"[{name}] {msg}",
                platform = "bim360"
            });
        }
    }
}
