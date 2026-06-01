using System.Text.RegularExpressions;

namespace AutodeskAutomation.Helpers
{
    public static class SlugHelper
    {
        public static string EmailToSlug(string email)
            => Regex.Replace(email.ToLowerInvariant(), @"[^a-z0-9]", "_");
    }
}
