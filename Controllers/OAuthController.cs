using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Http;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Controllers
{
    [RoutePrefix("api/auth/autodesk")]
    public class OAuthController : ApiController
    {
        private readonly OAuthService _oauth = OAuthService.Instance;
        private readonly DatabaseService _db = DatabaseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;
        private readonly SseService _sse = SseService.Instance;

        //  Step 1: Build authorization URL and return it to the SPA ─────────────
        // The frontend opens this URL in a popup or redirects to it.
        [HttpGet, Route("start")]
        public IHttpActionResult Start()
        {
            if (string.IsNullOrWhiteSpace(OAuthService.ClientId))
                return BadRequest("Autodesk.ClientId is not configured in Web.config. " +
                    "Register an app at https://developer.autodesk.com and add the Client ID.");

            var state = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                .Replace("+", "-").Replace("/", "_").TrimEnd('=');

            // Store state in DB (keyed by value) so callback can verify it
            _db.SetSetting(null, "oauth", $"state:{state}", DateTime.UtcNow.ToString("O"));

            var url = _oauth.BuildAuthorizationUrl(state);

            _srv.LoginPending = true;
            _srv.LoginDetected = false;
            _srv.LoginStartTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _sse.Broadcast("login-status", new { status = "browser-open", elapsed = 0 });

            return Ok(new { authUrl = url, state });
        }

        //  Step 2: Autodesk redirects here after user authorizes ─────────────────
        [HttpGet, Route("callback")]
        public async Task<HttpResponseMessage> Callback(string? code = null, string? state = null, string? error = null)
        {
            if (!string.IsNullOrEmpty(error))
            {
                _srv.LoginPending = false;
                _sse.Broadcast("login-status", new { status = "failed", error = "Autodesk denied access: " + error });
                return HtmlResponse("Authentication failed",
                    $"<p>Autodesk returned an error: <strong>{error}</strong></p><p>You can close this window.</p>", false);
            }

            if (string.IsNullOrEmpty(code))
                return HtmlResponse("Missing code", "<p>No authorization code received.</p>", false);

            // Verify state to prevent CSRF
            if (!string.IsNullOrEmpty(state))
            {
                var saved = _db.GetSetting(null, "oauth", $"state:{state}");
                if (saved == null)
                    return HtmlResponse("Invalid state", "<p>State parameter mismatch.</p>", false);
            }

            try
            {
                _sse.Broadcast("login-status", new { status = "exchanging-token",
                    elapsed = _srv.LoginElapsedSeconds });

                // Exchange auth code for tokens
                var token = await _oauth.ExchangeCodeAsync(code);

                // Get user email from userinfo endpoint
                var email = await _oauth.GetUserEmailAsync(token.AccessToken);
                if (string.IsNullOrEmpty(email) && _srv.ActiveUser != null)
                    email = _srv.ActiveUser;

                if (string.IsNullOrEmpty(email))
                    return HtmlResponse("Authentication failed",
                        "<p>Could not determine your Autodesk account email.</p>", false);

                token.UserEmail = email;
                _db.SaveAutodeskToken(token);

                // Activate user in server state
                _srv.ActiveUser = email;
                _srv.ActiveUserSlug = SlugHelper.EmailToSlug(email);
                _db.SaveLastUser(email);

                // Auto-detect account admin URLs from Autodesk hubs API
                _sse.Broadcast("login-status", new { status = "detecting-accounts",
                    elapsed = _srv.LoginElapsedSeconds });
                await AutodetectAccountsAsync(token.AccessToken, email);

                _srv.LoginPending = false;
                _srv.LoginDetected = true;
                _sse.Broadcast("login-status", new { status = "completed",
                    elapsed = _srv.LoginElapsedSeconds, user = email });
                _sse.Broadcast("user-changed", new { user = email });

                return HtmlResponse("Authentication successful",
                    $"<p>Signed in as <strong>{email}</strong>.</p>" +
                    "<p>You can close this window -- the dashboard has been updated.</p>", true);
            }
            catch (Exception ex)
            {
                _srv.LoginPending = false;
                _sse.Broadcast("login-status", new { status = "failed", error = ex.Message });
                return HtmlResponse("Authentication failed",
                    $"<p>Error: {ex.Message}</p>", false);
            }
        }

        //  Auto-detect Autodesk account admin URLs ───────────────────────────────
        private async Task AutodetectAccountsAsync(string accessToken, string userEmail)
        {
            try
            {
                var hubs = await _oauth.GetHubsAsync(accessToken);
                bool accFound = false, bimFound = false;

                foreach (var hub in hubs)
                {
                    var rawId     = hub["id"]?.ToString() ?? "";
                    var accountId = Regex.Replace(rawId, @"^[a-zA-Z]\.", "");
                    var extType   = (hub["attributes"]?["extension"]?["type"]?.ToString() ?? "").ToLower();
                    var hubName   = hub["attributes"]?["name"]?.ToString() ?? accountId;

                    if (extType.Contains("bim360") && !bimFound)
                    {
                        var url = $"https://admin.b360.autodesk.com/admin/{accountId}/projects";
                        _db.SetAdminUrl(userEmail, "bim360", url);
                        _sse.Broadcast("account-detected", new { platform = "bim360", accountId, hubName, url });
                        bimFound = true;
                    }
                    else if (!extType.Contains("bim360") && !accFound)
                    {
                        var url = $"https://acc.autodesk.com/account-admin/projects/accounts/{accountId}/active";
                        _db.SetAdminUrl(userEmail, "acc", url);
                        _sse.Broadcast("account-detected", new { platform = "acc", accountId, hubName, url });
                        accFound = true;
                    }
                    if (accFound && bimFound) break;
                }

                _sse.Broadcast("accounts-detected", new
                {
                    acc    = _db.GetAdminUrl(userEmail, "acc"),
                    bim360 = _db.GetAdminUrl(userEmail, "bim360"),
                    count  = hubs.Count
                });
            }
            catch (Exception ex)
            {
                _sse.Broadcast("account-detection-failed", new { error = ex.Message });
            }
        }

        //  Helper: return a styled HTML page for the callback tab ────────────────
        private HttpResponseMessage HtmlResponse(string title, string body, bool success)
        {
            var color = success ? "#22c55e" : "#ef4444";
            var html = $@"<!DOCTYPE html><html><head><title>{title}</title>
<style>body{{font-family:sans-serif;display:flex;align-items:center;justify-content:center;
min-height:100vh;margin:0;background:#f1f5f9}}
.card{{background:#fff;border-radius:12px;padding:40px;text-align:center;max-width:400px;
box-shadow:0 4px 20px rgba(0,0,0,.1)}}
h2{{color:{color};margin-top:0}}</style></head>
<body><div class='card'><h2>{title}</h2>{body}
<script>setTimeout(()=>window.close(),3000)</script>
</div></body></html>";

            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html");
            return response;
        }
    }
}
