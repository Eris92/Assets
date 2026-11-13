using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

internal sealed record AnnualReviewRecord(long Id,string UserLogin,string Kind,int Year,string DueOn,
    string Status,string Mode,string CompletedAt,string SnapshotSha256,string EvidenceReference);
internal sealed record ReviewReminder(long Id,string UserLogin,string Kind,int Year,string DueOn,
    int DaysBefore,string Status);

internal sealed class AnnualReviewStore {
    private readonly string conn;
    internal AnnualReviewStore(string conn){this.conn=conn;}
    private SqliteConnection Open(){var db=new SqliteConnection(conn);db.Open();return db;}
    internal void Initialize() {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS AnnualReviews(
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              UserLogin TEXT NOT NULL COLLATE NOCASE,
              Kind TEXT NOT NULL, Year INTEGER NOT NULL,
              DueOn TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'Pending',
              Mode TEXT NOT NULL DEFAULT '',
              CompletedAt TEXT NOT NULL DEFAULT '',
              SnapshotSha256 TEXT NOT NULL DEFAULT '',
              SnapshotJson TEXT NOT NULL DEFAULT '',
              EvidenceReference TEXT NOT NULL DEFAULT '',
              CompletedBy TEXT NOT NULL DEFAULT '',
              UNIQUE(UserLogin,Kind,Year));
            CREATE INDEX IF NOT EXISTS IX_AnnualReviews_Due ON AnnualReviews(DueOn,Status);
            CREATE TABLE IF NOT EXISTS ReviewReminders(
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              ReviewId INTEGER NOT NULL, DaysBefore INTEGER NOT NULL,
              Channel TEXT NOT NULL, Status TEXT NOT NULL DEFAULT 'Pending',
              AttemptedAt TEXT NOT NULL DEFAULT '',
              DeliveredAt TEXT NOT NULL DEFAULT '',
              Error TEXT NOT NULL DEFAULT '',
              UNIQUE(ReviewId,DaysBefore,Channel));
            """;
        cmd.ExecuteNonQuery();
        using var probe=db.CreateCommand();
        probe.CommandText="SELECT 1 FROM pragma_table_info('AnnualReviews') WHERE name='SnapshotJson'";
        if(probe.ExecuteScalar() is null){
            using var migrate=db.CreateCommand();
            migrate.CommandText="ALTER TABLE AnnualReviews ADD COLUMN SnapshotJson TEXT NOT NULL DEFAULT ''";
            migrate.ExecuteNonQuery();
        }
    }
    private static DateOnly DueDate(int year,string value) {
        if (value.Length!=5||value[2]!='-'||!int.TryParse(value[..2],out var month)||
            !int.TryParse(value[3..],out var day)||month is <1 or >12||day<1||day>DateTime.DaysInMonth(2028,month))
            throw new ArgumentException("Invalid review month-day. Use MM-DD");
        return new DateOnly(year,month,Math.Min(day,DateTime.DaysInMonth(year,month)));
    }
    internal AnnualReviewRecord[] Ensure(string login,Func<string,string?> setting) {
        var year=DateTime.UtcNow.Year;
        using var db=Open();using var tx=db.BeginTransaction();
        foreach(var(kind,key,defaultValue) in new[]{
            ("assets","AssetsDueMonthDay","12-01"),
            ("permissions","PermissionsDueMonthDay","12-01")
        }) {
            var due=DueDate(year,setting(key) is string x && !string.IsNullOrWhiteSpace(x)?x:defaultValue);
            using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT OR IGNORE INTO AnnualReviews(UserLogin,Kind,Year,DueOn)
                VALUES($user,$kind,$year,$due)
                """;
            cmd.Parameters.AddWithValue("$user",login);
            cmd.Parameters.AddWithValue("$kind",kind);
            cmd.Parameters.AddWithValue("$year",year);
            cmd.Parameters.AddWithValue("$due",due.ToString("yyyy-MM-dd"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return ForUser(login);
    }
    internal AnnualReviewRecord[] ForUser(string login) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,UserLogin,Kind,Year,DueOn,Status,Mode,CompletedAt,SnapshotSha256,EvidenceReference
            FROM AnnualReviews WHERE UserLogin=$user ORDER BY DueOn DESC,Kind LIMIT 20
            """;
        cmd.Parameters.AddWithValue("$user",login);
        using var rd=cmd.ExecuteReader();var result=new List<AnnualReviewRecord>();
        while(rd.Read())result.Add(Read(rd));
        return result.ToArray();
    }
    private static AnnualReviewRecord Read(SqliteDataReader rd) =>
        new(rd.GetInt64(0),rd.GetString(1),rd.GetString(2),rd.GetInt32(3),rd.GetString(4),
            rd.GetString(5),rd.GetString(6),rd.GetString(7),rd.GetString(8),rd.GetString(9));
    internal AnnualReviewRecord Confirm(string actor,string kind,string snapshot,string snapshotJson,bool discrepancy) {
        if(kind is not ("assets" or "permissions"))throw new ArgumentException("Invalid review kind");
        if(snapshot.Length!=64||!snapshot.All(Uri.IsHexDigit))throw new ArgumentException("Invalid snapshot hash");
        var year=DateTime.UtcNow.Year;
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE AnnualReviews SET Status=$status,Mode='electronic',CompletedAt=$at,
                SnapshotSha256=$hash,SnapshotJson=$json,EvidenceReference='',CompletedBy=$actor
            WHERE UserLogin=$user AND Kind=$kind AND Year=$year AND Status IN ('Pending','Discrepancy')
            """;
        cmd.Parameters.AddWithValue("$status",discrepancy?"Discrepancy":"Confirmed");
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$hash",snapshot.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$json",snapshotJson);
        cmd.Parameters.AddWithValue("$actor",actor);
        cmd.Parameters.AddWithValue("$user",actor);
        cmd.Parameters.AddWithValue("$kind",kind);
        cmd.Parameters.AddWithValue("$year",year);
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Review already confirmed or not registered");
        return ForUser(actor).Single(x=>x.Year==year && x.Kind==kind);
    }
    internal object Evidence(string login,string kind,int year) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT SnapshotSha256,SnapshotJson,Status,CompletedAt,Mode,EvidenceReference
            FROM AnnualReviews WHERE UserLogin=$user AND Kind=$kind AND Year=$year
            """;
        cmd.Parameters.AddWithValue("$user",login);cmd.Parameters.AddWithValue("$kind",kind);
        cmd.Parameters.AddWithValue("$year",year);
        using var rd=cmd.ExecuteReader();
        if(!rd.Read())throw new KeyNotFoundException("Annual review evidence not found");
        var hash=rd.GetString(0);var json=rd.GetString(1);
        var recomputed=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return new {
            login,kind,year,sha256=hash,snapshotIntegrity=json.Length>0&&
                recomputed.Equals(hash,StringComparison.OrdinalIgnoreCase),
            snapshotJson=json,status=rd.GetString(2),completedAt=rd.GetString(3),
            mode=rd.GetString(4),evidenceReference=rd.GetString(5)
        };
    }
    internal AnnualReviewRecord Manual(string login,string kind,int year,string digest,string reference,string actor) {
        if(kind is not ("assets" or "permissions") || digest.Length!=64 || !digest.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(reference)||reference.Length>256)
            throw new ArgumentException("Invalid evidence metadata");
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE AnnualReviews SET Status='Confirmed',Mode='paper',CompletedAt=$at,
                SnapshotSha256=$hash,EvidenceReference=$reference,CompletedBy=$actor
            WHERE UserLogin=$user AND Kind=$kind AND Year=$year AND Status IN ('Pending','Discrepancy')
            """;
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$hash",digest.ToLowerInvariant());
        cmd.Parameters.AddWithValue("$reference",reference);
        cmd.Parameters.AddWithValue("$actor",actor);
        cmd.Parameters.AddWithValue("$user",login);
        cmd.Parameters.AddWithValue("$kind",kind);
        cmd.Parameters.AddWithValue("$year",year);
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Review already confirmed or not registered");
        return ForUser(login).Single(x=>x.Year==year&&x.Kind==kind);
    }
    internal ReviewReminder[] DueReminders(DateOnly today) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            SELECT Id,UserLogin,Kind,Year,DueOn,Status FROM AnnualReviews
            WHERE Status IN ('Pending','Discrepancy') AND DueOn BETWEEN $today AND $future
            ORDER BY DueOn,Id LIMIT 10000
            """;
        cmd.Parameters.AddWithValue("$today",today.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("$future",today.AddDays(30).ToString("yyyy-MM-dd"));
        using var rd=cmd.ExecuteReader();var result=new List<ReviewReminder>();
        while(rd.Read()){
            var due=DateOnly.ParseExact(rd.GetString(4),"yyyy-MM-dd",CultureInfo.InvariantCulture);
            var days=due.DayNumber-today.DayNumber;
            if(days is not (30 or 14 or 1 or 0))continue;
            result.Add(new ReviewReminder(rd.GetInt64(0),rd.GetString(1),rd.GetString(2),
                rd.GetInt32(3),rd.GetString(4),days,"Pending"));
        }
        return result.ToArray();
    }
    internal bool Claim(long id,int days,string channel) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            INSERT OR IGNORE INTO ReviewReminders(ReviewId,DaysBefore,Channel,Status,AttemptedAt)
            VALUES($id,$days,$channel,'Processing',$at)
            """;
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$days",days);
        cmd.Parameters.AddWithValue("$channel",channel);
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery()==1;
    }
    internal void Finish(long id,int days,string channel,bool success,string? error) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="""
            UPDATE ReviewReminders SET Status=$status,DeliveredAt=CASE WHEN $ok=1 THEN $at ELSE '' END,
                Error=$error WHERE ReviewId=$id AND DaysBefore=$days AND Channel=$channel
            """;
        cmd.Parameters.AddWithValue("$status",success?"Delivered":"Failed");
        cmd.Parameters.AddWithValue("$ok",success?1:0);
        cmd.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$error",(error??"").Length>200?"Delivery failed":error??"");
        cmd.Parameters.AddWithValue("$id",id);
        cmd.Parameters.AddWithValue("$days",days);
        cmd.Parameters.AddWithValue("$channel",channel);
        cmd.ExecuteNonQuery();
    }
}

