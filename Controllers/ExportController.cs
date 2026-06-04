using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using AutodeskAutomation.Models;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Controllers
{
    [RoutePrefix("api/export")]
    public class ExportController : ApiController
    {
        private readonly DatabaseService _db = DatabaseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;
        private readonly SseService _sse = SseService.Instance;

        [HttpPost, Route("all")]
        public async Task<HttpResponseMessage> ExportAll([FromBody] ExportAllRequest body)
        {
            if (_srv.ChainRunning || _srv.AccRunning || _srv.Bim360Running)
                return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "Export already running" });
            if (_srv.LoginPending)
                return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "Login in progress" });

            var accProjects = _db.GetProjects(_srv.ActiveUser, "acc");
            var bim360Projects = _db.GetProjects(_srv.ActiveUser, "bim360");

            if (accProjects.Count == 0 && bim360Projects.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "No projects configured. Run Discover on both platforms first." });

            if (body?.Fresh == true)
            {
                _db.ResetCheckpoint(_srv.ActiveUser, "acc");
                _db.ResetCheckpoint(_srv.ActiveUser, "bim360");
            }

            _srv.AccRunning = true;
            _srv.Bim360Running = true;
            _srv.ChainRunning = true;

            _sse.Broadcast("export-all-start", new
            {
                accTotal = accProjects.Count, bim360Total = bim360Projects.Count, platform = "all"
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    if (accProjects.Count > 0)
                    {
                        _srv.Acc.Reset();
                        _sse.Broadcast("export-all-phase", new { phase = "acc", platform = "acc" });
                        var accResult = await AccBatchService.Instance.RunBatch(accProjects,
                            new BatchOptions { UserEmail = _srv.ActiveUser, ScreenshotsDir = GetAccScreenshotsDir() });
                        if (accResult.Stopped) return;
                    }

                    if (bim360Projects.Count > 0)
                    {
                        _srv.Bim360.Reset();
                        var adminUrl = _db.GetAdminUrl(_srv.ActiveUser, "bim360");
                        var accountId = bim360Projects.FirstOrDefault(p => p.AccountId != null)?.AccountId
                            ?? ExtractAccountId(adminUrl ?? "");
                        _sse.Broadcast("export-all-phase", new { phase = "bim360", platform = "bim360" });
                        await Bim360BatchService.Instance.RunBatch(bim360Projects, new BatchOptions
                        {
                            UserEmail = _srv.ActiveUser,
                            AuthStatePath = GetSharedAuthPath(),
                            AccountId = accountId,
                            ScreenshotsDir = GetBim360ScreenshotsDir()
                        });
                    }
                }
                catch (Exception ex)
                {
                    _sse.Broadcast("export-error", new { error = ex.Message, platform = "all" });
                }
                finally
                {
                    _srv.ChainRunning = false;
                    _srv.AccRunning = false;
                    _srv.Bim360Running = false;
                    _sse.Broadcast("export-all-complete", new { platform = "all" });
                }
            });

            return Request.CreateResponse(HttpStatusCode.OK, new
            {
                status = "started",
                accTotal = accProjects.Count,
                bim360Total = bim360Projects.Count
            });
        }

        [HttpPost, Route("all/pause")]
        public IHttpActionResult PauseAll()
        {
            if (!_srv.ChainRunning) return BadRequest("No combined export running");
            if (_srv.AccRunning) AccBatchService.Instance.Pause();
            if (_srv.Bim360Running) Bim360BatchService.Instance.Pause();
            return Ok(new { status = "pausing" });
        }

        [HttpPost, Route("all/resume")]
        public IHttpActionResult ResumeAll()
        {
            if (!_srv.ChainRunning) return BadRequest("No combined export running");
            if (_srv.AccRunning) AccBatchService.Instance.Resume();
            if (_srv.Bim360Running) Bim360BatchService.Instance.Resume();
            return Ok(new { status = "resumed" });
        }

        //  Login (OAuth) ─────────────────────────────────────────────────────────
        // Returns the Autodesk OAuth authorization URL; frontend opens it in a popup.
        // Actual token exchange happens in OAuthController.Callback.
        [HttpPost, Route("~/api/login/start")]
        public async Task<IHttpActionResult> StartLogin()
        {
            _srv.LoginPending   = false;
            _srv.LoginDetected  = false;
            _srv.LoginStartTime = null;

            if (_srv.AccRunning || _srv.Bim360Running)
                return Content(HttpStatusCode.Conflict,
                    new { error = "An export is running -- stop it before logging in." });

            //  Open a real browser window (Chrome) ──────────────────────────────
            // Playwright opens Chrome at https://acc.autodesk.com.
            //  If already logged in there  -> cookies are grabbed automatically,
            //   the browser detects the dashboard and completes without user action.
            //  If not logged in            -> user completes Autodesk login/MFA,
            //   then the browser detects the dashboard and saves the session.
            // Either way, the auth-state.json (cookies) is saved for future use.
            _srv.LoginPending   = true;
            _srv.LoginStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sse.Broadcast("login-status", new { status = "browser-open", elapsed = 0 });

            // Pass the Cloudsfer session email as a fallback in case Autodesk
            // email detection fails (e.g., no Bearer token captured from API calls)
            var sessionEmail = Request.Properties.ContainsKey("SessionEmail")
                ? Request.Properties["SessionEmail"] as string : null;

            _ = Task.Run(() => AutodeskLoginService.Instance.PerformLoginAsync(sessionEmail));

            return Ok(new { status = "started" });

            //  (dead code -- kept for reference: 2-legged fallback) ──────────────
            // The block below is no longer reached but shows how to use 2-legged
            // if the browser approach is unavailable (e.g. headless server).
            #pragma warning disable CS0162
            try
            {
                _srv.LoginPending   = true;
                _srv.LoginStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _sse.Broadcast("login-status", new { status = "detecting-accounts", elapsed = 0 });

                var token = await OAuthService.Instance.GetClientCredentialsTokenAsync();
                token.UserEmail = _srv.ActiveUser ?? "app";
                _db.SaveAutodeskToken(token);

                var hubs = await OAuthService.Instance.GetHubsAsync(token.AccessToken);
                bool accFound = false, bimFound = false;
                foreach (var hub in hubs)
                {
                    var rawId   = System.Text.RegularExpressions.Regex.Replace(hub["id"]?.ToString() ?? "", @"^[a-zA-Z]\.", "");
                    var extType = (hub["attributes"]?["extension"]?["type"]?.ToString() ?? "").ToLower();
                    var hubName = hub["attributes"]?["name"]?.ToString() ?? rawId;
                    if (extType.Contains("bim360") && !bimFound)
                    {
                        var url = $"https://admin.b360.autodesk.com/admin/{rawId}/projects";
                        _db.SetAdminUrl(_srv.ActiveUser, "bim360", url);
                        _sse.Broadcast("account-detected", new { platform = "bim360", accountId = rawId, hubName, url });
                        bimFound = true;
                    }
                    else if (!extType.Contains("bim360") && !accFound)
                    {
                        var url = $"https://acc.autodesk.com/account-admin/projects/accounts/{rawId}/active";
                        _db.SetAdminUrl(_srv.ActiveUser, "acc", url);
                        _sse.Broadcast("account-detected", new { platform = "acc", accountId = rawId, hubName, url });
                        accFound = true;
                    }
                    if (accFound && bimFound) break;
                }

                _srv.LoginPending  = false;
                _srv.LoginDetected = true;
                _sse.Broadcast("login-status", new { status = "completed", elapsed = 0 });
                return Ok(new { status = "completed" });
            }
            catch { /* unreachable */ }
            return Ok(new { status = "started" });
            #pragma warning restore CS0162
        }

        //  Check if Autodesk session is still alive (called on page load) ──────
        [HttpGet, Route("~/api/auth/session/check")]
        public IHttpActionResult CheckSession()
        {
            var authStatePath = GetSharedAuthPath();
            if (OAuthService.Instance.HasValidBrowserSession(authStatePath))
            {
                _srv.LoginDetected = true;
                return Ok(new { valid = true, user = _srv.ActiveUser ?? "saved session", source = "cookie" });
            }
            return Ok(new { valid = false });
        }

        [HttpPost, Route("~/api/login/cancel")]
        public IHttpActionResult CancelLogin()
        {
            _srv.LoginPending   = false;
            _srv.LoginDetected  = false;
            _srv.LoginStartTime = null;
            _sse.Broadcast("login-status", new { status = "cancelled" });
            return Ok(new { status = "cancelled" });
        }

        private string GetSharedAuthPath()
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "storage", "users", _srv.ActiveUserSlug ?? "_unknown_");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "auth-state.json");
        }

        private string GetAccScreenshotsDir()
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "storage", "users", _srv.ActiveUserSlug ?? "_unknown_", "acc-screenshots");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private string GetBim360ScreenshotsDir()
        {
            var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "storage", "users", _srv.ActiveUserSlug ?? "_unknown_", "bim360-screenshots");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private static string? ExtractAccountId(string url)
        {
            var m = System.Text.RegularExpressions.Regex.Match(url,
                @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }

        public class ExportAllRequest { public bool Fresh { get; set; } }
    }
}
