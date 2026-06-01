using System;

namespace AutodeskAutomation.Models.Documents
{
    public class ProjectDocument
    {
        // ID format: projects/{userEmail}/{platform}/{projectId}
        public string Id { get; set; } = null!;
        public string UserEmail { get; set; } = null!;
        public string Platform { get; set; } = null!;
        public string ProjectId { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? AccountId { get; set; }
        public string? HubId { get; set; }
        public string? HubName { get; set; }
        public string Status { get; set; } = "active";
        public string? Type { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
        public string? JobNumber { get; set; }
        public string? City { get; set; }
        public string? Country { get; set; }
        public string? Timezone { get; set; }
        public int? MemberCount { get; set; }
        public long? DatasetSize { get; set; }
        public string? RawPlatform { get; set; }
        public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    }
}
