using System.Net.Mail;
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
    options.AddPolicy("notifyTest", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.Identity?.Name ?? "Anonymous",
            _ => new FixedWindowRateLimiterOptions {
                PermitLimit = 2, Window = TimeSpan.FromMinutes(5), QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.RejectionStatusCode = 429;
});
builder.Services.AddSingleton(new AnnualReviewStore(new SqliteConnectionStringBuilder {
    DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()));
builder.Services.AddSingleton<PortalNotificationService>(sp =>
    new PortalNotificationService(
        new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString(),
        sp.GetRequiredService<IDataProtectionProvider>(),
        sp.GetRequiredService<ILogger<PortalNotificationService>>()));
builder.Services.AddHostedService<ReviewReminderWorker>();
var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions {
    OnPrepareResponse = context => {
        // After an IIS release swap, old JS/CSS must not be reused silently.
        context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
    }
});

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
        "AppliedAt TEXT NOT NULL DEFAULT ''",
        "DeclaredDeviceType TEXT NOT NULL DEFAULT ''",
        "DeclaredSerial TEXT NOT NULL DEFAULT ''",
        "DeclaredTag TEXT NOT NULL DEFAULT ''"
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

var entitlements = new EntitlementCatalog(ConnectionString());
entitlements.Initialize();
var delegations = new TeamDelegations(ConnectionString());
delegations.Initialize();
var custody = new HandoverLifecycle(ConnectionString());
custody.Initialize();
var evidenceVault = new EvidenceVault(dataDirectory);
var annualReviews = app.Services.GetRequiredService<AnnualReviewStore>();
annualReviews.Initialize();
var notifications = app.Services.GetRequiredService<PortalNotificationService>();
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
    id = obj["id"]?.ToString(), key = obj["objectKey"]?.ToString(), name = obj["name"]?.ToString() ?? obj["label"]?.ToString(), type = obj["objectType"]?["name"]?.ToString(),
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
string OwnerAqlForEmail(string email) {
    if (!System.Text.RegularExpressions.Regex.IsMatch(email,
            @"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}$"))
        throw new InvalidOperationException("The resolved AD email cannot be used safely in Jira AQL");
    return $"Osoba_odpowiedzialna.\"Email Address\" = \"{email}\"";
}

List<(string Email, string Source)> OwnerEmailCandidates(ClaimsPrincipal user) {
    var candidates = new List<(string Email, string Source)>();
    void Add(string? mail, string source) {
        if (string.IsNullOrWhiteSpace(mail)) return;
        var normalized = mail.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized,
                @"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}$"))
            return;
        if (!candidates.Any(x => x.Email.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            candidates.Add((normalized, source));
    }
    try {
        var ad = HandoverDirectory.Resolve(user.Identity?.Name ??
            throw new InvalidOperationException("Windows identity unavailable"));
        Add(ad.Email, "AD.mail");
    } catch (Exception ex) when (ex is System.DirectoryServices.DirectoryServicesCOMException
                              or InvalidOperationException or ArgumentException
                              or System.Runtime.InteropServices.COMException) {
        app.Logger.LogWarning("User AD mail lookup failed: {ErrorType}", ex.GetType().Name);
    }
    try {
        Add(OwnerEmail(user), "WindowsSAM+domain");
    } catch (InvalidOperationException) {
        if (candidates.Count == 0) throw;
    }
    // An email is not required if a configured Jira scope can be filtered
    // by the Windows login stored in OwnerAttributeId (the legacy behavior).
    return candidates;
}

// Match the authenticated AD identity to Jira's canonical user directory,
// following MC-SIRK's /rest/api/3/users/search pagination contract.
// Display names are accepted only when unique within the complete Jira directory.
var jiraIdentityCache=new System.Collections.Concurrent.ConcurrentDictionary<string,
    (DateTimeOffset Expires,string[] Aliases)>(StringComparer.OrdinalIgnoreCase);
async Task<string[]> JiraIdentityAliasesForEmails(IEnumerable<string> emails, string? adDisplayName, CancellationToken cancellationToken) {
    var site = Setting("SiteUrl", "Jira:SiteUrl");
    if (!Uri.TryCreate(site, UriKind.Absolute, out var baseUri) ||
        baseUri.Scheme != Uri.UriSchemeHttps || !baseUri.IsDefaultPort ||
        !baseUri.Host.EndsWith(".atlassian.net", StringComparison.OrdinalIgnoreCase) ||
        baseUri.AbsolutePath != "/" && baseUri.AbsolutePath != "")
        throw new InvalidOperationException("Valid Jira site URL is required for user identity lookup");
    var wanted = emails.Where(x=>!string.IsNullOrWhiteSpace(x))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    if(wanted.Count==0)return [];
    var cacheKey=string.Join("|",site,adDisplayName??"",
        string.Join(";",wanted.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)));
    if(jiraIdentityCache.TryGetValue(cacheKey,out var cached) &&
       cached.Expires>DateTimeOffset.UtcNow)return cached.Aliases;
    var users = new List<(string Id,string Email,string Name)>();
    using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("jira");
    const int size=100;
    const int maxUsers=10000;
    var complete=false;
    for(var offset=0;offset<maxUsers;offset+=size) {
        var uri = new Uri(baseUri, "rest/api/3/users/search?startAt="+offset+"&maxResults="+size);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization=JiraAuthorization();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response=await client.SendAsync(request,cancellationToken);
        if(!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Jira users lookup HTTP "+(int)response.StatusCode);
        var page=await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken:cancellationToken)
            ?? new JsonArray();
        foreach(var row in page) {
            var id=row?["accountId"]?.ToString();
            if(string.IsNullOrWhiteSpace(id))continue;
            users.Add((id,row?["emailAddress"]?.ToString()??"",row?["displayName"]?.ToString()??""));
        }
        if(page.Count<size){complete=true;break;}
    }
    if(!complete)
        throw new InvalidOperationException("Jira users directory exceeded safe lookup bound");
    var matched=users.Where(x=>wanted.Contains(x.Email))
        .GroupBy(x=>x.Id,StringComparer.Ordinal).Select(x=>x.First()).ToArray();
    // Jira Cloud may hide user emailAddress from API responses.
    // Only use an AD display name when it resolves to exactly one Jira account.
    if(matched.Length==0 && !string.IsNullOrWhiteSpace(adDisplayName)) {
        var candidates=users.Where(x=>string.Equals(x.Name,adDisplayName.Trim(),
            StringComparison.OrdinalIgnoreCase))
            .GroupBy(x=>x.Id,StringComparer.Ordinal).Select(x=>x.First()).ToArray();
        if(candidates.Length==1) matched=candidates;
    }
    var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var user in matched) {
        aliases.Add(user.Id);
        if(!string.IsNullOrWhiteSpace(user.Name) &&
            users.Where(x=>string.Equals(x.Name,user.Name,StringComparison.OrdinalIgnoreCase))
                 .Select(x=>x.Id).Distinct(StringComparer.Ordinal).Count()==1)
            aliases.Add(user.Name);
    }
    var resolved=aliases.ToArray();
    jiraIdentityCache[cacheKey]=(DateTimeOffset.UtcNow.AddMinutes(5),resolved);
    return resolved;
}

// Shared AQL snapshots are backend-only. User authorization and ownership checks
// are performed on each request, never cached across principals.
var aqlCache = new System.Collections.Concurrent.ConcurrentDictionary<string,
    (DateTimeOffset Expires,List<JsonNode> Items)>(StringComparer.Ordinal);
var aqlLocks = new System.Collections.Concurrent.ConcurrentDictionary<string,SemaphoreSlim>(StringComparer.Ordinal);
var aqlTtl = TimeSpan.FromMinutes(3);
string InventoryScopeAql() => "objectType in objectTypeAndChildren(\"Sprzęt użytkownika\")";
async Task<List<JsonNode>> QueryAssets(string? aqlOverride = null, bool force = false) {
    var aql = aqlOverride ?? Setting("Aql", "Jira:Aql");
    if(string.IsNullOrWhiteSpace(aql))
        throw new InvalidOperationException("Jira AQL is not configured");
    // Do not reuse a cached inventory after a Jira connection/config change.
    var key=string.Join("|",Setting("SiteUrl","Jira:SiteUrl"),
        Setting("WorkspaceId","Jira:WorkspaceId"),
        Setting("CloudId","Jira:CloudId"),aql);
    if(!force && aqlCache.TryGetValue(key,out var hit) && hit.Expires>DateTimeOffset.UtcNow)
        return hit.Items;
    var gate=aqlLocks.GetOrAdd(key,_=>new SemaphoreSlim(1,1));
    await gate.WaitAsync();
    try {
        if(!force && aqlCache.TryGetValue(key,out hit) && hit.Expires>DateTimeOffset.UtcNow)
            return hit.Items;
        var items=await FetchAssetsFromJira(aql);
        aqlCache[key]=(DateTimeOffset.UtcNow.Add(aqlTtl),items);
        return items;
    } finally {gate.Release();}
}

