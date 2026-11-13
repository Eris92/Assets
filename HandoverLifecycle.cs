using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal sealed record CustodyStage(long ProtocolId,int StageNumber,string SenderLogin,string ReceiverLogin,
    string Status,string EvidenceMode,string DispatchedAt,string ReceivedAt);
internal sealed record HandoverEvidence(long Id,long ProtocolId,int StageNumber,string Action,string Actor,
    string Utc,string Statement,string Ip,string UserAgent,string EvidenceRef,string PreviousHash,string Hash);

internal sealed class HandoverLifecycle {
    private readonly string connectionString;
    internal HandoverLifecycle(string connectionString) => this.connectionString=connectionString;
    private SqliteConnection Open() {
        var db=new SqliteConnection(connectionString);
        db.Open();return db;
    }
    internal void Initialize() {
        using var db=Open();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS HandoverStages(
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              ProtocolId INTEGER NOT NULL, StageNumber INTEGER NOT NULL,
              SenderLogin TEXT NOT NULL, ReceiverLogin TEXT NOT NULL,
              Status TEXT NOT NULL, EvidenceMode TEXT NOT NULL,
              DispatchedAt TEXT NOT NULL DEFAULT '',
              ReceivedAt TEXT NOT NULL DEFAULT '',
              UNIQUE(ProtocolId,StageNumber));
            CREATE INDEX IF NOT EXISTS IX_HandoverStages_Pending
              ON HandoverStages(SenderLogin,ReceiverLogin,Status);
            CREATE TABLE IF NOT EXISTS HandoverEvidenceEvents(
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              ProtocolId INTEGER NOT NULL, StageNumber INTEGER NOT NULL,
              Action TEXT NOT NULL, Actor TEXT NOT NULL, Utc TEXT NOT NULL,
              Statement TEXT NOT NULL, Ip TEXT NOT NULL,
              UserAgent TEXT NOT NULL, EvidenceRef TEXT NOT NULL,
              PreviousHash TEXT NOT NULL, Hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_HandoverEvidence_Protocol
              ON HandoverEvidenceEvents(ProtocolId,Id);
            """;
        cmd.ExecuteNonQuery();
        foreach(var column in new[]{
            "IntermediateLogin TEXT NOT NULL DEFAULT ''",
            "IntermediateName TEXT NOT NULL DEFAULT ''",
            "EvidenceMode TEXT NOT NULL DEFAULT 'hybrid'"
        }) {
            using var probe=db.CreateCommand();
            probe.CommandText="SELECT 1 FROM pragma_table_info('HandoverProtocols') WHERE name=$key";
            probe.Parameters.AddWithValue("$key",column.Split(' ')[0]);
            if(probe.ExecuteScalar() is null) {
                using var migrate=db.CreateCommand();
                migrate.CommandText="ALTER TABLE HandoverProtocols ADD COLUMN "+column;
                migrate.ExecuteNonQuery();
            }
        }
    }
    internal void Create(SqliteConnection db,SqliteTransaction tx,long protocolId,
        string source,string intermediary,string target,string evidenceMode,string actor,string ip,string agent) {
        if(evidenceMode is not ("electronic" or "paper" or "hybrid"))
            throw new ArgumentException("Invalid evidence mode");
        if(string.IsNullOrWhiteSpace(target))throw new ArgumentException("Receiver is mandatory");
        var sequence=string.IsNullOrWhiteSpace(intermediary)
            ? new[]{(Sender:source,Receiver:target)}
            : new[]{(Sender:source,Receiver:intermediary),(Sender:intermediary,Receiver:target)};
        for(var i=0;i<sequence.Length;i++) {
            using var cmd=db.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO HandoverStages(ProtocolId,StageNumber,SenderLogin,ReceiverLogin,Status,EvidenceMode)
                VALUES($id,$stage,$sender,$receiver,$status,$mode)
                """;
            cmd.Parameters.AddWithValue("$id",protocolId);
            cmd.Parameters.AddWithValue("$stage",i+1);
            cmd.Parameters.AddWithValue("$sender",sequence[i].Sender);
            cmd.Parameters.AddWithValue("$receiver",sequence[i].Receiver);
            cmd.Parameters.AddWithValue("$status",i==0?"PendingDispatch":"Blocked");
            cmd.Parameters.AddWithValue("$mode",evidenceMode);
            cmd.ExecuteNonQuery();
        }
        AddEvent(db,tx,protocolId,0,"Create",actor,"Protocol prepared",ip,agent,"");
    }
    private static bool IsParticipant(string actor,string expected,bool isOperator) =>
        expected=="WAREHOUSE" ? isOperator :
        string.Equals(actor,expected,StringComparison.OrdinalIgnoreCase);
    private static string Statement(string action) => action switch {
        "dispatch" => "Potwierdzam fizyczne wydanie wskazanego sprzetu w stanie opisanym w protokole.",
        "receive" => "Potwierdzam fizyczne odebranie wskazanego sprzetu i zapoznanie sie z protokolem.",
        "decline" => "Odmawiam potwierdzenia przekazania. Wymagana jest weryfikacja operatora.",
        "manual" => "Operator potwierdza posiadanie papierowego protokolu z podpisami obu stron.",
        _ => throw new ArgumentException("Invalid acknowledgement")
    };
    internal void Change(long protocolId,int stageNumber,string action,string actor,bool isOperator,
        string ip,string agent,string evidenceRef) {
        if(action is not ("dispatch" or "receive" or "decline" or "manual"))
            throw new ArgumentException("Invalid stage action");
        using var db=Open();
        using var tx=db.BeginTransaction();
        using var read=db.CreateCommand();
        read.Transaction=tx;
        read.CommandText="""
            SELECT SenderLogin,ReceiverLogin,Status,EvidenceMode
            FROM HandoverStages WHERE ProtocolId=$id AND StageNumber=$stage
            """;
        read.Parameters.AddWithValue("$id",protocolId);
        read.Parameters.AddWithValue("$stage",stageNumber);
        string sender,receiver,status,mode;
        using(var reader=read.ExecuteReader()) {
            if(!reader.Read())throw new KeyNotFoundException("Protocol stage does not exist");
            sender=reader.GetString(0);receiver=reader.GetString(1);
            status=reader.GetString(2);mode=reader.GetString(3);
        }
        var allowed=action switch {
            "dispatch" => status=="PendingDispatch" && IsParticipant(actor,sender,isOperator) && mode!="paper",
            "receive" => status=="PendingReceipt" && IsParticipant(actor,receiver,isOperator) && mode!="paper",
            "decline" => status is "PendingDispatch" or "PendingReceipt" &&
                         (IsParticipant(actor,sender,isOperator)||IsParticipant(actor,receiver,isOperator)),
            "manual" => status is "PendingDispatch" or "PendingReceipt" &&
                        isOperator && mode!="electronic" &&
                        !string.IsNullOrWhiteSpace(evidenceRef) && evidenceRef.Length <= 128 &&
                        System.Text.RegularExpressions.Regex.IsMatch(evidenceRef,@"^[0-9a-fA-F]{64}$"),
            _ => false
        };
        if(!allowed)throw new InvalidOperationException("Action is not allowed for this identity, stage or evidence mode");
        var next=action switch {
            "dispatch"=>"PendingReceipt", "receive" or "manual"=>"Completed",
            "decline"=>"Disputed", _=>throw new InvalidOperationException()
        };
        var at=DateTimeOffset.UtcNow.ToString("O");
        using(var update=db.CreateCommand()) {
            update.Transaction=tx;
            update.CommandText="""
                UPDATE HandoverStages
                SET Status=$next,
                    DispatchedAt=CASE WHEN $action='dispatch' OR $action='manual' THEN $at ELSE DispatchedAt END,
                    ReceivedAt=CASE WHEN $next='Completed' THEN $at ELSE ReceivedAt END
                WHERE ProtocolId=$id AND StageNumber=$stage AND Status=$before
                """;
            update.Parameters.AddWithValue("$next",next);
            update.Parameters.AddWithValue("$action",action);
            update.Parameters.AddWithValue("$at",at);
            update.Parameters.AddWithValue("$id",protocolId);
            update.Parameters.AddWithValue("$stage",stageNumber);
            update.Parameters.AddWithValue("$before",status);
            if(update.ExecuteNonQuery()!=1)throw new InvalidOperationException("Concurrent protocol modification");
        }
        AddEvent(db,tx,protocolId,stageNumber,action,actor,Statement(action),ip,agent,evidenceRef);
        if(next=="Completed") {
            using var nextStage=db.CreateCommand();
            nextStage.Transaction=tx;
            nextStage.CommandText="""
                UPDATE HandoverStages SET Status='PendingDispatch'
                WHERE ProtocolId=$id AND StageNumber=$stage AND Status='Blocked'
                """;
            nextStage.Parameters.AddWithValue("$id",protocolId);
            nextStage.Parameters.AddWithValue("$stage",stageNumber+1);
            if(nextStage.ExecuteNonQuery()==1) {
                using var p=db.CreateCommand();
                p.Transaction=tx;
                p.CommandText="UPDATE HandoverProtocols SET Status='PendingIntermediateDelivery' WHERE Id=$id";
                p.Parameters.AddWithValue("$id",protocolId);p.ExecuteNonQuery();
            } else {
                using var p=db.CreateCommand();p.Transaction=tx;
                p.CommandText="UPDATE HandoverProtocols SET Status='Completed',CompletedAt=$at,CompletedBy=$actor WHERE Id=$id";
                p.Parameters.AddWithValue("$at",at);p.Parameters.AddWithValue("$actor",actor);
                p.Parameters.AddWithValue("$id",protocolId);p.ExecuteNonQuery();
            }
        }
        if(next=="Disputed"){
            using var p=db.CreateCommand();p.Transaction=tx;
            p.CommandText="UPDATE HandoverProtocols SET Status='Disputed' WHERE Id=$id";
            p.Parameters.AddWithValue("$id",protocolId);p.ExecuteNonQuery();
        }
        tx.Commit();
    }
    private static void AddEvent(SqliteConnection db,SqliteTransaction tx,long protocolId,int stage,
        string action,string actor,string statement,string ip,string agent,string evidenceRef) {
        using var previous=db.CreateCommand();
        previous.Transaction=tx;
        previous.CommandText="SELECT Hash FROM HandoverEvidenceEvents WHERE ProtocolId=$id ORDER BY Id DESC LIMIT 1";
        previous.Parameters.AddWithValue("$id",protocolId);
        var prev=(string?)previous.ExecuteScalar()??new string('0',64);
        var time=DateTimeOffset.UtcNow.ToString("O");
        var source=JsonSerializer.Serialize(new {
            protocolId,stage,action,actor,time,statement,
            ip=ip.Length>64?ip[..64]:ip,
            agent=agent.Length>350?agent[..350]:agent,
            evidenceRef,previousHash=prev
        });
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        using var cmd=db.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO HandoverEvidenceEvents(ProtocolId,StageNumber,Action,Actor,Utc,Statement,Ip,UserAgent,EvidenceRef,PreviousHash,Hash)
            VALUES($id,$stage,$action,$actor,$time,$stmt,$ip,$ua,$evidence,$previous,$hash)
            """;
        cmd.Parameters.AddWithValue("$id",protocolId);
        cmd.Parameters.AddWithValue("$stage",stage);
        cmd.Parameters.AddWithValue("$action",action);
        cmd.Parameters.AddWithValue("$actor",actor);
        cmd.Parameters.AddWithValue("$time",time);
        cmd.Parameters.AddWithValue("$stmt",statement);
        cmd.Parameters.AddWithValue("$ip",ip.Length>64?ip[..64]:ip);
        cmd.Parameters.AddWithValue("$ua",agent.Length>350?agent[..350]:agent);
        cmd.Parameters.AddWithValue("$evidence",evidenceRef);
        cmd.Parameters.AddWithValue("$previous",prev);
        cmd.Parameters.AddWithValue("$hash",hash);
        cmd.ExecuteNonQuery();
    }
    internal CustodyStage[] Stages(long id) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT ProtocolId,StageNumber,SenderLogin,ReceiverLogin,Status,EvidenceMode,DispatchedAt,ReceivedAt
            FROM HandoverStages WHERE ProtocolId=$id ORDER BY StageNumber
            """;
        cmd.Parameters.AddWithValue("$id",id);
        using var rd=cmd.ExecuteReader();
        var rows=new List<CustodyStage>();
        while(rd.Read())rows.Add(new CustodyStage(rd.GetInt64(0),rd.GetInt32(1),rd.GetString(2),
            rd.GetString(3),rd.GetString(4),rd.GetString(5),rd.GetString(6),rd.GetString(7)));
        return rows.ToArray();
    }
    internal HandoverEvidence[] Events(long id) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,ProtocolId,StageNumber,Action,Actor,Utc,Statement,Ip,UserAgent,EvidenceRef,PreviousHash,Hash
            FROM HandoverEvidenceEvents WHERE ProtocolId=$id ORDER BY Id
            """;
        cmd.Parameters.AddWithValue("$id",id);
        using var rd=cmd.ExecuteReader();
        var rows=new List<HandoverEvidence>();
        while(rd.Read())rows.Add(new HandoverEvidence(rd.GetInt64(0),rd.GetInt64(1),rd.GetInt32(2),
            rd.GetString(3),rd.GetString(4),rd.GetString(5),rd.GetString(6),rd.GetString(7),
            rd.GetString(8),rd.GetString(9),rd.GetString(10),rd.GetString(11)));
        return rows.ToArray();
    }
    internal object[] Inbox(string actor,bool isOperator) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText=isOperator ? """
            SELECT s.ProtocolId,s.StageNumber,s.SenderLogin,s.ReceiverLogin,s.Status,s.EvidenceMode,s.DispatchedAt,s.ReceivedAt,
              p.ItemsJson,p.CreatedAt,p.Status
            FROM HandoverStages s JOIN HandoverProtocols p ON p.Id=s.ProtocolId
            WHERE s.Status IN ('PendingDispatch','PendingReceipt','Disputed')
            ORDER BY p.Id DESC LIMIT 200
            """ : """
            SELECT s.ProtocolId,s.StageNumber,s.SenderLogin,s.ReceiverLogin,s.Status,s.EvidenceMode,s.DispatchedAt,s.ReceivedAt,
              p.ItemsJson,p.CreatedAt,p.Status
            FROM HandoverStages s JOIN HandoverProtocols p ON p.Id=s.ProtocolId
            WHERE s.Status IN ('PendingDispatch','PendingReceipt','Disputed')
              AND (s.SenderLogin=$actor COLLATE NOCASE OR s.ReceiverLogin=$actor COLLATE NOCASE)
            ORDER BY p.Id DESC LIMIT 200
            """;
        if(!isOperator)cmd.Parameters.AddWithValue("$actor",actor);
        using var rd=cmd.ExecuteReader();
        var rows=new List<object>();
        while(rd.Read())rows.Add(new {
            protocolId=rd.GetInt64(0),stage=rd.GetInt32(1),sender=rd.GetString(2),
            receiver=rd.GetString(3),status=rd.GetString(4),evidenceMode=rd.GetString(5),
            dispatchedAt=rd.GetString(6),receivedAt=rd.GetString(7),
            items=JsonSerializer.Deserialize<HandoverItem[]>(rd.GetString(8))??[],
            assetCount=(JsonSerializer.Deserialize<HandoverItem[]>(rd.GetString(8))??[]).Length,
            createdAt=rd.GetString(9),protocolStatus=rd.GetString(10)
        });
        return rows.ToArray();
    }
    internal object[] PendingWithManager(string actor) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT p.Id,p.ItemsJson,s.ReceivedAt,s.Status
            FROM HandoverProtocols p
            JOIN HandoverStages s ON s.ProtocolId=p.Id AND s.StageNumber=1
            JOIN HandoverStages next ON next.ProtocolId=p.Id AND next.StageNumber=2
            WHERE s.ReceiverLogin=$manager COLLATE NOCASE AND s.Status='Completed'
              AND next.Status IN ('PendingDispatch','PendingReceipt','Disputed')
            ORDER BY p.Id DESC LIMIT 200
            """;
        cmd.Parameters.AddWithValue("$manager",actor);
        using var rd=cmd.ExecuteReader();
        var rows=new List<object>();
        while(rd.Read())rows.Add(new {
            id=rd.GetInt64(0),
            items=JsonSerializer.Deserialize<HandoverItem[]>(rd.GetString(1))??[],
            receivedAt=rd.GetString(2),status=rd.GetString(3)
        });
        return rows.ToArray();
    }
}
