using System;

namespace AutodeskAutomation.Models.Documents
{
    public class AppSessionDocument
    {
        // ID format: appsessions/{token}
        // RavenDB @expires metadata is set to ExpiresAt for automatic TTL deletion
        public string Id { get; set; } = null!;
        public string Token { get; set; } = null!;
        public string UserEmail { get; set; } = null!;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; }
    }
}