async Task<List<JsonNode>> FetchAssetsFromJira(string aql) {
    if (string.IsNullOrWhiteSpace(aql)) throw new InvalidOperationException("Jira AQL is not configured");
    var entries = new List<JsonNode>();
    // Jira may cap totalFilterCount. Use the authoritative count endpoint
    // as MC-SIRK does, and do not silently report partial inventory as complete.
    int? authoritativeTotal=null;
    try {
        var totalResponse=await Jira(HttpMethod.Post,"object/aql/totalcount",new {qlQuery=aql});
        if(int.TryParse(totalResponse["totalCount"]?.ToString(),out var totalCount) && totalCount>=0)
            authoritativeTotal=totalCount;
    } catch(Exception ex) {
        app.Logger.LogWarning(ex,"Jira AQL totalcount unavailable; use bounded page scan");
    }
    var seenIds=new HashSet<string>(StringComparer.Ordinal);
    // MC-SIRK resolves attribute names from each object type when AQL omits them.
    var typeAttributeNames = new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
    for (var page = 1; page <= 100; page++) {
        const int pageSize = 500;
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
        // Resolve missing attribute names before matching Osoba_odpowiedzialna.
        // Only metadata is fetched; ownership remains checked by exact user identity.
        var missingTypes = batch.OfType<JsonNode>()
            .Where(o => o["attributes"] is JsonArray attributes &&
                attributes.Any(a => a?["objectTypeAttribute"]?["name"] is null))
            .Select(o => o["objectType"]?["id"]?.ToString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal).ToArray();
        foreach(var typeId in missingTypes) {
            if(typeAttributeNames.ContainsKey(typeId!))continue;
            var names = new Dictionary<string,string>(StringComparer.Ordinal);
            try {
                var defs = await Jira(HttpMethod.Get, "objecttype/"+Uri.EscapeDataString(typeId!)+"/attributes");
                if(defs is JsonArray attributeDefinitions) {
                    foreach(var definition in attributeDefinitions) {
                        var id=definition?["id"]?.ToString();
                        var name=definition?["name"]?.ToString();
                        if(!string.IsNullOrWhiteSpace(id)&&!string.IsNullOrWhiteSpace(name))
                            names[id]=name;
                    }
                }
            } catch(Exception ex) {
                app.Logger.LogWarning(ex,"Jira object type attribute metadata unavailable for {TypeId}",typeId);
            }
            typeAttributeNames[typeId!]=names;
        }
        foreach(var obj in batch.OfType<JsonNode>()) {
            var typeId=obj["objectType"]?["id"]?.ToString();
            if(typeId is null||!typeAttributeNames.TryGetValue(typeId,out var names))continue;
            foreach(var attr in obj["attributes"] as JsonArray ?? new JsonArray()) {
                if(attr is not JsonObject attribute)continue;
                var id=attr["objectTypeAttributeId"]?.ToString();
                if(id is null||!names.TryGetValue(id,out var name))continue;
                if(attribute["objectTypeAttribute"] is not JsonObject definition) {
                    definition=new JsonObject();
                    attribute["objectTypeAttribute"]=definition;
                }
                definition["name"]=name;
            }
        }
        if(batch.Count==0) {
            if(authoritativeTotal.HasValue && entries.Count<authoritativeTotal.Value)
                throw new InvalidOperationException("Jira AQL returned an empty page before the authoritative total");
            return entries;
        }
        var offset=entries.Count;
        foreach(var obj in batch.OfType<JsonNode>()) {
            var id=obj["id"]?.ToString() ?? obj["objectKey"]?.ToString();
            if(string.IsNullOrWhiteSpace(id) || !seenIds.Add(id))
                throw new InvalidOperationException("Jira AQL pagination returned duplicate or missing asset ID");
            entries.Add(obj.DeepClone());
        }
        if(authoritativeTotal.HasValue) {
            if(entries.Count>=authoritativeTotal.Value)return entries;
        } else {
            // A full page requires a probe even if Jira reports a capped or
            // inconsistent totalFilterCount. Never return a truncated inventory.
            if(batch.Count<pageSize) {
                if(!JiraAssetWorkflow.HasMore(result,offset,batch.Count,pageSize))return entries;
            }
        }
    }
    throw new InvalidOperationException("Asset query limit reached; narrow the configured AQL");
}

