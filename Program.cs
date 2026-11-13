using System.Threading.RateLimiting;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Assets");
builder.Configuration.AddJsonFile(Path.Combine(configDirectory, "appsettings.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
var dbPath = builder.Configuration["Portal:DatabasePath"] ?? Path.Combine(configDirectory, "Data", "portal.db");
var dataDirectory = Path.GetDirectoryName(dbPath) ?? throw new InvalidOperationException("Invalid DatabasePath");
Directory.CreateDirectory(dataDirectory);
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection().SetApplicationName("Assets-WEB")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory)).ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHttpClient("jira", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddRateLimiter(options => {
    options.AddPolicy("clicks", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "Anonymous",
            _ => new FixedWindowRateLimiterOptions {
                PermitLimit = 240,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("handoverSearch", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "Anonymous",
            _ => new FixedWindowRateLimiterOptions {
                PermitLimit = 25, Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, AutoReplenishment = true
            }));
    options.RejectionStatusCode = 429;
});
var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

string ConnectionString() => new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
using (var db = new SqliteConnection(ConnectionString())) {
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS Reports (
          Id INTEGER PRIMARY KEY AUTOINCREMENT, Source TEXT NOT NULL, ObjectId TEXT NOT NULL,
          ObjectKey TEXT NOT NULL, ObjectName TEXT NOT NULL, AttributeId TEXT NOT NULL,
          AttributeName TEXT NOT NULL, OldValue TEXT NOT NULL, ProposedValue TEXT NOT NULL,
          Reason TEXT NOT NULL, Reporter TEXT NOT NULL, CreatedAt TEXT NOT NULL,
          Status TEXT NOT NULL, DecidedBy TEXT, DecidedAt TEXT, Error TEXT
        );
        CREATE TABLE IF NOT EXISTS Settings (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS HandoverProtocols (
          Id INTEGER PRIMARY KEY AUTOINCREMENT, Mode TEXT NOT NULL,
          SourceLogin TEXT NOT NULL, SourceName TEXT NOT NULL,
          TargetLogin TEXT NOT NULL, TargetName TEXT NOT NULL,
          ItemsJson TEXT NOT NULL, Notes TEXT NOT NULL,
          CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL,
          Status TEXT NOT NULL DEFAULT 'AwaitingSignatures',
          CompletedAt TEXT, CompletedBy TEXT
        );
        CREATE INDEX IF NOT EXISTS IX_HandoverProtocols_CreatedAt ON HandoverProtocols(CreatedAt);
        """;
    cmd.ExecuteNonQuery();
    foreach (var column in new[] {
        "Type TEXT NOT NULL DEFAULT 'attribute'",
        "AffectedEmployee TEXT NOT NULL DEFAULT ''",
        "SourceSnapshot TEXT NOT NULL DEFAULT ''",
        "JiraAuditState TEXT NOT NULL DEFAULT 'None'",
        "JiraCommentId TEXT NOT NULL DEFAULT ''",
        "JiraAuditError TEXT NOT NULL DEFAULT ''",
        "AppliedAt TEXT NOT NULL DEFAULT ''"
    }) {
        using var probe = db.CreateCommand();
        probe.CommandText = "SELECT name FROM pragma_table_info('Reports') WHERE name=$name";
        probe.Parameters.AddWithValue("$name", column.Split(' ')[0]);
        if (probe.ExecuteScalar() is null) {
            using var migration = db.CreateCommand();
            migration.CommandText = "ALTER TABLE Reports ADD COLUMN " + column;
            migration.ExecuteNonQuery();
        }
    }
}

var delegations = new TeamDelegations(ConnectionString());
delegations.Initialize();
var audit = new OperationalAudit(ConnectionString(), app.Logger);
audit.Initialize();
var deployments = new DeploymentCoordinator(dataDirectory, app.Environment.ContentRootPath);
// Server-side events are authoritative; browser clicks are separate, untrusted telemetry.
app.Use(async (ctx, next) => {
    if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(ctx); return; }
    var outcome = "Success";
    try {
        await next(ctx);
        if (ctx.Response.StatusCode >= 400) outcome = "HTTP " + ctx.Response.StatusCode;
    } catch {
        outcome = "UnhandledException";
        throw;
    } finally {
        audit.Write(ctx.User.Identity?.Name ?? "Anonymous", "Server",
            ctx.Request.Method, ctx.Request.Path.Value ?? "", outcome,
            ctx.Connection.RemoteIpAddress?.ToString(), ctx.TraceIdentifier, ctx.Response.StatusCode);
    }
});
var access = new AccessControl(ConnectionString(), () => Setting("AdminGroup", "Portal:AdminGroup"), () => app.Configuration.GetSection("Portal:AdminUsers").Get<string[]>() ?? []);
app.Use(async (ctx, next) => {
    if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(ctx); return; }
    ctx.Response.Headers.CacheControl = "no-store";
    if (ctx.Request.Path == "/api/me") { await next(ctx); return; }
    if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.StatusCode = 401; return; }
    var decision = access.Check(ctx.User);
    if (!decision.Allowed) {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "Portal access denied. Ask the administrator to grant access.", reason = decision.Reason });
        return;
    }
    await next(ctx);
});

string? Setting(string key, string fallback) {
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT Value FROM Settings WHERE Key=$key";
    cmd.Parameters.AddWithValue("$key", key);
    return cmd.ExecuteScalar() as string ?? app.Configuration[fallback];
}
string[] AllowedAttributes() {
    var stored = Setting("AllowedCorrectionAttributeIds", "Jira:AllowedCorrectionAttributeIds");
    if (stored is null) return app.Configuration.GetSection("Jira:AllowedCorrectionAttributeIds").Get<string[]>() ?? [];
    return stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
bool IsAdmin(ClaimsPrincipal user) => access.Check(user).Admin;
bool CanHandover(ClaimsPrincipal user) {
    if (user.Identity is not WindowsIdentity identity || !access.Check(user).Allowed) return false;
    if (IsAdmin(user)) return true;
    var configured = Setting("HandoverGroup", "Portal:HandoverGroup");
    if (string.IsNullOrWhiteSpace(configured)) return false;
    try {
        var sid = configured.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            ? new SecurityIdentifier(configured)
            : (SecurityIdentifier)new NTAccount(configured).Translate(typeof(SecurityIdentifier));
        return new WindowsPrincipal(identity).IsInRole(sid);
    } catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) {
        app.Logger.LogWarning("Handover AD group is not resolvable");
        return false;
    }
}
IResult AuthError(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();
bool IsValidText(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max;
string? JiraBase() {
    var cloudId = Setting("CloudId", "Jira:CloudId");
    var workspaceId = Setting("WorkspaceId", "Jira:WorkspaceId");
    if (Guid.TryParse(cloudId, out _) && Guid.TryParse(workspaceId, out _))
        return $"https://api.atlassian.com/ex/jira/{cloudId}/jsm/assets/workspace/{workspaceId}/v1/";
    // Read the original Bearer-token BaseUrl only when it targets Atlassian.
    var legacy = app.Configuration["Jira:BaseUrl"];
    if (Uri.TryCreate(legacy, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.Host.Equals("api.atlassian.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/ex/jira/", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Contains("/jsm/assets/workspace/", StringComparison.OrdinalIgnoreCase))
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
    return null;
}
string? JiraToken() {
    var encrypted = Setting("ApiToken", "Jira:EncryptedToken");
    if (!string.IsNullOrWhiteSpace(encrypted)) return app.Services.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("JiraApiToken.v1").Unprotect(encrypted);
    return app.Configuration["Jira:Token"];
}
AuthenticationHeaderValue JiraAuthorization() {
    var email = Setting("AccountEmail", "Jira:AccountEmail");
    var token = JiraToken();
    if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Jira credentials are not configured");
    if (!string.IsNullOrWhiteSpace(email))
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
    if (!string.IsNullOrWhiteSpace(app.Configuration["Jira:BaseUrl"]))
        return new AuthenticationHeaderValue("Bearer", token);
    throw new InvalidOperationException("Jira AccountEmail is not configured");
}
async Task<JsonNode> Jira(HttpMethod method, string path, object? body = null) {
    var baseUrl = JiraBase() ?? throw new InvalidOperationException("Jira BaseUrl is not configured");
    using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
    request.Headers.Authorization = JiraAuthorization();
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    if (body is not null) request.Content = JsonContent.Create(body);
    using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("jira");
    using var response = await client.SendAsync(request);
    var content = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Jira HTTP {(int)response.StatusCode}: {content[..Math.Min(content.Length, 300)]}");
    return JsonNode.Parse(content) ?? throw new InvalidOperationException("Empty Jira response");
}
string AttributeValue(JsonNode? attribute) => JiraAssetWorkflow.AttributeText(attribute);
JsonNode? Attribute(JsonNode obj, string id) => obj["attributes"]?.AsArray().FirstOrDefault(a => a?["objectTypeAttributeId"]?.ToString() == id);
object ProjectObject(JsonNode obj) => new {
    id = obj["id"]?.ToString(), key = obj["objectKey"]?.ToString(), name = obj["name"]?.ToString(), type = obj["objectType"]?["name"]?.ToString(),
    attributes = (obj["attributes"]?.AsArray() ?? new JsonArray()).Select(a => new {
        id = a?["objectTypeAttributeId"]?.ToString(),
        name = a?["objectTypeAttribute"]?["name"]?.ToString() ?? a?["objectTypeAttributeId"]?.ToString(),
        value = AttributeValue(a)
    })
};

string LoginKey(string value) => value.Trim().ToUpperInvariant();
TeamMember[] TeamDirectory() {
    var path = Path.Combine(configDirectory, "team.json");
    if (!File.Exists(path)) return [];
    var members = JsonSerializer.Deserialize<TeamMember[]>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    if (members.Length > 10000 || members.Any(p => !IsValidText(p.Login, 256) || !IsValidText(p.Name, 256) || !IsValidText(p.Manager, 256)) ||
        members.Select(p => LoginKey(p.Login)).Distinct().Count() != members.Length)
        throw new InvalidOperationException("Invalid team directory");
    return members;
}
TeamMember[] ReportsTo(string login) {
    var directory = TeamDirectory();
    if (directory.Length == 0) return HandoverDirectory.ManagedTeam(login);
    var result = new List<TeamMember>();
    var visited = new HashSet<string> { LoginKey(login) };
    var queue = new Queue<string>(); queue.Enqueue(login);
    while (queue.TryDequeue(out var manager)) {
        foreach (var person in directory.Where(p => LoginKey(p.Manager) == LoginKey(manager))) {
            if (!visited.Add(LoginKey(person.Login))) continue;
            result.Add(person); queue.Enqueue(person.Login);
        }
    }
    return result.ToArray();
}

TeamMember[] EffectiveTeam(string login) {
    var map = new Dictionary<string,TeamMember>(StringComparer.OrdinalIgnoreCase);
    foreach (var person in ReportsTo(login)) map.TryAdd(person.Login,person);
    // Delegated access does not itself create any additional manager authority.
    foreach (var delegation in delegations.ByDelegate(login)) {
        foreach (var member in ReportsTo(delegation.ManagerLogin)) map.TryAdd(member.Login,member);
    }
    return map.Values.Take(1500).ToArray();
}
bool OwnerMatches(string owner, string login) => owner.Split(',', StringSplitOptions.TrimEntries).Any(v =>
    v.Equals(login, StringComparison.OrdinalIgnoreCase) || v.Equals(login.Split('\\').Last(), StringComparison.OrdinalIgnoreCase));

// Match the Jira reference attribute by Email Address, as in the working deployment.
// The email value must originate from the authenticated Windows identity, never a URL parameter.
string OwnerEmail(ClaimsPrincipal user) {
    var windowsName = user.Identity?.Name ?? throw new InvalidOperationException("Windows identity is missing");
    var login = windowsName.Split('\\').Last();
    string? domain = Setting("UserEmailDomain", "Jira:UserEmailDomain");
    if (string.IsNullOrWhiteSpace(domain)) {
        var serviceEmail = Setting("AccountEmail", "Jira:AccountEmail");
        var at = serviceEmail?.LastIndexOf('@') ?? -1;
        if (at >= 0) domain = serviceEmail![(at + 1)..];
    }
    if (string.IsNullOrWhiteSpace(domain)) {
        // Legacy Jira:BaseUrl + Jira:Token config has no service account email.
        // Use the server's AD DNS suffix; an explicit UserEmailDomain overrides it.
        domain = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
    }
    if (login.Contains('@')) {
        var parts = login.Split('@');
        if (parts.Length != 2) throw new InvalidOperationException("Invalid Windows email identity");
        domain = parts[1];
        login = parts[0];
    }
    if (string.IsNullOrWhiteSpace(domain) || login.Length is < 1 or > 128 ||
        !login.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-') ||
        domain.Length > 253 || !domain.Contains('.') ||
        !domain.All(c => char.IsLetterOrDigit(c) || c is '.' or '-'))
        throw new InvalidOperationException("Cannot derive a safe owner email from the Windows identity and Jira UserEmailDomain");
    return $"{login}@{domain}";
}
string OwnerAql(ClaimsPrincipal user) {
    return $"Osoba_odpowiedzialna.\"Email Address\" = \"{OwnerEmail(user)}\"";
}
async Task<List<JsonNode>> QueryAssets(string? aqlOverride = null) {
    var aql = aqlOverride ?? Setting("Aql", "Jira:Aql");
    if (string.IsNullOrWhiteSpace(aql)) throw new InvalidOperationException("Jira AQL is not configured");
    var entries = new List<JsonNode>();
    for (var page = 1; page <= 100; page++) {
        const int pageSize = 100;
        var result = await Jira(HttpMethod.Post, $"object/aql?startAt={entries.Count}&maxResults={pageSize}&includeAttributes=true", new { qlQuery = aql });
        var batch = JiraAssetWorkflow.Entries(result);
        // Jira may return attribute names separately in objectTypeAttributes.
        var definitions = result["objectTypeAttributes"] as JsonArray ??
                          result["results"]?["objectTypeAttributes"] as JsonArray;
        if (definitions is not null) {
            var names = definitions.Where(d => d is not null)
                .Select(d => (Id: d?["id"]?.ToString(), Name: d?["name"]?.ToString()))
                .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Name))
                .ToDictionary(x => x.Id!, x => x.Name!, StringComparer.Ordinal);
            foreach (var entry in batch) {
                foreach (var attribute in entry?["attributes"] as JsonArray ?? new JsonArray()) {
                    var id = attribute?["objectTypeAttributeId"]?.ToString();
                    if (id is null || !names.TryGetValue(id, out var name) || attribute is not JsonObject item) continue;
                    if (item["objectTypeAttribute"] is not JsonObject definition) {
                        definition = new JsonObject(); item["objectTypeAttribute"] = definition;
                    }
                    definition["name"] = name;
                }
            }
        }
        entries.AddRange(batch.OfType<JsonNode>().Select(x => x.DeepClone()));
        if (!JiraAssetWorkflow.HasMore(result, entries.Count - batch.Count, batch.Count, pageSize)) return entries;
    }
    throw new InvalidOperationException("Asset query limit reached; narrow the configured AQL");
}

app.MapGet("/api/me", (HttpContext ctx, IAntiforgery anti) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var decision = access.Check(ctx.User);
        var tokens = decision.Allowed ? anti.GetAndStoreTokens(ctx) : null;
        return Results.Ok(new { name = ctx.User.Identity.Name, authenticationType = ctx.User.Identity.AuthenticationType,
            allowed = decision.Allowed, accessReason = decision.Reason, bootstrap = decision.Bootstrap, manager = decision.Allowed && EffectiveTeam(ctx.User.Identity.Name!).Length > 0, admin = decision.Admin, handover = decision.Allowed && CanHandover(ctx.User), adminGroup = Setting("AdminGroup", "Portal:AdminGroup"), csrf = tokens?.RequestToken,
            allowedAttributes = AllowedAttributes() });
    } catch (CryptographicException ex) {
        app.Logger.LogError(ex, "Unable to create /api/me antiforgery token. Verify Assets DPAPI key ring and IIS AppPool identity");
        return Results.Problem("Assets antiforgery key ring is unavailable. Check the application error logs.", statusCode: 503);
    } catch (Exception ex) {
        app.Logger.LogError(ex, "/api/me failed. Check SQLite permissions, team.json and Windows identity");
        return Results.Problem("Unable to read the authenticated Assets account. Check the application error logs.", statusCode: 500);
    }
});

app.MapGet("/api/admin/access", (HttpContext ctx) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    return Results.Ok(new { rules = access.Rules(), audit = access.Audit(), bootstrapGroup = Setting("AdminGroup", "Portal:AdminGroup"), bootstrapUsers = app.Configuration.GetSection("Portal:AdminUsers").Get<string[]>() ?? [] });
});
app.MapPost("/api/admin/access", async (HttpContext ctx, IAntiforgery anti, AccessInput input) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    try {
        var rule = access.Resolve(input);
        var current = access.Check(ctx.User);
        if (!current.Bootstrap && ctx.User.Identity is WindowsIdentity identity &&
            (rule.Sid == identity.User?.Value || rule.Kind == "Group" && new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(rule.Sid))))
            return Results.Conflict(new { error = "Use a bootstrap administrator to change a rule affecting your own access." });
        access.Save(rule, ctx.User.Identity!.Name!);
        return Results.Ok(rule);
    } catch (Exception ex) when (ex is ArgumentException or IdentityNotMappedException) {
        return Results.BadRequest(new { error = "Account or SID could not be resolved. Use DOMAIN\\account or a valid SID." });
    }
});
app.MapDelete("/api/admin/access/{sid}", async (HttpContext ctx, IAntiforgery anti, string sid) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    var rule = access.Rules().FirstOrDefault(r => r.Sid == sid);
    if (rule is null) return Results.NotFound();
    if (!access.Check(ctx.User).Bootstrap && ctx.User.Identity is WindowsIdentity identity &&
        (rule.Sid == identity.User?.Value || rule.Kind == "Group" && new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(rule.Sid))))
        return Results.Conflict(new { error = "Use a bootstrap administrator to remove a rule affecting your own access." });
    access.Save(rule, ctx.User.Identity!.Name!, remove: true);
    return Results.NoContent();
});

app.MapGet("/api/admin/settings", (HttpContext ctx) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    return Results.Ok(new {
        siteUrl = Setting("SiteUrl", "Jira:SiteUrl"), accountEmail = Setting("AccountEmail", "Jira:AccountEmail"),
        cloudId = Setting("CloudId", "Jira:CloudId"), workspaceId = Setting("WorkspaceId", "Jira:WorkspaceId"),
        aql = Setting("Aql", "Jira:Aql"), ownerAttributeId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId"),
        allowedCorrectionAttributeIds = AllowedAttributes(), adminGroup = Setting("AdminGroup", "Portal:AdminGroup"),
        handoverGroup = Setting("HandoverGroup", "Portal:HandoverGroup") ?? "",
        tokenConfigured = !string.IsNullOrWhiteSpace(JiraToken())
    });
});

app.MapPost("/api/admin/settings", async (HttpContext ctx, IAntiforgery anti, SettingsInput input) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if (!Uri.TryCreate(input.SiteUrl, UriKind.Absolute, out var site) || site.Scheme != "https" ||
        !site.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase) || site.AbsolutePath != "/" ||
        !IsValidText(input.AccountEmail, 254) || !Guid.TryParse(input.CloudId, out _) ||
        (!string.IsNullOrWhiteSpace(input.WorkspaceId) && !Guid.TryParse(input.WorkspaceId, out _)) ||
        (input.Aql?.Length ?? 0) > 2000 || !IsValidText(input.OwnerAttributeId, 32) ||
        !long.TryParse(input.OwnerAttributeId, out _) || !IsValidText(input.AdminGroup, 256) ||
        input.AllowedCorrectionAttributeIds is null || input.AllowedCorrectionAttributeIds.Length > 50 ||
        input.AllowedCorrectionAttributeIds.Any(id => !long.TryParse(id, out _)) ||
        input.ApiToken?.Length > 1000 || (input.HandoverGroup?.Length ?? 0) > 256)
        return Results.BadRequest(new { error = "Invalid settings" });
    try {
        var group = input.AdminGroup.Trim();
        if (!group.Equals(Setting("AdminGroup", "Portal:AdminGroup"), StringComparison.OrdinalIgnoreCase)) {
            var sid = group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? new SecurityIdentifier(group)
                : (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
            if (!new WindowsPrincipal((WindowsIdentity)ctx.User.Identity!).IsInRole(sid))
                return Results.BadRequest(new { error = "You must belong to the configured admin group" });
        }
        using var db = new SqliteConnection(ConnectionString()); db.Open();
        using var transaction = db.BeginTransaction();
        void Save(string key, string value) {
            using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$value", value); cmd.ExecuteNonQuery();
        }
        Save("SiteUrl", site.GetLeftPart(UriPartial.Authority));
        Save("AccountEmail", input.AccountEmail.Trim());
        Save("CloudId", input.CloudId.Trim());
        Save("WorkspaceId", input.WorkspaceId?.Trim() ?? "");
        Save("Aql", input.Aql?.Trim() ?? "");
        Save("OwnerAttributeId", input.OwnerAttributeId.Trim());
        Save("AllowedCorrectionAttributeIds", string.Join(',', input.AllowedCorrectionAttributeIds.Distinct()));
        Save("AdminGroup", group);
        if (!string.IsNullOrWhiteSpace(input.HandoverGroup)) {
            var handoverGroup = input.HandoverGroup.Trim();
            // Validate the delegated AD group at configuration time.
            _ = handoverGroup.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(handoverGroup)
                : (SecurityIdentifier)new NTAccount(handoverGroup).Translate(typeof(SecurityIdentifier));
        }
        Save("HandoverGroup", input.HandoverGroup?.Trim() ?? "");
        if (!string.IsNullOrWhiteSpace(input.ApiToken)) Save("ApiToken", app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("JiraApiToken.v1").Protect(input.ApiToken.Trim()));
        transaction.Commit();
        app.Logger.LogInformation("Settings updated by {User}", ctx.User.Identity?.Name);
        return Results.Ok();
    } catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) {
        return Results.BadRequest(new { error = "Admin group could not be resolved" });
    }
});

app.MapGet("/api/admin/jira/workspaces", async (HttpContext ctx) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    var siteUrl = Setting("SiteUrl", "Jira:SiteUrl");
    var cloudId = Setting("CloudId", "Jira:CloudId");
    var endpoints = new List<(string Name, Uri Url)>();
    if (Uri.TryCreate(siteUrl, UriKind.Absolute, out var site) &&
        site.Scheme == Uri.UriSchemeHttps && site.IsDefaultPort &&
        string.IsNullOrEmpty(site.UserInfo) &&
        site.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase)) {
        endpoints.Add(("Jira site", new Uri(site.GetLeftPart(UriPartial.Authority) + "/rest/servicedeskapi/assets/workspace")));
    }
    if (Guid.TryParse(cloudId, out var cloudGuid)) {
        endpoints.Add(("Atlassian gateway", new Uri($"https://api.atlassian.com/ex/jira/{cloudGuid:D}/rest/servicedeskapi/assets/workspace")));
    }
    if (endpoints.Count == 0)
        return Results.Problem("Configure a valid Jira site URL or Cloud ID.", statusCode: 503);

    try {
        var authorization = JiraAuthorization();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("jira");
        var failures = new List<string>();
        foreach (var endpoint in endpoints) {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
            request.Headers.Authorization = authorization;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, ctx.RequestAborted);
            var payload = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
            if (response.IsSuccessStatusCode) {
                try {
                    using var document = JsonDocument.Parse(payload);
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty("values", out var values) ||
                        values.ValueKind != JsonValueKind.Array)
                        throw new JsonException("Workspace response does not contain a values array.");
                    return Results.Content(payload, "application/json");
                } catch (JsonException ex) {
                    app.Logger.LogWarning(ex, "Unexpected workspace response from {Endpoint}", endpoint.Name);
                    failures.Add($"{endpoint.Name}: invalid JSON response");
                    continue;
                }
            }
            app.Logger.LogWarning("Jira workspace discovery via {Endpoint} returned HTTP {Status}",
                endpoint.Name, (int)response.StatusCode);
            failures.Add($"{endpoint.Name}: HTTP {(int)response.StatusCode}");
        }
        var details = string.Join("; ", failures);
        app.Logger.LogWarning("Workspace discovery failed: {Reasons}", details);
        return Results.Problem(
            $"Jira workspace discovery failed ({details}). Check API token permissions and authentication method.",
            statusCode: 502);
    } catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) {
        return Results.StatusCode(499);
    } catch (Exception ex) {
        app.Logger.LogError(ex, "Workspace discovery failed before Jira returned a usable response");
        return Results.Problem(
            "Workspace discovery failed before receiving a valid Jira response. Check server logs and encrypted token access.",
            statusCode: 502);
    }
});

app.MapGet("/api/admin/deployment", (HttpContext ctx) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    return Results.Ok(deployments.Status());
});

app.MapPost("/api/admin/deployment", async (HttpContext ctx, IAntiforgery anti, DeploymentInput input) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    var result = deployments.Enqueue(input.Action ?? "", ctx.User.Identity?.Name ?? "");
    audit.Write(ctx.User.Identity?.Name ?? "Unknown", "Server", "Deployment." + input.Action,
        "Assets-KL", result.Accepted ? "Queued" : "Rejected", ctx.Connection.RemoteIpAddress?.ToString(),
        ctx.TraceIdentifier, result.Accepted ? 202 : 409);
    if (!result.Accepted) return Results.Conflict(new { error = result.Message });
    return Results.Accepted("/api/admin/deployment", new { id = result.RequestId, status = "Queued" });
});

// UI clicks are best-effort client telemetry: never record typed values or secrets.
// Privileged actions remain auditable via server-side HTTP/decision events.
app.MapPost("/api/audit/click", async (HttpContext ctx, IAntiforgery anti, ClickInput input) => {
    await anti.ValidateRequestAsync(ctx);
    if (string.IsNullOrWhiteSpace(input.Target) || input.Target.Length > 120 ||
        !System.Text.RegularExpressions.Regex.IsMatch(input.Target, @"^[a-zA-Z0-9_.:\-=]+$") ||
        (input.View?.Length ?? 0) > 50)
        return Results.BadRequest();
    var view = input.View ?? "";
    if (view.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(view, @"^[a-zA-Z0-9_-]+$"))
        return Results.BadRequest();
    audit.Write(ctx.User.Identity?.Name ?? "Anonymous", "Browser", "Click",
        view.Length == 0 ? input.Target : view + ":" + input.Target,
        "Observed", ctx.Connection.RemoteIpAddress?.ToString(), ctx.TraceIdentifier);
    return Results.NoContent();
}).RequireRateLimiting("clicks");

app.MapGet("/api/admin/audit", (HttpContext ctx, string? actor, string? action, string? since, int? limit) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    if (actor?.Length > 128 || action?.Length > 80 || since?.Length > 40)
        return Results.BadRequest();
    if (!string.IsNullOrWhiteSpace(since) && !DateTimeOffset.TryParse(since, out _))
        return Results.BadRequest(new { error = "Invalid since UTC timestamp" });
    DateTimeOffset? start = string.IsNullOrWhiteSpace(since) ? null : DateTimeOffset.Parse(since);
    return Results.Ok(audit.Query(actor, action, start, Math.Clamp(limit ?? 300, 1, 1000)));
});

app.MapGet("/api/admin/audit/export", (HttpContext ctx, string? actor, string? action, string? since) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    if (actor?.Length > 128 || action?.Length > 80 || since?.Length > 40 ||
        (!string.IsNullOrWhiteSpace(since) && !DateTimeOffset.TryParse(since, out _)))
        return Results.BadRequest();
    DateTimeOffset? start = string.IsNullOrWhiteSpace(since) ? null : DateTimeOffset.Parse(since);
    return Results.File(OperationalAudit.Csv(audit.Query(actor, action, start, 10000)),
        "text/csv; charset=utf-8", $"Assets-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv");
});

// Handover inventory is isolated from regular self-service assets.
// Full Jira catalog access requires the explicit AD handover role or administrator.
app.MapGet("/api/handover/users", async (HttpContext ctx, string? q) => {
    if (!CanHandover(ctx.User)) return AuthError(ctx);
    if (q is null || q.Trim().Length is < 2 or > 80) return Results.BadRequest(new { error = "Enter at least 2 characters" });
    try {
        var people = await Task.Run(() => HandoverDirectory.Search(q), ctx.RequestAborted);
        return Results.Ok(people);
    } catch (Exception ex) {
        app.Logger.LogError(ex, "AD handover directory search failed");
        return Results.Problem("Nie mozna przeszukac AD. Sprawdz polaczenie z DC i uprawnienia konta app pool.", statusCode: 502);
    }
}).RequireRateLimiting("handoverSearch");

string SafeOwnerAql(HandoverPerson person) {
    var mail = person.Email;
    if (mail.Length > 254 || !System.Text.RegularExpressions.Regex.IsMatch(mail,
            @"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}$"))
        throw new InvalidOperationException("The AD email cannot be used in Jira AQL safely");
    return $"Osoba_odpowiedzialna.\"Email Address\" = \"{mail}\"";
}
bool IsEquipment(JsonNode obj) {
    var name = obj["objectType"]?["name"]?.ToString() ?? "";
    return !System.Text.RegularExpressions.Regex.IsMatch(name,
        @"^(user|users|person|persons|employee|employees|pracownik|pracownicy|uzytkownik|użytkownik|osoba|osoby)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}
object HandoverAsset(JsonNode obj) => new {
    id = obj["id"]?.ToString(),
    key = obj["objectKey"]?.ToString(),
    name = obj["name"]?.ToString() ?? obj["label"]?.ToString(),
    type = obj["objectType"]?["name"]?.ToString(),
    owner = AttributeValue(Attribute(obj, Setting("OwnerAttributeId", "Jira:OwnerAttributeId") ?? "")),
    serial = (obj["attributes"] as JsonArray ?? new JsonArray())
        .FirstOrDefault(a => (a?["objectTypeAttribute"]?["name"]?.ToString() ?? "")
            .Contains("seryj", StringComparison.OrdinalIgnoreCase)) is JsonNode serialAttr
        ? AttributeValue(serialAttr) : ""
};

app.MapGet("/api/handover/assets", async (HttpContext ctx, string? q, string? owner) => {
    if (!CanHandover(ctx.User)) return AuthError(ctx);
    var query = (q ?? "").Trim();
    if (query.Length > 100 || query.Any(char.IsControl) || (string.IsNullOrWhiteSpace(owner) && query.Length < 2))
        return Results.BadRequest(new { error = "Wpisz co najmniej 2 znaki lub wybierz pracownika." });
    try {
        HandoverPerson? person = null;
        if (!string.IsNullOrWhiteSpace(owner))
            person = await Task.Run(() => HandoverDirectory.Resolve(owner), ctx.RequestAborted);
        var aql = person is null ? "objectId > 0" : SafeOwnerAql(person);
        var objects = await QueryAssets(aql);
        var available = objects.Where(IsEquipment)
            .Where(o => query.Length == 0 ||
                (o["objectKey"]?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (o["name"]?.ToString() ?? o["label"]?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (o["objectType"]?["name"]?.ToString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (o["attributes"]?.ToJsonString() ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(101).ToList();
        return Results.Ok(new { items = available.Take(100).Select(HandoverAsset),
            truncated = available.Count > 100, owner = person?.Login });
    } catch (Exception ex) {
        app.Logger.LogError(ex, "Handover inventory search failed");
        return Results.Problem("Nie udalo sie pobrac ewidencji z Jira lub wyszukac pracownika w AD.",
            statusCode: 502);
    }
}).RequireRateLimiting("handoverSearch");

app.MapPost("/api/handover/protocols", async (HttpContext ctx, IAntiforgery anti,
    HandoverProtocolInput input) => {
    if (!CanHandover(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if (input.Mode is not ("return" or "transfer") ||
        input.AssetIds is null || input.AssetIds.Length is < 1 or > 20 ||
        input.AssetIds.Distinct(StringComparer.Ordinal).Count() != input.AssetIds.Length ||
        input.AssetIds.Any(s => !long.TryParse(s, out var n) || n <= 0) ||
        (input.Notes?.Length ?? 0) > 2000 ||
        (input.Mode == "return" && string.IsNullOrWhiteSpace(input.SourceLogin)) ||
        (input.Mode == "transfer" && string.IsNullOrWhiteSpace(input.TargetLogin)))
        return Results.BadRequest(new { error = "Invalid handover protocol input" });
    try {
        var source = string.IsNullOrWhiteSpace(input.SourceLogin) ? null :
            await Task.Run(() => HandoverDirectory.Resolve(input.SourceLogin), ctx.RequestAborted);
        var target = string.IsNullOrWhiteSpace(input.TargetLogin) ? null :
            await Task.Run(() => HandoverDirectory.Resolve(input.TargetLogin), ctx.RequestAborted);
        if (source is not null && target is not null &&
            source.Login.Equals(target.Login, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Zrodlowy i docelowy pracownik musza byc rozni." });
        var ownerId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId");
        if (!long.TryParse(ownerId, out _))
            return Results.Problem("Configure Jira OwnerAttributeId", statusCode: 503);
        HashSet<string>? owned = null;
        if (source is not null) {
            owned = (await QueryAssets(SafeOwnerAql(source)))
                .Select(o => o["id"]?.ToString() ?? "").ToHashSet(StringComparer.Ordinal);
        }
        var items = new List<HandoverItem>();
        foreach (var id in input.AssetIds) {
            var obj = await Jira(HttpMethod.Get, "object/" + id);
            if (!IsEquipment(obj)) return Results.BadRequest(new { error = "Object is not equipment" });
            var ownerAttr = Attribute(obj, ownerId!);
            if (ownerAttr is null)
                return Results.Conflict(new { error = "Owner attribute not found on asset " + id });
            var valueCount = JiraAssetWorkflow.ValueCount(ownerAttr);
            if (valueCount > 1)
                return Results.Conflict(new { error = "Asset has multiple owners: " + id });
            if (source is not null && (owned is null || !owned.Contains(id)))
                return Results.Conflict(new { error = "Asset is not assigned to the selected employee: " + id });
            if (source is null && valueCount != 0)
                return Results.Conflict(new { error = "Unassigned inventory search selected an assigned asset: " + id });
            items.Add(new HandoverItem(id,
                obj["objectKey"]?.ToString() ?? id,
                obj["name"]?.ToString() ?? obj["label"]?.ToString() ?? id,
                obj["objectType"]?["name"]?.ToString() ?? "",
                JiraAssetWorkflow.AttributeFingerprint(ownerAttr)));
        }
        using var db = new SqliteConnection(ConnectionString()); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO HandoverProtocols(Mode,SourceLogin,SourceName,TargetLogin,TargetName,
              ItemsJson,Notes,CreatedAt,CreatedBy,Status)
            VALUES($mode,$source,$sourceName,$target,$targetName,$items,$notes,$at,$actor,'AwaitingSignatures')
            """;
        cmd.Parameters.AddWithValue("$mode", input.Mode);
        cmd.Parameters.AddWithValue("$source", source?.Login ?? "");
        cmd.Parameters.AddWithValue("$sourceName", source?.Name ?? "");
        cmd.Parameters.AddWithValue("$target", target?.Login ?? "");
        cmd.Parameters.AddWithValue("$targetName", target?.Name ?? "");
        cmd.Parameters.AddWithValue("$items", JsonSerializer.Serialize(items));
        cmd.Parameters.AddWithValue("$notes", input.Notes?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$actor", ctx.User.Identity?.Name ?? "");
        cmd.ExecuteNonQuery();
        using var idCmd = db.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var protocolId = (long)idCmd.ExecuteScalar()!;
        audit.Write(ctx.User.Identity?.Name ?? "", "Server", "Handover.Created",
            "Protocol:" + protocolId, "AwaitingSignatures", ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.TraceIdentifier, 201);
        return Results.Created("/api/handover/protocols/" + protocolId, new { id = protocolId, status = "AwaitingSignatures" });
    } catch (Exception ex) {
        app.Logger.LogError(ex, "Handover protocol could not be prepared");
        return Results.Problem("Nie udalo sie zapisac protokolu. Zmiany w Jira nie zostaly wykonane.",
            statusCode: 502);
    }
}).RequireRateLimiting("handoverSearch");

app.MapGet("/api/handover/protocols", (HttpContext ctx) => {
    if (!CanHandover(ctx.User)) return AuthError(ctx);
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        SELECT Id,Mode,SourceLogin,SourceName,TargetLogin,TargetName,ItemsJson,
               Notes,CreatedAt,CreatedBy,Status
        FROM HandoverProtocols ORDER BY Id DESC LIMIT 100
        """;
    using var reader = cmd.ExecuteReader();
    var rows = new List<object>();
    while (reader.Read()) {
        var items = JsonSerializer.Deserialize<HandoverItem[]>(reader.GetString(6)) ?? [];
        rows.Add(new { id = reader.GetInt64(0), mode = reader.GetString(1),
            sourceLogin = reader.GetString(2), sourceName = reader.GetString(3),
            targetLogin = reader.GetString(4), targetName = reader.GetString(5),
            items, notes = reader.GetString(7), createdAt = reader.GetString(8),
            createdBy = reader.GetString(9), status = reader.GetString(10) });
    }
    return Results.Ok(rows);
});

app.MapGet("/api/handover/protocols/{id:long}/print", (HttpContext ctx, long id) => {
    if (!CanHandover(ctx.User)) return AuthError(ctx);
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        SELECT Mode,SourceName,SourceLogin,TargetName,TargetLogin,ItemsJson,Notes,
               CreatedAt,CreatedBy,Status FROM HandoverProtocols WHERE Id=$id
        """;
    cmd.Parameters.AddWithValue("$id", id);
    using var reader = cmd.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var items = JsonSerializer.Deserialize<HandoverItem[]>(reader.GetString(5)) ?? [];
    static string E(string? text) => System.Net.WebUtility.HtmlEncode(text ?? "");
    var mode = reader.GetString(0) == "return" ? "ZDANIE SPRZETU" : "PRZEKAZANIE SPRZETU";
    var rows = string.Join("", items.Select(a =>
        "<tr><td>" + E(a.Key) + "</td><td>" + E(a.Name) +
        "</td><td>" + E(a.Type) + "</td></tr>"));
    var html = "<!doctype html><html lang=\"pl\"><head><meta charset=\"utf-8\"/>" +
        "<title>Protokol Assets " + id + "</title>" +
        "<style>body{font:14px Arial;margin:40px;color:#183047}h1{font-size:23px}" +
        "table{border-collapse:collapse;width:100%;margin:25px 0}td,th{border:1px solid #aaa;padding:9px}" +
        ".sign{display:flex;gap:45px;margin-top:80px}.sign div{flex:1;border-top:1px solid #555;padding:12px 0}" +
        ".muted{color:#777;font-size:12px}@media print{button{display:none}}</style></head><body>" +
        "<button onclick=\"window.print()\">Drukuj / zapisz jako PDF</button>" +
        "<h1>PROTOKOL " + mode + " / ASSETS-" + id + "</h1>" +
        "<p>Data UTC: " + E(reader.GetString(7)) +
        "<br>Osoba wydajaca: " + E(reader.GetString(1)) + " (" + E(reader.GetString(2)) + ")" +
        "<br>Osoba odbierajaca: " + E(reader.GetString(3)) + " (" + E(reader.GetString(4)) + ")" +
        "<br>Przygotowal: " + E(reader.GetString(8)) + "</p>" +
        "<table><tr><th>ID</th><th>Sprzet</th><th>Typ</th></tr>" + rows + "</table>" +
        "<p>Uwagi: " + E(reader.GetString(6)) + "</p>" +
        "<div class=\"sign\"><div>Podpis wydajacego / zdającego</div>" +
        "<div>Podpis odbierajacego / magazynu</div></div>" +
        "<p class=\"muted\">Status: " + E(reader.GetString(9)) +
        ". Wydruk nie wykonuje automatycznie zmian przypisania w Jira; wymaga podpisow.</p>" +
        "</body></html>";
    return Results.Content(html, "text/html; charset=utf-8");
});

app.MapGet("/api/assets", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var ownerId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId");
        if (string.IsNullOrWhiteSpace(ownerId)) return Results.Problem("Configure Jira OwnerAttributeId", statusCode: 503);
        // Jira reference owner values are not Windows logins. Use Jira's
        // validated Email Address reference query, like the working build.
        var entries = await QueryAssets(OwnerAql(ctx.User));
        return Results.Ok(entries.Select(o => ProjectObject(o!)));
    } catch (Exception ex) { app.Logger.LogError(ex, "Asset query failed"); return Results.Problem("Nie udalo sie pobrac danych Jira.", statusCode: 502); }
});


