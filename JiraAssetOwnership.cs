using System.Text.Json.Nodes;

// Attribute-scoped Jira ownership matching. Never search unrelated asset attributes:
// otherwise a serial number, note or URL could be mistaken for a user's identity.
internal static class JiraAssetOwnership {
    internal static string[] Aliases(string windowsLogin,IEnumerable<string> emails) {
        if(string.IsNullOrWhiteSpace(windowsLogin))return [];
        var values=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var full=windowsLogin.Trim();
        values.Add(full);
        var sam=full.Split('\\').Last();
        if(!string.IsNullOrWhiteSpace(sam))values.Add(sam);
        foreach(var email in emails) {
            if(!string.IsNullOrWhiteSpace(email))values.Add(email.Trim());
        }
        return values.ToArray();
    }
    // Assets ownership is a reference to an Assets Users object (CMDB-*),
    // not to a Jira accountId. Only resolve a display-name candidate if it
    // maps to exactly one referenced Users object across the complete scope.
    // The caller must separately authenticate the AD/Jira identity first.
    internal static string[] UniqueReferencedOwnerAliases(
        IEnumerable<JsonNode> assets, string ownerAttributeId, string verifiedDisplayName) {
        if(string.IsNullOrWhiteSpace(verifiedDisplayName))return [];
        var matches=new Dictionary<string,(string Key,string Label)>(StringComparer.OrdinalIgnoreCase);
        foreach(var asset in assets) {
            if(asset["attributes"] is not JsonArray attributes)continue;
            foreach(var attribute in attributes) {
                if(attribute?["objectTypeAttributeId"]?.ToString()!=ownerAttributeId &&
                    !string.Equals(attribute?["objectTypeAttribute"]?["name"]?.ToString(),
                        "Osoba_odpowiedzialna",StringComparison.OrdinalIgnoreCase))continue;
                foreach(var value in attribute?["objectAttributeValues"] as JsonArray ?? new JsonArray()) {
                    var reference=value?["referencedObject"];
                    if(reference is null || !string.Equals(reference["objectType"]?["name"]?.ToString(),
                        "Users",StringComparison.OrdinalIgnoreCase))continue;
                    var label=reference["label"]?.ToString() ?? value?["displayValue"]?.ToString();
                    if(!string.Equals(label,verifiedDisplayName,StringComparison.OrdinalIgnoreCase))continue;
                    var id=reference["id"]?.ToString();
                    if(string.IsNullOrWhiteSpace(id))continue;
                    matches[id]=(reference["objectKey"]?.ToString() ?? "",label ?? "");
                }
            }
        }
        if(matches.Count!=1)return [];
        var item=matches.Single();
        return new[]{item.Key,item.Value.Key,item.Value.Label}
            .Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static bool Matches(JsonNode? obj,string ownerAttributeId,IEnumerable<string> aliases) {
        if(obj is null || string.IsNullOrWhiteSpace(ownerAttributeId))return false;
        if(obj["attributes"] is not JsonArray attrs)return false;
        // Attribute IDs are defined per Jira object type. A laptop and a monitor
        // can use different IDs for the same Osoba_odpowiedzialna field.
        var ownerAttributes=attrs.Where(a=>
            a?["objectTypeAttributeId"]?.ToString()==ownerAttributeId ||
            string.Equals(a?["objectTypeAttribute"]?["name"]?.ToString(),
                "Osoba_odpowiedzialna",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(ownerAttributes.Length==0)return false;
        var allowed=new HashSet<string>(aliases.Where(x=>!string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);
        if(allowed.Count==0)return false;
        foreach(var owner in ownerAttributes)
        foreach(var value in owner?["objectAttributeValues"] as JsonArray ?? new JsonArray()) {
            foreach(var candidate in Strings(value).Concat(ReferenceIdentityStrings(value?["referencedObject"]))) {
                // Preserve old assets-portal behavior: Jira displayValue can
                // contain a comma-separated list of assigned Windows logins.
                if(candidate.Split(',',StringSplitOptions.TrimEntries|
                    StringSplitOptions.RemoveEmptyEntries).Any(part=>allowed.Contains(part)))
                    return true;
            }
        }
        return false;
    }
    // MC-SIRK traverses referencedObject.attributes. Restrict this to
    // explicit identity fields to avoid matching unrelated notes or serials.
    private static IEnumerable<string> ReferenceIdentityStrings(JsonNode? reference) {
        if(reference?["attributes"] is not JsonArray attrs)yield break;
        var identityNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "Email Address","Email","E-mail","emailAddress","accountId",
            "User Name","Username","Login","SAMAccountName","Display Name"
        };
        foreach(var attribute in attrs) {
            var name=attribute?["objectTypeAttribute"]?["name"]?.ToString() ??
                attribute?["name"]?.ToString();
            if(name is null || !identityNames.Contains(name))continue;
            foreach(var value in attribute?["objectAttributeValues"] as JsonArray ?? new JsonArray()) {
                foreach(var text in Strings(value))yield return text;
            }
        }
    }
    private static IEnumerable<string> Strings(JsonNode? node,int depth=0) {
        if(node is null||depth>6)yield break;
        if(node is JsonValue v) {
            if(v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                yield return text.Trim();
            yield break;
        }
        if(node is JsonArray array) {
            foreach(var item in array)
                foreach(var text in Strings(item,depth+1))yield return text;
            yield break;
        }
        if(node is not JsonObject obj)yield break;
        // Value and reference fields only. Ignore arbitrary object notes and
        // attribute names, and never use substrings or suffix matches.
        foreach(var key in new[]{"value","displayValue","searchValue","user",
            "referencedObject","label","name","objectKey","key","emailAddress",
            "email","username","accountId"}) {
            if(!obj.TryGetPropertyValue(key,out var item))continue;
            foreach(var text in Strings(item,depth+1))yield return text;
        }

    }
}
