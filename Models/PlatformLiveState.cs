using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutodeskAutomation.Models
{
    public class PlatformLiveState
    {
        public string ExportStatus { get; set; } = "idle";
        public ProgressInfo Progress { get; set; } = new ProgressInfo();
        public ResultsInfo Results { get; set; } = new ResultsInfo();
        public Dictionary<string, ProjectStatus> ProjectStatuses { get; set; } = new Dictionary<string, ProjectStatus>();
        public List<object> RecentLogs { get; set; } = new List<object>();

        public void Reset()
        {
            ExportStatus = "idle";
            Progress = new ProgressInfo();
            Results = new ResultsInfo();
            ProjectStatuses = new Dictionary<string, ProjectStatus>();
            RecentLogs = new List<object>();
        }
    }

    public class ProgressInfo
    {
        public int Completed { get; set; }
        public int Total { get; set; }
    }

    public class ResultsInfo
    {
        public int Success { get; set; }
        public int Failed { get; set; }
        public int NoDm { get; set; }
        public int Skipped { get; set; }
        public int EmailsQueued { get; set; }
    }

    public class ProjectStatus
    {
        public string Status { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string? Error { get; set; }
    }
}