app.MapGet("/api/team", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var actor = ctx.User.Identity.Name!;
        var people = EffectiveTeam(actor);
        if (people.Length == 0) return Results.Ok(new { people = Array.Empty<object>(), equipment = Array.Empty<object>(), error = "No AD manager hierarchy, configured team.json or substitute assignment for this user." });
        var ownerId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId");
        if (!long.TryParse(ownerId,out _)) return Results.Problem("Configure Jira OwnerAttributeId", statusCode:503);
        var delegatedRoots = delegations.ByDelegate(actor).Select(x=>x.ManagerLogin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directTeam = ReportsTo(actor).Select(p=>p.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var equipment = new System.Collections.Concurrent.ConcurrentBag<object>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(3);
        var tasks = people.Select(async person => {
            await gate.WaitAsync(ctx.RequestAborted);
            try {
                // AQL reference lookup by actual AD mail, never raw displayValue comparison.
                var ad = await Task.Run(()=>HandoverDirectory.Resolve(person.Login),ctx.RequestAborted);
                var objects = await QueryAssets(SafeOwnerAql(ad));
                foreach (var obj in objects.Where(IsEquipment)) {
                    equipment.Add(new {
                        id=obj["id"]?.ToString(), key=obj["objectKey"]?.ToString(),
                        name=obj["name"]?.ToString() ?? obj["label"]?.ToString(),
                        owner=person.Login, type=obj["objectType"]?["name"]?.ToString(),
                        attributes=(obj["attributes"]?.AsArray() ?? new JsonArray()).Select(a=>new {
                            id=a?["objectTypeAttributeId"]?.ToString(),
                            name=a?["objectTypeAttribute"]?["name"]?.ToString() ?? a?["objectTypeAttributeId"]?.ToString(),
                            value=AttributeValue(a)
                        }).ToArray()
                    });
                }
            } catch (Exception ex) {
                app.Logger.LogWarning(ex,"Team inventory lookup failed for a scoped employee");
                failures.Add(person.Login);
            } finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        if (!failures.IsEmpty) {
            return Results.Problem("Nie udalo sie pobrac sprzetu wszystkich pracownikow. Nie pokazano niepelnej listy. Sprawdz AD/Jira i logi.",
                statusCode:502);
        }
        return Results.Ok(new {
            people=people.Select(p=>new {
                id=p.Login,name=p.Name,role=p.Role,
                manager=LoginKey(p.Manager)==LoginKey(actor) || delegatedRoots.Contains(p.Manager) ? "me" : p.Manager,
                delegated=!directTeam.Contains(p.Login)
            }),
            equipment=equipment.OrderBy(x=>System.Text.Json.JsonSerializer.Serialize(x)).ToArray()
        });
    } catch (Exception ex) {
        app.Logger.LogError(ex,"Team query failed");
        return Results.Problem("Nie udalo sie pobrac zespolu z AD/Jira.",statusCode:502);
    }
});

app.MapGet("/api/team/delegations", (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    return Results.Ok(new {
        own = delegations.ByManager(ctx.User.Identity.Name!),
        grantedToMe = delegations.ByDelegate(ctx.User.Identity.Name!)
    });
});
app.MapPost("/api/team/delegations", async (HttpContext ctx, IAntiforgery anti, DelegateInput input) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    await anti.ValidateRequestAsync(ctx);
    var actor=ctx.User.Identity.Name!;
    if (ReportsTo(actor).Length==0) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(input.Login) || input.Login.Length>128)
        return Results.BadRequest(new{error="Invalid substitute login"});
    try {
        var substitute=await Task.Run(()=>HandoverDirectory.Resolve(input.Login),ctx.RequestAborted);
        var domain=actor.Contains('\\')?actor.Split('\\')[0]:"";
        var canonical=string.IsNullOrWhiteSpace(domain)?substitute.Login:domain+"\\"+substitute.Login;
        if (string.Equals(canonical,actor,StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new{error="Cannot delegate to self"});
        var row=delegations.Add(actor,canonical,actor);
        audit.Write(actor,"Server","TeamDelegate.Add",canonical,"Granted",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,201);
        return Results.Created("/api/team/delegations",row);
    } catch (InvalidOperationException ex) {
        return Results.Conflict(new{error=ex.Message});
    } catch (SqliteException ex) when(ex.SqliteErrorCode==19) {
        return Results.Conflict(new{error="Substitute is already assigned"});
    } catch (Exception ex) {
        app.Logger.LogError(ex,"Failed to resolve substitute");
        return Results.Problem("Nie mozna zweryfikowac wskazanego konta w AD.",statusCode:502);
    }
});
app.MapDelete("/api/team/delegations/{id:long}", async (HttpContext ctx, IAntiforgery anti,long id) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    await anti.ValidateRequestAsync(ctx);
    if (!delegations.Revoke(ctx.User.Identity.Name!,id))return Results.NotFound();
    audit.Write(ctx.User.Identity.Name!,"Server","TeamDelegate.Revoke",id.ToString(),"Revoked",
        ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
    return Results.NoContent();
});

app.MapGet("/api/permissions", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var groups=await Task.Run(()=>HandoverDirectory.DirectGroups(ctx.User.Identity.Name!),ctx.RequestAborted);
        return Results.Ok(new{login=ctx.User.Identity.Name, source="AD direct memberOf",groups});
    } catch(Exception ex) {
        app.Logger.LogError(ex,"AD groups lookup failed");
        return Results.Problem("Nie mozna odczytac uprawnien AD.",statusCode:502);
    }
});
app.MapGet("/api/team/permissions", async (HttpContext ctx,string? login) => {
    if (ctx.User.Identity?.IsAuthenticated!=true) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(login) || login.Length>256) return Results.BadRequest();
    var target=EffectiveTeam(ctx.User.Identity.Name!).FirstOrDefault(x=>
        x.Login.Equals(login,StringComparison.OrdinalIgnoreCase));
    if(target is null)return Results.Forbid();
    try {
        var groups=await Task.Run(()=>HandoverDirectory.DirectGroups(target.Login),ctx.RequestAborted);
        return Results.Ok(new { login=target.Login,name=target.Name,source="AD direct memberOf",groups });
    } catch(Exception ex) {
        app.Logger.LogError(ex,"Team AD groups lookup failed");
        return Results.Problem("Nie mozna odczytac uprawnien AD.",statusCode:502);
    }
});

