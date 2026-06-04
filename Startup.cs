using System.IO;
using System.Web;
using System.Web.Http;
using AutodeskAutomation.Helpers;
using Microsoft.Owin;
using Microsoft.Owin.FileSystems;
using Microsoft.Owin.StaticFiles;
using Newtonsoft.Json.Serialization;
using Owin;

// OWIN startup class -- automatically discovered by Microsoft.Owin.Host.SystemWeb
// when the app is hosted in IIS or IIS Express.
[assembly: OwinStartup(typeof(AutodeskAutomation.Startup))]

namespace AutodeskAutomation
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            //  WebAPI 2 configuration ────────────────────────────────────────────
            var config = new HttpConfiguration();

            // Attribute routing ([Route], [RoutePrefix])
            config.MapHttpAttributeRoutes();

            // JSON: camelCase to match the existing vanilla JS frontend
            config.Formatters.JsonFormatter.SerializerSettings.ContractResolver =
                new CamelCasePropertyNamesContractResolver();
            config.Formatters.JsonFormatter.SerializerSettings.NullValueHandling =
                Newtonsoft.Json.NullValueHandling.Ignore;

            // Auth middleware: protects /api/* (except /api/auth/*) and /events
            config.MessageHandlers.Add(new AuthMiddleware());

            //  Static files are served by IIS directly from the project root ──────
            // index.html, app.js, style.css, cloudsfer-logo.png live at the project
            // root so IIS finds them without any extra OWIN file server middleware.

            //  WebAPI handles /api/* routes and /events ──────────────────────────
            app.UseWebApi(config);
        }
    }
}
