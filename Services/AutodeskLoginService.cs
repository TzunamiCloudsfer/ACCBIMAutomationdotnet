using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;

namespace AutodeskAutomation.Services
{
    public class AutodeskLoginService
    {
        private static readonly AutodeskLoginService _instance = new AutodeskLoginService();
        public static AutodeskLoginService Instance => _instance;

        private static readonly HttpClient _http = new HttpClient();
        private AutodeskLoginService() { }

        public async Task PerformLoginAsync(string? fallbackEmail = null)
        {
            var srv = ServerState.Instance;
            var db = DatabaseService.Instance;
            var sse = SseService.Instance;

            // Broadcast periodic elapsed updates
            var timer = new Timer(_ =>
            {
                if (srv.LoginPending)
                    sse.Broadcast("login-status", new { status = "waiting", elapsed = srv.LoginElapsedSeconds });
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

            try
            {
                void Log(string msg)
                {
                    Console.WriteLine($"[login] {msg}");
                    sse.Broadcast("log", new { level = "INFO", message = $"[Login] {msg}", platform = "auth" });
                }

                Log("Starting login — creating temp storage directory…");
                var tempAuthPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "storage", ".temp-auth-state.json");
                var tempDir = Path.GetDirectoryName(tempAuthPath);
                if (tempDir != null) Directory.CreateDirectory(tempDir);
                Log($"Temp path: {tempAuthPath}");

                Log("Initialising Playwright…");
                using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
                Log("Playwright ready. Launching Chrome…");

                var launchOpts = BrowserHelper.HeadedOptions();
                Log($"Chrome executable: {launchOpts.ExecutablePath ?? "(Playwright default)"}");
                var browser = await playwright.Chromium.LaunchAsync(launchOpts);
                Log("Chrome launched. Creating context…");
                var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = null });
                var page = await context.NewPageAsync();
                Log("Browser ready — navigating to acc.autodesk.com…");

                // Capture first Bearer token from any Autodesk API domain
                string? capturedAuth = null;
                page.Request += (_, req) =>
                {
                    if (capturedAuth != null) return;
                    var reqUrl = req.Url;
                    // Broad filter — catch any autodesk.com API call with a Bearer token
                    if (!reqUrl.Contains("autodesk.com") && !reqUrl.Contains("autodesk.io")) return;
                    if (req.Headers.TryGetValue("authorization", out var auth) &&
                        auth.StartsWith("Bearer "))
                        capturedAuth = auth;
                };

                await page.GotoAsync("https://acc.autodesk.com/", new PageGotoOptions
                    { WaitUntil = WaitUntilState.DOMContentLoaded });
                await Task.Delay(8000);

                bool loginDetected = false;
                for (int i = 0; i < 200 && !loginDetected; i++)
                {
                    await Task.Delay(3000);
                    string url;
                    try { url = page.Url; } catch { break; }

                    var onLoginPage = url.Contains("identity.autodesk") ||
                                      url.Contains("accounts.autodesk") ||
                                      (url.Contains("login") && url.Contains("autodesk"));
                    var bareRoot = url == "https://acc.autodesk.com" || url == "https://acc.autodesk.com/";
                    var onDashboard = url.StartsWith("https://acc.autodesk.com") && !onLoginPage && !bareRoot;

                    if (onDashboard)
                    {
                        // Wait for captured token if not yet received
                        if (capturedAuth == null)
                        {
                            for (int w = 0; w < 5 && capturedAuth == null; w++)
                                await Task.Delay(2000);
                        }

                        // ── Navigate to BIM360 admin and wait for full authentication ─────────
                        // The export browser needs cookies for admin.b360.autodesk.com.
                        // We navigate there now so the user can complete BIM360 sign-in
                        // interactively if it shows a login page.
                        Log("Navigating to admin.b360.autodesk.com — sign in if prompted…");
                        sse.Broadcast("login-status", new
                        {
                            status  = "waiting",
                            elapsed = srv.LoginElapsedSeconds,
                            message = "Connecting to BIM360 admin — sign in if a login page appears in the browser…"
                        });

                        try
                        {
                            await page.GotoAsync("https://admin.b360.autodesk.com/",
                                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });

                            // Wait up to 3 minutes for the BIM360 admin dashboard
                            // (user may need to complete a second Autodesk sign-in for BIM360)
                            for (int w = 0; w < 60; w++)
                            {
                                await Task.Delay(3000);
                                var bimUrl = page.Url;
                                Log($"BIM360 URL: {bimUrl}");

                                if (bimUrl.Contains("admin.b360.autodesk.com") &&
                                    !bimUrl.Contains("signin") &&
                                    !bimUrl.Contains("identity.autodesk") &&
                                    !bimUrl.Contains("accounts.autodesk"))
                                {
                                    Log("BIM360 admin authenticated!");
                                    break;
                                }
                            }
                        }
                        catch (Exception bimEx)
                        {
                            Log($"BIM360 navigation: {bimEx.Message}");
                        }

                        // Save combined state (ACC + BIM360 cookies + localStorage)
                        await context.StorageStateAsync(new BrowserContextStorageStateOptions
                            { Path = tempAuthPath });
                        await browser.CloseAsync();
                        loginDetected = true;
                    }
                }