app.MapPost("/api/reports", async (HttpContext ctx, IAntiforgery anti, ReportInput input) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    await anti.ValidateRequestAsync(ctx);
    var type = input.Type ?? "attribute";
    if (!long.TryParse(input.ObjectId, out var numericId) || numericId <= 0 ||
        type is not ("attribute" or "not-held" or "employee-left" or "assignment") || (input.Reason?.Length ?? 0) > 2000 ||
        (type == "attribute" && (!IsValidText(input.AttributeId, 32) || !IsValidText(input.ProposedValue, 500)))) return Results.BadRequest();
    if (type == "attribute" && !AllowedAttributes().Contains(input.AttributeId!)) return Results.BadRequest(new { error = "Attribute is not permitted for correction" });
    try {
        var obj = await Jira(HttpMethod.Get, $"object/{numericId}");
        var ownerId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId") ?? "";
        if (!long.TryParse(ownerId, out _)) return Results.Problem("Configure Jira OwnerAttributeId", statusCode: 503);
        var owner = AttributeValue(Attribute(obj, ownerId));
        var own = OwnerMatches(owner, ctx.User.Identity.Name!);
        if (!own) {
            // Server-side Jira AQL is authoritative when the owner is an Assets
            // object reference rather than a plain Windows login string.
            var ownedObjects = await QueryAssets(OwnerAql(ctx.User));
            own = ownedObjects.Any(o => o["id"]?.ToString() == input.ObjectId);
        }
        var employees = own ? Array.Empty<TeamMember>() : ReportsTo(ctx.User.Identity.Name!).Where(p => OwnerMatches(owner, p.Login)).ToArray();
        if (!own && employees.Length != 1) return Results.Forbid();
        if (type == "not-held" && !own || (type is "employee-left" or "assignment") && (own || employees.Length != 1)) return Results.Forbid();
        if (type != "attribute" && owner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length != 1)
            return Results.Conflict(new { error = "Multiple owners; assignment requires manual review" });
        var attributeId = type == "attribute" ? input.AttributeId! : ownerId;
        var attr = Attribute(obj, attributeId);
        if (attr is null) return Results.BadRequest(new { error = "Attribute missing" });
        using var db = new SqliteConnection(ConnectionString()); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO Reports(Source,ObjectId,ObjectKey,ObjectName,AttributeId,AttributeName,OldValue,ProposedValue,Reason,Reporter,CreatedAt,Status,Type,AffectedEmployee,SourceSnapshot) VALUES ('Jira', $oid,$key,$name,$aid,$aname,$old,$new,$reason,$user,$at,'Pending',$type,$employee,$snapshot)";
        cmd.Parameters.AddWithValue("$oid", input.ObjectId);
        cmd.Parameters.AddWithValue("$key", obj["objectKey"]?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$name", obj["name"]?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$aid", attributeId);
        cmd.Parameters.AddWithValue("$aname", attr["objectTypeAttribute"]?["name"]?.ToString() ?? attributeId);
        cmd.Parameters.AddWithValue("$old", AttributeValue(attr));
        cmd.Parameters.AddWithValue("$snapshot", JiraAssetWorkflow.AttributeFingerprint(attr));
        cmd.Parameters.AddWithValue("$new", type == "attribute" ? input.ProposedValue!.Trim() : "");
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$employee", employees.FirstOrDefault()?.Name ?? "");
        cmd.Parameters.AddWithValue("$reason", (input.Reason ?? "").Trim());
        cmd.Parameters.AddWithValue("$user", ctx.User.Identity.Name);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        using var idCommand = db.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid()";
        var reportId = (long)idCommand.ExecuteScalar()!;
        return Results.Created($"/api/reports/{reportId}", new { id = reportId });
    } catch (Exception ex) { app.Logger.LogError(ex, "Report creation failed"); return Results.Problem("Nie udalo sie zapisac zgloszenia.", statusCode: 502); }
});

