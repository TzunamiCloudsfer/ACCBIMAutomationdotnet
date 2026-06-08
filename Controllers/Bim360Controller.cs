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
    [RoutePrefix("api/bim360")]
    public class Bim360Controller : ApiController
    {
        private readonly DatabaseService _db = DatabaseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;
        private readonly SseService _sse = SseService.Instance;

        //  Projects ──────────────────────────────────────────────────────────────
        [HttpGet, Route("projects")]
        public IHttpActionResult GetProjects()
        {
            var projects = _db.GetProjects(_srv.ActiveUser, "bim360");
            var cp = _db.LoadCheckpoint(_srv.ActiveUser, "bim360");
            var enriched = projects.Select(p => new
            {
                id = p.ProjectId, name = p.Name, accountId = p.AccountId,
                status = cp.Completed.Contains(p.ProjectId) ? "completed"
                       : cp.NoDm.Contains(p.ProjectId) ? "no_dm"
                       : "pending"
            });
            return Ok(new { projects = enriched, total = projects.Count });
        }

        [HttpPost, Route("projects/discover")]
        public async Task<HttpResponseMessage> Discover()
        {
            if (_srv.Bim360Running)
                return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "BIM360 export is running" });

            var response = Request.CreateResponse(HttpStatusCode.OK, new { status = "started" });
            _ = Task.Run(async () =>
            {
                try
                {
                    var adminUrl = _db.GetAdminUrl(_srv.ActiveUser, "bim360")
                                ?? _db.GetAdminUrl(_srv.ActiveUser, "acc");

                    if (string.IsNullOrEmpty(adminUrl))
                    {
                        _sse.Broadcast("discover-complete",
                            new { projects = new object[0], count = 0, platform = "bim360" });
                        return;
                    }

                    var authPath = GetSharedAuthPath();
                    var all = await AutodeskApiService.Instance
                        .FetchBim360Projects(adminUrl, authPath);

                    // BIM360 HQ API returns nothing for pure ACC hubs — fall back to ACC Admin API
                    bool usedAccFallback = false;
                    if (all.Count == 0)
                    {
                        var accAdminUrl = _db.GetAdminUrl(_srv.ActiveUser, "acc") ?? adminUrl;
                        all = await AutodeskApiService.Instance
                            .FetchAccProjects(accAdminUrl, authPath);
                        usedAccFallback = true;
                    }

                    var active = all.Where(p =>
                        string.Equals(p.Status, "active", StringComparison.OrdinalIgnoreCase)).ToList();

                    // Merge any additional BIM360-platform projects cached from a prior ACC discovery
                    var accProjs = _db.GetProjects(_srv.ActiveUser, "acc");
                    var bim360Extra = accProjs.Where(p =>
                        string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase));
                    var knownIds = new HashSet<string>(active.Select(p => p.ProjectId));
                    foreach (var p in bim360Extra)
                        if (!knownIds.Contains(p.ProjectId)) active.Add(p);

                    if (usedAccFallback)
                    {
                        // Split by the platform icon: "B" (RawPlatform=bim360) → bim360, ACC icon → acc
                        var bim360Projects = active.Where(p =>
                            string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase)).ToList();
                        var accProjects = active.Where(p =>
                            !string.Equals(p.RawPlatform, "bim360", StringComparison.OrdinalIgnoreCase)).ToList();

                        _db.SaveProjectDocuments(_srv.ActiveUser, "bim360", bim360Projects);
                        _db.SaveProjectDocuments(_srv.ActiveUser, "acc", accProjects);
                    }
                    else
                    {
                        _db.SaveProjectDocuments(_srv.ActiveUser, "bim360", active);
                    }

                    _sse.Broadcast("discover-complete", new
                    {
                        projects = active.Select(p => new { id = p.ProjectId, name = p.Name, accountId = p.AccountId }),
                        count = active.Count,
                        platform = "bim360"
                    });
                }
                catch (Exception ex)
                {
                    _sse.Broadcast("discover-error", new { error = ex.Message, platform = "bim360" });
                }
            });
            return response;
        }

        //  Checkpoint ────────────────────────────────────────────────────────────
        // Force-reset the running flag if a previous export crashed without cleanup
        [HttpPost, Route("export/reset")]
        public IHttpActionResult ResetExportState()
        {
            _srv.Bim360Running = false;
            _srv.Bim360Paused  = false;
            Bim360BatchService.Instance.Stop();
            _sse.Broadcast("export-complete", new { results = new { }, stopped = true, platform = "bim360" });
            return Ok(new { status = "reset" });
        }

        [HttpDelete, Route("checkpoint")]
        public async Task<IHttpActionResult> ResetCheckpoint()
        {
            // Stop any running export first
            if (_srv.Bim360Running)
            {
                Bim360BatchService.Instance.Stop();
                await Task.Delay(600);           // let background task notice the stop
                _srv.Bim360Running = false;
                _srv.Bim360Paused  = false;
                _srv.Bim360.Reset();
            }

            _db.ResetCheckpoint(_srv.ActiveUser, "bim360");
            _srv.Bim360FreshNext = true;   // next export start will treat all projects as pending

            var total = _db.GetProjects(_srv.ActiveUser, "bim360").Count;
            _sse.Broadcast("checkpoint-reset", new { platform = "bim360", total });

            return Ok(new { status = "ok", total });
        }

        [HttpPost, Route("checkpoint/reset-projects")]
        public IHttpActionResult ResetProjects([FromBody] ResetProjectsRequest body)
        {
            if (body?.ProjectIds == null || body.ProjectIds.Count == 0)
                return BadRequest("projectIds required");
            _db.ResetProjectsCheckpoint(_srv.ActiveUser, "bim360", body.ProjectIds);
            return Ok(new { status = "ok", reset = body.ProjectIds.Count });
        }

        //  Export ────────────────────────────────────────────────────────────────
        [HttpPost, Route("export/start")]
        public async Task<HttpResponseMessage> StartExport([FromBody] ExportRequest body)
        {
            // Auto-reset a stuck export flag -- if nothing is actually running
            // (no SSE clients active, or flag left over from a crashed previous run)
            // allow starting fresh instead of blocking forever.
            if (_srv.Bim360Running)
            {
                Bim360BatchService.Instance.Stop();
                _srv.Bim360Running = false;
                _srv.Bim360Paused  = false;
                await Task.Delay(500);  // let any in-flight task notice the stop
            }

            if (_srv.LoginPending)
                return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "Login in progress" });

            var allProjects = _db.GetProjects(_srv.ActiveUser, "bim360");
            if (allProjects.Count == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "No projects configured. Run Discover first." });

            var adminUrl = _db.GetAdminUrl(_srv.ActiveUser, "bim360");
            var accountId = allProjects.FirstOrDefault(p => p.AccountId != null)?.AccountId
                ?? ExtractAccountId(adminUrl ?? "");

            // Consume the fresh-next flag set by ResetCheckpoint, or honour explicit body flag
            bool fresh = (body?.Fresh == true) || _srv.Bim360FreshNext;
            _srv.Bim360FreshNext = false;

            if (fresh) _db.ResetCheckpoint(_srv.ActiveUser, "bim360");

            var projects = body?.ProjectIds?.Count > 0
                ? allProjects.Where(p => body.ProjectIds.Contains(p.ProjectId)).ToList()
                : allProjects;

            // Explicitly selected projects should always be exported, even if previously completed.
            // Reset only those checkpoints so the batch filter treats them as pending.
            if (body?.ProjectIds?.Count > 0 && !fresh)
                _db.ResetProjectsCheckpoint(_srv.ActiveUser, "bim360", body.ProjectIds);

            _srv.Bim360.Reset();
            _srv.Bim360Running = true;
            _srv.Bim360Paused = false;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Bim360BatchService.Instance.RunBatch(projects, new BatchOptions
                    {
                        UserEmail      = _srv.ActiveUser,
                        AuthStatePath  = GetSharedAuthPath(),
                        AccountId      = accountId,
                        ScreenshotsDir = GetBim360ScreenshotsDir(),
                        Fresh          = fresh
                    });
                }
                catch (Exception ex)
                {
                    _srv.Bim360Running = false;
                    _sse.Broadcast("export-error", new { error = ex.Message, platform = "bim360" });
                }
            });

            return Request.CreateResponse(HttpStatusCode.OK,
                new { status = "started", total = projects.Count });
        }

        [HttpPost, Route("export/pause")]
        public IHttpActionResult PauseExport()
        {
            if (!_srv.Bim360Running) return BadRequest("No BIM360 export running");
            Bim360BatchService.Instance.Pause();
            return Ok(new { status = "pausing" });
        }

        [HttpPost, Route("export/resume")]
        public IHttpActionResult ResumeExport()
        {
            if (!_srv.Bim360Running) return BadRequest("No BIM360 export running");
            Bim360BatchService.Instance.Resume();
            return Ok(new { status = "resumed" });
        }

        //  Reports ───────────────────────────────────────────────────────────────
        [HttpGet, Route("reports")]
        public IHttpActionResult GetReports()
            => Ok(new { reports = _db.GetRuns(_srv.ActiveUser, "bim360").Select(RunToReport) });

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
            _db.DeleteRun(id); return Ok(new { status = "deleted" });
        }

        //  Error Logs ────────────────────────────────────────────────────────────
        [HttpGet, Route("logs")]
        public IHttpActionResult GetLogs()
            => Ok(new { logs = _db.GetErrors(_srv.ActiveUser, "bim360").Select(ErrToLog) });

        [HttpGet, Route("logs/{id}")]
        public IHttpActionResult GetLog(string id)
        {
            var e = _db.GetErrorById(id);
            if (e == null) return NotFound();
            return Ok(ErrToLog(e));
        }

        [HttpGet, Route("logs/{id}/download")]
        public HttpResponseMessage DownloadLog(string id)
        {
            var e = _db.GetErrorById(id);
            if (e == null) return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Not found" });
            var response = Request.CreateResponse(HttpStatusCode.OK, ErrToLog(e));
            response.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                { FileName = $"error-{id}.json" };
            return response;
        }

        [HttpDelete, Route("logs/{id}")]
        public IHttpActionResult DeleteLog(string id)
        {
            _db.DeleteError(id); return Ok(new { status = "deleted" });
        }

        //  Admin URL ─────────────────────────────────────────────────────────────
        [HttpPost, Route("admin-url")]
        public IHttpActionResult SetAdminUrl([FromBody] AdminUrlRequest body)
        {
            if (string.IsNullOrEmpty(body?.Url)) return BadRequest("url required");
            _db.SetAdminUrl(_srv.ActiveUser, "bim360", body.Url);
            return Ok(new { status = "ok" });
        }

        //  Helpers ───────────────────────────────────────────────────────────────
        private string GetSharedAuthPath()
        {
            var dir = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
                "storage", "users", _srv.ActiveUserSlug ?? "_unknown_");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, "auth-state.json");
        }

        private string GetBim360ScreenshotsDir()
        {
            var dir = System.IO.Path.Combine(
                System.AppDomain.CurrentDomain.BaseDirectory,
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

        private static object RunToReport(ExportRunDocument r) => new
        {
            id = r.Id, timestamp = r.StartedAt, completedAt = r.CompletedAt,
            total = r.Total, success = r.Success, no_dm = r.NoDm,
            failed = r.Failed, skipped = r.Skipped, emailsQueued = r.EmailsQueued, note = r.Note
        };

        private static object ErrToLog(ErrorLogDocument e) => new
        {
            id = e.Id, timestamp = e.LoggedAt, projectName = e.ProjectName,
            projectId = e.ProjectId, error = e.ErrorMessage, screenshotPath = e.ScreenshotPath
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
