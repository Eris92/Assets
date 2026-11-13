using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

internal sealed record AuditEntry(long Id, string Utc, string Actor, string Source, string Action,
    string Target, string Outcome, string Ip, string TraceId, int HttpStatus);

internal sealed class OperationalAudit {
    private readonly string connectionString;
    private readonly ILogger logger;

    internal OperationalAudit(string connectionString, ILogger logger) {
        this.connectionString = connectionString;
        this.logger = logger;
    }
    private SqliteConnection Open() {
        var db = new SqliteConnection(connectionString);
        db.DefaultTimeout = 5;
        db.Open();
        return db;
    }
    internal void Initialize() {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS PortalAudit (
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              Utc TEXT NOT NULL, Actor TEXT NOT NULL, Source TEXT NOT NULL,
              Action TEXT NOT NULL, Target TEXT NOT NULL, Outcome TEXT NOT NULL,
              ClientIp TEXT NOT NULL, TraceId TEXT NOT NULL, HttpStatus INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_PortalAudit_Utc ON PortalAudit(Utc);
            CREATE INDEX IF NOT EXISTS IX_PortalAudit_ActorUtc ON PortalAudit(Actor,Utc);
            DELETE FROM PortalAudit WHERE Utc < $old;
            """;
        cmd.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-180).ToString("O"));
        cmd.ExecuteNonQuery();
    }
    internal void Write(string actor, string source, string action, string target, string outcome,
        string? ip = null, string? traceId = null, int status = 0) {
        static string Safe(string? value, int max) => (value ?? "").Length <= max ? (value ?? "") : value![..max];
        try {
            using var db = Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO PortalAudit(Utc,Actor,Source,Action,Target,Outcome,ClientIp,TraceId,HttpStatus)
                VALUES ($utc,$actor,$source,$action,$target,$outcome,$ip,$trace,$status)
                """;
            cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$actor", Safe(actor, 256));
            cmd.Parameters.AddWithValue("$source", Safe(source, 24));
            cmd.Parameters.AddWithValue("$action", Safe(action, 90));
            cmd.Parameters.AddWithValue("$target", Safe(target, 180));
            cmd.Parameters.AddWithValue("$outcome", Safe(outcome, 64));
            cmd.Parameters.AddWithValue("$ip", Safe(ip, 64));
            cmd.Parameters.AddWithValue("$trace", Safe(traceId, 128));
            cmd.Parameters.AddWithValue("$status", status);
            cmd.ExecuteNonQuery();
        } catch (Exception ex) {
            // An audit storage outage must be visible to operators but must not
            // turn read-only requests into 500s or silently skip the business action.
            logger.LogError(ex, "Portal audit write failed for {Action}", action);
        }
    }
    internal List<AuditEntry> Query(string? actor, string? action, DateTimeOffset? since, int limit) {
        limit = Math.Clamp(limit, 1, 10000);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT Id,Utc,Actor,Source,Action,Target,Outcome,ClientIp,TraceId,HttpStatus
            FROM PortalAudit
            WHERE ($actor = '' OR Actor LIKE '%' || $actor || '%')
              AND ($action = '' OR Action LIKE '%' || $action || '%')
              AND ($since = '' OR Utc >= $since)
            ORDER BY Id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$actor", actor ?? "");
        cmd.Parameters.AddWithValue("$action", action ?? "");
        cmd.Parameters.AddWithValue("$since", since?.UtcDateTime.ToString("O") ?? "");
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var rows = new List<AuditEntry>();
        while (reader.Read()) rows.Add(new AuditEntry(reader.GetInt64(0),reader.GetString(1),
            reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),
            reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetInt32(9)));
        return rows;
    }
    internal static byte[] Csv(IEnumerable<AuditEntry> rows) {
        static string CsvField(string? text) {
            // Protect spreadsheet exports from formula injection.
            var value = text ?? "";
            if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        var sb = new StringBuilder("Id,Utc,Actor,Source,Action,Target,Outcome,ClientIp,TraceId,HttpStatus\r\n");
        foreach (var x in rows) {
            sb.Append(string.Join(",", new[] { x.Id.ToString(CultureInfo.InvariantCulture), CsvField(x.Utc),
                CsvField(x.Actor), CsvField(x.Source), CsvField(x.Action), CsvField(x.Target),
                CsvField(x.Outcome), CsvField(x.Ip), CsvField(x.TraceId), x.HttpStatus.ToString(CultureInfo.InvariantCulture) }));
            sb.Append("\r\n");
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
