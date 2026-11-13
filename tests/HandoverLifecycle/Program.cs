using Microsoft.Data.Sqlite;
record HandoverItem(string Id,string Key,string Name,string Type,string OwnerSnapshot);

static void Verify(bool result,string description){
    if(!result)throw new Exception("FAILED: "+description);
    Console.WriteLine("PASS: "+description);
}
var folder=Path.Combine(Path.GetTempPath(),"assets-custody-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try {
    var file=Path.Combine(folder,"portal.db");
    var conn=new SqliteConnectionStringBuilder{DataSource=file}.ToString();
    using(var db=new SqliteConnection(conn)) {
        db.Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE HandoverProtocols (
              Id INTEGER PRIMARY KEY,Mode TEXT NOT NULL,
              SourceLogin TEXT NOT NULL,SourceName TEXT NOT NULL,
              TargetLogin TEXT NOT NULL,TargetName TEXT NOT NULL,
              ItemsJson TEXT NOT NULL,Notes TEXT NOT NULL,CreatedAt TEXT NOT NULL,CreatedBy TEXT NOT NULL,
              Status TEXT NOT NULL,CompletedAt TEXT,CompletedBy TEXT);
            """;
        cmd.ExecuteNonQuery();
    }
    var lifecycle=new HandoverLifecycle(conn);
    lifecycle.Initialize();
    using(var db=new SqliteConnection(conn)) {
        db.Open();
        using var tx=db.BeginTransaction();
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO HandoverProtocols(
              Id,Mode,SourceLogin,SourceName,TargetLogin,TargetName,ItemsJson,Notes,CreatedAt,CreatedBy,Status
            ) VALUES(1,'transfer','DOMAIN\sender','Sender','DOMAIN\receiver','Receiver',
              '[]','','2026-11-13T10:00:00Z','DOMAIN\operator','PendingDispatch')
            """;
        cmd.ExecuteNonQuery();
        lifecycle.Create(db,tx,1,"DOMAIN\\sender","DOMAIN\\manager","DOMAIN\\receiver",
            "hybrid","DOMAIN\\operator","192.0.2.1","TestAgent");
        tx.Commit();
    }
    var stages=lifecycle.Stages(1);
    Verify(stages.Length==2 && stages[0].Status=="PendingDispatch" && stages[1].Status=="Blocked",
        "Two-stage protocol begins at sender");
    try{
        lifecycle.Change(1,1,"receive","DOMAIN\\receiver",false,"192.0.2.2","Test","");
        throw new Exception("FAILED: unauthorised receipt allowed");
    }catch(InvalidOperationException){Console.WriteLine("PASS: unauthorised receipt blocked");}
    lifecycle.Change(1,1,"dispatch","DOMAIN\\sender",false,"192.0.2.2","Test","");
    Verify(lifecycle.Stages(1)[0].Status=="PendingReceipt","First leg sent");
    lifecycle.Change(1,1,"receive","DOMAIN\\manager",false,"192.0.2.3","Test","");
    stages=lifecycle.Stages(1);
    Verify(stages[0].Status=="Completed" && stages[1].Status=="PendingDispatch",
        "Manager receives and second leg activates");
    Verify(lifecycle.PendingWithManager("DOMAIN\\manager",false).Length==1,
        "Unsettled equipment remains on intermediate manager");
    Verify(lifecycle.PendingWithManager("DOMAIN\\operator",true).Length==1,
        "Operator can audit every unresolved intermediary");
    lifecycle.Change(1,2,"dispatch","DOMAIN\\manager",false,"192.0.2.3","Test","");
    lifecycle.Change(1,2,"receive","DOMAIN\\receiver",false,"192.0.2.4","Test","");
    Verify(lifecycle.PendingWithManager("DOMAIN\\manager",false).Length==0,"Completed custody no longer pending");
    Verify(lifecycle.Stages(1).All(x=>x.Status=="Completed"),"All stages complete");
    Verify(lifecycle.VerifyChain(1),"Cryptographic event chain verifies");
    Verify(lifecycle.Events(1).Length==5,"All five evidence events recorded");
    try {
        lifecycle.Change(1,2,"receive","DOMAIN\\receiver",false,"192.0.2.4","Test","");
        throw new Exception("FAILED: double acknowledgement accepted");
    }catch(InvalidOperationException){Console.WriteLine("PASS: double acknowledgement blocked");}
    using(var db=new SqliteConnection(conn)) {
        db.Open();using var cmd=db.CreateCommand();
        cmd.CommandText="UPDATE HandoverEvidenceEvents SET Statement='forged' WHERE Id=(SELECT MIN(Id) FROM HandoverEvidenceEvents)";
        cmd.ExecuteNonQuery();
    }
    Verify(!lifecycle.VerifyChain(1),"Tampered evidence chain rejected");
} finally {
    SqliteConnection.ClearAllPools();
    Directory.Delete(folder,true);
}
