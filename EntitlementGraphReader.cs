using Azure.Core;
using Azure.Identity;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

internal sealed record GraphReadResult(EntitlementItem[] Items, Dictionary<string,string> Status);
internal static class EntitlementGraphReader {
    internal static async Task<GraphReadResult> ReadAsync(
        string login,string upn, string tenantId,string clientId,string certThumb,
        CancellationToken ct) {
        if(!Guid.TryParse(tenantId,out _)||!Guid.TryParse(clientId,out _)||
           string.IsNullOrWhiteSpace(upn)||upn.Length>254 ||
           !System.Text.RegularExpressions.Regex.IsMatch(certThumb,@"^[0-9a-fA-F]{40}$"))
            throw new ArgumentException("Invalid Entra certificate connector configuration");
        using var store=new X509Store(StoreName.My,StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        using var certificate=store.Certificates.Find(X509FindType.FindByThumbprint,
            certThumb,validOnly:false).OfType<X509Certificate2>()
            .FirstOrDefault(c=>c.HasPrivateKey && DateTime.UtcNow>=c.NotBefore.ToUniversalTime() &&
                DateTime.UtcNow<c.NotAfter.ToUniversalTime())
            ?? throw new InvalidOperationException("Entra certificate is not found or has no usable private key");
        var auth=new ClientCertificateCredential(tenantId,clientId,certificate);
        var token=await auth.GetTokenAsync(new TokenRequestContext(
            ["https://graph.microsoft.com/.default"]),ct);
        using var handler=new HttpClientHandler{AllowAutoRedirect=false};
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(16)};
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token.Token);
        var encoded=Uri.EscapeDataString(upn);
        var now=DateTimeOffset.UtcNow.ToString("O");
        var rows=new List<EntitlementItem>();
        var status=new Dictionary<string,string>(StringComparer.Ordinal);
        try {
            var groups=await Paged(client,"/v1.0/users/"+encoded+"/transitiveMemberOf?$top=100",ct);
            foreach(var g in groups) {
                var id=g["id"]?.ToString()??"";
                if(!Guid.TryParse(id,out _))continue;
                var type=g["@odata.type"]?.ToString()??"";
                var name=g["displayName"]?.ToString()??id;
                var email=g["mail"]?.ToString()??"";
                if(type.EndsWith("directoryRole",StringComparison.OrdinalIgnoreCase)) {
                    rows.Add(new EntitlementItem(EntitlementCatalog.Entra,id,name,
                        "DirectoryRole","Entra directory role","Transitive (Graph)",login,now));
                } else if(type.EndsWith("group",StringComparison.OrdinalIgnoreCase)) {
                    var unified=(g["groupTypes"] as JsonArray)?.Any(n=>
                        string.Equals(n?.ToString(),"Unified",StringComparison.OrdinalIgnoreCase))??false;
                    var mailEnabled=bool.TryParse(g["mailEnabled"]?.ToString(),out var me)&&me;
                    var securityEnabled=bool.TryParse(g["securityEnabled"]?.ToString(),out var se)&&se;
                    var source=mailEnabled&&!securityEnabled&&!unified?
                        EntitlementCatalog.Exchange:EntitlementCatalog.Entra;
                    var kind=unified?"Microsoft365Group":
                        mailEnabled&&!securityEnabled?"DistributionGroup":
                        mailEnabled&&securityEnabled?"MailSecurityGroup":"SecurityGroup";
                    rows.Add(new EntitlementItem(source,id,name,kind,
                        string.IsNullOrWhiteSpace(email)?"":"Mail: "+email,
                        "Direct or transitive (Graph)",login,now));
                }
            }
            status["Entra"]="Connected (Graph transitive membership)";
            status["Exchange"]="Graph mail-enabled groups only; delegation requires EXO import";
        }catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            status["Entra"]="Graph memberships unavailable: "+Short(ex);
        }
        try {
            var teams=await Paged(client,"/v1.0/users/"+encoded+"/joinedTeams",ct);
            foreach(var t in teams) {
                var id=t["id"]?.ToString()??"";
                if(!Guid.TryParse(id,out _))continue;
                rows.Add(new EntitlementItem(EntitlementCatalog.Teams,id,
                    t["displayName"]?.ToString()??id,"Team",
                    t["description"]?.ToString()??"","Member (Graph)",login,now));
            }
            status["Teams"]="Connected (joined teams)";
        }catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            status["Teams"]="Graph teams unavailable: "+Short(ex);
        }
        try {
            var user=await Single(client,"/v1.0/users/"+encoded+
                "?$select=id,displayName,assignedLicenses,licenseAssignmentStates",ct);
            var licensed=(user["assignedLicenses"] as JsonArray)??new JsonArray();
            var skus=await Paged(client,"/v1.0/subscribedSkus?$select=skuId,skuPartNumber",ct);
            var names=skus.Where(o=>Guid.TryParse(o["skuId"]?.ToString(),out _))
                .GroupBy(o=>o["skuId"]!.ToString(),StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g=>g.Key,g=>g.First()["skuPartNumber"]?.ToString()??g.Key,
                    StringComparer.OrdinalIgnoreCase);
            var assignments=user["licenseAssignmentStates"] as JsonArray;
            foreach(var lic in licensed) {
                var sku=lic?["skuId"]?.ToString()??"";
                if(!Guid.TryParse(sku,out _))continue;
                var states=assignments?.Where(x=>string.Equals(
                    x?["skuId"]?.ToString(),sku,StringComparison.OrdinalIgnoreCase)).ToArray()??[];
                var via=states.Any(x=>!string.IsNullOrWhiteSpace(x?["assignedByGroup"]?.ToString()))
                    ?"Group assignment":"Direct or unspecified";
                var details=states.Length==0?"Assigned SKU":
                    string.Join("; ",states.Select(x=>"State: "+(x?["state"]?.ToString()??"Unknown")+
                        (string.IsNullOrWhiteSpace(x?["assignedByGroup"]?.ToString())?"":" via "+
                         x!["assignedByGroup"]!.ToString())));
                rows.Add(new EntitlementItem(EntitlementCatalog.M365License,sku,
                    names.GetValueOrDefault(sku,sku),"License",details,via,login,now));
            }
            status["M365License"]="Connected (assignedLicenses + subscribedSkus)";
        }catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or InvalidOperationException) {
            status["M365License"]="Graph licenses unavailable: "+Short(ex);
        }
        status["SharePoint"]="Direct site/file rights require a separate collector; group-based access not equivalent";
        return new GraphReadResult(rows.ToArray(),status);
    }
    private static string Short(Exception ex) {
        var message=ex.Message;
        // Never return arbitrary Graph response bodies, identities, or token details to the UI.
        if(message.StartsWith("Graph HTTP ",StringComparison.Ordinal))
            return message.Length>70?message[..70]:message;
        return ex is TaskCanceledException?"timeout":"connector error; inspect server logs";
    }
    private static async Task<JsonNode> Single(HttpClient client,string path,CancellationToken ct) {
        var uri=Guarded(path);
        using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
        if(!response.IsSuccessStatusCode)
            throw new HttpRequestException("Graph HTTP "+(int)response.StatusCode);
        await using var stream=await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream,cancellationToken:ct) ??
            throw new InvalidOperationException("Graph returned null JSON");
    }
    private static async Task<JsonNode[]> Paged(HttpClient client,string path,CancellationToken ct) {
        var result=new List<JsonNode>();
        var uri=Guarded(path);
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(var page=0;page<25;page++) {
            if(!seen.Add(uri.AbsoluteUri))throw new InvalidOperationException("Graph paging loop");
            using var response=await client.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);
            if(!response.IsSuccessStatusCode)
                throw new HttpRequestException("Graph HTTP "+(int)response.StatusCode);
            await using var stream=await response.Content.ReadAsStreamAsync(ct);
            var data=await JsonNode.ParseAsync(stream,cancellationToken:ct) ??
                throw new InvalidOperationException("Graph returned null JSON");
            if(data["value"] is not JsonArray items)
                throw new InvalidOperationException("Graph value array missing");
            foreach(var item in items)if(item is not null)result.Add(item.DeepClone());
            if(result.Count>10000)throw new InvalidOperationException("Graph pagination safety limit");
            var next=data["@odata.nextLink"]?.ToString();
            if(string.IsNullOrWhiteSpace(next))return result.ToArray();
            uri=Guarded(next);
        }
        throw new InvalidOperationException("Graph page count exceeded");
    }
    private static Uri Guarded(string path) {
        var uri=Uri.TryCreate(path,UriKind.Absolute,out var parsed)?
            parsed!:new Uri("https://graph.microsoft.com"+path);
        if(uri.Scheme!="https"||!uri.Host.Equals("graph.microsoft.com",StringComparison.OrdinalIgnoreCase)||
           uri.Port!=443||!uri.AbsolutePath.StartsWith("/v1.0/",StringComparison.Ordinal)||
           uri.UserInfo.Length!=0 ||uri.Fragment.Length!=0)
            throw new InvalidOperationException("Graph endpoint outside allowed authority");
        return uri;
    }
}