                if (!loginDetected)
                {
                    try { await browser.CloseAsync(); } catch { }
                    throw new Exception("Login timeout after 10 minutes. Please try again.");
                }

                // Detect logged-in user email
                string? detectedEmail = null;
                if (capturedAuth != null)
                {
                    try
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get,
                            "https://api.userprofile.autodesk.com/userinfo");
                        req.Headers.TryAddWithoutValidation("authorization", capturedAuth);
                        var resp = await _http.SendAsync(req);
                        if (resp.IsSuccessStatusCode)
                        {
                            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                            detectedEmail = json["email"]?.ToString()?.ToLowerInvariant().Trim();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[login] Could not fetch userinfo: {ex.Message}");
                    }
                }

                // Fallback chain: existing active user → Cloudsfer session email → "autodesk-user"
                if (string.IsNullOrEmpty(detectedEmail) && srv.ActiveUser != null)
                {
                    detectedEmail = srv.ActiveUser;
                    Console.WriteLine($"[login] Using existing active user: {detectedEmail}");
                }
                if (string.IsNullOrEmpty(detectedEmail) && !string.IsNullOrEmpty(fallbackEmail))
                {
                    detectedEmail = fallbackEmail;
                    Console.WriteLine($"[login] Using Cloudsfer session email as fallback: {detectedEmail}");
                }
                if (string.IsNullOrEmpty(detectedEmail))
                {
                    detectedEmail = "autodesk-user";
                    Console.WriteLine("[login] Could not detect email — using default slug.");
                }

