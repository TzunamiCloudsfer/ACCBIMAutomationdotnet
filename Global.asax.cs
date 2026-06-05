using System;
using System.IO;
using System.Web;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using AutodeskAutomation.Services;

namespace AutodeskAutomation
{
    public class WebApplication : HttpApplication
    {
        protected void Application_Start(object sender, EventArgs e)
        {
            //  Connect to RavenDB (retry up to 30 s to allow server startup) ─────
            Exception? lastEx = null;
            for (int attempt = 1; attempt <= 10; attempt++)
            {
                try
                {
                    DatabaseService.Instance.Initialize();
                    lastEx = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    if (attempt < 10)
                    {
                        System.Diagnostics.Trace.TraceWarning(
                            $"[RavenDB] Connection attempt {attempt}/10 failed: {ex.Message} -- retrying in 3s...");
                        System.Threading.Thread.Sleep(3000);
                    }
                }
            }
            if (lastEx != null)
                throw new ApplicationException(
                    "Could not connect to RavenDB after 10 attempts. " +
                    "Make sure RavenDB is running on http://localhost:8080 " +
                    "(run C:\\RavenDB\\Install-Service.ps1 as Administrator to register it as a service).\n\n" +
                    "Original error: " + lastEx.Message, lastEx);

            //  Restore last logged-in Autodesk user across restarts ─────────────
            var lastUser = DatabaseService.Instance.GetLastUser();
            if (!string.IsNullOrEmpty(lastUser))
            {
                var srv = ServerState.Instance;
                srv.ActiveUser = lastUser;
                srv.ActiveUserSlug = SlugHelper.EmailToSlug(lastUser);
            }

            //  Ensure Playwright Chromium browser is installed ───────────────────
            // Runs silently in the background -- does nothing if already installed
            System.Threading.Tasks.Task.Run(() =>
            {
                try { Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }); }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("[Playwright] install: " + ex.Message); }
            });

            //  Ensure storage directories exist ─────────────────────────────────
            var storageRoot = Path.Combine(HttpRuntime.AppDomainAppPath, "storage");
            Directory.CreateDirectory(storageRoot);

            //  Migrate any legacy file-based project data into RavenDB ─────────
            var db = DatabaseService.Instance;
            var slug = ServerState.Instance.ActiveUserSlug;
            if (!string.IsNullOrEmpty(slug))
            {
                var userDir = Path.Combine(storageRoot, "users", slug);
                db.MigrateProjectsFromFile(ServerState.Instance.ActiveUser, "acc",
                    Path.Combine(userDir, "projects.json"));
                db.MigrateProjectsFromFile(ServerState.Instance.ActiveUser, "bim360",
                    Path.Combine(userDir, "projects.json"));
                db.MigrateCheckpointFromFile(ServerState.Instance.ActiveUser, "acc",
                    Path.Combine(userDir, "checkpoint.json"));
                db.MigrateCheckpointFromFile(ServerState.Instance.ActiveUser, "bim360",
                    Path.Combine(userDir, "checkpoint.json"));
            }
        }

        protected void Application_End(object sender, EventArgs e)
        {
            // Clean shutdown -- stop any running exports
            AccBatchService.Instance.Stop();
            Bim360BatchService.Instance.Stop();
        }

        protected void Application_Error(object sender, EventArgs e)
        {
            var ex = Server.GetLastError();
            if (ex != null)
                System.Diagnostics.Trace.TraceError("[Application_Error] " + ex.ToString());
        }
    }
}
