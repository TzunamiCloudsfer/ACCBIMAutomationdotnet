using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using AutodeskAutomation.Models;
using AutodeskAutomation.Models.Documents;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Controllers
{
    [RoutePrefix("api/summary-report")]
    public class SummaryReportController : ApiController
    {
        private readonly DatabaseService _db = DatabaseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;

        //  List all captured reports ─────────────────────────────────────────────
        [HttpGet, Route("reports")]
        public IHttpActionResult GetReports(
            string? platform = null, string? status = null,
            string? batchRunId = null, int limit = 100)
        {
            using var session = _db.OpenSession();
            var query = session.Query<ReportDocument>()
                .Where(r => r.UserEmail == (_srv.ActiveUser ?? "__global__"));

            if (!string.IsNullOrEmpty(platform))
                query = query.Where(r => r.Platform == platform);
            if (!string.IsNullOrEmpty(status))
                query = query.Where(r => r.Status == status);
            if (!string.IsNullOrEmpty(batchRunId))
                query = query.Where(r => r.RunId == batchRunId);

            var reports = query.OrderByDescending(r => r.FirstSeenAt)
                .Take(limit).ToList();

            return Ok(new { reports = reports.Select(ToDto), total = reports.Count });
        }

        //  List report runs (one per project export) ─────────────────────────────
        [HttpGet, Route("runs")]
        public IHttpActionResult GetRuns(string? platform = null, int limit = 50)
        {
            using var session = _db.OpenSession();
            var query = session.Query<ReportRunDocument>()
                .Where(r => r.UserEmail == (_srv.ActiveUser ?? "__global__"));

            if (!string.IsNullOrEmpty(platform))
                query = query.Where(r => r.Platform == platform);

            var runs = query.OrderByDescending(r => r.StartedAt).Take(limit).ToList();
            return Ok(new { runs = runs.Select(RunToDto) });
        }

        //  Single report run + its captured reports ──────────────────────────────
        [HttpGet, Route("runs/{runId}")]
        public IHttpActionResult GetRun(string runId)
        {
            using var session = _db.OpenSession();
            var run = session.Load<ReportRunDocument>($"reportruns/{runId}");
            if (run == null) return NotFound();

            var reports = session.Query<ReportDocument>()
                .Where(r => r.RunId == runId).ToList();

            return Ok(new { run = RunToDto(run), reports = reports.Select(ToDto) });
        }

        //  Download redirect for a completed report ──────────────────────────────
        [HttpGet, Route("reports/{reportId}/download")]
        public HttpResponseMessage Download(string reportId)
        {
            using var session = _db.OpenSession();
            var report = session.Load<ReportDocument>($"reports/{reportId}");
            if (report == null)
                return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Not found" });
            if (report.Status != "complete" || string.IsNullOrEmpty(report.DownloadUrl))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Report not yet complete or has no download URL" });

            var response = Request.CreateResponse(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(report.DownloadUrl);
            return response;
        }

        //  CSV export of all captured reports ────────────────────────────────────
        [HttpGet, Route("csv")]
        public HttpResponseMessage GetCsv(string? platform = null, string? batchRunId = null)
        {
            using var session = _db.OpenSession();
            var query = session.Query<ReportDocument>()
                .Where(r => r.UserEmail == (_srv.ActiveUser ?? "__global__"));

            if (!string.IsNullOrEmpty(platform)) query = query.Where(r => r.Platform == platform);
            if (!string.IsNullOrEmpty(batchRunId)) query = query.Where(r => r.RunId == batchRunId);

            var reports = query.OrderByDescending(r => r.FirstSeenAt).Take(5000).ToList();

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("ProjectId,ProjectName,Platform,Title,Status,CreatedBy,CreatorName," +
                           "Format,AutodeskCreatedAt,CompletedAt,DownloadUrl,ErrorMessage");
            foreach (var r in reports)
            {
                csv.AppendLine(string.Join(",",
                    Q(r.ProjectId), Q(r.Title), Q(r.Platform), Q(r.Title), Q(r.Status),
                    Q(r.CreatedBy), Q(r.CreatorName), Q(r.Format),
                    Q(r.AutodeskCreatedAt?.ToString("o")), Q(r.CompletedAt?.ToString("o")),
                    Q(r.DownloadUrl), Q(r.ErrorMessage)));
            }

            var response = Request.CreateResponse(HttpStatusCode.OK);
            response.Content = new System.Net.Http.StringContent(csv.ToString(),
                System.Text.Encoding.UTF8, "text/csv");
            response.Content.Headers.ContentDisposition =
                new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                { FileName = "summary-report.csv" };
            return response;
        }

        //  DTO helpers ───────────────────────────────────────────────────────────
        private static object ToDto(ReportDocument r) => new
        {
            id            = r.ReportId,
            runId         = r.RunId,
            projectId     = r.ProjectId,
            platform      = r.Platform,
            title         = r.Title,
            status        = r.Status,
            format        = r.Format,
            createdBy     = r.CreatedBy,
            creatorName   = r.CreatorName,
            errorMessage  = r.ErrorMessage,
            downloadUrl   = r.Status == "complete" ? r.DownloadUrl : null,
            urlExpiresAt  = r.UrlExpiresAt,
            autodeskCreatedAt = r.AutodeskCreatedAt,
            completedAt   = r.CompletedAt,
            firstSeenAt   = r.FirstSeenAt,
        };

        private static object RunToDto(ReportRunDocument r) => new
        {
            runId       = r.RunId,
            projectId   = r.ProjectId,
            projectName = r.ProjectName,
            platform    = r.Platform,
            batchRunId  = r.BatchRunId,
            status      = r.Status,
            startedAt   = r.StartedAt,
            endedAt     = r.EndedAt,
            expected    = r.ExpectedCount,
            notes       = r.Notes,
        };

        private static string Q(string? s)
            => s == null ? "" : $"\"{s.Replace("\"", "\"\"")}\"";
    }
}