                // Write user-specific auth state for both platforms
                var slug = SlugHelper.EmailToSlug(detectedEmail);
                var bimDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "storage", "users", slug);
                Directory.CreateDirectory(bimDir);
                var authPath = Path.Combine(bimDir, "auth-state.json");
                File.Copy(tempAuthPath, authPath, overwrite: true);
                try { File.Delete(tempAuthPath); } catch { }

                // Migrate file-based data into RavenDB for this user
                db.MigrateProjectsFromFile(detectedEmail, "acc",
                    Path.Combine(bimDir, "projects.json"));
                db.MigrateProjectsFromFile(detectedEmail, "bim360",
                    Path.Combine(bimDir, "projects.json"));
                db.MigrateCheckpointFromFile(detectedEmail, "acc",
                    Path.Combine(bimDir, "checkpoint.json"));
                db.MigrateCheckpointFromFile(detectedEmail, "bim360",
                    Path.Combine(bimDir, "checkpoint.json"));

                // Store the captured Bearer token so API calls can use it without a browser
                if (capturedAuth != null && detectedEmail != null)
                {
                    try
                    {
                        var tokenDoc = new Models.Documents.AutodeskTokenDocument
                        {
                            UserEmail    = detectedEmail,
                            AccessToken  = capturedAuth.Replace("Bearer ", ""),
                            RefreshToken = null,  // browser-captured tokens can't be refreshed
                            ExpiresAt    = DateTime.UtcNow.AddHours(1),  // Autodesk tokens last ~1h
                            Scope        = "data:read account:read",
                            SavedAt      = DateTime.UtcNow
                        };
                        db.SaveAutodeskToken(tokenDoc);
                        Log($"Bearer token stored for API use (user: {detectedEmail})");
                    }
                    catch (Exception ex)
                    {
                        Log($"Could not store Bearer token: {ex.Message}");
                    }
                }

                // Auto-detect Autodesk account IDs
                if (capturedAuth != null)
                {
                    sse.Broadcast("login-status", new
                    {
                        status = "detecting-accounts",
                        elapsed = srv.LoginElapsedSeconds
                    });
                    await AutoDetectAccounts(capturedAuth, detectedEmail);
                }

                // Activate user in server state
                var prevUser = srv.ActiveUser;
                srv.ActiveUser = detectedEmail;
                srv.ActiveUserSlug = slug;
                db.SaveLastUser(detectedEmail);

                if (detectedEmail != prevUser && !srv.IsRunning)
                {
                    srv.Acc.Reset();
                    srv.Bim360.Reset();
                }

                srv.LoginPending = false;
                srv.LoginDetected = true;
                sse.Broadcast("login-status", new
                {
                    status = "completed",
                    elapsed = srv.LoginElapsedSeconds,
                    user = srv.ActiveUser
                });
                sse.Broadcast("user-changed", new { user = srv.ActiveUser });
            }
            catch (Exception ex)
            {
                srv.LoginPending = false;
                srv.LoginDetected = false;
                srv.LoginStartTime = null;
                SseService.Instance.Broadcast("login-status", new { status = "failed", error = ex.Message });
            }
            finally
            {
                timer.Dispose();
            }
        }

        private async Task AutoDetectAccounts(string authHeader, string userEmail)
        {
            var db = DatabaseService.Instance;
            var sse = SseService.Instance;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://developer.api.autodesk.com/project/v1/hubs");
                req.Headers.TryAddWithoutValidation("authorization", authHeader);
                req.Headers.TryAddWithoutValidation("accept", "application/vnd.api+json");
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) return;

                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                var hubs = json["data"] as JArray ?? new JArray();

                bool accFound = false, bimFound = false;
                foreach (var hub in hubs)
                {
                    var rawId = hub["id"]?.ToString() ?? "";
                    var accountId = Regex.Replace(rawId, @"^[a-zA-Z]\.", "");
                    var extType = (hub["attributes"]?["extension"]?["type"]?.ToString() ?? "").ToLower();
                    var hubName = hub["attributes"]?["name"]?.ToString() ?? accountId;

                    if (extType.Contains("bim360") && !bimFound)
                    {
                        var url = $"https://admin.b360.autodesk.com/admin/{accountId}/projects";
                        db.SetAdminUrl(userEmail, "bim360", url);
                        sse.Broadcast("account-detected", new { platform = "bim360", accountId, hubName, url });
                        bimFound = true;
                    }
                    else if (!extType.Contains("bim360") && !accFound)
                    {
                        var url = $"https://acc.autodesk.com/account-admin/projects/accounts/{accountId}/active";
                        db.SetAdminUrl(userEmail, "acc", url);
                        sse.Broadcast("account-detected", new { platform = "acc", accountId, hubName, url });
                        accFound = true;
                    }

                    if (accFound && bimFound) break;
                }

                sse.Broadcast("accounts-detected", new
                {
                    acc = db.GetAdminUrl(userEmail, "acc"),
                    bim360 = db.GetAdminUrl(userEmail, "bim360"),
                    count = hubs.Count
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[auto-detect] Account detection failed: {ex.Message}");
                SseService.Instance.Broadcast("account-detection-failed", new { error = ex.Message });
            }
        }
    }
}
