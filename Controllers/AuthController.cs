using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Web.Http;
using AutodeskAutomation.Helpers;
using AutodeskAutomation.Models;
using AutodeskAutomation.Services;

namespace AutodeskAutomation.Controllers
{
    [RoutePrefix("api/auth")]
    public class AuthController : ApiController
    {
        private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(7);
        private readonly DatabaseService _db = DatabaseService.Instance;
        private readonly ServerState _srv = ServerState.Instance;

        [HttpGet, Route("me")]
        public IHttpActionResult Me()
        {
            var cookies = CookieHelper.ParseCookies(Request);
            cookies.TryGetValue("cloudsfer_session", out var token);
            var session = _db.GetAppSession(token);
            if (session != null)
                return Ok(new { authenticated = true, email = session.UserEmail });
            return Ok(new { authenticated = false, email = (string?)null });
        }

        [HttpPost, Route("signup")]
        public HttpResponseMessage Signup([FromBody] AuthRequest body)
        {
            if (body == null || string.IsNullOrEmpty(body.Email) ||
                string.IsNullOrEmpty(body.Password) || body.Password.Length < 6)
            {
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "A valid email and a password of at least 6 characters are required." });
            }

            var norm = body.Email.ToLowerInvariant().Trim();
            if (_db.GetAuthUser(norm) != null)
            {
                return Request.CreateResponse(HttpStatusCode.Conflict,
                    new { error = "This email is already registered. Please sign in." });
            }

            try { _db.CreateAuthUser(norm, body.Password); }
            catch (Exception e)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = "Could not create account: " + e.Message });
            }

            return Request.CreateResponse(HttpStatusCode.OK, new { ok = true, email = norm });
        }

        [HttpPost, Route("login")]
        public HttpResponseMessage Login([FromBody] AuthRequest body)
        {
            if (body == null || string.IsNullOrEmpty(body.Email) || string.IsNullOrEmpty(body.Password))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Email and password are required." });

            var norm = body.Email.ToLowerInvariant().Trim();
            if (!_db.VerifyAuthUser(norm, body.Password))
                return Request.CreateResponse(HttpStatusCode.Unauthorized,
                    new { error = "Invalid email or password." });

            _db.UpdateAuthLastLogin(norm);
            var token = GenerateToken();
            _db.CreateAppSession(token, norm, SessionTtl);
            ActivateUser(norm);

            var response = Request.CreateResponse(HttpStatusCode.OK, new { ok = true, email = norm });
            response.Headers.Add("Set-Cookie",
                CookieHelper.BuildSetCookieHeader("cloudsfer_session", token,
                    (int)SessionTtl.TotalSeconds));
            return response;
        }

        [HttpPost, Route("logout")]
        public HttpResponseMessage Logout()
        {
            var cookies = CookieHelper.ParseCookies(Request);
            cookies.TryGetValue("cloudsfer_session", out var token);
            _db.DeleteAppSession(token);
            var response = Request.CreateResponse(HttpStatusCode.OK, new { ok = true });
            response.Headers.Add("Set-Cookie", CookieHelper.BuildClearCookieHeader("cloudsfer_session"));
            return response;
        }

        private static string GenerateToken()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLower();
        }

        private void ActivateUser(string email)
        {
            _srv.ActiveUser = email;
            _srv.ActiveUserSlug = SlugHelper.EmailToSlug(email);
            _db.SaveLastUser(email);
            SseService.Instance.Broadcast("user-changed", new { user = email });
        }

        public class AuthRequest
        {
            public string? Email { get; set; }
            public string? Password { get; set; }
        }
    }
}
