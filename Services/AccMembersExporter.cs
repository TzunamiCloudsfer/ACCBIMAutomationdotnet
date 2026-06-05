using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AutodeskAutomation.Models.Documents;
using Newtonsoft.Json.Linq;

namespace AutodeskAutomation.Services
{
    /// <summary>
    /// Exports ACC project member lists (emails, roles) via the ACC Admin API.
    /// Corresponds to the Export button on:
    ///   https://acc.autodesk.com/project-admin/members/projects/{projectId}
    /// </summary>
    public class AccMembersExporter
    {
        private static readonly HttpClient _http = new HttpClient();

        public async Task<MembersExportResult> ExportMembersAsync(
            string projectId, string accountId, string? userEmail)
        {
            // Get a valid access token
            var token = DatabaseService.Instance.GetAutodeskToken(userEmail);
            if (token == null || token.IsExpired)
            {
                if (token?.RefreshToken != null)
                {
                    try
                    {
                        token = await OAuthService.Instance.RefreshAsync(token.RefreshToken);
                        token.UserEmail = userEmail ?? "app";
                        DatabaseService.Instance.SaveAutodeskToken(token);
                    }
                    catch { return MembersExportResult.Fail("Token refresh failed"); }
                }
                else
                    return MembersExportResult.Fail("No valid token -- please log in first");
            }

            var bearer = token.AccessToken;
            var members = new List<MemberInfo>();

            //  Fetch all project users via ACC Admin API ─────────────────────────
            // Endpoint: GET /construction/admin/v1/projects/{projectId}/users
            // Returns: id, email, name, role, status, accessLevel, etc.
            int offset = 0;
            const int limit = 200;
            bool hasMore = true;

            while (hasMore)
            {
                var url = $"https://developer.api.autodesk.com/construction/admin/v1/projects/{projectId}/users" +
                          $"?limit={limit}&offset={offset}";

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[members] GET users {resp.StatusCode}: {body.Substring(0, Math.Min(300, body.Length))}");
                    // Try legacy HQ API
                    return await ExportMembersLegacyAsync(projectId, accountId, bearer);
                }

                var json = JObject.Parse(body);
                var results = json["results"] as JArray ?? new JArray();

                foreach (var u in results)
                {
                    members.Add(new MemberInfo
                    {
                        UserId = u["autodeskId"]?.ToString() ?? u["userId"]?.ToString(),
                        Email  = u["email"]?.ToString(),
                        Name   = u["name"]?.ToString()
                               ?? $"{u["firstName"]} {u["lastName"]}".Trim(),
                        Role   = u["roles"]?.First?["name"]?.ToString()
                               ?? u["role"]?.ToString(),
                        Status = u["status"]?.ToString(),
                    });
                }

                var pagination = json["pagination"];
                var total      = pagination?["totalResults"]?.Value<int>() ?? results.Count;
                offset += results.Count;
                hasMore = offset < total && results.Count == limit;
            }

            if (members.Count == 0)
                return MembersExportResult.Fail("No members returned from API");

            //  Save to RavenDB ───────────────────────────────────────────────────
            var doc = new ProjectMemberDocument
            {
                Id          = $"projectmembers/{Helpers.SlugHelper.EmailToSlug(userEmail ?? "app")}/{projectId}",
                UserEmail   = userEmail ?? "app",
                ProjectId   = projectId,
                AccountId   = accountId,
                Members     = members,
                ExportedAt  = DateTime.UtcNow
            };

            var db = DatabaseService.Instance;
            using var session = GetSession(db);
            session.Store(doc);
            session.SaveChanges();

            Console.WriteLine($"[members] Saved {members.Count} members for project {projectId}");
            SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                message = $"Saved {members.Count} members -- emails: {string.Join(", ", GetSampleEmails(members))}",
                platform = "acc"
            });

            return new MembersExportResult
            {
                Success     = true,
                MemberCount = members.Count,
                Members     = members,
                Message     = $"Fetched and saved {members.Count} project members"
            };
        }

        //  Fallback: HQ v1 API (older BIM360/ACC accounts) ──────────────────────
        private async Task<MembersExportResult> ExportMembersLegacyAsync(
            string projectId, string accountId, string bearer)
        {
            var members = new List<MemberInfo>();
            int offset = 0;
            const int limit = 100;
            bool hasMore = true;

            while (hasMore)
            {
                var url = $"https://developer.api.autodesk.com/hq/v1/accounts/{accountId}/projects/{projectId}/users" +
                          $"?limit={limit}&offset={offset}";

                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return MembersExportResult.Fail($"HQ API {resp.StatusCode}");

                JArray? items;
                try { items = JArray.Parse(body); } catch { break; }
                if (items == null || items.Count == 0) break;

                foreach (var u in items)
                {
                    members.Add(new MemberInfo
                    {
                        UserId = u["uid"]?.ToString(),
                        Email  = u["email"]?.ToString(),
                        Name   = u["name"]?.ToString(),
                        Role   = u["role"]?.ToString(),
                        Status = u["status"]?.ToString(),
                    });
                }

                if (items.Count < limit) break;
                offset += limit;
                hasMore = true;
            }

            if (members.Count == 0)
                return MembersExportResult.Fail("No members found via legacy API");

            SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                message = $"[Legacy API] {members.Count} members found",
                platform = "acc"
            });

            return new MembersExportResult { Success = true, MemberCount = members.Count, Members = members };
        }

        private static Raven.Client.Documents.Session.IDocumentSession GetSession(DatabaseService db)
        {
            // Access the RavenDB store via reflection (since it's private in DatabaseService)
            // -- or use a public method we'll add next
            return db.OpenSession();
        }

        private static IEnumerable<string> GetSampleEmails(List<MemberInfo> members)
        {
            var emails = new List<string>();
            foreach (var m in members)
            {
                if (!string.IsNullOrEmpty(m.Email)) emails.Add(m.Email);
                if (emails.Count >= 3) break;
            }
            if (members.Count > 3) emails.Add($"...+{members.Count - 3} more");
            return emails;
        }
    }

    public class MembersExportResult
    {
        public bool Success { get; set; }
        public int MemberCount { get; set; }
        public List<MemberInfo>? Members { get; set; }
        public string? Message { get; set; }

        public static MembersExportResult Fail(string msg) =>
            new MembersExportResult { Success = false, Message = msg };
    }
}