app.MapGet("/api/reports", (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = IsAdmin(ctx.User)
        ? "SELECT * FROM Reports ORDER BY Id DESC LIMIT 200"
        : "SELECT * FROM Reports WHERE Reporter=$user ORDER BY Id DESC LIMIT 100";
    if (!IsAdmin(ctx.User)) cmd.Parameters.AddWithValue("$user", ctx.User.Identity.Name!);
    using var reader = cmd.ExecuteReader();
    var rows = new List<object>();
    while (reader.Read()) rows.Add(new { id = reader.GetInt64(0), source = reader.GetString(1), objectId = reader.GetString(2), key = reader.GetString(3), name = reader.GetString(4), attributeId = reader.GetString(5), attribute = reader.GetString(6), oldValue = reader.GetString(7), proposedValue = reader.GetString(8), reason = reader.GetString(9), reporter = reader.GetString(10), createdAt = reader.GetString(11), status = reader.GetString(12), decidedBy = reader.IsDBNull(13) ? null : reader.GetString(13), decidedAt = reader.IsDBNull(14) ? null : reader.GetString(14), error = reader.IsDBNull(15) ? null : reader.GetString(15), type = reader.GetString(reader.GetOrdinal("Type")), affectedEmployee = reader.GetString(reader.GetOrdinal("AffectedEmployee")),
        jiraAuditState = reader.GetString(reader.GetOrdinal("JiraAuditState")),
        jiraCommentId = reader.GetString(reader.GetOrdinal("JiraCommentId")),
        jiraAuditError = reader.GetString(reader.GetOrdinal("JiraAuditError")),
        appliedAt = reader.GetString(reader.GetOrdinal("AppliedAt")) });
    return Results.Ok(rows);
});

