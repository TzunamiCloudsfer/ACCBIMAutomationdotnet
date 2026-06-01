using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AutodeskAutomation.Models.Documents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutodeskAutomation.Services
{
    /// <summary>
    /// Triggers BIM360 Document Log exports via REST API (no browser needed).
    /// Uses the OAuth Bearer token captured during login.
    /// Falls back gracefully so the caller can switch to browser automation.
    /// </summary>
    public class Bim360ApiExporter
    {
        private static readonly HttpClient _http = new HttpClient();

        // ── Public entry point ────────────────────────────────────────────────────
        public async Task<ApiExportResult> ExportDocumentLogAsync(
            string accountId, string projectId, string? userEmail)
        {
            var token = DatabaseService.Instance.GetAutodeskToken(userEmail);

            // Filter out expired 2-legged app tokens and try to get a fresh one
            if (token != null && token.UserEmail == "app" && token.IsExpired)
                token = null;

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
                    catch { /* fall through to client credentials */ }
                }

                if (token == null || token.IsExpired)
                {
                    // Fall back to client credentials
                    try
                    {
                        token = await OAuthService.Instance.GetClientCredentialsTokenAsync();
                        token.UserEmail = "app";
                        DatabaseService.Instance.SaveAutodeskToken(token);
                        Console.WriteLine("[api-export] Using 2-legged client credentials token");
                    }
                    catch (Exception ex)
                    {
                        return ApiExportResult.NotAvailable($"Could not get token: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"[api-export] Using token for user={token.UserEmail}, expires={token.ExpiresAt:HH:mm}");
            SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                message = $"[API] Using token (user={token.UserEmail}, expires {token.ExpiresAt:HH:mm})",
                platform = "bim360"
            });

            var bearer = token.AccessToken;

            // ── Step 1: Verify project exists via Data Management API ─────────────
            var prefixedProjectId = projectId.StartsWith("b.") ? projectId : $"b.{projectId}";
            var foldersUrl = $"https://developer.api.autodesk.com/data/v1/projects/{prefixedProjectId}/folders/root/contents";

            JObject? rootContents = null;
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, foldersUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
                var resp = await _http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    rootContents = JObject.Parse(body);
                }
                else
                {
                    var preview = body.Length > 300 ? body.Substring(0, 300) : body;
                    Console.WriteLine($"[api-export] Folder list {resp.StatusCode}: {preview}");
                    SseService.Instance.Broadcast("log", new
                    {
                        level = "WARN",
                        message = $"[API] Folder list {resp.StatusCode} for project {projectId}: {preview}",
                        platform = "bim360"
                    });
                    return ApiExportResult.NotAvailable($"Folder API {resp.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                return ApiExportResult.NotAvailable($"Folder API error: {ex.Message}");
            }

            // ── Step 2: Find Plans and Project Files folder IDs ───────────────────
            var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var data = rootContents?["data"] as JArray ?? new JArray();
            foreach (var item in data)
            {
                var type = item["type"]?.ToString();
                var name = item["attributes"]?["name"]?.ToString() ?? "";
                var id   = item["id"]?.ToString() ?? "";
                if (type == "folders" && !string.IsNullOrEmpty(id))
                    folders[name] = id;
            }

            Console.WriteLine($"[api-export] Root folders: {string.Join(", ", folders.Keys)}");
            SseService.Instance.Broadcast("log", new
            {
                level = "INFO",
                message = $"[API] Found {folders.Count} root folders: {string.Join(", ", folders.Keys)}",
                platform = "bim360"
            });

            // ── Step 3: Trigger document log export for each root ─────────────────
            int queued = 0;
            foreach (var kvp in folders)
            {
                var folderName = kvp.Key;
                var folderId   = kvp.Value;

                // Only export Plans and Project Files roots
                if (!folderName.Equals("Plans", StringComparison.OrdinalIgnoreCase) &&
                    !folderName.Equals("Project Files", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var exported = await TriggerFolderExportAsync(
                        bearer, prefixedProjectId, accountId, folderId, folderName);
                    if (exported)
                    {
                        queued++;
                        Console.WriteLine($"[api-export] ✓ Queued export for '{folderName}'");
                        SseService.Instance.Broadcast("log", new
                        {
                            level = "INFO",
                            message = $"[API] Document log export queued for '{folderName}'",
                            platform = "bim360"
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[api-export] '{folderName}' export failed: {ex.Message}");
                }
            }

            if (queued == 0 && folders.Count > 0)
                return ApiExportResult.NotAvailable(
                    "No export API available — will use browser automation");

            return new ApiExportResult
            {
                Success      = queued > 0,
                EmailsQueued = queued,
                Message      = $"Queued {queued} export(s) via API"
            };
        }

        // ── Try various known export endpoints ────────────────────────────────────
        private async Task<bool> TriggerFolderExportAsync(
            string bearer, string projectId, string accountId, string folderId, string folderName)
        {
            // Attempt 1: BIM360 Docs document-log export endpoint
            var endpoints = new[]
            {
                // BIM360 Docs API
                ($"https://developer.api.autodesk.com/bim360/docs/v1/projects/{projectId}/folders/{folderId}/document-log/export",
                 HttpMethod.Post, "{}"),

                // Alternate Docs API path
                ($"https://developer.api.autodesk.com/bim360/docs/v1/projects/{projectId}/export",
                 HttpMethod.Post,
                 JsonConvert.SerializeObject(new { folderId, type = "document_log", includeSubFolders = true })),

                // HQ v1 API
                ($"https://developer.api.autodesk.com/hq/v1/accounts/{accountId}/projects/{projectId}/rfis/export",
                 HttpMethod.Post,
                 JsonConvert.SerializeObject(new { folderId })),
            };

            foreach (var (url, method, body) in endpoints)
            {
                try
                {
                    var req = new HttpRequestMessage(method, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    if (body != "{}" || method == HttpMethod.Post)
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                    var resp = await _http.SendAsync(req);
                    var respBody = await resp.Content.ReadAsStringAsync();
                    Console.WriteLine($"[api-export] {url} → {resp.StatusCode}: {respBody.Substring(0, Math.Min(200, respBody.Length))}");

                    if (resp.IsSuccessStatusCode ||
                        (int)resp.StatusCode == 202 ||  // 202 Accepted = queued
                        (int)resp.StatusCode == 201)     // 201 Created = export created
                        return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[api-export] {url} error: {ex.Message}");
                }
            }

            return false;
        }
    }

    public class ApiExportResult
    {
        public bool Success { get; set; }
        public int EmailsQueued { get; set; }
        public string? Message { get; set; }
        public bool IsApiUnavailable { get; set; }

        public static ApiExportResult NotAvailable(string reason) => new ApiExportResult
        {
            Success = false,
            IsApiUnavailable = true,
            Message = reason
        };
    }
}
