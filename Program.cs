using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Server.IISIntegration;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
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

var dbPath = app.Configuration["Portal:DatabasePath"] ?? throw new InvalidOperationException("DatabasePath missing");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
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
        """;
    cmd.ExecuteNonQuery();
}

bool IsAdmin(ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true &&
    user.IsInRole(app.Configuration["Portal:AdminGroup"] ?? "__not_configured__");
IResult AuthError(HttpContext ctx) => ctx.User.Identity?.IsAuthenticated == true ? Results.Forbid() : Results.Unauthorized();
bool IsValidText(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max;
string? JiraBase() {
    var raw = app.Configuration["Jira:BaseUrl"];
    return Uri.TryCreate(raw, UriKind.Absolute, out var u) && u.Scheme == "https" && !raw!.Contains("CLOUD_ID") ? raw.TrimEnd('/') + "/" : null;
}
async Task<JsonNode> Jira(HttpMethod method, string path, object? body = null) {
    var baseUrl = JiraBase() ?? throw new InvalidOperationException("Jira BaseUrl is not configured");
    var token = app.Configuration["Jira:Token"];
    if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Jira Token is not configured");
    using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), path));
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
    id = obj["id"]?.ToString(), key = obj["objectKey"]?.ToString(), name = obj["name"]?.ToString(),
    attributes = (obj["attributes"]?.AsArray() ?? new JsonArray()).Select(a => new {
        id = a?["objectTypeAttributeId"]?.ToString(),
        name = a?["objectTypeAttribute"]?["name"]?.ToString() ?? a?["objectTypeAttributeId"]?.ToString(),
        value = AttributeValue(a)
    })
};

app.MapGet("/api/me", (HttpContext ctx, IAntiforgery anti) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var tokens = anti.GetAndStoreTokens(ctx);
    return Results.Ok(new { name = ctx.User.Identity.Name, admin = IsAdmin(ctx.User), csrf = tokens.RequestToken,
        allowedAttributes = app.Configuration.GetSection("Jira:AllowedCorrectionAttributeIds").Get<string[]>() ?? [] });
});

app.MapGet("/api/assets", async (HttpContext ctx) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    try {
        var ownerId = app.Configuration["Jira:OwnerAttributeId"];
        if (string.IsNullOrWhiteSpace(ownerId)) return Results.Problem("Configure Jira OwnerAttributeId", statusCode: 503);
        var result = await Jira(HttpMethod.Post, "object/aql", new { qlQuery = app.Configuration["Jira:Aql"], page = 1, resultsPerPage = 100, includeAttributes = true });
        var entries = result["values"]?.AsArray() ?? result["objectEntries"]?.AsArray() ?? result["results"]?["objectEntries"]?.AsArray() ?? new JsonArray();
        var login = ctx.User.Identity.Name!.Split('\\').Last();
        var mine = entries.Where(o => o is not null && AttributeValue(Attribute(o!, ownerId)).Split(',', StringSplitOptions.TrimEntries).Any(v =>
            v.Equals(login, StringComparison.OrdinalIgnoreCase) || v.Equals(ctx.User.Identity.Name, StringComparison.OrdinalIgnoreCase)));
        return Results.Ok(mine.Select(o => ProjectObject(o!)));
    } catch (Exception ex) { app.Logger.LogError(ex, "Asset query failed"); return Results.Problem("Nie udalo sie pobrac danych Jira.", statusCode: 502); }
});

app.MapPost("/api/reports", async (HttpContext ctx, IAntiforgery anti, ReportInput input) => {
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    await anti.ValidateRequestAsync(ctx);
    if (!long.TryParse(input.ObjectId, out var numericId) || numericId <= 0 || !IsValidText(input.AttributeId, 32) ||
        !IsValidText(input.ProposedValue, 500) || !IsValidText(input.Reason, 2000)) return Results.BadRequest();
    var allowed = app.Configuration.GetSection("Jira:AllowedCorrectionAttributeIds").Get<string[]>() ?? [];
    if (!allowed.Contains(input.AttributeId)) return Results.BadRequest(new { error = "Attribute is not permitted for correction" });
    try {
        var obj = await Jira(HttpMethod.Get, $"object/{numericId}");
        var ownerId = app.Configuration["Jira:OwnerAttributeId"] ?? "";
        var login = ctx.User.Identity.Name!.Split('\\').Last();
        if (!AttributeValue(Attribute(obj, ownerId)).Split(',', StringSplitOptions.TrimEntries).Any(v =>
            v.Equals(login, StringComparison.OrdinalIgnoreCase) || v.Equals(ctx.User.Identity.Name, StringComparison.OrdinalIgnoreCase))) return Results.Forbid();
        var attr = Attribute(obj, input.AttributeId);
        if (attr is null) return Results.BadRequest(new { error = "Attribute missing" });
        using var db = new SqliteConnection(ConnectionString()); db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO Reports(Source,ObjectId,ObjectKey,ObjectName,AttributeId,AttributeName,OldValue,ProposedValue,Reason,Reporter,CreatedAt,Status) VALUES ('Jira', $oid,$key,$name,$aid,$aname,$old,$new,$reason,$user,$at,'Pending')";
        cmd.Parameters.AddWithValue("$oid", input.ObjectId);
        cmd.Parameters.AddWithValue("$key", obj["objectKey"]?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$name", obj["name"]?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$aid", input.AttributeId);
        cmd.Parameters.AddWithValue("$aname", attr["objectTypeAttribute"]?["name"]?.ToString() ?? input.AttributeId);
        cmd.Parameters.AddWithValue("$old", AttributeValue(attr));
        cmd.Parameters.AddWithValue("$new", input.ProposedValue.Trim());
        cmd.Parameters.AddWithValue("$reason", input.Reason.Trim());
        cmd.Parameters.AddWithValue("$user", ctx.User.Identity.Name);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        return Results.Created($"/api/reports/{db.LastInsertRowId}", new { id = db.LastInsertRowId });
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
    while (reader.Read()) rows.Add(new { id = reader.GetInt64(0), source = reader.GetString(1), objectId = reader.GetString(2), key = reader.GetString(3), name = reader.GetString(4), attributeId = reader.GetString(5), attribute = reader.GetString(6), oldValue = reader.GetString(7), proposedValue = reader.GetString(8), reason = reader.GetString(9), reporter = reader.GetString(10), createdAt = reader.GetString(11), status = reader.GetString(12), decidedBy = reader.IsDBNull(13) ? null : reader.GetString(13), decidedAt = reader.IsDBNull(14) ? null : reader.GetString(14), error = reader.IsDBNull(15) ? null : reader.GetString(15) });
    return Results.Ok(rows);
});

app.MapPost("/api/reports/{id:long}/decision", async (HttpContext ctx, IAntiforgery anti, long id, DecisionInput input) => {
    if (!IsAdmin(ctx.User)) return AuthError(ctx);
    await anti.ValidateRequestAsync(ctx);
    if (input.Action is not ("approve" or "reject")) return Results.BadRequest();
    using var db = new SqliteConnection(ConnectionString()); db.Open();
    using var transaction = db.BeginTransaction();
    using var select = db.CreateCommand(); select.Transaction = transaction;
    select.CommandText = "SELECT ObjectId,AttributeId,OldValue,ProposedValue,Status FROM Reports WHERE Id=$id";
    select.Parameters.AddWithValue("$id", id);
    using var reader = select.ExecuteReader();
    if (!reader.Read()) return Results.NotFound();
    var objectId = reader.GetString(0); var attributeId = reader.GetString(1); var oldValue = reader.GetString(2); var proposed = reader.GetString(3); var status = reader.GetString(4);
    reader.Close();
    if (status != "Pending") return Results.Conflict(new { error = "Already processed" });
    if (input.Action == "approve") {
        try {
            var obj = await Jira(HttpMethod.Get, $"object/{objectId}");
            if (AttributeValue(Attribute(obj, attributeId)) != oldValue) return Results.Conflict(new { error = "Source changed since report; review again" });
            var typeId = obj["objectType"]?["id"]?.ToString() ?? obj["objectTypeId"]?.ToString();
            if (string.IsNullOrEmpty(typeId)) throw new InvalidOperationException("Object type missing");
            await Jira(HttpMethod.Put, $"object/{objectId}", new { objectTypeId = typeId, attributes = new[] { new { objectTypeAttributeId = attributeId, objectAttributeValues = new[] { new { value = proposed } } } } });
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

record ReportInput(string ObjectId, string AttributeId, string ProposedValue, string Reason);
record DecisionInput(string Action);
