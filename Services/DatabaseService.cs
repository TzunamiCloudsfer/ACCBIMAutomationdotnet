using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AutodeskAutomation.Models;
using AutodeskAutomation.Models.Documents;
using AutodeskAutomation.Models.Documents;
using Raven.Client.Documents;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace AutodeskAutomation.Services
{
    public class DatabaseService
    {
        private static readonly DatabaseService _instance = new DatabaseService();
        public static DatabaseService Instance => _instance;

        private IDocumentStore _store = null!;
        private const string DbName = "AutodeskAutomation";

        // Configurable — override in Web.config appSettings key "RavenDbUrl"
        public static string ServerUrl { get; set; } = "http://localhost:8080";

        private DatabaseService() { }

        public void Initialize()
        {
            // Read server URL from config if provided
            var cfgUrl = System.Configuration.ConfigurationManager.AppSettings["RavenDbUrl"];
            if (!string.IsNullOrWhiteSpace(cfgUrl)) ServerUrl = cfgUrl;

            _store = new DocumentStore
            {
                Urls = new[] { ServerUrl },
                Database = DbName
            };
            _store.Initialize();

            // Create the database if it doesn't exist yet
            try
            {
                _store.Maintenance.Server.Send(
                    new CreateDatabaseOperation(new DatabaseRecord(DbName)));
            }
            catch (Exception ex) when (ex.Message.Contains("already exist") || ex.GetType().Name.Contains("AlreadyExists"))
            {
                // Expected on subsequent startups — database already created
            }

            // Note: AppSession expiry is handled lazily in GetAppSession() — expired
            // sessions are deleted on read. Automatic RavenDB TTL cleanup requires a
            // paid license (free license minimum is 36 hours), so we skip it here.

            Console.WriteLine($"[db] Connected to RavenDB at {ServerUrl}, database: {DbName}");
        }

        // ── Helpers ──────────────────────────────────────────────────────────────
        private static string U(string? email) => email ?? "__global__";
        private static string SettingId(string? email, string platform, string key)
            => $"settings/{U(email)}/{platform}/{key}";
        private static string ProjectId(string? email, string platform, string projectId)
            => $"projects/{U(email)}/{platform}/{Uri.EscapeDataString(projectId)}";
        private static string CheckpointId(string? email, string platform, string projectId)
            => $"checkpoints/{U(email)}/{platform}/{Uri.EscapeDataString(projectId)}";
        private static string AuthUserId(string email)
            => $"authusers/{email.ToLowerInvariant()}";
        private static string SessionId(string token)
            => $"appsessions/{token}";

        // ── Settings ──────────────────────────────────────────────────────────────
        public string? GetSetting(string? userEmail, string platform, string key)
        {
            using var session = _store.OpenSession();
            var doc = session.Load<SettingDocument>(SettingId(userEmail, platform, key));
            return doc?.Value;
        }

        public void SetSetting(string? userEmail, string platform, string key, string value)
        {
            using var session = _store.OpenSession();
            var id = SettingId(userEmail, platform, key);
            var doc = session.Load<SettingDocument>(id);
            if (doc == null)
            {
                doc = new SettingDocument { Id = id };
                session.Store(doc);
            }
            doc.Value = value;
            doc.UpdatedAt = DateTime.UtcNow;
            session.SaveChanges();
        }

        public string? GetAdminUrl(string? userEmail, string platform)
            => GetSetting(userEmail, platform, "accountAdminUrl")
            ?? GetSetting(null, platform, "accountAdminUrl");

        public void SetAdminUrl(string? userEmail, string platform, string url)
            => SetSetting(userEmail, platform, "accountAdminUrl", url);

        public void SaveLastUser(string email)
            => SetSetting(null, "system", "lastActiveUser", email);

        public string? GetLastUser()
            => GetSetting(null, "system", "lastActiveUser");

        // ── Auth Users ────────────────────────────────────────────────────────────
        public void CreateAuthUser(string email, string password)
        {
            var salt = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(salt);
            var saltHex = Convert.ToBase64String(salt);
            var hash = HashPassword(password, saltHex);

            using var session = _store.OpenSession();
            var id = AuthUserId(email);
            if (session.Load<AuthUserDocument>(id) != null)
                throw new InvalidOperationException("Email already registered.");
            session.Store(new AuthUserDocument
            {
                Id = id,
                Email = email.ToLowerInvariant(),
                PasswordHash = hash,
                Salt = saltHex,
                CreatedAt = DateTime.UtcNow
            });
            session.SaveChanges();
        }

        public AuthUserDocument? GetAuthUser(string email)
        {
            using var session = _store.OpenSession();
            return session.Load<AuthUserDocument>(AuthUserId(email));
        }

        public bool VerifyAuthUser(string email, string password)
        {
            var user = GetAuthUser(email);
            if (user == null) return false;
            try { return HashPassword(password, user.Salt) == user.PasswordHash; }
            catch { return false; }
        }

        public void UpdateAuthLastLogin(string email)
        {
            using var session = _store.OpenSession();
            var user = session.Load<AuthUserDocument>(AuthUserId(email));
            if (user != null) { user.LastLogin = DateTime.UtcNow; session.SaveChanges(); }
        }

        private static string HashPassword(string password, string saltBase64)
        {
            var salt = Convert.FromBase64String(saltBase64);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
            return Convert.ToBase64String(pbkdf2.GetBytes(64));
        }

        // ── App Sessions ──────────────────────────────────────────────────────────
        public void CreateAppSession(string token, string userEmail, TimeSpan ttl)
        {
            using var session = _store.OpenSession();
            var doc = new AppSessionDocument
            {
                Id = SessionId(token),
                Token = token,
                UserEmail = userEmail.ToLowerInvariant(),
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(ttl)
            };
            session.Store(doc);
            // Set RavenDB TTL so the document is auto-deleted after expiry
            var metadata = session.Advanced.GetMetadataFor(doc);
            metadata[Raven.Client.Constants.Documents.Metadata.Expires] = doc.ExpiresAt;
            session.SaveChanges();
        }

        public AppSessionDocument? GetAppSession(string? token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            using var session = _store.OpenSession();
            var doc = session.Load<AppSessionDocument>(SessionId(token));
            if (doc == null) return null;
            if (doc.ExpiresAt < DateTime.UtcNow)
            {
                session.Delete(doc.Id);
                session.SaveChanges();
                return null;
            }
            return doc;
        }

        public void DeleteAppSession(string? token)
        {
            if (string.IsNullOrEmpty(token)) return;
            using var session = _store.OpenSession();
            session.Delete(SessionId(token));
            session.SaveChanges();
        }

        // ── Projects ──────────────────────────────────────────────────────────────
        public List<ProjectDocument> GetProjects(string? userEmail, string platform)
        {
            using var session = _store.OpenSession();
            return session.Query<ProjectDocument>()
                .Where(p => p.UserEmail == U(userEmail) && p.Platform == platform)
                .OrderBy(p => p.Name)
                .ToList();
        }

        public void SaveProjects(string? userEmail, string platform, IEnumerable<dynamic> projects)
        {
            var email = U(userEmail);
            using var session = _store.OpenSession();

            // Delete existing projects for this user+platform
            var existing = session.Query<ProjectDocument>()
                .Where(p => p.UserEmail == email && p.Platform == platform)
                .ToList();
            foreach (var e in existing)
                session.Delete(e.Id);

            // Insert new projects
            foreach (var p in projects)
            {
                var projId = (string)(p.id ?? p.Id ?? p.project_id ?? "");
                if (string.IsNullOrEmpty(projId)) continue;

                var doc = new ProjectDocument
                {
                    Id = ProjectId(userEmail, platform, projId),
                    UserEmail = email,
                    Platform = platform,
                    ProjectId = projId,
                    Name = (string)(p.name ?? p.Name ?? ""),
                    AccountId = (string?)(p.accountId ?? p.AccountId ?? null),
                    HubId = (string?)(p.hubId ?? p.HubId ?? null),
                    HubName = (string?)(p.hubName ?? p.HubName ?? null),
                    Status = (string?)(p.status ?? p.Status ?? null) ?? "active",
                    Type = (string?)(p.type ?? p.Type ?? null),
                    RawPlatform = (string?)(p.platform ?? p.Platform ?? null),
                    DiscoveredAt = DateTime.UtcNow
                };
                session.Store(doc);
            }
            session.SaveChanges();
        }

        public void SaveProjectDocuments(string? userEmail, string platform, IEnumerable<ProjectDocument> projects)
        {
            var email = U(userEmail);
            using var session = _store.OpenSession();
            var existing = session.Query<ProjectDocument>()
                .Where(p => p.UserEmail == email && p.Platform == platform)
                .ToList();
            foreach (var e in existing)
                session.Delete(e.Id);

            foreach (var p in projects)
            {
                p.Id = ProjectId(userEmail, platform, p.ProjectId);
                p.UserEmail = email;
                p.Platform = platform;
                session.Store(p);
            }
            session.SaveChanges();
        }

        // ── Checkpoints ───────────────────────────────────────────────────────────
        public CheckpointData LoadCheckpoint(string? userEmail, string platform)
        {
            using var session = _store.OpenSession();
            var rows = session.Query<CheckpointDocument>()
                .Where(c => c.UserEmail == U(userEmail) && c.Platform == platform)
                .ToList();

            return new CheckpointData
            {
                Completed       = rows.Where(r => r.Status == "completed").Select(r => r.ProjectId).ToList(),
                NoDm            = rows.Where(r => r.Status == "no_dm").Select(r => r.ProjectId).ToList(),
                FilteredBim360  = rows.Where(r => r.Status == "filtered_bim360").Select(r => r.ProjectId).ToList()
            };
        }

        public bool IsCompleted(string? userEmail, string platform, ProjectDocument project)
        {
            using var session = _store.OpenSession();
            var doc = session.Load<CheckpointDocument>(CheckpointId(userEmail, platform, project.ProjectId));
            return doc != null;
        }

        public bool IsNoDm(string? userEmail, string platform, ProjectDocument project)
        {
            using var session = _store.OpenSession();
            var doc = session.Load<CheckpointDocument>(CheckpointId(userEmail, platform, project.ProjectId));
            return doc?.Status == "no_dm";
        }

        private void UpsertCheckpoint(string? userEmail, string platform, string projectId, string status)
        {
            using var session = _store.OpenSession();
            var id = CheckpointId(userEmail, platform, projectId);
            var doc = session.Load<CheckpointDocument>(id);
            if (doc == null)
            {
                doc = new CheckpointDocument { Id = id, UserEmail = U(userEmail), Platform = platform, ProjectId = projectId };
                session.Store(doc);
            }
            doc.Status = status;
            doc.MarkedAt = DateTime.UtcNow;
            session.SaveChanges();
        }

        public void MarkCompleted(string? userEmail, string platform, ProjectDocument project)
            => UpsertCheckpoint(userEmail, platform, project.ProjectId, "completed");

        public void MarkNoDm(string? userEmail, string platform, ProjectDocument project)
            => UpsertCheckpoint(userEmail, platform, project.ProjectId, "no_dm");

        public void MarkFilteredBim360(string? userEmail, string platform, ProjectDocument project)
            => UpsertCheckpoint(userEmail, platform, project.ProjectId, "filtered_bim360");

        public void ResetCheckpoint(string? userEmail, string platform)
        {
            using var session = _store.OpenSession();
            var rows = session.Query<CheckpointDocument>()
                .Where(c => c.UserEmail == U(userEmail) && c.Platform == platform)
                .ToList();
            foreach (var r in rows) session.Delete(r.Id);
            session.SaveChanges();
        }

        public void ResetProjectsCheckpoint(string? userEmail, string platform, IEnumerable<string> projectIds)
        {
            using var session = _store.OpenSession();
            foreach (var pid in projectIds)
                session.Delete(CheckpointId(userEmail, platform, pid));
            session.SaveChanges();
        }

        // ── Export Runs ───────────────────────────────────────────────────────────
        public string CreateRun(string? userEmail, string platform)
        {
            using var session = _store.OpenSession();
            var doc = new ExportRunDocument
            {
                UserEmail = U(userEmail),
                Platform = platform,
                StartedAt = DateTime.UtcNow
            };
            session.Store(doc);
            session.SaveChanges();
            return doc.Id;
        }

        public void CompleteRun(string runId, int total, int success, int noDm, int failed,
            int skipped, int emailsQueued, string? note = null)
        {
            using var session = _store.OpenSession();
            var doc = session.Load<ExportRunDocument>(runId);
            if (doc == null) return;
            doc.CompletedAt = DateTime.UtcNow;
            doc.Total = total;
            doc.Success = success;
            doc.NoDm = noDm;
            doc.Failed = failed;
            doc.Skipped = skipped;
            doc.EmailsQueued = emailsQueued;
            doc.Note = note;
            session.SaveChanges();
        }

        public List<ExportRunDocument> GetRuns(string? userEmail, string platform)
        {
            using var session = _store.OpenSession();
            return session.Query<ExportRunDocument>()
                .Where(r => r.UserEmail == U(userEmail) && r.Platform == platform)
                .OrderByDescending(r => r.StartedAt)
                .ToList();
        }

        public ExportRunDocument? GetRunById(string runId)
        {
            using var session = _store.OpenSession();
            return session.Load<ExportRunDocument>(runId);
        }

        public void DeleteRun(string runId)
        {
            using var session = _store.OpenSession();
            session.Delete(runId);
            session.SaveChanges();
        }

        // ── Error Logs ────────────────────────────────────────────────────────────
        public string LogError(string? userEmail, string platform, string? runId,
            ProjectDocument? project, string? errorMessage, string? screenshotPath)
        {
            using var session = _store.OpenSession();
            var doc = new ErrorLogDocument
            {
                UserEmail = U(userEmail),
                Platform = platform ?? "bim360",
                RunId = runId,
                ProjectId = project?.ProjectId,
                ProjectName = project?.Name ?? "unknown",
                ErrorMessage = errorMessage,
                ScreenshotPath = screenshotPath,
                LoggedAt = DateTime.UtcNow
            };
            session.Store(doc);
            session.SaveChanges();
            return doc.Id;
        }

        public List<ErrorLogDocument> GetErrors(string? userEmail, string platform)
        {
            using var session = _store.OpenSession();
            return session.Query<ErrorLogDocument>()
                .Where(e => e.UserEmail == U(userEmail) && e.Platform == platform)
                .OrderByDescending(e => e.LoggedAt)
                .ToList();
        }

        public ErrorLogDocument? GetErrorById(string id)
        {
            using var session = _store.OpenSession();
            return session.Load<ErrorLogDocument>(id);
        }

        public void DeleteError(string id)
        {
            using var session = _store.OpenSession();
            session.Delete(id);
            session.SaveChanges();
        }

        // ── Migration from JSON files (one-time import on startup) ────────────────
        public void MigrateProjectsFromFile(string? userEmail, string platform, string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                var json = File.ReadAllText(filePath);
                dynamic cfg = Newtonsoft.Json.JsonConvert.DeserializeObject(json)!;

                if (cfg.accountAdminUrl != null && GetAdminUrl(userEmail, platform) == null)
                    SetAdminUrl(userEmail, platform, (string)cfg.accountAdminUrl);

                if (cfg.projects != null)
                {
                    var existing = GetProjects(userEmail, platform);
                    if (existing.Count == 0)
                    {
                        var list = new List<ProjectDocument>();
                        foreach (var p in cfg.projects)
                        {
                            var pid = (string?)(p.id ?? p.Id ?? null);
                            if (string.IsNullOrEmpty(pid)) continue;
                            list.Add(new ProjectDocument
                            {
                                ProjectId = pid,
                                Name = (string?)(p.name ?? p.Name ?? "") ?? "",
                                AccountId = (string?)(p.accountId ?? null),
                                Status = "active"
                            });
                        }
                        SaveProjectDocuments(userEmail, platform, list);
                    }
                }
            }
            catch { /* no file or parse error — skip silently */ }
        }

        public void MigrateCheckpointFromFile(string? userEmail, string platform, string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                var cp = LoadCheckpoint(userEmail, platform);
                if (cp.Completed.Count + cp.NoDm.Count + cp.FilteredBim360.Count > 0) return;

                var json = File.ReadAllText(filePath);
                dynamic data = Newtonsoft.Json.JsonConvert.DeserializeObject(json)!;
                var email = U(userEmail);

                if (data.completed != null)
                    foreach (var id in data.completed)
                        UpsertCheckpoint(userEmail, platform, (string)id, "completed");

                if (data.no_dm != null)
                    foreach (var id in data.no_dm)
                        UpsertCheckpoint(userEmail, platform, (string)id, "no_dm");

                if (data.filtered_bim360 != null)
                    foreach (var id in data.filtered_bim360)
                        UpsertCheckpoint(userEmail, platform, (string)id, "filtered_bim360");
            }
            catch { /* skip */ }
        }

        // ── Raw session access (for services that store custom documents) ─────────
        public Raven.Client.Documents.Session.IDocumentSession OpenSession()
            => _store.OpenSession();

        // ── Autodesk OAuth Tokens ─────────────────────────────────────────────────
        private static string TokenId(string? userEmail)
            => $"autodesk-tokens/{U(userEmail)}";

        public void SaveAutodeskToken(AutodeskTokenDocument token)
        {
            using var session = _store.OpenSession();
            token.Id = TokenId(token.UserEmail);
            session.Store(token);
            session.SaveChanges();
        }

        public AutodeskTokenDocument? GetAutodeskToken(string? userEmail)
        {
            using var session = _store.OpenSession();
            return session.Load<AutodeskTokenDocument>(TokenId(userEmail));
        }

        public void DeleteAutodeskToken(string? userEmail)
        {
            using var session = _store.OpenSession();
            session.Delete(TokenId(userEmail));
            session.SaveChanges();
        }
    }
}
