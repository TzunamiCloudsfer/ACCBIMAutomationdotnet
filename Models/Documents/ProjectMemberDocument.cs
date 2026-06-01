using System;

namespace AutodeskAutomation.Models.Documents
{
    public class ProjectMemberDocument
    {
        // ID: projectmembers/{userEmail}/{projectId}
        public string Id { get; set; } = null!;
        public string UserEmail { get; set; } = null!;
        public string ProjectId { get; set; } = null!;
        public string ProjectName { get; set; } = null!;
        public string AccountId { get; set; } = null!;
        public System.Collections.Generic.List<MemberInfo> Members { get; set; } = new();
        public DateTime ExportedAt { get; set; } = DateTime.UtcNow;
    }

    public class MemberInfo
    {
        public string? Email { get; set; }
        public string? Name { get; set; }
        public string? Role { get; set; }
        public string? Status { get; set; }
        public string? UserId { get; set; }
    }
}
