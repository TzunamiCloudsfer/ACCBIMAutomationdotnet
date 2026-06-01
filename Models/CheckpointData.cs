using System.Collections.Generic;

namespace AutodeskAutomation.Models
{
    public class CheckpointData
    {
        public List<string> Completed { get; set; } = new List<string>();
        public List<string> NoDm { get; set; } = new List<string>();
        public List<string> FilteredBim360 { get; set; } = new List<string>();
        public int EmailsSent => Completed.Count;
    }
}
