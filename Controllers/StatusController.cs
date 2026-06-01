using System.IO;
using System.Linq;
using System.Web.Http;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Controllers
{
    [RoutePrefix("api/status")]
    public class StatusController : ApiController
    {
        private readonly DatabaseService _db = DatabaseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;

        [HttpGet, Route("")]
        public IHttpActionResult Get()
        {
            var accProjects = _db.GetProjects(_srv.ActiveUser, "acc");
            var accCp = _db.LoadCheckpoint(_srv.ActiveUser, "acc");
            var accAdminUrl = _db.GetAdminUrl(_srv.ActiveUser, "acc");

            var bim360Projects = _db.GetProjects(_srv.ActiveUser, "bim360");
            var bim360Cp = _db.LoadCheckpoint(_srv.ActiveUser, "bim360");
            var bimAdminUrl = _db.GetAdminUrl(_srv.ActiveUser, "bim360");

            var accNonBim360 = accProjects.Where(p =>
                !string.Equals(p.RawPlatform, "bim360", System.StringComparison.OrdinalIgnoreCase)).ToList();

            // sessionValid: true when a fresh Playwright auth-state.json exists
            // (same check as HasValidBrowserSession) OR the user is already detected
            var authPath = _srv.ActiveUserSlug != null
                ? Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory,
                    "storage", "users", _srv.ActiveUserSlug, "auth-state.json")
                : string.Empty;
            var sessionValid = _srv.LoginDetected
                || OAuthService.Instance.HasValidBrowserSession(authPath);

            return Ok(new
            {
                sessionValid,
                sessionAge    = 0,
                isRunning     = _srv.IsRunning,
                chainRunning  = _srv.ChainRunning,
                loginPending  = _srv.LoginPending,
                loginDetected = _srv.LoginDetected,
                loginElapsed  = _srv.LoginElapsedSeconds,
                runningPlatform = _srv.RunningPlatform,
                activeUser    = _srv.ActiveUser,
                acc = new
                {
                    projectCount = accNonBim360.Count,
                    bim360Count = accProjects.Count - accNonBim360.Count,
                    completedCount = accCp.Completed.Count,
                    noDmCount = accCp.NoDm.Count,
                    pendingCount = accNonBim360.Count(p =>
                        !accCp.Completed.Contains(p.ProjectId) &&
                        !accCp.NoDm.Contains(p.ProjectId) &&
                        !accCp.FilteredBim360.Contains(p.ProjectId)),
                    accountAdminUrl = accAdminUrl
                },
                bim360 = new
                {
                    projectCount = bim360Projects.Count,
                    completedCount = bim360Cp.Completed.Count,
                    noDmCount = bim360Cp.NoDm.Count,
                    pendingCount = bim360Projects.Count(p =>
                        !bim360Cp.Completed.Contains(p.ProjectId) &&
                        !bim360Cp.NoDm.Contains(p.ProjectId)),
                    accountAdminUrl = bimAdminUrl
                }
            });
        }
    }
}
