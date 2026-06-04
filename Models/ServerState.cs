namespace AutodeskAutomation.Models
{
    // Singleton -- mirrors Node's `srv` object in server-unified.js
    public class ServerState
    {
        private static readonly ServerState _instance = new ServerState();
        public static ServerState Instance => _instance;

        private ServerState() { }

        public bool LoginPending { get; set; }
        public bool LoginDetected { get; set; }
        public long? LoginStartTime { get; set; }  // ms since epoch

        public bool AccRunning { get; set; }
        public bool AccPaused { get; set; }
        public bool Bim360Running { get; set; }
        public bool Bim360Paused { get; set; }
        public bool ChainRunning { get; set; }

        public string? ActiveUser { get; set; }
        public string? ActiveUserSlug { get; set; }

        public PlatformLiveState Acc { get; } = new PlatformLiveState();
        public PlatformLiveState Bim360 { get; } = new PlatformLiveState();

        public bool IsRunning => AccRunning || Bim360Running || ChainRunning;
        public bool IsPaused => AccPaused || Bim360Paused;

        public string? RunningPlatform =>
            AccRunning && Bim360Running ? "both" :
            AccRunning ? "acc" :
            Bim360Running ? "bim360" : null;

        public int LoginElapsedSeconds =>
            LoginStartTime.HasValue
                ? (int)((System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LoginStartTime.Value) / 1000)
                : 0;
    }
}
