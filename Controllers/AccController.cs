using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using AutodeskAutomation.Models;
using AutodeskAutomation.Models.Documents;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Controllers
{
    [RoutePrefix("api/acc")]
    public class AccController : ApiController
    {
        private readonly DatabaseService _db = DatabaseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;
        private readonly SseService _sse = SseService.Instance;

        //  Projects ──────────────────────────────────────────────────────────────
        [HttpGet, Route("projects")]
        public IHttpActionResult GetProjects()
        {
            var projects = _db.GetProjects(_srv.ActiveUser, "acc");
            var cp = _db.LoadCheckpoint(_srv.ActiveUser, "acc");
            var enriched = projects.Select(p =>
            {
                var isBim360 = string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase);
                var status = isBim360 ? "skipped"
                    : cp.Completed.Contains(p.ProjectId) ? "completed"
                    : cp.NoDm.Contains(p.ProjectId) ? "no_dm"
                    : cp.FilteredBim360.Contains(p.ProjectId) ? "no_dm"
                    : "pending";
                return new { id = p.ProjectId, name = p.Name, accountId = p.AccountId,
                             hubName = p.HubName, rawPlatform = p.RawPlatform, status };
            });
            return Ok(new { projects = enriched, total = projects.Count });
        }

        [HttpPost, Route("projects/discover")]
        public async Task<HttpResponseMessage> Discover()
        {
            if (_srv.AccRunning)
                return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "ACC export is running" });

            var response = Request.CreateResponse(HttpStatusCode.OK, new { status = "started" });
            _ = Task.Run(async () =>
            {
                try
                {
                    var adminUrl = _db.GetAdminUrl(_srv.ActiveUser, "acc");
                    var authPath = GetSharedAuthPath();
                    var projects = await AutodeskApiService.Instance
                        .FetchAccProjects(adminUrl, authPath);

                    var active = projects.Where(p =>
                        !string.Equals(p.Status, "inactive", StringComparison.OrdinalIgnoreCase)).ToList();
                    _db.SaveProjectDocuments(_srv.ActiveUser, "acc", active);

                    // Auto-set admin URL if not already set
                    if (string.IsNullOrEmpty(adminUrl) && active.Count > 0)
                    {
                        var accountId = active.FirstOrDefault(p => p.AccountId != null)?.AccountId;
                        if (accountId != null)
                        {
                            var url = $"https://acc.autodesk.com/account-admin/projects/accounts/{accountId}/active";
                            _db.SetAdminUrl(_srv.ActiveUser, "acc", url);
                            _sse.Broadcast("account-detected", new { platform = "acc", accountId, url });
                        }
                    }

                    _sse.Broadcast("discover-complete", new { projects = active.Select(p =>
                        new { id = p.ProjectId, name = p.Name, accountId = p.AccountId }),
                        count = active.Count, platform = "acc" });
                }
                catch (Exception ex)
                {
                    _sse.Broadcast("discover-error", new { error = ex.Message, platform = "acc" });
                }
            });
            return response;
        }

        //  Checkpoint ────────────────────────────────────────────────────────────
        [HttpDelete, Route("checkpoint")]
        public async Task<IHttpActionResult> ResetCheckpoint()
        {
            if (_srv.AccRunning)
            {
                AccBatchService.Instance.Stop();
                await Task.Delay(600);
                _srv.AccRunning = false;
                _srv.Acc.Reset();
            }

            _db.ResetCheckpoint(_srv.ActiveUser, "acc");
            _srv.AccFreshNext = true;

            var total = _db.GetProjects(_srv.ActiveUser, "acc").Count;
            _sse.Broadcast("checkpoint-reset", new { platform = "acc", total });

            return Ok(new { status = "ok", total });
        }

        [HttpPost, Route("checkpoint/reset-projects")]
        public IHttpActionResult ResetProjects([FromBody] ResetProjectsRequest body)
        {
            if (body?.ProjectIds == null || body.ProjectIds.Count == 0)
                return BadRequest("projectIds required");
            _db.ResetProjectsCheckpoint(_srv.ActiveUser, "acc", body.ProjectIds);
            return Ok(new { status = "ok", reset = body.ProjectIds.Count });
        }

        //  Export ────────────────────────────────────────────────────────────────
        [HttpPost, Route("export/start")]
        public async Task<HttpResponseMessage> StartExport([FromBody] ExportRequest body)
        {
            if (_srv.AccRunning)
            {
                AccBatchService.Instance.Stop();
                _srv.AccRunning = false;
                _srv.AccPaused  = false;
                await Task.Delay(500);
            }

            if (_srv.LoginPending)
                return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "Login in progress" });

            var allProjects = _db.GetProjects(_srv.ActiveUser, "acc");
            if (allProjects.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "No projects configured. Run Discover first." });

            bool fresh = (body?.Fresh == true) || _srv.AccFreshNext;
            _srv.AccFreshNext = false;

            var projects = body?.ProjectIds?.Count > 0
                ? allProjects.Where(p => body.ProjectIds.Contains(p.ProjectId)).ToList()
                : allProjects;

            // Explicitly selected projects should always be exported, even if previously completed.
            // Reset only those checkpoints so the batch filter treats them as pending.
            if (body?.ProjectIds?.Count > 0 && !fresh)
                _db.ResetProjectsCheckpoint(_srv.ActiveUser, "acc", body.ProjectIds);

            _srv.Acc.Reset();
            _srv.AccRunning = true;
            _srv.AccPaused = false;

            _ = Task.Run(async () =>
            {
                try
                {
                    await AccBatchService.Instance.RunBatch(projects, new BatchOptions
                    {
                        UserEmail      = _srv.ActiveUser,
                        Fresh          = fresh,
                        ScreenshotsDir = GetAccScreenshotsDir()
                    });
                }
                catch (Exception ex)
                {
                    _srv.AccRunning = false;
                    _sse.Broadcast("export-error", new { error = ex.Message, platform = "acc" });
                }
            });

            return Request.CreateResponse(HttpStatusCode.OK,
                new { status = "started", total = projects.Count });
        }

        [HttpPost, Route("export/pause")]
        public IHttpActionResult PauseExport()
        {
            if (!_srv.AccRunning) return BadRequest("No ACC export running");
            AccBatchService.Instance.Pause();
            return Ok(new { status = "pausing" });
        }

        [HttpPost, Route("export/resume")]
        public IHttpActionResult ResumeExport()
        {
            if (!_srv.AccRunning) return BadRequest("No ACC export running");
            AccBatchService.Instance.Resume();
            return Ok(new { status = "resumed" });
        }

        //  Reports ───────────────────────────────────────────────────────────────
        [HttpGet, Route("reports")]
        public IHttpActionResult GetReports()
        {
            var runs = _db.GetRuns(_srv.ActiveUser, "acc");
            return Ok(new { reports = runs.Select(RunToReport) });
        }

        [HttpGet, Route("reports/{id}/download")]
        public HttpResponseMessage DownloadReport(string id)
        {
            var run = _db.GetRunById(id);
            if (run == null) return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Not found" });
            var response = Request.CreateResponse(HttpStatusCode.OK, RunToReport(run));
            response.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                { FileName = $"run-{id}.json" };
            return response;
        }

        [HttpDelete, Route("reports/{id}")]
        public IHttpActionResult DeleteReport(string id)
        {
            _db.DeleteRun(id);
            return Ok(new { status = "deleted" });
        }

        //  Members export ────────────────────────────────────────────────────────
        // Fetches project member list (emails, roles) from ACC Admin API and saves it.
        // Equivalent to the Export button on:
        //   https://acc.autodesk.com/project-admin/members/projects/{projectId}
        [HttpPost, Route("projects/{projectId}/members/export")]
        public async Task<IHttpActionResult> ExportMembers(string projectId)
        {
            var allProjects = _db.GetProjects(_srv.ActiveUser, "acc");
            var project = allProjects.FirstOrDefault(p => p.ProjectId == projectId);
            var accountId = project?.AccountId
                ?? AutodeskApiService.ExtractAccountId(_db.GetAdminUrl(_srv.ActiveUser, "acc") ?? "")
                ?? "";

            _sse.Broadcast("log", new { level = "INFO",
                message = $"Fetching members for project {projectId}...", platform = "acc" });

            var result = await new Services.AccMembersExporter()
                .ExportMembersAsync(projectId, accountId, _srv.ActiveUser);

            if (!result.Success)
                return BadRequest(result.Message);

            return Ok(new
            {
                status      = "ok",
                memberCount = result.MemberCount,
                members     = result.Members?.Select(m => new
                {
                    email  = m.Email,
                    name   = m.Name,
                    role   = m.Role,
                    status = m.Status
                })
            });
        }

        // GET members is omitted -- use POST .../members/export which returns the data directly

        //  Admin URL ─────────────────────────────────────────────────────────────
        [HttpPost, Route("admin-url")]
        public IHttpActionResult SetAdminUrl([FromBody] AdminUrlRequest body)
        {
            if (string.IsNullOrEmpty(body?.Url)) return BadRequest("url required");
            _db.SetAdminUrl(_srv.ActiveUser, "acc", body.Url);
            return Ok(new { status = "ok" });
        }

        //  Helpers ───────────────────────────────────────────────────────────────
        private string GetSharedAuthPath()
        {
            if (_srv.ActiveUserSlug == null) return string.Empty;
            var dir = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "storage", "users", _srv.ActiveUserSlug);
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "auth-state.json");
        }

        private string GetAccScreenshotsDir()
        {
            var dir = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "storage", "users", _srv.ActiveUserSlug ?? "_unknown_", "acc-screenshots");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }

        private static object RunToReport(ExportRunDocument r) => new
        {
            id = r.Id,
            timestamp = r.StartedAt,
            completedAt = r.CompletedAt,
            total = r.Total,
            success = r.Success,
            no_dm = r.NoDm,
            failed = r.Failed,
            skipped = r.Skipped,
            emailsQueued = r.EmailsQueued,
            note = r.Note
        };

        public class ResetProjectsRequest { public List<string>? ProjectIds { get; set; } }
        public class ExportRequest
        {
            public List<string>? ProjectIds { get; set; }
            public bool Fresh { get; set; }
        }
        public class AdminUrlRequest { public string? Url { get; set; } }
    }
}
