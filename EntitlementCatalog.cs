using Microsoft.Data.Sqlite;

internal sealed record EntitlementItem(
    string Source, string Key, string Name, string Kind, string Details,
    string Via, string UserLogin, string CapturedAt, string Evidence = "");
internal sealed record EntitlementMetadata(
    string Source, string Key, string Note, bool Hidden, string UpdatedAt, string UpdatedBy);
internal sealed record EntitlementImportInput(
    string Source, string UserLogin, EntitlementImportItem[] Items);
internal sealed record EntitlementImportItem(
    string Key, string Name, string Kind, string Details, string Via, string Evidence);
internal sealed record EntitlementMetadataInput(
    string Source, string Key, string Note, bool Hidden);

internal sealed class EntitlementCatalog {
    internal const string LocalAD = "LocalAD";
    internal const string Entra = "Entra";
    internal const string Exchange = "Exchange";
    internal const string Files = "Files";
    internal const string SharePoint = "SharePoint";
    internal const string Teams = "Teams";
    internal const string M365License = "M365License";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        LocalAD,Entra,Exchange,Files,SharePoint,Teams,M365License
    };
    // Local AD and Graph are queried live. External imports are only for
    // trusted read-only collectors, never mixed with live source authority.
    private static readonly HashSet<string> ImportAllowed = new(StringComparer.Ordinal) {
        Exchange,Files,SharePoint,Teams,M365License,Entra
    };
    internal static bool IsAllowed(string source) => Allowed.Contains(source);
    private readonly string connection;
    internal EntitlementCatalog(string connection) { this.connection=connection; }
    private SqliteConnection Open() {
        var db=new SqliteConnection(connection);db.Open();return db;
    }
    internal void Initialize() {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS EntitlementMetadata(
                Source TEXT NOT NULL, ItemKey TEXT NOT NULL,
                Note TEXT NOT NULL DEFAULT '', Hidden INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL, UpdatedBy TEXT NOT NULL,
                PRIMARY KEY(Source,ItemKey));
            CREATE TABLE IF NOT EXISTS EntitlementSnapshots(
                Source TEXT NOT NULL, UserLogin TEXT NOT NULL COLLATE NOCASE,
                ItemKey TEXT NOT NULL, Name TEXT NOT NULL,
                Kind TEXT NOT NULL, Details TEXT NOT NULL,
                Via TEXT NOT NULL, Evidence TEXT NOT NULL,
                CapturedAt TEXT NOT NULL, ImportedBy TEXT NOT NULL,
                PRIMARY KEY(Source,UserLogin,ItemKey));
            CREATE INDEX IF NOT EXISTS IX_EntitlementSnapshots_User
                ON EntitlementSnapshots(UserLogin,Source);
            """;
        cmd.ExecuteNonQuery();
    }
    internal static bool ValidItem(string source,string key) =>
        IsAllowed(source) && !string.IsNullOrWhiteSpace(key) && key.Length<=2048 &&
        key.All(c=>!char.IsControl(c));
    internal EntitlementMetadata? Metadata(string source,string key) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Source,ItemKey,Note,Hidden,UpdatedAt,UpdatedBy FROM EntitlementMetadata
            WHERE Source=$source AND ItemKey=$key
            """;
        cmd.Parameters.AddWithValue("$source",source);cmd.Parameters.AddWithValue("$key",key);
        using var rd=cmd.ExecuteReader();
        return rd.Read()?new EntitlementMetadata(rd.GetString(0),rd.GetString(1),
            rd.GetString(2),rd.GetInt32(3)!=0,rd.GetString(4),rd.GetString(5)):null;
    }
    internal Dictionary<string,EntitlementMetadata> MetadataAll() {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Source,ItemKey,Note,Hidden,UpdatedAt,UpdatedBy FROM EntitlementMetadata";
        using var rd=cmd.ExecuteReader();
        var result=new Dictionary<string,EntitlementMetadata>(StringComparer.Ordinal);
        while(rd.Read()) {
            var m=new EntitlementMetadata(rd.GetString(0),rd.GetString(1),
                rd.GetString(2),rd.GetInt32(3)!=0,rd.GetString(4),rd.GetString(5));
            result[m.Source+"\n"+m.Key]=m;
        }
        return result;
    }
    internal void Update(EntitlementMetadataInput input,string actor) {
        if(!ValidItem(input.Source,input.Key) || input.Note is null ||
           input.Note.Length>2000 || input.Note.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t')))
           throw new ArgumentException("Invalid entitlement annotation");
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            INSERT INTO EntitlementMetadata(Source,ItemKey,Note,Hidden,UpdatedAt,UpdatedBy)
            VALUES($source,$key,$note,$hidden,$at,$actor)
            ON CONFLICT(Source,ItemKey)
            DO UPDATE SET Note=excluded.Note,Hidden=excluded.Hidden,
                UpdatedAt=excluded.UpdatedAt,UpdatedBy=excluded.UpdatedBy
            """;
        cmd.Parameters.AddWithValue("$source",input.Source);
        cmd.Parameters.AddWithValue("$key",input.Key);
        cmd.Parameters.AddWithValue("$note",input.Note.Trim());
        cmd.Parameters.AddWithValue("$hidden",input.Hidden?1:0);
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$actor",actor);
        cmd.ExecuteNonQuery();
    }
    internal void Import(EntitlementImportInput input,string actor) {
        if (!ImportAllowed.Contains(input.Source) ||
            input.Items is null || input.Items.Length > 1000 ||
            string.IsNullOrWhiteSpace(input.UserLogin) || input.UserLogin.Length > 256 ||
            input.UserLogin.Any(char.IsControl))
            throw new ArgumentException("Invalid entitlement snapshot");
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var i in input.Items) {
            if(!ValidItem(input.Source,i.Key) || !seen.Add(i.Key) ||
                !Text(i.Name,256,1) || !Text(i.Kind,80,1) ||
                !Text(i.Details,2000) || !Text(i.Via,500) ||
                !Text(i.Evidence,2000))
                throw new ArgumentException("Invalid or duplicate snapshot entry");
        }
        var at=DateTimeOffset.UtcNow.ToString("O");
        using var db=Open();
        using var tx=db.BeginTransaction();
        using(var delete=db.CreateCommand()) {
            delete.Transaction=tx;
            delete.CommandText="DELETE FROM EntitlementSnapshots WHERE Source=$source AND UserLogin=$user";
            delete.Parameters.AddWithValue("$source",input.Source);
            delete.Parameters.AddWithValue("$user",input.UserLogin);
            delete.ExecuteNonQuery();
        }
        foreach(var row in input.Items) {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO EntitlementSnapshots(
                    Source,UserLogin,ItemKey,Name,Kind,Details,Via,Evidence,CapturedAt,ImportedBy)
                VALUES($source,$user,$key,$name,$kind,$details,$via,$evidence,$at,$actor)
                """;
            cmd.Parameters.AddWithValue("$source",input.Source);
            cmd.Parameters.AddWithValue("$user",input.UserLogin);
            cmd.Parameters.AddWithValue("$key",row.Key);
            cmd.Parameters.AddWithValue("$name",row.Name);
            cmd.Parameters.AddWithValue("$kind",row.Kind);
            cmd.Parameters.AddWithValue("$details",row.Details);
            cmd.Parameters.AddWithValue("$via",row.Via);
            cmd.Parameters.AddWithValue("$evidence",row.Evidence);
            cmd.Parameters.AddWithValue("$at",at);
            cmd.Parameters.AddWithValue("$actor",actor);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private static bool Text(string? text,int max,int min=0) =>
        text is not null && text.Length>=min && text.Length<=max &&
        !text.Any(c=>char.IsControl(c)&&c is not ('\r' or '\n' or '\t'));
    internal EntitlementItem[] Imports(string login) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Source,ItemKey,Name,Kind,Details,Via,UserLogin,CapturedAt,Evidence
            FROM EntitlementSnapshots WHERE UserLogin=$user ORDER BY Source,Name LIMIT 10000
            """;
        cmd.Parameters.AddWithValue("$user",login);
        using var rd=cmd.ExecuteReader();
        var result=new List<EntitlementItem>();
        while(rd.Read()) result.Add(new EntitlementItem(
            rd.GetString(0),rd.GetString(1),rd.GetString(2),rd.GetString(3),
            rd.GetString(4),rd.GetString(5),rd.GetString(6),
            rd.GetString(7),rd.GetString(8)));
        return result.ToArray();
    }
}
