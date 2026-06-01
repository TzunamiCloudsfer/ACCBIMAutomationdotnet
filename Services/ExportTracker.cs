using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using AutodeskAutomation.Models.Documents;
using Microsoft.Playwright;
using Newtonsoft.Json.Linq;

namespace AutodeskAutomation.Services
{
    /// <summary>
    /// C# port of exportTracker.js.
    /// After triggering a BIM360/ACC export, polls GET /reports/v2/projects/{id}/reports
    /// to capture the new Autodesk-generated report rows (set-difference vs pre-existing).
    /// Saves them to RavenDB. Background sweeper checks pending reports every 60s.
    /// </summary>
    public class ExportRun
    {
        private readonly string _runId;
        private readonly IPage? _page;
        private readonly string _projectId;
        private readonly string? _productId;
        private readonly string? _userEmail;
        private readonly string? _platform;
        private readonly string? _accountId;
        private readonly string? _batchRunId;
        private readonly int? _expectedCount;
        private readonly HashSet<string> _snapshot;
        private readonly DateTime _startedAt;
        private string? _capturedBearer;

        private static readonly HttpClient _http = new HttpClient();
        private static readonly HashSet<string> _terminal = new HashSet<string>
            { "complete", "error", "empty" };

        private ExportRun(string runId, IPage? page, string projectId,
            string? productId, string? userEmail, string? platform,
            string? accountId, string? batchRunId, int? expectedCount,
            HashSet<string> snapshot, DateTime startedAt)
        {
            _runId         = runId;
            _page          = page;
            _projectId     = projectId;
            _productId     = productId ?? "docs";
            _userEmail     = userEmail;
            _platform      = platform;
            _accountId     = accountId;
            _batchRunId    = batchRunId;
            _expectedCount = expectedCount;
            _snapshot      = snapshot;
            _startedAt     = startedAt;
        }

        // â”€â”€ begin: snapshot existing report IDs before triggering export â”€â”€â”€â”€â”€â”€â”€â”€â”€
        public static async Task<ExportRun> Begin(IPage page, ExportRunMeta meta)
        {
            var runId     = Guid.NewGuid().ToString("N");
            var productId = meta.ProductId ?? "docs";
            var startedAt = DateTime.UtcNow;

            // Attach bearer listener
            string? capturedBearer = null;
            page.Request += (_, req) =>
            {
                if (capturedBearer != null) return;
                if (!req.Url.Contains("autodesk.com")) return;
                if (req.Headers.TryGetValue("authorization", out var auth) &&
                    auth.StartsWith("Bearer "))
                    capturedBearer = auth;
            };

            // Snapshot existing reports
            var preExisting = new List<string>();
            try
            {
                var rows = await ListReports(page, meta.ProjectId, productId, capturedBearer);
                preExisting = rows.Select(r => r["id"]?.ToString()).Where(s => s != null).ToList()!;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[tracker] snapshot failed: {ex.Message}");
            }

            // Save run row to RavenDB
            var db = DatabaseService.Instance;
            using var session = db.OpenSession();
            var runDoc = new ReportRunDocument
            {
                Id             = $"reportruns/{runId}",
                RunId          = runId,
                UserEmail      = meta.UserEmail,
                Platform       = meta.Platform,
                BatchRunId     = meta.BatchRunId,
                AccountId      = meta.AccountId,
                ProjectId      = meta.ProjectId,
                ProjectName    = meta.ProjectName,
                ProductId      = productId,
                StartedAt      = startedAt,
                Status         = "running",
                PreExistingIds = preExisting,
                ExpectedCount  = meta.ExpectedCount,
            };
            session.Store(runDoc);
            session.SaveChanges();

            return new ExportRun(runId, page, meta.ProjectId, productId,
                meta.UserEmail, meta.Platform, meta.AccountId, meta.BatchRunId,
                meta.ExpectedCount, new HashSet<string>(preExisting), startedAt)
            {
                _capturedBearer = capturedBearer
            };
        }

        private string? _capturedBearerField;
        private string? _capturedBearerProp
        {
            get => _capturedBearerField;
            set => _capturedBearerField = value;
        }

