using System;

namespace AutodeskAutomation.Models.Documents
{
    public class CheckpointDocument
    {
        // ID format: checkpoints/{userEmail}/{platform}/{projectId}
        public string Id { get; set; } = null!;
        public string UserEmail { get; set; } = null!;
        public string Platform { get; set; } = null!;
        public string ProjectId { get; set; } = null!;
        // completed | no_dm | filtered_bim360
        public string Status { get; set; } = null!;
        public DateTime MarkedAt { get; set; } = DateTime.UtcNow;
    }
}
