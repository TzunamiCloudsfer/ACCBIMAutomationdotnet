using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web;
using AutodeskAutomation.Models.Documents;
using Newtonsoft.Json.Linq;

namespace AutodeskAutomation.Services
{
    public class OAuthService
    {
        private static readonly OAuthService _instance = new OAuthService();
        public static OAuthService Instance => _instance;

        private static readonly HttpClient _http = new HttpClient();

        // V2 endpoints (APS -- Autodesk Platform Services)
        private const string AuthEndpoint  = "https://developer.api.autodesk.com/authentication/v2/authorize";
        private const string TokenEndpoint = "https://developer.api.autodesk.com/authentication/v2/token";
        private const string UserInfoUrl   = "https://api.userprofile.autodesk.com/userinfo";
        private const string HubsUrl       = "https://developer.api.autodesk.com/project/v1/hubs";

        // Scopes: data:read for project files, account:read for admin, user-profile:read for email
        public const string Scopes = "data:read account:read user-profile:read";

        private OAuthService() { }

        //  Config helpers ────────────────────────────────────────────────────────
        public static string ClientId
            => System.Configuration.ConfigurationManager.AppSettings["Autodesk.ClientId"] ?? "";
        public static string ClientSecret
            => System.Configuration.ConfigurationManager.AppSettings["Autodesk.ClientSecret"] ?? "";
        public static string RedirectUri
            => System.Configuration.ConfigurationManager.AppSettings["Autodesk.RedirectUri"]
            ?? "http://localhost:54147/api/auth/autodesk/callback";

        //  Build the authorization URL ───────────────────────────────────────────
        public string BuildAuthorizationUrl(string state)
        {
            return $"{AuthEndpoint}" +
                   $"?response_type=code" +
                   $"&client_id={Uri.EscapeDataString(ClientId)}" +
                   $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                   $"&scope={Uri.EscapeDataString(Scopes)}" +
                   $"&state={Uri.EscapeDataString(state)}";
        }

        //  Exchange authorization code for tokens ────────────────────────────────
        public async Task<AutodeskTokenDocument> ExchangeCodeAsync(string code)
        {
            // V1 token endpoint uses Basic auth for client credentials
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}"));

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type",   "authorization_code"),
                new KeyValuePair<string, string>("code",         code),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            });

            var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = form };
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

            var resp = await _http.SendAsync(request);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Token exchange failed ({resp.StatusCode}): {body}");

            return ParseTokenResponse(body);
        }

        //  Refresh an expired access token ───────────────────────────────────────
        public async Task<AutodeskTokenDocument> RefreshAsync(string refreshToken)
        {
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}"));

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type",    "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
            });

            var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = form };
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Token refresh failed ({resp.StatusCode}): {body}");

            return ParseTokenResponse(body);
        }

        //  Get a valid token for a user (auto-refresh if expired) ────────────────
        public async Task<string> GetValidAccessTokenAsync(string? userEmail)
        {
            var doc = DatabaseService.Instance.GetAutodeskToken(userEmail);
            if (doc == null) throw new Exception("No Autodesk token. Please log in first.");

            if (doc.IsExpired && !string.IsNullOrEmpty(doc.RefreshToken))
            {
                var refreshed = await RefreshAsync(doc.RefreshToken);
                refreshed.UserEmail = doc.UserEmail;
                DatabaseService.Instance.SaveAutodeskToken(refreshed);
                return refreshed.AccessToken;
            }

            return doc.AccessToken;
        }

        //  Get user email from Autodesk userinfo endpoint ────────────────────────
        public async Task<string?> GetUserEmailAsync(string accessToken)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, UserInfoUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["email"]?.ToString()?.ToLowerInvariant().Trim();
        }

        //  Get hubs using an access token ────────────────────────────────────────
        public async Task<JArray> GetHubsAsync(string accessToken)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, HubsUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new JArray();
            var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
            return json["data"] as JArray ?? new JArray();
        }

        //  Check if a saved Playwright browser session (auth-state.json) exists 
        // Only returns true if the file exists AND is  30 days old.
        // Does NOT try client credentials -- this is purely a "was the user already
        // logged in via the browser?" check, so Chrome doesn't need to open again.
        public bool HasValidBrowserSession(string authStatePath)
        {
            if (!System.IO.File.Exists(authStatePath)) return false;
            var age = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(authStatePath);
            return age.TotalDays <= 30;
        }

        //  Get valid OAuth token (for REST API calls, not browser automation) 
        public async Task<AutodeskTokenDocument?> GetExistingSessionAsync(string? userEmail, string authStatePath)
        {
            // 1. Check stored RavenDB OAuth token (3-legged, from a real user login)
            var token = DatabaseService.Instance.GetAutodeskToken(userEmail);
            if (token != null && token.UserEmail != "app")  // ignore 2-legged app tokens
            {
                if (!token.IsExpired) return token;

                if (!string.IsNullOrEmpty(token.RefreshToken))
                {
                    try
                    {
                        var refreshed = await RefreshAsync(token.RefreshToken);
                        refreshed.UserEmail = token.UserEmail;
                        DatabaseService.Instance.SaveAutodeskToken(refreshed);
                        return refreshed;
                    }
                    catch { /* refresh failed */ }
                }
            }

            return null;  // no valid session -- caller must open the browser
        }

        //  2-legged Client Credentials (no redirect URL needed) ─────────────────
        // Use this when 3-legged OAuth callback URL is not yet registered.
        // Gives app-level access to project APIs without user login.
        public async Task<AutodeskTokenDocument> GetClientCredentialsTokenAsync()
        {
            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}"));

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope",      "data:read account:read"),
            });

            var req = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = form };
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);

            var resp = await _http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Client credentials token failed ({resp.StatusCode}): {body}");

            return ParseTokenResponse(body);
        }

        //  Helpers ───────────────────────────────────────────────────────────────
        private static AutodeskTokenDocument ParseTokenResponse(string json)
        {
            var obj = JObject.Parse(json);
            var expiresIn = obj["expires_in"]?.Value<int>() ?? 3600;
            return new AutodeskTokenDocument
            {
                AccessToken  = obj["access_token"]?.ToString()  ?? "",
                RefreshToken = obj["refresh_token"]?.ToString(),
                ExpiresAt    = DateTime.UtcNow.AddSeconds(expiresIn),
                Scope        = obj["scope"]?.ToString(),
                SavedAt      = DateTime.UtcNow,
            };
        }
    }
}