// Every candidate must originate from the authenticated Windows account.
// Never perform a tenant-wide fallback that could leak other employees' assets.
async Task<(List<JsonNode> Objects, (string Email,string Source,int Count)[] Matches)> QueryMyAssets(ClaimsPrincipal user, bool refresh = false) {
    var identity=user.Identity?.Name??throw new InvalidOperationException("Windows identity unavailable");
    var emails=OwnerEmailCandidates(user);
    string? adDisplayName=null;
    try {
        adDisplayName=HandoverDirectory.Resolve(identity).Name;
    } catch(Exception ex) {
        app.Logger.LogWarning(ex,"Unable to resolve AD display name for Jira identity mapping");
    }
    var aliases=JiraAssetOwnership.Aliases(identity,emails.Select(x=>x.Email)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var verifiedJiraIdentity=false;
    // Jira Assets Users references are distinct from Jira account IDs.
    // Resolve the AD email to the same stable identity before matching assets.
    try {
        var jiraIdentities=await JiraIdentityAliasesForEmails(emails.Select(x=>x.Email), adDisplayName, CancellationToken.None);
        verifiedJiraIdentity=jiraIdentities.Length>0;
        foreach (var id in jiraIdentities) aliases.Add(id);
    } catch (Exception ex) {
        app.Logger.LogWarning(ex, "Jira accountId lookup unavailable; no unverified identity aliases added");
    }
    var rows=new Dictionary<string,JsonNode>(StringComparer.Ordinal);
    var matches=new List<(string Email,string Source,int Count)>();
    var ownerAttributeId=Setting("OwnerAttributeId","Jira:OwnerAttributeId") ??
        throw new InvalidOperationException("OwnerAttributeId must be configured");
    var configuredAql=Setting("Aql","Jira:Aql");
    // Use MC-SIRK's confirmed equipment subtree when no custom scope is defined.
    // Keep the scoped exact-owner filter; never return unfiltered Jira inventory.
    var equipmentAql="objectType in objectTypeAndChildren(\"Sprzęt użytkownika\")";
    try {
        // Legacy behavior restored: query configured AQL (ALL pages) and filter
        // the specified owner attribute on the server, never in the browser.
        // This retains SAMAccountName / DOMAIN\sam mappings stored by Jira.
        var scope=await QueryAssets(equipmentAql,refresh);
        if(verifiedJiraIdentity && !string.IsNullOrWhiteSpace(adDisplayName)) {
            foreach(var alias in JiraAssetOwnership.UniqueReferencedOwnerAliases(
                scope,ownerAttributeId,adDisplayName))aliases.Add(alias);
        }
        var assigned=scope.Where(obj=>JiraAssetOwnership.Matches(obj,ownerAttributeId,aliases)).ToArray();
        foreach(var obj in assigned) {
            var id=obj["id"]?.ToString();
            if(!string.IsNullOrWhiteSpace(id))rows.TryAdd(id,obj);
        }
        matches.Add((identity,"Equipment AQL + owner attribute",assigned.Length));
    } catch(Exception ex) {
        app.Logger.LogWarning(ex,"Jira equipment AQL unavailable; attempting exact email-reference query");
        matches.Add((identity,"Equipment AQL (unavailable)",0));
    }
    // The owner's authenticated Jira Assets Users reference is evaluated in the
    // complete equipment scope above. Avoid redundant email-reference AQL scans,
    // which add high latency and do not match this Jira schema.
    if(matches.All(x=>x.Source.Contains("unavailable",StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Jira equipment inventory query failed");
    return (rows.Values.ToList(),matches.ToArray());
}

// Admin-only inventory mapping diagnostics. No raw foreign asset ownership data.
app.MapGet("/api/admin/jira/ownership-diagnostics", async (HttpContext ctx) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    try {
        var login=ctx.User.Identity?.Name ?? throw new InvalidOperationException("Missing Windows identity");
        var emails=OwnerEmailCandidates(ctx.User);
        HandoverPerson? ad=null;
        try { ad=HandoverDirectory.Resolve(login); }
        catch(Exception ex) { app.Logger.LogWarning(ex,"Jira diagnostic AD lookup failed"); }
        var aliases=JiraAssetOwnership.Aliases(login,emails.Select(e=>e.Email))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jiraAliasCount=0;
        string identityStatus;
        try {
            var jiraAliases=await JiraIdentityAliasesForEmails(
                emails.Select(e=>e.Email),ad?.Name,ctx.RequestAborted);
            jiraAliasCount=jiraAliases.Length;
            foreach(var alias in jiraAliases) aliases.Add(alias);
            identityStatus=jiraAliasCount>0 ? "resolved" : "not_found";
        } catch(Exception ex) {
            app.Logger.LogWarning(ex,"Jira diagnostic identity lookup failed");
            identityStatus="failed: "+ex.GetType().Name;
        }
        var ownerId=Setting("OwnerAttributeId","Jira:OwnerAttributeId") ?? "";
        var scope=await QueryAssets("objectType in objectTypeAndChildren(\"Sprzęt użytkownika\")");
        var attributeName="Osoba_odpowiedzialna";
        var scopeOwnerAttributes=scope.SelectMany(o=>o["attributes"] as JsonArray ?? new JsonArray())
            .Count(a=>string.Equals(a?["objectTypeAttribute"]?["name"]?.ToString(),
                attributeName,StringComparison.OrdinalIgnoreCase));
        var scopeConfiguredAttributes=scope.SelectMany(o=>o["attributes"] as JsonArray ?? new JsonArray())
            .Count(a=>a?["objectTypeAttributeId"]?.ToString()==ownerId);
        var ownerReferenceAliases=identityStatus=="resolved" && !string.IsNullOrWhiteSpace(ad?.Name)
            ? JiraAssetOwnership.UniqueReferencedOwnerAliases(scope,ownerId,ad.Name)
            : [];
        foreach(var alias in ownerReferenceAliases)aliases.Add(alias);
        var matches=scope.Count(o=>JiraAssetOwnership.Matches(o,ownerId,aliases));
        var examples=new[]{"K24-085","M25-021","M25-022","SD/2022/035"};
        var knownAssets=examples.Select(key=>new {
            key,
            found=scope.Any(o=>string.Equals(o["objectKey"]?.ToString(),key,StringComparison.OrdinalIgnoreCase)
                 || string.Equals(o["label"]?.ToString(),key,StringComparison.OrdinalIgnoreCase)
                 || string.Equals(o["name"]?.ToString(),key,StringComparison.OrdinalIgnoreCase)),
            ownerAttributeFound=scope.Where(o=>string.Equals(o["objectKey"]?.ToString(),key,StringComparison.OrdinalIgnoreCase)
                 || string.Equals(o["label"]?.ToString(),key,StringComparison.OrdinalIgnoreCase))
                .Any(o=>(o["attributes"] as JsonArray ?? new JsonArray()).Any(a=>
                    a?["objectTypeAttributeId"]?.ToString()==ownerId ||
                    string.Equals(a?["objectTypeAttribute"]?["name"]?.ToString(),
                        attributeName,StringComparison.OrdinalIgnoreCase))),
            assignedToCurrentUser=scope.Any(o=>
                (string.Equals(o["objectKey"]?.ToString(),key,StringComparison.OrdinalIgnoreCase)
                || string.Equals(o["label"]?.ToString(),key,StringComparison.OrdinalIgnoreCase))
                && JiraAssetOwnership.Matches(o,ownerId,aliases))
        }).ToArray();
        // Admin-only bounded shape inspection for the four confirmed assets.
        // Do not return unrelated attributes, tokens, or whole Jira objects.
        var ownerSamples=examples.Select(key => {
            var asset=scope.FirstOrDefault(o=>
                string.Equals(o["objectKey"]?.ToString(),key,StringComparison.OrdinalIgnoreCase)
                || string.Equals(o["label"]?.ToString(),key,StringComparison.OrdinalIgnoreCase));
            var attrs=(asset?["attributes"] as JsonArray ?? new JsonArray())
                .Where(a=>a?["objectTypeAttributeId"]?.ToString()==ownerId ||
                    string.Equals(a?["objectTypeAttribute"]?["name"]?.ToString(),
                        attributeName,StringComparison.OrdinalIgnoreCase))
                .Select(a=>new {
                    attributeId=a?["objectTypeAttributeId"]?.ToString(),
                    attributeName=a?["objectTypeAttribute"]?["name"]?.ToString(),
                    values=(a?["objectAttributeValues"] as JsonArray ?? new JsonArray())
                        .Take(8).Select(v=>new {
                            value=v?["value"]?.ToString(),
                            searchValue=v?["searchValue"]?.ToString(),
                            displayValue=v?["displayValue"]?.ToString(),
                            userAccountId=v?["user"]?["accountId"]?.ToString(),
                            referenceId=v?["referencedObject"]?["id"]?.ToString(),
                            referenceKey=v?["referencedObject"]?["objectKey"]?.ToString(),
                            referenceLabel=v?["referencedObject"]?["label"]?.ToString(),
                            referenceType=v?["referencedObject"]?["objectType"]?["name"]?.ToString(),
                            referenceAttributeNames=(v?["referencedObject"]?["attributes"] as JsonArray ?? new JsonArray())
                                .Select(x=>x?["objectTypeAttribute"]?["name"]?.ToString())
                                .Where(x=>!string.IsNullOrWhiteSpace(x)).Take(12).ToArray()
                        }).ToArray()
                }).ToArray();
            return new { key, attributes=attrs };
        }).ToArray();
        return Results.Ok(new {
            identityStatus, jiraAliasCount, adDisplayNameAvailable=!string.IsNullOrWhiteSpace(ad?.Name),
            configuredOwnerAttribute=long.TryParse(ownerId,out _),
            scopeObjects=scope.Count, scopeOwnerAttributes,scopeConfiguredAttributes,matches,
            ownerReferenceResolved=ownerReferenceAliases.Length>0,
            knownAssets, ownerSamples
        });
    } catch(Exception ex) {
        app.Logger.LogError(ex,"Jira ownership diagnostics failed");
        return Results.Problem("Jira ownership diagnostics failed. See server logs.",statusCode:502);
    }
});

app.MapGet("/api/assets/diagnostics", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!access.Check(ctx.User).Allowed) return Results.Forbid();
    try {
        var (items, candidates) = await QueryMyAssets(ctx.User);
        return Results.Ok(new {
            login = ctx.User.Identity.Name,
            sources = candidates.Select(x => new {source = x.Source, email = x.Email, objects = x.Count}),
            totalUniqueObjects = items.Count,
            note = "Only Jira objects owned by the authenticated Windows identity are queried."
        });
    } catch (Exception ex) {
        app.Logger.LogError(ex, "My Assets read-only diagnostics failed");
        return Results.Problem("Nie mozna sprawdzic mapowania AD mail i Jira. Sprawdz logi serwera.", statusCode: 502);
    }
});

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

app.MapPost("/api/admin/access/group", async (HttpContext ctx, IAntiforgery anti, AdminGroupInput input) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if (!IsValidText(input.AdminGroup, 256))
        return Results.BadRequest(new { error = "Provide an AD group or SID." });
    try {
        var group = input.AdminGroup.Trim();
        var sid = group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            ? new SecurityIdentifier(group)
            : (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
        if (ctx.User.Identity is not WindowsIdentity identity || !new WindowsPrincipal(identity).IsInRole(sid))
            return Results.Forbid();
        using var db = new SqliteConnection(ConnectionString()); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO Settings(Key,Value) VALUES('AdminGroup',$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
        cmd.Parameters.AddWithValue("$value", group);
        cmd.ExecuteNonQuery();
        app.Logger.LogInformation("Portal admin group updated by {User}", ctx.User.Identity.Name);
        return Results.Ok();
    } catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) {
        return Results.BadRequest(new { error = "AD group could not be resolved." });
    }
});

app.MapGet("/api/admin/settings", (HttpContext ctx) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    return Results.Ok(new {
        siteUrl = Setting("SiteUrl", "Jira:SiteUrl"), accountEmail = Setting("AccountEmail", "Jira:AccountEmail"),
        cloudId = Setting("CloudId", "Jira:CloudId"), workspaceId = Setting("WorkspaceId", "Jira:WorkspaceId"),
        aql = Setting("Aql", "Jira:Aql"), ownerAttributeId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId"),
        allowedCorrectionAttributeIds = AllowedAttributes(),
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
        !long.TryParse(input.OwnerAttributeId, out _) ||
        input.AllowedCorrectionAttributeIds is null || input.AllowedCorrectionAttributeIds.Length > 50 ||
        input.AllowedCorrectionAttributeIds.Any(id => !long.TryParse(id, out _)) ||
        input.ApiToken?.Length > 1000 || (input.HandoverGroup?.Length ?? 0) > 256)
        return Results.BadRequest(new { error = "Invalid settings" });
    try {
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
async Task<List<JsonNode>> QueryAssetsForPerson(HandoverPerson person,string windowsDomain) {
    var full = string.IsNullOrWhiteSpace(windowsDomain) ? person.Login :
        windowsDomain + "\\" + person.Login;
    var aliases = JiraAssetOwnership.Aliases(full,[person.Email]);
    var ownerId = Setting("OwnerAttributeId","Jira:OwnerAttributeId") ??
        throw new InvalidOperationException("OwnerAttributeId must be configured");
    var configured = Setting("Aql","Jira:Aql");
    var found = new Dictionary<string,JsonNode>(StringComparer.Ordinal);
    if(!string.IsNullOrWhiteSpace(configured)) {
        foreach(var obj in await QueryAssets(configured)) {
            if(!JiraAssetOwnership.Matches(obj,ownerId,aliases))continue;
            var id=obj["id"]?.ToString();
            if(!string.IsNullOrWhiteSpace(id))found.TryAdd(id,obj);
        }
    }
    try {
        foreach(var obj in await QueryAssets(SafeOwnerAql(person))) {
            var id=obj["id"]?.ToString();
            if(!string.IsNullOrWhiteSpace(id))found.TryAdd(id,obj);
        }
    } catch(Exception ex) when(!string.IsNullOrWhiteSpace(configured)) {
        app.Logger.LogWarning(ex,"Optional person email AQL failed; retaining owner-attribute results");
    }
    return found.Values.ToList();
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
        var objects = person is null
            ? await QueryAssets("objectId > 0")
            : await QueryAssetsForPerson(person, ctx.User.Identity!.Name!.Split('\\')[0]);
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
        (input.IntermediateLogin?.Length ?? 0) > 128 ||
        (input.EvidenceMode is not null and not ("paper" or "electronic" or "hybrid")) ||
        (input.Mode == "return" && !string.IsNullOrWhiteSpace(input.IntermediateLogin)) ||
        (input.Mode == "return" && string.IsNullOrWhiteSpace(input.SourceLogin)) ||
        (input.Mode == "transfer" && string.IsNullOrWhiteSpace(input.TargetLogin)))
        return Results.BadRequest(new { error = "Invalid handover protocol input" });
    try {
        var source = string.IsNullOrWhiteSpace(input.SourceLogin) ? null :
            await Task.Run(() => HandoverDirectory.Resolve(input.SourceLogin), ctx.RequestAborted);
        var target = string.IsNullOrWhiteSpace(input.TargetLogin) ? null :
            await Task.Run(() => HandoverDirectory.Resolve(input.TargetLogin), ctx.RequestAborted);
        var intermediary = string.IsNullOrWhiteSpace(input.IntermediateLogin) ? null :
            await Task.Run(() => HandoverDirectory.Resolve(input.IntermediateLogin), ctx.RequestAborted);
        if (intermediary is not null && ((source is not null &&
            source.Login.Equals(intermediary.Login, StringComparison.OrdinalIgnoreCase)) ||
            (target is not null && target.Login.Equals(intermediary.Login, StringComparison.OrdinalIgnoreCase))))
            return Results.BadRequest(new { error = "Kierownik posredniczacy musi byc inna osoba." });
        if (source is not null && target is not null &&
            source.Login.Equals(target.Login, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Zrodlowy i docelowy pracownik musza byc rozni." });
        var ownerId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId");
        if (!long.TryParse(ownerId, out _))
            return Results.Problem("Configure Jira OwnerAttributeId", statusCode: 503);
        HashSet<string>? owned = null;
        if (source is not null) {
            owned = (await QueryAssetsForPerson(source,ctx.User.Identity!.Name!.Split('\\')[0]))
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
        var actor = ctx.User.Identity?.Name ?? "";
        var domain = actor.Contains('\\') ? actor.Split('\\')[0] : "";
        if (string.IsNullOrWhiteSpace(domain)) return Results.Problem(
            "Windows domain-qualified identity is required for custody confirmations", statusCode: 503);
        string Canonical(HandoverPerson? person) =>
            person is null ? "WAREHOUSE" : domain + "\\" + person.Login;
        var evidenceMode = input.EvidenceMode ?? Setting("EvidenceMode","Portal:EvidenceMode") ?? "hybrid";
        if (evidenceMode is not ("paper" or "electronic" or "hybrid"))
            return Results.BadRequest(new { error = "Invalid evidence mode" });
        using var db = new SqliteConnection(ConnectionString()); db.Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO HandoverProtocols(Mode,SourceLogin,SourceName,TargetLogin,TargetName,
              ItemsJson,Notes,CreatedAt,CreatedBy,Status,IntermediateLogin,IntermediateName,EvidenceMode)
            VALUES($mode,$source,$sourceName,$target,$targetName,$items,$notes,$at,$actor,
              'PendingDispatch',$intermediate,$intermediateName,$evidenceMode)
            """;
        cmd.Parameters.AddWithValue("$mode", input.Mode);
        cmd.Parameters.AddWithValue("$source", source?.Login ?? "");
        cmd.Parameters.AddWithValue("$sourceName", source?.Name ?? "");
        cmd.Parameters.AddWithValue("$target", target?.Login ?? "");
        cmd.Parameters.AddWithValue("$targetName", target?.Name ?? "");
        cmd.Parameters.AddWithValue("$items", JsonSerializer.Serialize(items));
        cmd.Parameters.AddWithValue("$notes", input.Notes?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$intermediate", intermediary?.Login ?? "");
        cmd.Parameters.AddWithValue("$intermediateName", intermediary?.Name ?? "");
        cmd.Parameters.AddWithValue("$evidenceMode", evidenceMode);
        cmd.ExecuteNonQuery();
        using var idCmd = db.CreateCommand();
        idCmd.Transaction = tx;
        idCmd.CommandText = "SELECT last_insert_rowid()";
        var protocolId = (long)idCmd.ExecuteScalar()!;
        custody.Create(db,tx,protocolId,Canonical(source),intermediary is null ? "" : Canonical(intermediary),
            Canonical(input.Mode=="return" ? null : target),evidenceMode,actor,
            ctx.Connection.RemoteIpAddress?.ToString() ?? "",
            ctx.Request.Headers.UserAgent.ToString());
        tx.Commit();
        audit.Write(ctx.User.Identity?.Name ?? "", "Server", "Handover.Created",
            "Protocol:" + protocolId, "PendingDispatch", ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.TraceIdentifier, 201);
        return Results.Created("/api/handover/protocols/" + protocolId, new { id = protocolId, status = "PendingDispatch" });
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
            createdBy = reader.GetString(9), status = reader.GetString(10),
            stages = custody.Stages(reader.GetInt64(0)) });
    }
    return Results.Ok(rows);
});



// Annual inventory and rights review: data is collected from current authoritative
// sources, then the user explicitly confirms a SHA-256 snapshot. Not a QES.
app.MapGet("/api/reviews/mine", (HttpContext ctx) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    try {
        return Results.Ok(annualReviews.Ensure(ctx.User.Identity.Name!,
            key=>Setting(key,"Reviews:"+key)));
    } catch(Exception ex) {
        app.Logger.LogError(ex,"Review registration failed");
        return Results.Problem("Nie mozna utworzyc przegladu rocznego.",statusCode:502);
    }
});
async Task<(string Hash,string Json,object Items)> ReadReviewSnapshot(ClaimsPrincipal actor,CancellationToken token,string kind) {
    if(kind=="assets") {
        var (entries, _) = await QueryMyAssets(actor);
        var normalized=entries.Select(e=>new {
            id=e["id"]?.ToString(),key=e["objectKey"]?.ToString(),
            attributes=e["attributes"]?.ToJsonString()
        }).OrderBy(e=>e.id,StringComparer.Ordinal).ToArray();
        var json=JsonSerializer.Serialize(normalized);
        var display=entries.Select(e=>new {
            id=e["id"]?.ToString(),key=e["objectKey"]?.ToString(),
            name=e["name"]?.ToString()??e["label"]?.ToString(),
            type=e["objectType"]?["name"]?.ToString(),
            serial=(e["attributes"] as JsonArray??new JsonArray()).FirstOrDefault(a=>
                (a?["objectTypeAttribute"]?["name"]?.ToString()??"").Contains("seryj",StringComparison.OrdinalIgnoreCase))
                is JsonNode a ? AttributeValue(a) : ""
        }).OrderBy(e=>e.key,StringComparer.Ordinal).ToArray();
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),json,display);
    }
    if(kind=="permissions") {
        var inventory=await ReadEntitlementInventory(actor.Identity!.Name!,false,token);
        // Never allow an annual confirmation if the mandatory on-prem
        // source is unavailable. Connected optional sources are included.
        if(!inventory.Sources.TryGetValue(EntitlementCatalog.LocalAD,out var adState) ||
           !adState.StartsWith("Connected",StringComparison.Ordinal))
            throw new InvalidOperationException("AD entitlements could not be fully verified.");
        var normalized=inventory.Items.Select(x=>new {
            x.Source,x.Key,x.Name,x.Kind,x.Details,x.Via,x.Note
        }).OrderBy(x=>x.Source,StringComparer.Ordinal)
          .ThenBy(x=>x.Key,StringComparer.Ordinal).ToArray();
        var sourceManifest=inventory.Sources.OrderBy(x=>x.Key,StringComparer.Ordinal)
            .Select(x=>new {x.Key,x.Value}).ToArray();
        // The evidence manifest records which sources were connected or
        // unavailable at review time. No volatile Graph fetched-at values.
        var json=JsonSerializer.Serialize(new {items=normalized,sources=sourceManifest});
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant(),
            json,normalized);
    }
    throw new ArgumentException("Invalid annual review type");
}
app.MapGet("/api/reviews/{kind}/snapshot", async (HttpContext ctx,string kind) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    if(kind is not ("assets" or "permissions"))return Results.BadRequest();
    try{
        var snapshot=await ReadReviewSnapshot(ctx.User,ctx.RequestAborted,kind);
        return Results.Ok(new{kind,sha256=snapshot.Hash,items=snapshot.Items,
            source=kind=="assets"?"Jira Assets (assigned objects)":"AD direct memberOf"});
    }catch(Exception ex){
        app.Logger.LogError(ex,"Annual review snapshot failed");
        return Results.Problem("Nie mozna pobrac listy do przegladu.",statusCode:502);
    }
});
app.MapPost("/api/reviews/{kind}/confirm", async (HttpContext ctx, IAntiforgery anti,
    string kind, ReviewConfirmInput input) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    await anti.ValidateRequestAsync(ctx);
    if(!input.Confirmed || kind is not ("assets" or "permissions") ||
       string.IsNullOrWhiteSpace(input.ExpectedHash) || input.ExpectedHash.Length!=64 ||
       !input.ExpectedHash.All(Uri.IsHexDigit))
       return Results.BadRequest(new{error="Review must be based on the inspected snapshot"});
    try {
        var login=ctx.User.Identity.Name!;
        annualReviews.Ensure(login,key=>Setting(key,"Reviews:"+key));
        var snapshot=await ReadReviewSnapshot(ctx.User,ctx.RequestAborted,kind);
        if(!string.Equals(input.ExpectedHash,snapshot.Hash,StringComparison.OrdinalIgnoreCase))
            return Results.Conflict(new{error="Dane zmienily sie od czasu wyswietlenia. Odswiez liste i przejrzyj ponownie."});
        var result=annualReviews.Confirm(login,kind,snapshot.Hash,snapshot.Json,input.Discrepancy);
        audit.Write(login,"Server","Review."+kind,"Annual:"+result.Year,
            input.Discrepancy?"Discrepancy":"Confirmed",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
        return Results.Ok(result);
    }catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
    catch(Exception ex){
        app.Logger.LogError(ex,"Annual review confirmation failed");
        return Results.Problem("Nie mozna potwierdzic przegladu. Zmiana nie zostala zapisana.",statusCode:502);
    }
});
app.MapGet("/api/reviews/{kind}/{year:int}/evidence",(HttpContext ctx,string kind,int year) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    if(kind is not ("assets" or "permissions")||year is <2025 or >2100)
        return Results.BadRequest();
    try{return Results.Ok(annualReviews.Evidence(ctx.User.Identity.Name!,kind,year));}
    catch(KeyNotFoundException){return Results.NotFound();}
});

app.MapGet("/api/team/reviews",(HttpContext ctx) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    var people=EffectiveTeam(ctx.User.Identity.Name!);
    return Results.Ok(people.Select(p=>new {
        p.Login,p.Name,reviews=annualReviews.Ensure(p.Login,key=>Setting(key,"Reviews:"+key))
    }).ToArray());
});
app.MapPost("/api/admin/reviews/manual", async (HttpContext ctx,IAntiforgery anti,
    ReviewManualInput input) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if(string.IsNullOrWhiteSpace(input.Login) || input.Login.Length>256 ||
       input.Year is <2025 or >2100)return Results.BadRequest();
    try {
        if(!evidenceVault.Exists(input.Sha256))
            return Results.BadRequest(new {error="First upload a signed PDF to the evidence vault."});
        var person=await Task.Run(()=>HandoverDirectory.Resolve(input.Login),ctx.RequestAborted);
        var domain=ctx.User.Identity!.Name!.Split('\\')[0];
        var canonical=domain+"\\"+person.Login;
        annualReviews.Ensure(canonical,key=>Setting(key,"Reviews:"+key));
        var result=annualReviews.Manual(canonical,input.Kind,input.Year,
            input.Sha256,input.EvidenceReference,ctx.User.Identity.Name!);
        audit.Write(ctx.User.Identity.Name!,"Server","Review.Manual",
            canonical+"/"+input.Kind+"/"+input.Year,"ArchivedEvidence",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
        return Results.Ok(result);
    } catch(ArgumentException){return Results.BadRequest();}
    catch(InvalidOperationException ex){return Results.Conflict(new{error=ex.Message});}
    catch(Exception ex){app.Logger.LogError(ex,"Manual review confirmation failed");
        return Results.Problem("Nie mozna zapisac dowodu przegladu.",statusCode:502);}
});
app.MapGet("/api/admin/notifications",(HttpContext ctx) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    return Results.Ok(new {
        delivery=notifications.Status(),
        assetsDueMonthDay=Setting("AssetsDueMonthDay","Reviews:AssetsDueMonthDay")??"12-01",
        permissionsDueMonthDay=Setting("PermissionsDueMonthDay","Reviews:PermissionsDueMonthDay")??"12-01",
        evidenceMode=Setting("EvidenceMode","Portal:EvidenceMode")??"hybrid"
    });
});
app.MapPost("/api/admin/notifications", async (HttpContext ctx,IAntiforgery anti,
    NotificationSettingsInput input) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if(input.AssetsDueMonthDay?.Length!=5 || input.PermissionsDueMonthDay?.Length!=5 ||
       !System.Text.RegularExpressions.Regex.IsMatch(input.AssetsDueMonthDay,@"^(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])$") ||
       !System.Text.RegularExpressions.Regex.IsMatch(input.PermissionsDueMonthDay,@"^(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])$") ||
       input.EvidenceMode is not ("paper" or "electronic" or "hybrid"))
       return Results.BadRequest(new{error="Invalid annual review dates or evidence mode"});
    try {
        // Validate dates against a leap year; 02-29 is supported only for leap years.
        foreach(var due in new[]{input.AssetsDueMonthDay,input.PermissionsDueMonthDay}) {
            var month=int.Parse(due[..2]);var day=int.Parse(due[3..]);
            if(day>DateTime.DaysInMonth(2028,month))return Results.BadRequest(new{error="Invalid day of month"});
        }
        notifications.Save(new NotifierSettings(input.Channel,input.Host,input.Port,
            input.FromEmail,input.Username,input.SmtpPassword,input.TeamsWebhook));
        using var db=new SqliteConnection(ConnectionString());db.Open();
        using var tx=db.BeginTransaction();
        foreach(var(k,v) in new[]{
            ("AssetsDueMonthDay",input.AssetsDueMonthDay),
            ("PermissionsDueMonthDay",input.PermissionsDueMonthDay),
            ("EvidenceMode",input.EvidenceMode)
        }){
            using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            cmd.Parameters.AddWithValue("$key",k);cmd.Parameters.AddWithValue("$value",v);cmd.ExecuteNonQuery();
        }
        tx.Commit();
        audit.Write(ctx.User.Identity!.Name!,"Server","NotificationSettings.Update","Notifications","Saved",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
        return Results.Ok();
    } catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
});
app.MapPost("/api/admin/notifications/test", async (HttpContext ctx,IAntiforgery anti,
    NotificationTestInput input) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if(!MailAddress.TryCreate(input.To,out _) || input.To.Length>254 ||
       input.Channel is not ("Smtp" or "Teams"))return Results.BadRequest();
    try {
        await notifications.Send(input.To,"Assets Portal - test powiadomien",
            "Powiadomienie testowe. Nie wymaga dzialania.",input.Channel,ctx.RequestAborted);
        audit.Write(ctx.User.Identity!.Name!,"Server","Notification.Test",input.Channel,"Sent",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
        return Results.Ok(new {status="Sent"});
    } catch(Exception ex) {
        app.Logger.LogError(ex,"Notification test failed");
        return Results.Problem("Wysylka testowa nie powiodla sie. Sprawdz logi serwera.",statusCode:502);
    }
}).RequireRateLimiting("notifyTest");

app.MapPost("/api/evidence", async (HttpContext ctx, IAntiforgery anti) => {
    if(!CanHandover(ctx.User) && !IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if(ctx.Request.ContentType is null ||
        !ctx.Request.ContentType.StartsWith("multipart/form-data",StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new{error="Expected multipart/form-data with PDF field 'file'"});
    if(ctx.Request.ContentLength is > 12_000_000)
        return Results.Problem("File is too large",statusCode:413);
    try {
        var form=await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var file=form.Files.GetFile("file");
        if(file is null)return Results.BadRequest(new{error="Missing PDF field 'file'"});
        var result=await evidenceVault.Save(file,ctx.User.Identity?.Name??"",ctx.RequestAborted);
        audit.Write(ctx.User.Identity?.Name??"","Server","Evidence.Upload",
            result.Sha256,"Archived",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,201);
        return Results.Created("/api/evidence/"+result.Sha256,result);
    } catch(ArgumentException ex) {
        return Results.BadRequest(new{error=ex.Message});
    } catch(Exception ex) {
        app.Logger.LogError(ex,"Evidence archive upload failed");
        return Results.Problem("Nie mozna bezpiecznie zapisac podpisanego PDF.",statusCode:502);
    }
}).RequireRateLimiting("handoverSearch");
app.MapGet("/api/evidence/{sha}", (HttpContext ctx,string sha) => {
    if(!CanHandover(ctx.User)&&!IsAdmin(ctx.User)) return AuthError(ctx);
    if(!evidenceVault.Exists(sha))return Results.NotFound();
    ctx.Response.Headers.CacheControl="no-store";
    ctx.Response.Headers.XContentTypeOptions="nosniff";
    ctx.Response.Headers.ContentSecurityPolicy="sandbox";
    return Results.File(evidenceVault.GetPath(sha),
        "application/pdf","Assets-evidence-"+sha.ToLowerInvariant()+".pdf");
});

app.MapGet("/api/handover/inbox", (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    return Results.Ok(custody.Inbox(ctx.User.Identity.Name!,CanHandover(ctx.User)));
});
app.MapGet("/api/handover/pending-custody", (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    return Results.Ok(custody.PendingWithManager(ctx.User.Identity.Name!,CanHandover(ctx.User)));
});
app.MapPost("/api/handover/protocols/{id:long}/stage/{stage:int}", async (
    HttpContext ctx, IAntiforgery anti, long id, int stage, HandoverActionInput input) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    await anti.ValidateRequestAsync(ctx);
    if (!input.Confirmed) return Results.BadRequest(new { error = "Explicit confirmation required" });
    if (stage is < 1 or > 2) return Results.BadRequest();
    try {
        if(input.Action=="manual" && !evidenceVault.Exists(input.EvidenceSha256))
            return Results.BadRequest(new {error="Archived PDF evidence not found. Upload the signed protocol first."});
        custody.Change(id,stage,input.Action,ctx.User.Identity.Name!,CanHandover(ctx.User),
            ctx.Connection.RemoteIpAddress?.ToString()??"",
            ctx.Request.Headers.UserAgent.ToString(),input.EvidenceSha256??"");
        audit.Write(ctx.User.Identity.Name!,"Server","Custody."+input.Action,
            "ASSETS-"+id+"/stage-"+stage,"Recorded",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
        return Results.Ok(new { id,stage,status="Recorded" });
    } catch(KeyNotFoundException) { return Results.NotFound(); }
    catch(InvalidOperationException ex) { return Results.Conflict(new {error=ex.Message}); }
    catch(ArgumentException) { return Results.BadRequest(); }
});
app.MapGet("/api/handover/protocols/{id:long}/evidence", (HttpContext ctx,long id) => {
    if (ctx.User.Identity?.IsAuthenticated!=true) return Results.Unauthorized();
    var stages = custody.Stages(id);
    if (stages.Length==0)return Results.NotFound();
    var actor=ctx.User.Identity.Name!;
    if (!CanHandover(ctx.User) && !stages.Any(s=>
        string.Equals(s.SenderLogin,actor,StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s.ReceiverLogin,actor,StringComparison.OrdinalIgnoreCase)))
        return Results.Forbid();
    return Results.Ok(new {
        protocolId=id, hashAlgorithm="SHA-256",
        warning="Local hash chain is audit evidence, not a qualified electronic signature or trusted timestamp.",
        chainValid=custody.VerifyChain(id),events=custody.Events(id),stages
    });
});

app.MapGet("/api/handover/protocols/{id:long}/print", (HttpContext ctx, long id) => {
    var stages = custody.Stages(id);
    if(stages.Length==0)return Results.NotFound();
    if(!CanHandover(ctx.User) && !stages.Any(s =>
        string.Equals(s.SenderLogin,ctx.User.Identity?.Name,StringComparison.OrdinalIgnoreCase) ||
        string.Equals(s.ReceiverLogin,ctx.User.Identity?.Name,StringComparison.OrdinalIgnoreCase)))
        return AuthError(ctx);
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
        "<br>Przygotowal: " + E(reader.GetString(8)) +
        "<br>Etapy potwierdzen: " + E(string.Join(" | ", stages.Select(st =>
            st.StageNumber + ": " + st.SenderLogin + " -> " + st.ReceiverLogin + " [" + st.Status + "]"))) + "</p>" +
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
        var (entries, _) = await QueryMyAssets(ctx.User,ctx.Request.Query.ContainsKey("refresh"));
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
        if (!long.TryParse(ownerId,out _)) return Results.Ok(new { people = people.Select(p=>new { id=p.Login, name=p.Name, role=p.Role, manager=LoginKey(p.Manager)==LoginKey(actor) ? "me" : p.Manager, delegated=false }), equipment=Array.Empty<object>(), inventoryComplete=false, inventoryError="Nie skonfigurowano pola wlasciciela Jira." });
        var delegatedRoots = delegations.ByDelegate(actor).Select(x=>x.ManagerLogin).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var directTeam = ReportsTo(actor).Select(p=>p.Login).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Reuse the same complete equipment snapshot as My Assets.
        // Never issue one Jira AQL scan for every AD employee.
        List<JsonNode> scoped;
        try {
            scoped = await QueryAssets(InventoryScopeAql(),ctx.Request.Query.ContainsKey("refresh"));
        } catch(Exception ex) {
            app.Logger.LogWarning(ex, "Team Jira scoped inventory query failed");
            return Results.Ok(new {
                people=people.Select(p=>new {
                    id=p.Login,name=p.Name,role=p.Role,
                    manager=LoginKey(p.Manager)==LoginKey(actor) || delegatedRoots.Contains(p.Manager) ? "me" : p.Manager,
                    delegated=!directTeam.Contains(p.Login)
                }),
                equipment=Array.Empty<object>(), inventoryComplete=false,
                inventoryError="Nie udalo sie pobrac zasobow Jira. Struktura AD jest dostepna."
            });
        }
        var equipment = new System.Collections.Concurrent.ConcurrentBag<object>();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(3);
        var tasks = people.Select(async person => {
            await gate.WaitAsync(ctx.RequestAborted);
            try {
                // Apply the same owner-attribute mapping as the legacy portal.
                // AD mail/reference AQL is additional, not the only source.
                HandoverPerson? ad = null;
                try {
                    ad = await Task.Run(()=>HandoverDirectory.Resolve(person.Login),ctx.RequestAborted);
                } catch(Exception ex) when(scoped.Count>0) {
                    app.Logger.LogWarning(ex,"AD mail resolution unavailable for scoped team entry");
                }
                var aliases = JiraAssetOwnership.Aliases(person.Login,
                    ad is null ? Array.Empty<string>() : new[]{ad.Email});
                var objects = new Dictionary<string,JsonNode>(StringComparer.Ordinal);
                foreach(var obj in scoped.Where(o=>JiraAssetOwnership.Matches(o,ownerId!,aliases))) {
                    var id=obj["id"]?.ToString();
                    if(!string.IsNullOrWhiteSpace(id))objects.TryAdd(id,obj);
                }
                if(ad is not null && !string.IsNullOrWhiteSpace(ad.Name)) {
                    foreach(var reference in JiraAssetOwnership.UniqueReferencedOwnerAliases(
                        scoped,ownerId!,ad.Name)) aliases.Add(reference);
                    foreach(var obj in scoped.Where(o=>JiraAssetOwnership.Matches(o,ownerId!,aliases))) {
                        var id=obj["id"]?.ToString();
                        if(!string.IsNullOrWhiteSpace(id))objects.TryAdd(id,obj);
                    }
                }
                foreach (var obj in objects.Values.Where(IsEquipment)) {
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
        var inventoryComplete = failures.IsEmpty;
        return Results.Ok(new {
            people=people.Select(p=>new {
                id=p.Login,name=p.Name,role=p.Role,
                manager=LoginKey(p.Manager)==LoginKey(actor) || delegatedRoots.Contains(p.Manager) ? "me" : p.Manager,
                delegated=!directTeam.Contains(p.Login)
            }),
            equipment=inventoryComplete ? equipment.OrderBy(x=>System.Text.Json.JsonSerializer.Serialize(x)).ToArray() : Array.Empty<object>(),
            inventoryComplete,
            inventoryError=inventoryComplete ? null : "Nie udalo sie zweryfikowac sprzetu wszystkich pracownikow. Lista AD jest dostepna, ale status sprzetu pozostaje nieznany."
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
        grantedToMe = delegations.ByDelegate(ctx.User.Identity.Name!),
        canManage = ReportsTo(ctx.User.Identity.Name!).Length > 0
    });
});
app.MapGet("/api/team/delegates/search", async (HttpContext ctx,string? q) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    if(ReportsTo(ctx.User.Identity.Name!).Length==0)return Results.Forbid();
    if(q is null || q.Trim().Length is <2 or >80)
        return Results.BadRequest(new{error="Enter at least 2 characters"});
    try {
        var people=await Task.Run(()=>HandoverDirectory.Search(q),ctx.RequestAborted);
        return Results.Ok(people.Select(p=>new {
            login=ctx.User.Identity.Name!.Split('\\')[0]+"\\"+p.Login,
            p.Name,p.Department,p.Email
        }));
    } catch(Exception ex) {
        app.Logger.LogError(ex,"Team delegate user search failed");
        return Results.Problem("Nie mozna przeszukac AD.",statusCode:502);
    }
}).RequireRateLimiting("handoverSearch");

app.MapPost("/api/team/delegations", async (HttpContext ctx, IAntiforgery anti, DelegateInput input) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    await anti.ValidateRequestAsync(ctx);
    var actor=ctx.User.Identity.Name!;
    if (ReportsTo(actor).Length==0) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(input.Login) || input.Login.Length>128)
        return Results.BadRequest(new{error="Invalid substitute login"});
    var actorDomain=actor.Contains('\\')?actor.Split('\\')[0]:"";
    if(input.Login.Contains('\\') &&
       !input.Login.StartsWith(actorDomain+"\\",StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new{error="Only delegates from the same AD domain are supported"});
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

// Returns only the current Windows user's entitlements or their effective
// AD team scope. Hidden entries are filtered ON THE SERVER, not via CSS.
async Task<EntitlementInventoryResponse> ReadEntitlementInventory(string login,bool isAdmin,CancellationToken ct) {
    var entries=new List<EntitlementItem>();
    var status=new Dictionary<string,string>(StringComparer.Ordinal) {
        [EntitlementCatalog.LocalAD]="Not loaded",
        [EntitlementCatalog.Entra]="Not configured",
        [EntitlementCatalog.Exchange]="Exchange delegate export not connected",
        [EntitlementCatalog.Files]="Share/NTFS collector not connected",
        [EntitlementCatalog.SharePoint]="SharePoint direct permissions collector not connected",
        [EntitlementCatalog.Teams]="Not configured",
        [EntitlementCatalog.M365License]="Not configured"
    };
    var now=DateTimeOffset.UtcNow.ToString("O");
    try {
        var ad=await Task.Run(()=>HandoverDirectory.DirectGroupEntries(login),ct);
        entries.AddRange(ad.Select(g=>new EntitlementItem(EntitlementCatalog.LocalAD,
            g.Id,g.Name,"DirectGroup","Active Directory direct memberOf",
            "Direct AD",login,now)));
        status[EntitlementCatalog.LocalAD]="Connected (direct groups only)";
        try {
            var nested=await Task.Run(()=>HandoverDirectory.TransitiveGroupEntries(login),ct);
            var directIds=ad.Select(x=>x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries.AddRange(nested.Where(x=>!directIds.Contains(x.Id)).Select(g=>
                new EntitlementItem(EntitlementCatalog.LocalAD,g.Id,g.Name,
                "NestedGroup","Active Directory nested group membership",
                "Indirect AD (LDAP recursive)",login,now)));
            status[EntitlementCatalog.LocalAD]="Connected (direct + transitive AD groups; excludes primary group)";
        } catch(Exception ex) {
            app.Logger.LogWarning(ex,"Nested AD membership lookup failed");
            status[EntitlementCatalog.LocalAD]="Partial (direct AD only; recursive lookup failed)";
        }
    } catch(Exception ex) {
        app.Logger.LogWarning(ex,"Entitlement AD query failed for scoped user");
        status[EntitlementCatalog.LocalAD]="AD unavailable; inspect server logs";
    }
    var snapshot=entitlements.Imports(login);
    entries.AddRange(snapshot);
    foreach(var source in snapshot.Select(x=>x.Source).Distinct(StringComparer.Ordinal)) {
        var last=snapshot.Where(x=>x.Source==source).Max(x=>x.CapturedAt);
        status[source]="Imported snapshot at "+last+" (not a live query)";
    }
    var tenant=Setting("GraphTenantId","Graph:TenantId");
    var client=Setting("GraphClientId","Graph:ClientId");
    var thumb=Setting("GraphCertificateThumbprint","Graph:CertificateThumbprint");
    if(!string.IsNullOrWhiteSpace(tenant)||!string.IsNullOrWhiteSpace(client)||
       !string.IsNullOrWhiteSpace(thumb)) {
        if(string.IsNullOrWhiteSpace(tenant)||string.IsNullOrWhiteSpace(client)||
           string.IsNullOrWhiteSpace(thumb)) {
            status[EntitlementCatalog.Entra]="Incomplete Graph connector configuration";
        } else {
            try {
                var person=await Task.Run(()=>HandoverDirectory.Resolve(login),ct);
                var graph=await EntitlementGraphReader.ReadAsync(login,person.Email,
                    tenant,client,thumb,ct);
                foreach(var e in graph.Items) {
                    // Live graph data takes precedence over previously imported
                    // cloud snapshots when the same source/key exists.
                    entries.RemoveAll(x=>x.Source==e.Source && x.Key==e.Key);
                    entries.Add(e);
                }
                foreach(var kv in graph.Status)status[kv.Key]=kv.Value;
            }catch(Exception ex) {
                app.Logger.LogError(ex,"Graph entitlement lookup could not start");
                status[EntitlementCatalog.Entra]="Graph connector unavailable; inspect server logs";
            }
        }
    }
    var metadata=entitlements.MetadataAll();
    var list=new List<EntitlementDisplay>();
    foreach(var entry in entries.DistinctBy(e=>e.Source+"\n"+e.Key,
             StringComparer.OrdinalIgnoreCase)) {
        var key=entry.Source+"\n"+entry.Key;
        metadata.TryGetValue(key,out var m);
        if(m?.Hidden==true&&!isAdmin)continue;
        list.Add(new EntitlementDisplay(entry.Source,entry.Key,entry.Name,entry.Kind,
            entry.Details,entry.Via,entry.UserLogin,entry.CapturedAt,
            m?.Note??"",m?.Hidden??false,
            DateTimeOffset.TryParse(entry.CapturedAt,out var stamp) &&
                DateTimeOffset.UtcNow-stamp>TimeSpan.FromDays(7),
            isAdmin?(m?.Hidden==true?"Admin only":"User visible"):"User visible"));
    }
    return new EntitlementInventoryResponse(login,list.ToArray(),status,
        "Not an exhaustive effective-access report: SharePoint item rights, nested on-prem ACLs and Exchange delegate rights require their source collectors.");
}
app.MapGet("/api/entitlements", async (HttpContext ctx,string? login) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    if(!access.Check(ctx.User).Allowed)return Results.Forbid();
    var actor=ctx.User.Identity.Name!;
    var requested=string.IsNullOrWhiteSpace(login)?actor:login.Trim();
    if(requested.Length>256||requested.Any(char.IsControl))return Results.BadRequest();
    if(!string.Equals(requested,actor,StringComparison.OrdinalIgnoreCase) &&
       !EffectiveTeam(actor).Any(p=>p.Login.Equals(requested,StringComparison.OrdinalIgnoreCase)))
        return Results.Forbid();
    try {
        return Results.Ok(await ReadEntitlementInventory(requested,IsAdmin(ctx.User),ctx.RequestAborted));
    }catch(OperationCanceledException)when(ctx.RequestAborted.IsCancellationRequested){return Results.StatusCode(499);}
    catch(Exception ex) {
        app.Logger.LogError(ex,"Entitlement inventory failed");
        return Results.Problem("Nie mozna pobrac ewidencji uprawnien.",statusCode:502);
    }
}).RequireRateLimiting("handoverSearch");

// A stable reference to an entitlement is validated against current source
// inventory; a reported privilege MUST remain in the user's authorized scope.
// A report is never a request to perform automatic AD/Entra/EXO modification.
app.MapPost("/api/reports/entitlements",async(HttpContext ctx,IAntiforgery anti,
    EntitlementReportInput input) => {
    if(ctx.User.Identity?.IsAuthenticated!=true)return Results.Unauthorized();
    if(!access.Check(ctx.User).Allowed)return Results.Forbid();
    await anti.ValidateRequestAsync(ctx);
    var actor=ctx.User.Identity.Name!;
    var target=input.EmployeeLogin?.Trim()??"";
    if(target.Length==0||target.Length>256||target.Any(char.IsControl) ||
       !EntitlementCatalog.ValidItem(input.Source??"",input.Key??"") ||
       input.Kind is not ("unneeded" or "excessive") ||
       string.IsNullOrWhiteSpace(input.Reason) ||
       input.Reason.Length is <10 or >2000)
        return Results.BadRequest(new{error="Invalid entitlement report"});
    if(!target.Equals(actor,StringComparison.OrdinalIgnoreCase) &&
       !EffectiveTeam(actor).Any(p=>p.Login.Equals(target,StringComparison.OrdinalIgnoreCase)))
        return Results.Forbid();
    try {
        var inventory=await ReadEntitlementInventory(target,false,ctx.RequestAborted);
        var row=inventory.Items.FirstOrDefault(x=>
            x.Source==input.Source && x.Key.Equals(input.Key,StringComparison.OrdinalIgnoreCase));
        if(row is null)return Results.Conflict(new{
            error="Nie znaleziono aktywnego uprawnienia w widocznej ewidencji. Odswiez dane."});
        using var db=new SqliteConnection(ConnectionString());db.Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            INSERT INTO Reports(Source,ObjectId,ObjectKey,ObjectName,AttributeId,AttributeName,
                OldValue,ProposedValue,Reason,Reporter,CreatedAt,Status,Type,AffectedEmployee,SourceSnapshot)
            VALUES($source,'',$key,$name,'','Access review',$old,'',$reason,$reporter,
                $at,'Pending',$type,$employee,$snapshot)
            """;
        cmd.Parameters.AddWithValue("$source",row.Source);
        cmd.Parameters.AddWithValue("$key",row.Key);
        cmd.Parameters.AddWithValue("$name",row.Name);
        cmd.Parameters.AddWithValue("$old",row.Details);
        cmd.Parameters.AddWithValue("$reason",input.Reason.Trim());
        cmd.Parameters.AddWithValue("$reporter",actor);
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$type","permission-"+input.Kind);
        cmd.Parameters.AddWithValue("$employee",target);
        cmd.Parameters.AddWithValue("$snapshot",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.Source+"\n"+
                row.Key+"\n"+row.CapturedAt))).ToLowerInvariant());
        cmd.ExecuteNonQuery();
        using var identity=db.CreateCommand();identity.CommandText="SELECT last_insert_rowid()";
        var id=(long)identity.ExecuteScalar()!;
        audit.Write(actor,"Server","Entitlement.Report","Report:"+id,"PendingManualReview",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,201);
        return Results.Created("/api/reports/"+id,new{id,status="Pending",accessChanged=false});
    }catch(Exception ex) {
        app.Logger.LogError(ex,"Entitlement report validation failed");
        return Results.Problem("Nie mozna zweryfikowac uprawnienia w zrodlach.",statusCode:502);
    }
}).RequireRateLimiting("handoverSearch");

app.MapGet("/api/admin/entitlements/metadata",(HttpContext ctx) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    return Results.Ok(entitlements.MetadataAll().Values.OrderBy(x=>x.Source)
        .ThenBy(x=>x.Key).ToArray());
});
app.MapPut("/api/admin/entitlements/metadata",async(HttpContext ctx,IAntiforgery anti,
    EntitlementMetadataInput input) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    try {
        entitlements.Update(input,ctx.User.Identity!.Name!);
        audit.Write(ctx.User.Identity.Name!,"Server","Entitlement.Metadata",
            input.Source+"/"+input.Key,input.Hidden?"Hidden":"Visible",
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
        return Results.Ok(entitlements.Metadata(input.Source,input.Key));
    }catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
}).RequireRateLimiting("handoverSearch");

// Trusted collectors can publish an entire user/source snapshot through an
// administrator-authenticated import. This does not grant app privileges,
// remove memberships, or infer effective file permissions.
app.MapPost("/api/admin/entitlements/import",async(HttpContext ctx,IAntiforgery anti,
    EntitlementImportInput input) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    try {
        entitlements.Import(input,ctx.User.Identity!.Name!);
        audit.Write(ctx.User.Identity.Name!,"Server","Entitlement.Import",
            input.Source+"/"+input.UserLogin,"Rows:"+input.Items.Length,
            ctx.Connection.RemoteIpAddress?.ToString(),ctx.TraceIdentifier,200);
        return Results.Ok(new {imported=input.Items.Length,source=input.Source,user=input.UserLogin,
            status="Snapshot stored; connector-specific source verification required"});
    }catch(ArgumentException ex){return Results.BadRequest(new{error=ex.Message});}
}).RequireRateLimiting("handoverSearch");