// The Cloud Assets comment endpoint is also used by MC-SIRK. Its request is
// non-idempotent: after a timeout, never automatically replay the comment.
async Task<string> PostJiraAuditComment(long objectId, string html, CancellationToken cancellationToken) {
    var workspaceId = Setting("WorkspaceId", "Jira:WorkspaceId");
    if (!Guid.TryParse(workspaceId, out var guid))
        throw new InvalidOperationException("Jira WorkspaceId is not configured");
    using var request = new HttpRequestMessage(HttpMethod.Post,
        $"https://api.atlassian.com/jsm/assets/workspace/{guid:D}/v1/comment/create");
    request.Headers.Authorization = JiraAuthorization();
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Content = JsonContent.Create(new { objectId, comment = html, role = 0 });
    using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("jira");
    using var response = await client.SendAsync(request, cancellationToken);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException($"Jira Assets comment HTTP {(int)response.StatusCode}");
    var payload = await response.Content.ReadFromJsonAsync<JsonNode>(cancellationToken: cancellationToken);
    var commentId = payload?["id"]?.ToString();
    if (string.IsNullOrWhiteSpace(commentId))
        throw new InvalidOperationException("Jira Assets did not confirm the comment ID");
    return commentId;
}

app.MapPost("/api/reports/{id:long}/decision", async (HttpContext ctx, IAntiforgery anti, long id, DecisionInput input) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if (input.Action is not ("approve" or "reject")) return Results.BadRequest();
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var select = db.CreateCommand();
    select.CommandText = "SELECT ObjectId,AttributeId,OldValue,ProposedValue,Status,Type,SourceSnapshot,ObjectKey,AttributeName,Reason,Reporter FROM Reports WHERE Id=$id";
    select.Parameters.AddWithValue("$id", id);
    using var reader = select.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var objectId = reader.GetString(0);
    var attributeId = reader.GetString(1);
    var oldValue = reader.GetString(2);
    var proposed = reader.GetString(3);
    var status = reader.GetString(4);
    var reportType = reader.GetString(5);
    var snapshot = reader.GetString(6);
    var objectKey = reader.GetString(7);
    var attributeName = reader.GetString(8);
    var reason = reader.GetString(9);
    var reporter = reader.GetString(10);
    reader.Close();
    if (status != "Pending") return Results.Conflict(new { error = $"Report is {status}; no replay is permitted" });
    var actor = ctx.User.Identity!.Name!;
    var timestamp = DateTimeOffset.UtcNow.ToString("O");
    using (var claim = db.CreateCommand()) {
        claim.CommandText = "UPDATE Reports SET Status=$status,DecidedBy=$by,DecidedAt=$at WHERE Id=$id AND Status='Pending'";
        claim.Parameters.AddWithValue("$status", input.Action == "approve" ? "Applying" : "Rejected");
        claim.Parameters.AddWithValue("$by", actor);
        claim.Parameters.AddWithValue("$at", timestamp);
        claim.Parameters.AddWithValue("$id", id);
        if (claim.ExecuteNonQuery() != 1) return Results.Conflict(new { error = "Report processed concurrently" });
    }
    void Persist(string nextStatus, string auditState, string? commentId = null, string? error = null, bool applied = false) {
        using var update = db.CreateCommand();
        update.CommandText = "UPDATE Reports SET Status=$status,JiraAuditState=$audit,JiraCommentId=$comment,JiraAuditError=$error,AppliedAt=CASE WHEN $applied=1 THEN $at ELSE AppliedAt END WHERE Id=$id";
        update.Parameters.AddWithValue("$status", nextStatus);
        update.Parameters.AddWithValue("$audit", auditState);
        update.Parameters.AddWithValue("$comment", commentId ?? "");
        update.Parameters.AddWithValue("$error", error ?? "");
        update.Parameters.AddWithValue("$applied", applied ? 1 : 0);
        update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.ExecuteNonQuery();
    }
    if (input.Action == "reject") {
        app.Logger.LogInformation("Report {ReportId} rejected by {Actor}", id, actor);
        return Results.Ok(new { status = "Rejected" });
    }
    var writeStarted = false;
    try {
        if (!long.TryParse(objectId, out var objectNumber) || objectNumber <= 0)
            throw new InvalidOperationException("Invalid Jira object ID");
        var obj = await Jira(HttpMethod.Get, $"object/{objectId}");
        var attribute = Attribute(obj, attributeId);
        if (attribute is null) {
            Persist("Pending", "None");
            return Results.Conflict(new { error = "Jira attribute no longer exists; review report" });
        }
        var matches = !string.IsNullOrEmpty(snapshot)
            ? JiraAssetWorkflow.AttributeFingerprint(attribute) == snapshot
            : AttributeValue(attribute) == oldValue;
        if (!matches) {
            Persist("Pending", "None");
            return Results.Conflict(new { error = "Jira source changed since report; create a new report" });
        }
        // Owner removal is destructive if multiple references exist.
        if (reportType != "attribute" && JiraAssetWorkflow.ValueCount(attribute) != 1) {
            Persist("Pending", "None");
            return Results.Conflict(new { error = "Multiple or missing owner references; manual review required" });
        }
        var typeId = obj["objectType"]?["id"]?.ToString() ?? obj["objectTypeId"]?.ToString();
        if (string.IsNullOrEmpty(typeId)) throw new InvalidOperationException("Object type missing");
        var values = reportType == "attribute"
            ? new object[] { new { value = proposed } }
            : Array.Empty<object>();
        // A timeout after this point is ambiguous. Never auto-retry a PUT.
        writeStarted = true;
        await Jira(HttpMethod.Put, $"object/{objectId}", new {
            objectTypeId = typeId,
            attributes = new[] { new { objectTypeAttributeId = attributeId, objectAttributeValues = values } }
        });
        var reloaded = await Jira(HttpMethod.Get, $"object/{objectId}");
        if (!JiraAssetWorkflow.IsVerified(Attribute(reloaded, attributeId), reportType, proposed))
            throw new InvalidOperationException("Jira accepted PUT but read-back verification failed");
        // Durable barrier: the attribute update was verified and will never be replayed.
        Persist("AppliedAuditPending", "Pending", applied: true);
        var commentHtml = JiraAssetWorkflow.AuditComment(id, objectKey, attributeName,
            oldValue, proposed, reason, reporter, actor, reportType);
        try {
            var commentId = await PostJiraAuditComment(objectNumber, commentHtml, ctx.RequestAborted);
            Persist("Approved", "Posted", commentId: commentId, applied: true);
            app.Logger.LogInformation("Jira correction and comment completed for report {Id}: {CommentId}", id, commentId);
            return Results.Ok(new { status = "Approved", jiraCommentId = commentId });
        } catch (Exception commentError) {
            app.Logger.LogError(commentError, "Jira updated but audit comment failed for report {Id}", id);
            Persist("AppliedAuditPending", "Pending", error: "Jira comment was not confirmed. Verify comment manually before retry.", applied: true);
            return Results.Problem("Jira change applied and verified, but audit comment was not confirmed. Do not approve again; inspect the report.", statusCode: 502);
        }
    } catch (Exception ex) {
        app.Logger.LogError(ex, "Jira correction failed for report {Id}, writeStarted={WriteStarted}", id, writeStarted);
        if (writeStarted) {
            Persist("NeedsReview", "Review", error: "Remote PUT result is ambiguous or verification failed; manual reconciliation required.");
            return Results.Problem("Jira update result needs manual review. No automatic retry was performed.", statusCode: 502);
        }
        Persist("Pending", "None", error: "Preflight failed; no Jira write attempted.");
        return Results.Problem("Jira preflight failed; no changes were made.", statusCode: 502);
    }
});

app.Run();

record DelegateInput(string Login);
record DeploymentInput(string? Action);
record ClickInput(string Target, string? View);
record ReportInput(string ObjectId, string? AttributeId, string? ProposedValue, string? Reason, string? Type = null);
record TeamMember(string Login, string Name, string Manager, string? Role = null);
record DecisionInput(string Action);
record SettingsInput(string SiteUrl, string AccountEmail, string CloudId, string? WorkspaceId, string? Aql,
    string OwnerAttributeId, string[] AllowedCorrectionAttributeIds, string AdminGroup, string? ApiToken,
    string? HandoverGroup = null);
record HandoverProtocolInput(string Mode, string? SourceLogin, string? TargetLogin, string[] AssetIds, string? Notes);
record HandoverItem(string Id, string Key, string Name, string Type, string OwnerSnapshot);
