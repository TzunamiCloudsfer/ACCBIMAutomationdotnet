using System;

namespace AutodeskAutomation.Models.Documents
{
    public class AutodeskTokenDocument
    {
        // ID format: autodesk-tokens/{userEmail}
        public string Id { get; set; } = null!;
        public string UserEmail { get; set; } = null!;
        public string AccessToken { get; set; } = null!;
        public string? RefreshToken { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string? Scope { get; set; }
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;

        public bool IsExpired => DateTime.UtcNow >= ExpiresAt.AddMinutes(-2);
    }
}
