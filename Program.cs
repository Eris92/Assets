using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
var configDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AssetsPortal");
builder.Configuration.AddJsonFile(Path.Combine(configDirectory, "appsettings.json"), optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
var dbPath = builder.Configuration["Portal:DatabasePath"] ?? throw new InvalidOperationException("DatabasePath missing");
var dataDirectory = Path.GetDirectoryName(dbPath) ?? throw new InvalidOperationException("Invalid DatabasePath");
Directory.CreateDirectory(dataDirectory);
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection().SetApplicationName("AssetsPortal")
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory)).ProtectKeysWithDpapi();
builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddHttpClient("jira", c => c.Timeout = TimeSpan.FromSeconds(20));
var app = builder.Build();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
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
        """;
    cmd.ExecuteNonQuery();
    foreach (var column in new[] { "Type TEXT NOT NULL DEFAULT 'attribute'", "AffectedEmployee TEXT NOT NULL DEFAULT ''" }) {
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
bool IsAdmin(ClaimsPrincipal user) {
    if (user.Identity is not WindowsIdentity identity || !identity.IsAuthenticated) return false;
    var group = Setting("AdminGroup", "Portal:AdminGroup");
    if (string.IsNullOrWhiteSpace(group)) return false;
    try {
        var sid = group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
            ? new SecurityIdentifier(group)
            : (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
        return new WindowsPrincipal(identity).IsInRole(sid);
    } catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) {
        app.Logger.LogWarning("Configured admin group could not be resolved"); return false;
    }
}
IResult AuthError(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();
bool IsValidText(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max;
string? JiraBase() {
    var cloudId = Setting("CloudId", "Jira:CloudId");
    var workspaceId = Setting("WorkspaceId", "Jira:WorkspaceId");
    if (!Guid.TryParse(cloudId, out _) || !Guid.TryParse(workspaceId, out _)) return null;
    return $"https://api.atlassian.com/ex/jira/{cloudId}/jsm/assets/workspace/{workspaceId}/v1/";
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
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Jira credentials are not configured");
    return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
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
string AttributeValue(JsonNode? attribute) => string.Join(", ", (attribute?["objectAttributeValues"]?.AsArray() ?? new JsonArray())
    .Select(v => v?["displayValue"]?.ToString() ?? v?["value"]?.ToString() ?? ""));
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
bool OwnerMatches(string owner, string login) => owner.Split(',', StringSplitOptions.TrimEntries).Any(v =>
    v.Equals(login, StringComparison.OrdinalIgnoreCase) || v.Equals(login.Split('\\').Last(), StringComparison.OrdinalIgnoreCase));
async Task<List<JsonNode>> QueryAssets() {
    var entries = new List<JsonNode>();
    for (var page = 1; page <= 100; page++) {
        var result = await Jira(HttpMethod.Post,  $"object/aql?startAt={entries.Count}&maxResults=100&includeAttributes=true", new { qlQuery = Setting("Aql", "Jira:Aql") });
        var batch = result["values"]?.AsArray() ?? result["objectEntries"]?.AsArray() ?? result["results"]?["objectEntries"]?.AsArray() ?? new JsonArray();
        entries.AddRange(batch.OfType<JsonNode>());
        var total = result["total"]?.GetValue<int>() ?? result["totalFilterCount"]?.GetValue<int>();
        if (batch.Count == 0 || (total.HasValue && entries.Count >= total.Value) || (!total.HasValue && batch.Count < 100)) return entries;
    }
    throw new InvalidOperationException("Asset query limit reached; narrow the configured AQL");
}

app.MapGet("/api/me", (HttpContext ctx, IAntiforgery anti) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var tokens = anti.GetAndStoreTokens(ctx);
    return Results.Ok(new { name = ctx.User.Identity.Name, authenticationType = ctx.User.Identity.AuthenticationType,
        manager = ReportsTo(ctx.User.Identity.Name!).Length > 0, admin = IsAdmin(ctx.User), adminGroup = Setting("AdminGroup", "Portal:AdminGroup"), csrf = tokens.RequestToken,
        allowedAttributes = AllowedAttributes() });
});

app.MapGet("/api/admin/settings", (HttpContext ctx) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    return Results.Ok(new {
        siteUrl = Setting("SiteUrl", "Jira:SiteUrl"), accountEmail = Setting("AccountEmail", "Jira:AccountEmail"),
        cloudId = Setting("CloudId", "Jira:CloudId"), workspaceId = Setting("WorkspaceId", "Jira:WorkspaceId"),
        aql = Setting("Aql", "Jira:Aql"), ownerAttributeId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId"),
        allowedCorrectionAttributeIds = AllowedAttributes(), adminGroup = Setting("AdminGroup", "Portal:AdminGroup"),
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
        !IsValidText(input.Aql, 2000) || !IsValidText(input.OwnerAttributeId, 32) ||
        !long.TryParse(input.OwnerAttributeId, out _) || !IsValidText(input.AdminGroup, 256) ||
        input.AllowedCorrectionAttributeIds is null || input.AllowedCorrectionAttributeIds.Length > 50 ||
        input.AllowedCorrectionAttributeIds.Any(id => !long.TryParse(id, out _)) ||
        input.ApiToken?.Length > 1000) return Results.BadRequest(new { error = "Invalid settings" });
    try {
        var group = input.AdminGroup.Trim();
        var sid = group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? new SecurityIdentifier(group)
            : (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
        if (!new WindowsPrincipal((WindowsIdentity)ctx.User.Identity!).IsInRole(sid))
            return Results.BadRequest(new { error = "You must belong to the configured admin group" });
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
        Save("Aql", input.Aql.Trim());
        Save("OwnerAttributeId", input.OwnerAttributeId.Trim());
        Save("AllowedCorrectionAttributeIds", string.Join(',', input.AllowedCorrectionAttributeIds.Distinct()));
        Save("AdminGroup", group);
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
    if (string.IsNullOrWhiteSpace(siteUrl)) return Results.Problem("Configure Jira site URL", statusCode: 503);
    try {
        using var request = new HttpRequestMessage(HttpMethod.Get, siteUrl.TrimEnd('/') + "/rest/servicedeskapi/assets/workspace");
        request.Headers.Authorization = JiraAuthorization();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("jira");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");
    } catch (Exception ex) { app.Logger.LogError(ex, "Workspace discovery failed"); return Results.Problem("Nie udalo sie pobrac workspace Jira.", statusCode: 502); }
});

app.MapGet("/api/assets", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var ownerId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId");
        if (string.IsNullOrWhiteSpace(ownerId)) return Results.Problem("Configure Jira OwnerAttributeId", statusCode: 503);
        var entries = await QueryAssets();
        var mine = entries.Where(o => OwnerMatches(AttributeValue(Attribute(o, ownerId)), ctx.User.Identity.Name!));
        return Results.Ok(mine.Select(o => ProjectObject(o!)));
    } catch (Exception ex) { app.Logger.LogError(ex, "Asset query failed"); return Results.Problem("Nie udalo sie pobrac danych Jira.", statusCode: 502); }
});

app.MapGet("/api/team", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var people = ReportsTo(ctx.User.Identity.Name!);
        if (people.Length == 0) return Results.Ok(new { people = Array.Empty<object>(), equipment = Array.Empty<object>() });
        var ownerId = Setting("OwnerAttributeId", "Jira:OwnerAttributeId");
        if (string.IsNullOrWhiteSpace(ownerId)) return Results.Problem("Configure Jira OwnerAttributeId", statusCode: 503);
        var objects = await QueryAssets();
        var equipment = new List<object>();
        foreach (var obj in objects) {
            var owners = people.Where(p => OwnerMatches(AttributeValue(Attribute(obj, ownerId)), p.Login)).ToArray();
            if (owners.Length != 1) continue;
            equipment.Add(new { id = obj["id"]?.ToString(), key = obj["objectKey"]?.ToString(), name = obj["name"]?.ToString(), owner = owners[0].Login,
                type = obj["objectType"]?["name"]?.ToString(),
                attributes = (obj["attributes"]?.AsArray() ?? new JsonArray()).Select(a => new {
                    id = a?["objectTypeAttributeId"]?.ToString(), name = a?["objectTypeAttribute"]?["name"]?.ToString() ?? a?["objectTypeAttributeId"]?.ToString(), value = AttributeValue(a) }) });
        }
        return Results.Ok(new { people = people.Select(p => new { id = p.Login, name = p.Name, role = p.Role, manager = LoginKey(p.Manager) == LoginKey(ctx.User.Identity.Name!) ? "me" : p.Manager }), equipment });
    } catch (Exception ex) { app.Logger.LogError(ex, "Team query failed"); return Results.Problem("Nie udalo sie pobrac zespolu. Sprawdz team.json i Jira.", statusCode: 502); }
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
        cmd.CommandText = "INSERT INTO Reports(Source,ObjectId,ObjectKey,ObjectName,AttributeId,AttributeName,OldValue,ProposedValue,Reason,Reporter,CreatedAt,Status,Type,AffectedEmployee) VALUES ('Jira', $oid,$key,$name,$aid,$aname,$old,$new,$reason,$user,$at,'Pending',$type,$employee)";
        cmd.Parameters.AddWithValue("$oid", input.ObjectId);
        cmd.Parameters.AddWithValue("$key", obj["objectKey"]?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$name", obj["name"]?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$aid", attributeId);
        cmd.Parameters.AddWithValue("$aname", attr["objectTypeAttribute"]?["name"]?.ToString() ?? attributeId);
        cmd.Parameters.AddWithValue("$old", AttributeValue(attr));
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
    while (reader.Read()) rows.Add(new { id = reader.GetInt64(0), source = reader.GetString(1), objectId = reader.GetString(2), key = reader.GetString(3), name = reader.GetString(4), attributeId = reader.GetString(5), attribute = reader.GetString(6), oldValue = reader.GetString(7), proposedValue = reader.GetString(8), reason = reader.GetString(9), reporter = reader.GetString(10), createdAt = reader.GetString(11), status = reader.GetString(12), decidedBy = reader.IsDBNull(13) ? null : reader.GetString(13), decidedAt = reader.IsDBNull(14) ? null : reader.GetString(14), error = reader.IsDBNull(15) ? null : reader.GetString(15), type = reader.GetString(reader.GetOrdinal("Type")), affectedEmployee = reader.GetString(reader.GetOrdinal("AffectedEmployee")) });
    return Results.Ok(rows);
});

app.MapPost("/api/reports/{id:long}/decision", async (HttpContext ctx, IAntiforgery anti, long id, DecisionInput input) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if (input.Action is not ("approve" or "reject")) return Results.BadRequest();
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var transaction = db.BeginTransaction();
    using var select = db.CreateCommand(); select.Transaction = transaction;
    select.CommandText = "SELECT ObjectId,AttributeId,OldValue,ProposedValue,Status,Type FROM Reports WHERE Id=$id";
    select.Parameters.AddWithValue("$id", id);
    using var reader = select.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var objectId = reader.GetString(0); var attributeId = reader.GetString(1); var oldValue = reader.GetString(2); var proposed = reader.GetString(3); var status = reader.GetString(4); var reportType = reader.GetString(5);
    reader.Close();
    if (status != "Pending") return Results.Conflict(new { error = "Already processed" });
    if (input.Action == "approve") {
        try {
            var obj = await Jira(HttpMethod.Get, $"object/{objectId}");
            if (AttributeValue(Attribute(obj, attributeId)) != oldValue) return Results.Conflict(new { error = "Source changed since report; review again" });
            var typeId = obj["objectType"]?["id"]?.ToString() ?? obj["objectTypeId"]?.ToString();
            if (string.IsNullOrEmpty(typeId)) throw new InvalidOperationException("Object type missing");
            await Jira(HttpMethod.Put, $"object/{objectId}", new { objectTypeId = typeId, attributes = new[] { new { objectTypeAttributeId = attributeId, objectAttributeValues = reportType == "attribute" ? new object[] { new { value = proposed } } : Array.Empty<object>() } } });
        } catch (Exception ex) { app.Logger.LogError(ex, "Correction failed for report {Id}", id); return Results.Problem("Korekta w Jira nie powiodla sie; zgloszenie nadal oczekuje.", statusCode: 502); }
    }
    using var update = db.CreateCommand(); update.Transaction = transaction;
    update.CommandText = "UPDATE Reports SET Status=$status,DecidedBy=$by,DecidedAt=$at WHERE Id=$id AND Status='Pending'";
    update.Parameters.AddWithValue("$status", input.Action == "approve" ? "Approved" : "Rejected");
    update.Parameters.AddWithValue("$by", ctx.User.Identity!.Name!);
    update.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
    update.Parameters.AddWithValue("$id", id);
    update.ExecuteNonQuery(); transaction.Commit();
    return Results.Ok();
});

app.Run();

record ReportInput(string ObjectId, string? AttributeId, string? ProposedValue, string? Reason, string? Type = null);
record TeamMember(string Login, string Name, string Manager, string? Role = null);
record DecisionInput(string Action);
record SettingsInput(string SiteUrl, string AccountEmail, string CloudId, string? WorkspaceId, string Aql,
    string OwnerAttributeId, string[] AllowedCorrectionAttributeIds, string AdminGroup, string? ApiToken);