internal sealed class PortalNotificationService {
    private readonly string connectionString;
    private readonly IDataProtector protector;
    private readonly ILogger logger;
    internal PortalNotificationService(string conn,IDataProtectionProvider provider,ILogger logger) {
        connectionString=conn;protector=provider.CreateProtector("Assets.NotificationSecrets.v1");this.logger=logger;
    }
    private SqliteConnection Open(){var db=new SqliteConnection(connectionString);db.Open();return db;}
    internal string? Get(string key) {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Value FROM Settings WHERE Key=$key";
        cmd.Parameters.AddWithValue("$key","Notifications."+key);
        return cmd.ExecuteScalar() as string;
    }
    private string Secret(string key) {
        var encrypted=Get(key);
        return string.IsNullOrWhiteSpace(encrypted)?"":protector.Unprotect(encrypted);
    }
    internal void Save(NotifierSettings input) {
        if(!Enum.TryParse<NotificationChannel>(input.Channel,true,out _) ||
           input.Host.Length>253 || input.FromEmail.Length>254 ||
           input.Username.Length>254 || (input.SmtpPassword?.Length??0)>1000 ||
           (input.TeamsWebhook?.Length??0)>2000 || input.Port is <1 or >65535)
            throw new ArgumentException("Invalid notification settings");
        if(!string.IsNullOrWhiteSpace(input.Host) &&
           (!System.Text.RegularExpressions.Regex.IsMatch(input.Host,@"^[a-zA-Z0-9.-]+$") ||
            !MailAddress.TryCreate(input.FromEmail,out _)))
            throw new ArgumentException("Invalid SMTP hostname or sender");
        if(!string.IsNullOrWhiteSpace(input.TeamsWebhook)) {
            var url=VerifyWorkflowUrl(input.TeamsWebhook);
            if(url is null)throw new ArgumentException("Teams webhook must be a verified HTTPS Power Automate workflow URL");
        }
        using var db=Open();using var tx=db.BeginTransaction();
        void Put(string name,string val) {
            using var cmd=db.CreateCommand();cmd.Transaction=tx;
            cmd.CommandText="INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            cmd.Parameters.AddWithValue("$key","Notifications."+name);
            cmd.Parameters.AddWithValue("$value",val);cmd.ExecuteNonQuery();
        }
        Put("Channel",input.Channel);
        Put("Host",input.Host.Trim());Put("Port",input.Port.ToString(CultureInfo.InvariantCulture));
        Put("Username",input.Username.Trim());Put("FromEmail",input.FromEmail.Trim());
        if(!string.IsNullOrWhiteSpace(input.SmtpPassword))Put("SmtpPassword",protector.Protect(input.SmtpPassword));
        if(!string.IsNullOrWhiteSpace(input.TeamsWebhook))Put("TeamsWebhook",protector.Protect(input.TeamsWebhook.Trim()));
        tx.Commit();
    }
    internal object Status()=>new {
        channel=Get("Channel")??"Disabled",host=Get("Host")??"",
        port=int.TryParse(Get("Port"),out var port)?port:587,
        username=Get("Username")??"",fromEmail=Get("FromEmail")??"",
        smtpPasswordConfigured=!string.IsNullOrEmpty(Get("SmtpPassword")),
        teamsWebhookConfigured=!string.IsNullOrEmpty(Get("TeamsWebhook"))
    };
    private enum NotificationChannel{Disabled,Smtp,Teams,Both}
    private static Uri? VerifyWorkflowUrl(string text) {
        if(!Uri.TryCreate(text,UriKind.Absolute,out var uri) || uri.Scheme!=Uri.UriSchemeHttps ||
           !uri.IsDefaultPort || uri.UserInfo.Length>0 || uri.Fragment.Length>0)return null;
        var host=uri.Host.ToLowerInvariant();
        if(!(host=="api.powerplatform.com" || host.EndsWith(".api.powerplatform.com",StringComparison.Ordinal) ||
             host.EndsWith(".logic.azure.com",StringComparison.Ordinal) ||
             host.EndsWith(".powerautomate.com",StringComparison.Ordinal))) return null;
        return uri;
    }
    internal async Task Send(string recipient,string subject,string body,string channel,CancellationToken ct) {
        if(channel=="Smtp") {
            var host=Get("Host");var from=Get("FromEmail");
            if(string.IsNullOrWhiteSpace(host)||!MailAddress.TryCreate(from,out var sender) ||
                !MailAddress.TryCreate(recipient,out var receiver))
                throw new InvalidOperationException("SMTP is not configured");
            var port=int.TryParse(Get("Port"),out var p)?p:587;
            if(port!=587 && port!=25) throw new InvalidOperationException("Only STARTTLS SMTP ports 25/587 are supported");
            using var client=new SmtpClient(host,port) { EnableSsl=true,DeliveryMethod=SmtpDeliveryMethod.Network,
                Timeout=15000,UseDefaultCredentials=false };
            var user=Get("Username");
            if(!string.IsNullOrWhiteSpace(user))client.Credentials=new NetworkCredential(user,Secret("SmtpPassword"));
            using var mail=new MailMessage(sender,receiver) {Subject=subject,Body=body,IsBodyHtml=false};
            // SmtpClient is synchronous under the hood; execute off the request/host thread.
            await Task.Run(()=>client.Send(mail),ct);
        } else if(channel=="Teams") {
            var uri=VerifyWorkflowUrl(Secret("TeamsWebhook")) ??
                throw new InvalidOperationException("Teams Workflows webhook is not configured");
            using var handler=new HttpClientHandler {AllowAutoRedirect=false};
            using var http=new HttpClient(handler) {Timeout=TimeSpan.FromSeconds(12)};
            using var resp=await http.PostAsJsonAsync(uri,new {text=subject+"\n"+body, recipient},ct);
            if(!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("Teams workflow returned HTTP "+(int)resp.StatusCode);
        } else throw new ArgumentException("Unknown notification channel");
    }
    internal string[] EnabledChannels() {
        var current=Get("Channel")??"Disabled";
        return current switch {"Smtp"=>["Smtp"],"Teams"=>["Teams"],"Both"=>["Smtp","Teams"],_=>[]};
    }
}

internal sealed class ReviewReminderWorker : BackgroundService {
    private readonly IServiceProvider services;
    private readonly ILogger<ReviewReminderWorker> logger;
    internal ReviewReminderWorker(IServiceProvider services,ILogger<ReviewReminderWorker> logger) {
        this.services=services;this.logger=logger;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // The first scan runs shortly after app startup; later scans are bounded.
        await Task.Delay(TimeSpan.FromSeconds(20),stoppingToken);
        using var timer=new PeriodicTimer(TimeSpan.FromHours(6));
        do {
            try {
                var store=services.GetRequiredService<AnnualReviewStore>();
                var notifier=services.GetRequiredService<PortalNotificationService>();
                var tz=TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
                var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,tz));
                foreach(var review in store.DueReminders(today)) {
                    if(stoppingToken.IsCancellationRequested)break;
                    HandoverPerson person;
                    try { person=HandoverDirectory.Resolve(review.UserLogin); }
                    catch(Exception ex){logger.LogWarning(ex,"Cannot resolve review recipient in AD");continue;}
                    foreach(var channel in notifier.EnabledChannels()) {
                        if(!store.Claim(review.Id,review.DaysBefore,channel))continue;
                        try {
                            var subject="Assets Portal: coroczny przeglad "+(review.Kind=="assets"?"sprzetu":"uprawnien");
                            var body=$"Uzytkownik: {review.UserLogin}\nTermin: {review.DueOn}\nPozostalo dni: {review.DaysBefore}\nOtworz Assets Portal i wykonaj przeglad.";
                            await notifier.Send(person.Email,subject,body,channel,stoppingToken);
                            store.Finish(review.Id,review.DaysBefore,channel,true,null);
                        } catch(Exception ex) {
                            logger.LogError(ex,"Review reminder delivery failed: {ReviewId}/{Channel}",review.Id,channel);
                            store.Finish(review.Id,review.DaysBefore,channel,false,"Delivery failed");
                        }
                    }
                }
            } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { break; }
            catch(Exception ex){logger.LogError(ex,"Annual review reminder cycle failed");}
        } while(await timer.WaitForNextTickAsync(stoppingToken));
    }
}
internal sealed record NotifierSettings(string Channel,string Host,int Port,string FromEmail,
    string Username,string? SmtpPassword,string? TeamsWebhook);