        // â”€â”€ sweep: fetch reports, save new ones (set-difference) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        public async Task<List<JObject>> Sweep()
        {
            // Refresh bearer from page if available
            string? bearer = _capturedBearer;
            if (bearer == null)
            {
                var stored = DatabaseService.Instance.GetAutodeskToken(_userEmail);
                if (stored != null && !stored.IsExpired)
                    bearer = $"Bearer {stored.AccessToken}";
            }

            var rows = await ListReports(_page, _projectId, _productId ?? "docs", bearer);
            var slack = _startedAt.AddSeconds(-5);
            var db = DatabaseService.Instance;

            var saved = new List<JObject>();
            foreach (var raw in rows)
            {
                var id = raw["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                if (_snapshot.Contains(id)) continue;

                DateTime.TryParse(raw["createdAt"]?.ToString(), out var createdAt);
                if (createdAt < slack) continue;

                var status = raw["status"]?.ToString() ?? "pending";
                var isTerminal = _terminal.Contains(status);
                var now = DateTime.UtcNow;

                using var session = db.OpenSession();
                var existing = session.Load<ReportDocument>($"reports/{id}");
                if (existing == null)
                {
                    existing = new ReportDocument { Id = $"reports/{id}", ReportId = id };
                    session.Store(existing);
                }

                existing.RunId          = _runId;
                existing.UserEmail      = _userEmail;
                existing.Platform       = _platform;
                existing.ProjectId      = _projectId;
                existing.AccountId      = _accountId;
                existing.ProductId      = _productId;
                existing.Title          = raw["title"]?.ToString();
                existing.Type           = raw["type"]?.ToString();
                existing.Service        = raw["service"]?.ToString();
                existing.Format         = raw["format"]?.ToString();
                existing.Status         = status;
                existing.ErrorMessage   = raw["errorMessage"]?.ToString();
                existing.DownloadUrl    = raw["url"]?.ToString();
                existing.CreatedBy      = raw["createdBy"]?.ToString();
                existing.CreatorName    = raw["creatorName"]?.ToString();
                existing.AutodeskCreatedAt = createdAt == default ? null : createdAt;
                existing.LastSeenAt     = now;
                if (isTerminal) existing.CompletedAt = now;
                session.SaveChanges();
                saved.Add(raw);
            }

            Console.WriteLine($"[tracker] sweep: {saved.Count} new reports for project {_projectId}");
            if (saved.Count > 0)
                AutodeskAutomation.Services.SseService.Instance.Broadcast("summary-report-update",
                    new { platform = _platform, projectId = _projectId, count = saved.Count });
            return saved;
        }

        // â”€â”€ finalize: poll until expected reports appear or timeout â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        public async Task<List<ReportDocument>> FinalizeAsync(
            int pollIntervalMs = 7000, int maxWaitMs = 60_000)
        {
            var expected = _expectedCount ?? 0;
            var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);

            while (true)
            {
                try { await Sweep(); } catch (Exception ex)
                {
                    Console.WriteLine($"[tracker] sweep error: {ex.Message}");
                }

                var all = GetReportsForRun();
                var terminalCount = all.Count(r => _terminal.Contains(r.Status ?? ""));
                bool enoughTerm = expected > 0 ? terminalCount >= expected : terminalCount > 0;
                bool enoughSeen = expected > 0 ? all.Count >= expected : all.Count > 0;

                if (enoughTerm)
                {
                    UpdateRunStatus("completed");
                    return all;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    UpdateRunStatus(enoughSeen ? "running" : "abandoned");
                    return all;
                }
                await Task.Delay(pollIntervalMs);
            }
        }

        public void Fail(string reason)
        {
            try { Sweep().GetAwaiter().GetResult(); } catch { }
            UpdateRunStatus("failed", reason);
        }

        // â”€â”€ helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static async Task<List<JObject>> ListReports(
            IPage? page, string projectId, string productId, string? bearer)
        {
            var results = new List<JObject>();
            int offset = 0;
            const int limit = 100;

            while (true)
            {
                var url = $"https://developer.api.autodesk.com/reports/v2/projects/{projectId}/reports" +
                          $"?productId={productId}&filter[status]=complete,pending,error,empty" +
                          $"&sort[]=createdAt,DESC&limit={limit}&offset={offset}";

                string? body = null;
                int statusCode = 0;

                // Try via Playwright page context (carries cookies)
                if (page != null)
                {
                    try
                    {
                        var opts = bearer != null
                            ? new APIRequestContextOptions
                            {
                                Headers = new Dictionary<string, string>
                                {
                                    ["authorization"] = bearer,
                                    ["accept"] = "application/json"
                                }
                            }
                            : null;
                        var resp = opts != null
                            ? await page.Context.APIRequest.GetAsync(url, opts)
                            : await page.Context.APIRequest.GetAsync(url);
                        statusCode = resp.Status;
                        if (resp.Ok)
                            body = await resp.TextAsync();
                    }
                    catch { }
                }

                // Fall back to HttpClient with stored token
                if (body == null && bearer != null)
                {
                    var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Authorization =
                        new AuthenticationHeaderValue("Bearer", bearer.Replace("Bearer ", ""));
                    var resp = await _http.SendAsync(req);
                    statusCode = (int)resp.StatusCode;
                    if (resp.IsSuccessStatusCode)
                        body = await resp.Content.ReadAsStringAsync();
                }

                if (body == null) break;

                var json = JObject.Parse(body);
                var rows = json["results"] as JArray ?? new JArray();
                foreach (var r in rows) results.Add((JObject)r);

                var total = json["pagination"]?["totalResults"]?.Value<int>() ?? results.Count;
                offset += rows.Count;
                if (rows.Count < limit || results.Count >= total) break;
            }

            return results;
        }

        private List<ReportDocument> GetReportsForRun()
        {
            using var session = DatabaseService.Instance.OpenSession();
            return session.Query<ReportDocument>()
                .Where(r => r.RunId == _runId)
                .ToList();
        }

        private void UpdateRunStatus(string status, string? notes = null)
        {
            using var session = DatabaseService.Instance.OpenSession();
            var run = session.Load<ReportRunDocument>($"reportruns/{_runId}");
            if (run == null) return;
            run.Status  = status;
            run.EndedAt = DateTime.UtcNow;
            if (notes != null) run.Notes = notes;
            session.SaveChanges();
        }
    }

    public class ExportRunMeta
    {
        public string ProjectId { get; set; } = null!;
        public string? ProjectName { get; set; }
        public string? AccountId { get; set; }
        public string? UserEmail { get; set; }
        public string? Platform { get; set; }
        public string? BatchRunId { get; set; }
        public string? ProductId { get; set; }
        public int? ExpectedCount { get; set; }
    }
}

