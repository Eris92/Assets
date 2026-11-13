using Microsoft.Data.Sqlite;
internal sealed record TeamDelegation(long Id, string ManagerLogin, string DelegateLogin, string CreatedAt, string CreatedBy);

internal sealed class TeamDelegations {
    private readonly string connection;
    internal TeamDelegations(string connection) { this.connection=connection; }
    private SqliteConnection Open() {
        var db = new SqliteConnection(connection);
        db.Open();
        return db;
    }
    internal void Initialize() {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS TeamDelegations(
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              ManagerLogin TEXT NOT NULL COLLATE NOCASE,
              DelegateLogin TEXT NOT NULL COLLATE NOCASE,
              CreatedAt TEXT NOT NULL, CreatedBy TEXT NOT NULL, RevokedAt TEXT NOT NULL DEFAULT '');
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TeamDelegations_Active
              ON TeamDelegations(ManagerLogin,DelegateLogin) WHERE RevokedAt='';
            CREATE INDEX IF NOT EXISTS IX_TeamDelegations_Delegate
              ON TeamDelegations(DelegateLogin,RevokedAt);
            """;
        cmd.ExecuteNonQuery();
    }
    internal TeamDelegation[] ByManager(string login) => Query("ManagerLogin",login);
    internal TeamDelegation[] ByDelegate(string login) => Query("DelegateLogin",login);
    private TeamDelegation[] Query(string field,string login) {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText=$"SELECT Id,ManagerLogin,DelegateLogin,CreatedAt,CreatedBy FROM TeamDelegations WHERE {field}=$login AND RevokedAt='' ORDER BY Id";
        cmd.Parameters.AddWithValue("$login", login);
        using var reader=cmd.ExecuteReader();
        var results=new List<TeamDelegation>();
        while(reader.Read()) results.Add(new TeamDelegation(reader.GetInt64(0),reader.GetString(1),
            reader.GetString(2),reader.GetString(3),reader.GetString(4)));
        return results.ToArray();
    }
    internal TeamDelegation Add(string manager,string substitute,string actor) {
        using var db=Open();
        using var tx=db.BeginTransaction();
        using var query=db.CreateCommand();
        query.Transaction=tx;
        query.CommandText="SELECT COUNT(*) FROM TeamDelegations WHERE ManagerLogin=$m AND RevokedAt=''";
        query.Parameters.AddWithValue("$m",manager);
        if(Convert.ToInt64(query.ExecuteScalar())>=3)throw new InvalidOperationException("Maximum 3 active substitutes per manager");
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="INSERT INTO TeamDelegations(ManagerLogin,DelegateLogin,CreatedAt,CreatedBy) VALUES($m,$d,$t,$a)";
        cmd.Parameters.AddWithValue("$m",manager);
        cmd.Parameters.AddWithValue("$d",substitute);
        cmd.Parameters.AddWithValue("$t",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$a",actor);
        cmd.ExecuteNonQuery();
        using var id=db.CreateCommand();
        id.Transaction=tx;id.CommandText="SELECT last_insert_rowid()";
        var newId=Convert.ToInt64(id.ExecuteScalar());
        tx.Commit();
        return ByManager(manager).Single(x=>x.Id==newId);
    }
    internal bool Revoke(string manager,long id) {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE TeamDelegations SET RevokedAt=$date WHERE Id=$id AND ManagerLogin=$manager AND RevokedAt=''";
        cmd.Parameters.AddWithValue("$date",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$manager",manager);
        return cmd.ExecuteNonQuery()==1;
    }
}
