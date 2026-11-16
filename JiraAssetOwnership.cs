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
            foreach(var candidate in Strings(value)) {
                // Preserve old assets-portal behavior: Jira displayValue can
                // contain a comma-separated list of assigned Windows logins.
                if(candidate.Split(',',StringSplitOptions.TrimEntries|
                    StringSplitOptions.RemoveEmptyEntries).Any(part=>allowed.Contains(part)))
                    return true;
            }
        }
        return false;
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
