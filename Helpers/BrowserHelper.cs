using System.IO;
using Microsoft.Playwright;

namespace AutodeskAutomation.Helpers
{
    // Returns launch options that point to an already-installed browser when
    // Playwright's own Chromium download is not available.
    public static class BrowserHelper
    {
        private static readonly string[] ChromePaths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        };

        public static BrowserTypeLaunchOptions HeadedOptions()
        {
            var opts = new BrowserTypeLaunchOptions
            {
                Headless = false,
                Args = new[] { "--start-maximized" }
            };
            var exe = FindBrowser();
            if (exe != null) opts.ExecutablePath = exe;
            return opts;
        }

        public static BrowserTypeLaunchOptions HeadlessOptions()
        {
            var opts = new BrowserTypeLaunchOptions { Headless = true };
            var exe = FindBrowser();
            if (exe != null) opts.ExecutablePath = exe;
            return opts;
        }

        private static string? FindBrowser()
        {
            foreach (var path in ChromePaths)
                if (File.Exists(path)) return path;
            return null;  // fall back to Playwright's own Chromium
        }
    }
}
