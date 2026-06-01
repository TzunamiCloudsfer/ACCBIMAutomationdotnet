namespace AutodeskAutomation.Services
{
    public class BatchOptions
    {
        public string? UserEmail { get; set; }
        public string? AuthStatePath { get; set; }
        public string? AccountId { get; set; }
        public string? ScreenshotsDir { get; set; }
        public bool Fresh { get; set; }
    }

    public class BatchResult
    {
        public int Success { get; set; }
        public int Failed { get; set; }
        public int NoDm { get; set; }
        public int Skipped { get; set; }
        public int EmailsQueued { get; set; }
        public bool Stopped { get; set; }
    }
}
