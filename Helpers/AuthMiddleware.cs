using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Helpers
{
    // Cloudsfer account login is no longer required.
    // All requests pass through; if a legacy session cookie exists it is honoured.
    public class AuthMiddleware : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var cookies = CookieHelper.ParseCookies(request);
            if (cookies.TryGetValue("cloudsfer_session", out var token))
            {
                var session = DatabaseService.Instance.GetAppSession(token);
                if (session != null)
                    request.Properties["SessionEmail"] = session.UserEmail;
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
