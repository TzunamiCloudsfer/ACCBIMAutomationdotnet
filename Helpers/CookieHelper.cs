using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;

namespace AutodeskAutomation.Helpers
{
    public static class CookieHelper
    {
        public static Dictionary<string, string> ParseCookies(HttpRequestMessage request)
        {
            var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.Headers.TryGetValues("Cookie", out var values))
            {
                foreach (var header in values)
                {
                    foreach (var pair in header.Split(';'))
                    {
                        var idx = pair.IndexOf('=');
                        if (idx < 0) continue;
                        var key = pair.Substring(0, idx).Trim();
                        var val = pair.Substring(idx + 1).Trim();
                        try { val = Uri.UnescapeDataString(val); } catch { }
                        cookies[key] = val;
                    }
                }
            }
            return cookies;
        }

        public static string BuildSetCookieHeader(string name, string value, int maxAgeSec,
            bool httpOnly = true, string sameSite = "Strict", string path = "/")
        {
            var encoded = Uri.EscapeDataString(value);
            return $"{name}={encoded}; Path={path}; Max-Age={maxAgeSec};" +
                   (httpOnly ? " HttpOnly;" : "") +
                   $" SameSite={sameSite}";
        }

        public static string BuildClearCookieHeader(string name, string path = "/")
            => $"{name}=; Path={path}; Max-Age=0; HttpOnly; SameSite=Strict";
    }
}
