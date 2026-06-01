using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutodeskAutomation.Models;
using AutodeskAutomation.Models.Documents;
using Newtonsoft.Json.Linq;

namespace AutodeskAutomation.Services
{
    // Fetches project lists from Autodesk Admin APIs using the stored OAuth access token.
    public class AutodeskApiService
    {
        private static readonly AutodeskApiService _instance = new AutodeskApiService();
        public static AutodeskApiService Instance => _instance;

        private static readonly HttpClient _http = new HttpClient();
        private AutodeskApiService() { }

        public async Task<List<ProjectDocument>> FetchAccProjects(string? adminUrl, string? authStatePath)
        {
            if (string.IsNullOrEmpty(adminUrl)) return new List<ProjectDocument>();
            var accountId = ExtractAccountId(adminUrl);
            if (string.IsNullOrEmpty(accountId)) return new List<ProjectDocument>();
            var token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token)) return new List<ProjectDocument>();
            return await PaginateAccProjects(token, accountId);
        }

        public async Task<List<ProjectDocument>> FetchBim360Projects(string adminUrl, string? authStatePath)
        {
            var accountId = ExtractAccountId(adminUrl);
            if (string.IsNullOrEmpty(accountId)) return new List<ProjectDocument>();
            var token = await GetTokenAsync();
            if (string.IsNullOrEmpty(token)) return new List<ProjectDocument>();
            return await PaginateBim360Projects(token, accountId);
        }

        private static async Task<string?> GetTokenAsync()
        {
            try
            {
                // Try 3-legged token first (user-specific)
                var token = DatabaseService.Instance.GetAutodeskToken(ServerState.Instance.ActiveUser);
                if (token != null && !token.IsExpired)
                    return token.AccessToken;
                if (token?.RefreshToken != null)
                    return await OAuthService.Instance.GetValidAccessTokenAsync(ServerState.Instance.ActiveUser);

                // Fall back to 2-legged client credentials (no user login needed)
                var appToken = await OAuthService.Instance.GetClientCredentialsTokenAsync();
                return appToken.AccessToken;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<List<ProjectDocument>> PaginateAccProjects(string authHeader, string accountId)
        {
            var results = new List<ProjectDocument>();
            string? nextHref = $"https://developer.api.autodesk.com/construction/admin/v1/accounts/{accountId}/projects?limit=100";

            while (!string.IsNullOrEmpty(nextHref))
            {
                var req = new HttpRequestMessage(HttpMethod.Get, nextHref);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeader);
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) break;

                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                var items = json["results"] as JArray ?? json["data"] as JArray ?? new JArray();

                foreach (var item in items)
                {
                    var id = (item["id"] ?? item["project_id"])?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;
                    id = Regex.Replace(id, @"^[a-zA-Z]\.", "");

                    var platform = (item["platform"] ?? item["type"])?.ToString()?.ToLower();
                    platform = (platform != null && (platform.Contains("bim") || platform.Contains("360")))
                        ? "bim360" : null;

                    results.Add(new ProjectDocument
                    {
                        ProjectId   = id,
                        Name        = item["name"]?.ToString() ?? "",
                        AccountId   = accountId,
                        Status      = item["status"]?.ToString() ?? "active",
                        RawPlatform = platform
                    });
                }

                nextHref = json["links"]?["next"]?["href"]?.ToString();
                if (string.IsNullOrEmpty(nextHref))
                {
                    var offset = json["pagination"]?["offset"]?.Value<int>() ?? 0;
                    var limit  = json["pagination"]?["limit"]?.Value<int>()  ?? 100;
                    var total  = json["pagination"]?["total"]?.Value<int>()  ?? 0;
                    if (offset + limit < total)
                        nextHref = $"https://developer.api.autodesk.com/construction/admin/v1/accounts/{accountId}/projects?limit={limit}&offset={offset + limit}";
                }
            }
            return results;
        }

        private static async Task<List<ProjectDocument>> PaginateBim360Projects(string authHeader, string accountId)
        {
            var results = new List<ProjectDocument>();
            int offset = 0; const int limit = 100;

            while (true)
            {
                var url = $"https://developer.api.autodesk.com/hq/v1/accounts/{accountId}/projects?limit={limit}&offset={offset}";
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authHeader);
                var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) break;

                var body = await resp.Content.ReadAsStringAsync();
                JArray? items;
                try { items = JArray.Parse(body); } catch { break; }
                if (items == null || items.Count == 0) break;

                foreach (var item in items)
                {
                    var id = item["id"]?.ToString();
                    if (string.IsNullOrEmpty(id)) continue;
                    id = Regex.Replace(id, @"^[a-zA-Z]\.", "");
                    results.Add(new ProjectDocument
                    {
                        ProjectId = id,
                        Name      = item["name"]?.ToString() ?? "",
                        AccountId = accountId,
                        Status    = item["status"]?.ToString() ?? "active"
                    });
                }
                if (items.Count < limit) break;
                offset += limit;
            }
            return results;
        }

        public static string? ExtractAccountId(string url)
        {
            var m = Regex.Match(url,
                @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Value : null;
        }
    }
}
