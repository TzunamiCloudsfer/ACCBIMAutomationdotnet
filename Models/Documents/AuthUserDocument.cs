using System;

namespace AutodeskAutomation.Models.Documents
{
    public class AuthUserDocument
    {
        // ID format: authusers/{email}
        public string Id { get; set; } = null!;
        public string Email { get; set; } = null!;
        public string PasswordHash { get; set; } = null!;
        public string Salt { get; set; } = null!;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? LastLogin { get; set; }
    }
}
