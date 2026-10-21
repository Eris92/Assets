using System.Security.Claims;
using System.Security.Principal;
using Microsoft.Data.Sqlite;

public sealed class AccessControl {
    private readonly string connectionString;
    private readonly Func<string?> adminGroup;
    private readonly Func<string[]> adminUsers;
    public AccessControl(string connectionString, Func<string?> adminGroup, Func<string[]> adminUsers) {
        this.connectionString = connectionString; this.adminGroup = adminGroup; this.adminUsers = adminUsers;
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS AccessRules (Sid TEXT PRIMARY KEY, Name TEXT NOT NULL, Kind TEXT NOT NULL, Role TEXT NOT NULL, Effect TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AccessAudit (Id INTEGER PRIMARY KEY AUTOINCREMENT, At TEXT NOT NULL, Actor TEXT NOT NULL, Action TEXT NOT NULL, Sid TEXT NOT NULL, BeforeJson TEXT NOT NULL, AfterJson TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public List<AccessRule> Rules() {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT Sid,Name,Kind,Role,Effect FROM AccessRules ORDER BY Name";
        using var reader = cmd.ExecuteReader(); var rows = new List<AccessRule>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return rows;
    }
    public PortalAccess Check(ClaimsPrincipal user) {
        if (user.Identity is not WindowsIdentity identity || !identity.IsAuthenticated || identity.User is null)
            return AccessPolicy.Evaluate(false, false, []);
        var principal = new WindowsPrincipal(identity);
        bool InGroup(string group) {
            try {
                var sid = group.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? new SecurityIdentifier(group) : (SecurityIdentifier)new NTAccount(group).Translate(typeof(SecurityIdentifier));
                return principal.IsInRole(sid);
            } catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException) { return false; }
        }
        var group = adminGroup();
        var bootstrap = adminUsers().Any(u => u.Equals(identity.Name, StringComparison.OrdinalIgnoreCase) || u.Equals(identity.User.Value, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(group) && InGroup(group));
        return AccessPolicy.Evaluate(true, bootstrap, Rules().Where(r => r.Kind == "User" ? r.Sid == identity.User.Value : InGroup(r.Sid)));
    }
    public AccessRule Resolve(AccessInput input) {
        if (input.Kind is not ("User" or "Group") || input.Role is not ("User" or "Admin") || input.Effect is not ("Allow" or "Deny") || string.IsNullOrWhiteSpace(input.Account) || input.Account.Length > 256)
            throw new ArgumentException("Invalid access rule");
        var account = input.Account.Trim();
        var sid = account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? new SecurityIdentifier(account) : (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        // Resolve name as well to reject stale or unknown SIDs before granting access.
        var name = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
        return new(sid.Value, name, input.Kind, input.Role, input.Effect);
    }
    public void Save(AccessRule rule, string actor, bool remove = false) {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var select = db.CreateCommand(); select.Transaction = tx;
        select.CommandText = "SELECT Sid,Name,Kind,Role,Effect FROM AccessRules WHERE Sid=$sid"; select.Parameters.AddWithValue("$sid", rule.Sid);
        AccessRule? before = null;
        using (var reader = select.ExecuteReader()) if (reader.Read()) before = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = remove ? "DELETE FROM AccessRules WHERE Sid=$sid" : "INSERT INTO AccessRules(Sid,Name,Kind,Role,Effect) VALUES($sid,$name,$kind,$role,$effect) ON CONFLICT(Sid) DO UPDATE SET Name=excluded.Name,Kind=excluded.Kind,Role=excluded.Role,Effect=excluded.Effect";
        cmd.Parameters.AddWithValue("$sid", rule.Sid);
        if (!remove) { cmd.Parameters.AddWithValue("$name", rule.Name); cmd.Parameters.AddWithValue("$kind", rule.Kind); cmd.Parameters.AddWithValue("$role", rule.Role); cmd.Parameters.AddWithValue("$effect", rule.Effect); }
        cmd.ExecuteNonQuery();
        using var audit = db.CreateCommand(); audit.Transaction = tx;
        audit.CommandText = "INSERT INTO AccessAudit(At,Actor,Action,Sid,BeforeJson,AfterJson) VALUES($at,$actor,$action,$sid,$before,$after)";
        audit.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); audit.Parameters.AddWithValue("$actor", actor); audit.Parameters.AddWithValue("$action", remove ? "Remove" : "Save"); audit.Parameters.AddWithValue("$sid", rule.Sid);
        audit.Parameters.AddWithValue("$before", System.Text.Json.JsonSerializer.Serialize(before)); audit.Parameters.AddWithValue("$after", System.Text.Json.JsonSerializer.Serialize(remove ? null : rule)); audit.ExecuteNonQuery();
        tx.Commit();
    }
    public List<object> Audit() {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT At,Actor,Action,Sid,BeforeJson,AfterJson FROM AccessAudit ORDER BY Id DESC LIMIT 100";
        using var reader = cmd.ExecuteReader(); var rows = new List<object>();
        while (reader.Read()) rows.Add(new { at=reader.GetString(0), actor=reader.GetString(1), action=reader.GetString(2), sid=reader.GetString(3), before=reader.GetString(4), after=reader.GetString(5) });
        return rows;
    }
}
public record AccessInput(string Account, string Kind, string Role, string Effect);
