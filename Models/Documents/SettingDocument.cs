using System;

namespace AutodeskAutomation.Models.Documents
{
    public class SettingDocument
    {
        // ID format: settings/{userEmail}/{platform}/{key}
        public string Id { get; set; } = null!;
        public string Value { get; set; } = null!;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
