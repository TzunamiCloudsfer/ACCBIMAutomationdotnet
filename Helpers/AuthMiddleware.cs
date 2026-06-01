using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Helpers
{
    // Protects /api/* (except /api/auth/*) and /events
    // Reads the cloudsfer_session cookie and looks it up in RavenDB
    public class AuthMiddleware : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri.AbsolutePath;
            bool requiresAuth =
                path == "/events" ||
                (path.StartsWith("/api/") && !path.StartsWith("/api/auth/"));

            if (requiresAuth)
            {
                var cookies = CookieHelper.ParseCookies(request);
                cookies.TryGetValue("cloudsfer_session", out var token);
                var session = DatabaseService.Instance.GetAppSession(token);

                if (session == null)
                {
                    return request.CreateResponse(HttpStatusCode.Unauthorized,
                        new { error = "Not authenticated" });
                }

                // Attach email to request properties so controllers can read it
                request.Properties["SessionEmail"] = session.UserEmail;
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