app.MapGet("/api/admin/entitlements/graph",(HttpContext ctx) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    var tenant=Setting("GraphTenantId","Graph:TenantId")??"";
    var client=Setting("GraphClientId","Graph:ClientId")??"";
    var thumb=Setting("GraphCertificateThumbprint","Graph:CertificateThumbprint")??"";
    return Results.Ok(new{tenantId=tenant,clientId=client,
        certificateThumbprint=thumb,configured=Guid.TryParse(tenant,out _)&&
        Guid.TryParse(client,out _)&&thumb.Length==40});
});
app.MapPost("/api/admin/entitlements/graph",async(HttpContext ctx,IAntiforgery anti,
    GraphConfigurationInput input) => {
    if(!IsAdmin(ctx.User))return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    var disable=string.IsNullOrWhiteSpace(input.TenantId)&&
        string.IsNullOrWhiteSpace(input.ClientId)&&
        string.IsNullOrWhiteSpace(input.CertificateThumbprint);
    if(!disable&&(!Guid.TryParse(input.TenantId,out _)||
       !Guid.TryParse(input.ClientId,out _)||
       !System.Text.RegularExpressions.Regex.IsMatch(input.CertificateThumbprint??"",
            @"^[a-fA-F0-9]{40}$")))
        return Results.BadRequest(new{error="Tenant ID, Client ID and SHA1 certificate thumbprint must be valid"});
    using var db=new SqliteConnection(ConnectionString());db.Open();
    using var tx=db.BeginTransaction();
    foreach(var kv in new[]{
        ("GraphTenantId",input.TenantId??""),
        ("GraphClientId",input.ClientId??""),
        ("GraphCertificateThumbprint",input.CertificateThumbprint??"")
    }) {
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO Settings(Key,Value) VALUES($key,$value)
            ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value
            """;
        cmd.Parameters.AddWithValue("$key",kv.Item1);
        cmd.Parameters.AddWithValue("$value",kv.Item2.Trim());
        cmd.ExecuteNonQuery();
    }
    tx.Commit();
    audit.Write(ctx.User.Identity!.Name!,"Server","Graph.Configuration","Graph",
        disable?"Disabled":"Configured",ctx.Connection.RemoteIpAddress?.ToString(),
        ctx.TraceIdentifier,200);
    return Results.Ok(new{configured=!disable});
}).RequireRateLimiting("handoverSearch");

app.MapGet("/api/permissions", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var groups=await Task.Run(()=>HandoverDirectory.DirectGroupEntries(ctx.User.Identity.Name!),ctx.RequestAborted);
        return Results.Ok(new{login=ctx.User.Identity.Name, source="AD direct memberOf",groups});
    } catch(Exception ex) {
        app.Logger.LogError(ex,"AD groups lookup failed");
        return Results.Problem("Nie mozna odczytac uprawnien AD.",statusCode:502);
    }
});
// AD reporting hierarchy without Jira dependency. Only the current manager and
// explicitly delegated team viewers can retrieve these Windows logins.
app.MapGet("/api/team/members", (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!access.Check(ctx.User).Allowed) return Results.Forbid();
    var items = EffectiveTeam(ctx.User.Identity.Name!);
    if (items.Length == 0) return Results.Forbid();
    return Results.Ok(items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .Select(p => new { login = p.Login, name = p.Name, role = p.Role }).ToArray());
});

app.MapGet("/api/team/permissions", async (HttpContext ctx,string? login) => {
    if (ctx.User.Identity?.IsAuthenticated!=true) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(login) || login.Length>256) return Results.BadRequest();
    var target=EffectiveTeam(ctx.User.Identity.Name!).FirstOrDefault(x=>
        x.Login.Equals(login,StringComparison.OrdinalIgnoreCase));
    if(target is null)return Results.Forbid();
    try {
        var groups=await Task.Run(()=>HandoverDirectory.DirectGroupEntries(target.Login),ctx.RequestAborted);
        return Results.Ok(new { login=target.Login,name=target.Name,source="AD direct memberOf",groups });
    } catch(Exception ex) {
        app.Logger.LogError(ex,"Team AD groups lookup failed");
        return Results.Problem("Nie mozna odczytac uprawnien AD.",statusCode:502);
    }
});

// A separate self-disclosure flow for devices not listed in Jira Assets.
// Do not require an existing ObjectId and NEVER write to Jira on acceptance.
// EmployeeLogin is authorized by the server (self or effective direct/delegated team).
// List direct AD groups by current delegated manager scope, 10 users per page.
// The client clearly labels that flat filtering covers loaded rows only.
app.MapGet("/api/team/permissions/all", async (HttpContext ctx, int? offset) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!access.Check(ctx.User).Allowed) return Results.Forbid();
    var current = Math.Max(0, offset ?? 0);
    var people = EffectiveTeam(ctx.User.Identity.Name!)
        .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    if (people.Length == 0) return Results.Forbid();
    if (current > people.Length) return Results.BadRequest(new { error = "Invalid page offset" });
    var page = people.Skip(current).Take(10).ToArray();
    var result = new System.Collections.Concurrent.ConcurrentBag<object>();
    var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
    using var throttle = new SemaphoreSlim(3);
    await Task.WhenAll(page.Select(async person => {
        await throttle.WaitAsync(ctx.RequestAborted);
        try {
            var groups = await Task.Run(() => HandoverDirectory.DirectGroupEntries(person.Login),
                ctx.RequestAborted);
            foreach (var group in groups)
                result.Add(new { login = person.Login, person = person.Name, id = group.Id,
                    name = group.Name, source = "AD memberOf" });
        } catch (Exception ex) {
            app.Logger.LogWarning(ex, "Team AD group listing failed for authorized member");
            failures.Add(person.Login);
        } finally { throttle.Release(); }
    }));
    if (!failures.IsEmpty) return Results.Problem(
        "Nie udalo sie odczytac uprawnien wszystkich pracownikow. Nie pokazano niepelnej strony.",
        statusCode: 502);
    return Results.Ok(new {
        peopleLoaded = page.Length, totalPeople = people.Length,
        nextOffset = current + page.Length < people.Length ? (int?)(current + page.Length) : null,
        items = result.OrderBy(x => System.Text.Json.JsonSerializer.Serialize(x)).ToArray()
    });
}).RequireRateLimiting("handoverSearch");

// Report unnecessary, excessive or otherwise undisclosed app access. This is
// human-triaged, not automated AD/Entra privilege modification.
app.MapPost("/api/reports/permissions", async (HttpContext ctx, IAntiforgery anti,
    PermissionReportInput input) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!access.Check(ctx.User).Allowed) return Results.Forbid();
    await anti.ValidateRequestAsync(ctx);
    static bool Valid(string? s, int max, int min = 0) =>
        s is not null && s.Length >= min && s.Length <= max &&
        !s.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));
    var kind = input.Kind?.Trim() ?? "";
    var employee = input.EmployeeLogin?.Trim() ?? "";
    var groupDn = input.GroupDn?.Trim() ?? "";
    var external = input.ExternalAccess?.Trim() ?? "";
    var reason = input.Reason?.Trim() ?? "";
    if (kind is not ("unneeded" or "excessive" or "unlisted") ||
        !Valid(employee, 256, 1) || !Valid(reason, 2000, 10) ||
        !Valid(groupDn, 2048) || !Valid(external, 160) ||
        (kind == "unlisted" && external.Length < 3) ||
        (kind != "unlisted" && groupDn.Length < 4))
        return Results.BadRequest(new { error = "Invalid entitlement report input" });
    var actor = ctx.User.Identity.Name!;
    var own = string.Equals(employee, actor, StringComparison.OrdinalIgnoreCase);
    var subordinate = own ? null : EffectiveTeam(actor).FirstOrDefault(p =>
        string.Equals(p.Login, employee, StringComparison.OrdinalIgnoreCase));
    if (!own && subordinate is null) return Results.Forbid();
    var targetLogin = own ? actor : subordinate!.Login;
    DirectoryGroup? reportedGroup = null;
    if (kind != "unlisted") {
        try {
            var groups = await Task.Run(() => HandoverDirectory.DirectGroupEntries(targetLogin),
                ctx.RequestAborted);
            reportedGroup = groups.FirstOrDefault(g =>
                g.Id.Equals(groupDn, StringComparison.OrdinalIgnoreCase));
            if (reportedGroup is null)
                return Results.Conflict(new { error =
                    "To czlonkostwo nie jest juz bezposrednio przypisane w AD. Odswiez uprawnienia." });
        } catch (Exception ex) {
            app.Logger.LogError(ex, "AD permission report membership verification failed");
            return Results.Problem("Nie mozna zweryfikowac czlonkostwa w AD.", statusCode: 502);
        }
    }
    using var db = new SqliteConnection(ConnectionString());
    db.Open();
    using var cmd = db.CreateCommand();
    cmd.CommandText = """
        INSERT INTO Reports (Source,ObjectId,ObjectKey,ObjectName,AttributeId,AttributeName,
          OldValue,ProposedValue,Reason,Reporter,CreatedAt,Status,Type,AffectedEmployee,SourceSnapshot)
        VALUES ($source,'',$key,$name,'','Uprawnienie',
          $old,'',$reason,$reporter,$time,'Pending',$type,$employee,$snapshot)
        """;
    var label = reportedGroup?.Name ?? external;
    cmd.Parameters.AddWithValue("$source", kind == "unlisted" ? "UserDisclosure" : "AD memberOf");
    cmd.Parameters.AddWithValue("$key", kind == "unlisted" ? "INNY-DOSTEP" : "AD");
    cmd.Parameters.AddWithValue("$name", label);
    cmd.Parameters.AddWithValue("$old", reportedGroup?.Id ?? external);
    cmd.Parameters.AddWithValue("$reason", reason);
    cmd.Parameters.AddWithValue("$reporter", actor);
    cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
    cmd.Parameters.AddWithValue("$type", "permission-" + kind);
    cmd.Parameters.AddWithValue("$employee", targetLogin);
    cmd.Parameters.AddWithValue("$snapshot", reportedGroup is null ? "" :
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reportedGroup.Id))).ToLowerInvariant());
    cmd.ExecuteNonQuery();
    using var idQuery = db.CreateCommand();
    idQuery.CommandText = "SELECT last_insert_rowid()";
    var id = (long)idQuery.ExecuteScalar()!;
    audit.Write(actor, "Server", "Permission.Report", "Report:" + id,
        "PendingManualReview", ctx.Connection.RemoteIpAddress?.ToString(),
        ctx.TraceIdentifier, 201);
    return Results.Created("/api/reports/" + id,
        new { id, status = "Pending", requiresManualReview = true, adModified = false });
}).RequireRateLimiting("handoverSearch");

app.MapPost("/api/reports/discovered", async (HttpContext ctx, IAntiforgery anti, DiscoveredEquipmentInput input) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!access.Check(ctx.User).Allowed) return Results.Forbid();
    await anti.ValidateRequestAsync(ctx);
    static bool HasBadControl(string? value) => value?.Any(char.IsControl) ?? false;
    var name = input.Name?.Trim() ?? "";
    var type = input.EquipmentType?.Trim() ?? "";
    var serial = input.Serial?.Trim() ?? "";
    var tag = input.AssetTag?.Trim() ?? "";
    var description = input.Description?.Trim() ?? "";
    var employee = input.EmployeeLogin?.Trim() ?? "";
    if (name.Length is < 2 or > 120 || type.Length is < 2 or > 80 ||
        serial.Length > 120 || tag.Length > 120 || description.Length is < 10 or > 2000 ||
        employee.Length is < 1 or > 256 || (serial.Length == 0 && tag.Length == 0) ||
        new[] { name, type, serial, tag, employee }.Any(HasBadControl) ||
        description.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) {
        return Results.BadRequest(new { error = "Podaj typ, nazwe, numer seryjny lub identyfikator oraz opis (minimum 10 znakow)." });
    }
    var actor = ctx.User.Identity.Name!;
    var targetIsSelf = string.Equals(employee, actor, StringComparison.OrdinalIgnoreCase);
    var target = targetIsSelf ? null : EffectiveTeam(actor).FirstOrDefault(p =>
        string.Equals(p.Login, employee, StringComparison.OrdinalIgnoreCase));
    if (!targetIsSelf && target is null) return Results.Forbid();
    var targetLogin = targetIsSelf ? actor : target!.Login;
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var tx = db.BeginTransaction();
    using var cmd = db.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = """
        INSERT INTO Reports (Source,ObjectId,ObjectKey,ObjectName,AttributeId,AttributeName,
          OldValue,ProposedValue,Reason,Reporter,CreatedAt,Status,Type,AffectedEmployee,SourceSnapshot,
          DeclaredDeviceType,DeclaredSerial,DeclaredTag)
        VALUES ('EmployeeDisclosure','', $key,$name,'','Dodatkowy sprzet',
          'Brak na liscie', $proposed,$reason,$reporter,$at,'Pending','discovered',$employee,'',
          $type,$serial,$tag)
        """;
    cmd.Parameters.AddWithValue("$key", string.IsNullOrWhiteSpace(tag) ? "NIEUJETY-W-JIRA" : tag);
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$proposed", serial);
    cmd.Parameters.AddWithValue("$reason", description);
    cmd.Parameters.AddWithValue("$reporter", actor);
    cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
    cmd.Parameters.AddWithValue("$employee", targetLogin);
    cmd.Parameters.AddWithValue("$type", type);
    cmd.Parameters.AddWithValue("$serial", serial);
    cmd.Parameters.AddWithValue("$tag", tag);
    cmd.ExecuteNonQuery();
    using var idCommand = db.CreateCommand();
    idCommand.Transaction = tx;
    idCommand.CommandText = "SELECT last_insert_rowid()";
    var id = (long)idCommand.ExecuteScalar()!;
    tx.Commit();
    audit.Write(actor, "Server", "Asset.Discovered", "Disclosure:" + id,
        "PendingManualReview", ctx.Connection.RemoteIpAddress?.ToString(),
        ctx.TraceIdentifier, 201);
    return Results.Created("/api/reports/" + id, new { id, status = "Pending", requiresManualVerification = true });
}).RequireRateLimiting("handoverSearch");

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
            var (ownedObjects, _) = await QueryMyAssets(ctx.User);
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
        appliedAt = reader.GetString(reader.GetOrdinal("AppliedAt")),
        equipmentType = reader.GetString(reader.GetOrdinal("DeclaredDeviceType")),
        serialNumber = reader.GetString(reader.GetOrdinal("DeclaredSerial")),
        assetTag = reader.GetString(reader.GetOrdinal("DeclaredTag")) });
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
    if (reportType.StartsWith("permission-", StringComparison.Ordinal)) {
        // A declaration is not authority to change AD/Entra rights. Human owner
        // approval and a separate least-privilege change process are mandatory.
        var result = input.Action == "approve" ? "NeedsAccessReview" : "Rejected";
        using var update = db.CreateCommand();
        update.CommandText = """
            UPDATE Reports SET Status=$status,DecidedBy=$actor,DecidedAt=$at
            WHERE Id=$id AND Status='Pending'
            """;
        update.Parameters.AddWithValue("$status", result);
        update.Parameters.AddWithValue("$actor", ctx.User.Identity!.Name!);
        update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        if (update.ExecuteNonQuery() != 1) return Results.Conflict(new { error = "Already processed" });
        audit.Write(ctx.User.Identity.Name!, "Server", "Permission.Decision",
            "Report:" + id, result, ctx.Connection.RemoteIpAddress?.ToString(),
            ctx.TraceIdentifier, 200);
        return Results.Ok(new { status = result, needsManualAccessReview = result == "NeedsAccessReview",
            adModified = false });
    }
    if (reportType == "discovered") {
        // Approval means accepting the declaration for manual inventory verification.
        // No Jira object exists yet: do not invoke Jira GET/PUT or claim automatic registration.
        using var update = db.CreateCommand();
        update.CommandText = """
            UPDATE Reports SET Status=$status,DecidedBy=$by,DecidedAt=$at
            WHERE Id=$id AND Status='Pending'
            """;
        update.Parameters.AddWithValue("$status", input.Action == "reject" ? "Rejected" : "NeedsRegistration");
        update.Parameters.AddWithValue("$by", ctx.User.Identity!.Name!);
        update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        if (update.ExecuteNonQuery() != 1)
            return Results.Conflict(new { error = "Report already processed" });
        audit.Write(ctx.User.Identity.Name!, "Server", "Asset.DisclosureDecision",
            "Disclosure:" + id, input.Action == "approve" ? "NeedsRegistration" : "Rejected",
            ctx.Connection.RemoteIpAddress?.ToString(), ctx.TraceIdentifier, 200);
        return Results.Ok(new {
            status = input.Action == "approve" ? "NeedsRegistration" : "Rejected",
            requiresManualRegistration = input.Action == "approve",
            jiraModified = false
        });
    }
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

record ReviewConfirmInput(bool Confirmed,string ExpectedHash,bool Discrepancy=false);
record ReviewManualInput(string Login,string Kind,int Year,string Sha256,string EvidenceReference);
record NotificationSettingsInput(string Channel,string Host,int Port,string FromEmail,
    string Username,string? SmtpPassword,string? TeamsWebhook,
    string AssetsDueMonthDay,string PermissionsDueMonthDay,string EvidenceMode);
record NotificationTestInput(string Channel,string To);
record DelegateInput(string Login);
record DeploymentInput(string? Action);
record ClickInput(string Target, string? View);
record ReportInput(string ObjectId, string? AttributeId, string? ProposedValue, string? Reason, string? Type = null);
record DiscoveredEquipmentInput(string EmployeeLogin, string EquipmentType, string Name,
    string? Serial, string? AssetTag, string? Description);
record PermissionReportInput(string EmployeeLogin, string Kind, string? GroupDn,
    string? ExternalAccess, string Reason);
record GraphConfigurationInput(string TenantId,string ClientId,string CertificateThumbprint);
record TeamMember(string Login, string Name, string Manager, string? Role = null);
record DecisionInput(string Action);
record SettingsInput(string SiteUrl, string AccountEmail, string CloudId, string? WorkspaceId, string? Aql,
    string OwnerAttributeId, string[] AllowedCorrectionAttributeIds, string? ApiToken,
    string? HandoverGroup = null);
record AdminGroupInput(string AdminGroup);
record HandoverProtocolInput(string Mode, string? SourceLogin, string? TargetLogin, string[] AssetIds,
    string? Notes, string? IntermediateLogin = null, string? EvidenceMode = null);
record HandoverActionInput(string Action, bool Confirmed, string? EvidenceSha256 = null);
record HandoverItem(string Id, string Key, string Name, string Type, string OwnerSnapshot);
