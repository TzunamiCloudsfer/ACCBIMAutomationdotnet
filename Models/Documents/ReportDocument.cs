using System;

namespace AutodeskAutomation.Models.Documents
{
    // One Autodesk-generated report (PDF/CSV emailed to admin)
    // Captured via GET /reports/v2/projects/{id}/reports
    public class ReportDocument
    {
        public string Id { get; set; } = null!;          // reports/{reportId}
        public string ReportId { get; set; } = null!;    // Autodesk report UUID
        public string RunId { get; set; } = null!;       // -> ReportRunDocument.Id
        public string? UserEmail { get; set; }
        public string? Platform { get; set; }
        public string? ProjectId { get; set; }
        public string? AccountId { get; set; }
        public string? ProductId { get; set; }
        public string? CreatedBy { get; set; }
        public string? CreatorName { get; set; }
        public string? Title { get; set; }
        public string? Type { get; set; }
        public string? Service { get; set; }
        public string? Format { get; set; }
        public string? Status { get; set; }          // pending | complete | error | empty
        public string? ErrorMessage { get; set; }
        public string? DownloadUrl { get; set; }     // signed URL (~30-day TTL)
        public DateTime? UrlExpiresAt { get; set; }
        public DateTime? AutodeskCreatedAt { get; set; }
        public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }

    // One per project-level export -- holds the pre-existing snapshot
    public class ReportRunDocument
    {
        public string Id { get; set; } = null!;          // reportruns/{runId}
        public string RunId { get; set; } = null!;
        public string? UserEmail { get; set; }
        public string? Platform { get; set; }
        public string? BatchRunId { get; set; }
        public string? AccountId { get; set; }
        public string? ProjectId { get; set; }
        public string? ProjectName { get; set; }
        public string? ProductId { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string Status { get; set; } = "running"; // running|completed|abandoned|failed
        public System.Collections.Generic.List<string> PreExistingIds { get; set; } = new();
        public int? ExpectedCount { get; set; }
        public string? Notes { get; set; }
    }
}
