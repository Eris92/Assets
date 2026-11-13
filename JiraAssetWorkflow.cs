using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

// Jira Assets payload parsing and change validation. No network or DB dependencies.
public static class JiraAssetWorkflow {
    public static JsonArray Entries(JsonNode response) =>
        response["values"] as JsonArray ??
        response["objectEntries"] as JsonArray ??
        response["results"]?["objectEntries"] as JsonArray ??
        new JsonArray();

    public static bool HasMore(JsonNode response, int startAt, int count, int pageSize) {
        if (count == 0) return false;
        if (bool.TryParse(response["hasMoreResults"]?.ToString(), out var hasMore)) return hasMore;
        if (bool.TryParse(response["isLast"]?.ToString(), out var isLast)) return !isLast;
        if (count < pageSize) return false;
        var totalText = response["totalFilterCount"]?.ToString() ?? response["total"]?.ToString();
        if (int.TryParse(totalText, out var total) && total >= 0 && total < 1000)
            return startAt + count < total;
        // Assets totalFilterCount may be capped at 1000; a full page is not proof of EOF.
        return true;
    }

    public static int ValueCount(JsonNode? attribute) =>
        (attribute?["objectAttributeValues"] as JsonArray)?.Count ?? 0;

    public static string AttributeFingerprint(JsonNode? attribute) {
        var values = attribute?["objectAttributeValues"] as JsonArray;
        if (values is null || values.Count == 0) return "[]";
        var stable = values.Select(value => new {
            Value = value?["value"]?.ToString() ?? "",
            User = value?["user"]?["accountId"]?.ToString() ?? "",
            Reference = value?["referencedObject"]?["id"]?.ToString() ??
                        value?["referencedObject"]?["objectKey"]?.ToString() ?? ""
        }).Select(x => JsonSerializer.Serialize(x)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(stable);
    }

    public static string AttributeText(JsonNode? attribute) =>
        string.Join(", ", (attribute?["objectAttributeValues"] as JsonArray ?? new JsonArray())
            .Select(value => value?["displayValue"]?.ToString() ??
                             value?["referencedObject"]?["label"]?.ToString() ??
                             value?["referencedObject"]?["name"]?.ToString() ??
                             value?["referencedObject"]?["objectKey"]?.ToString() ??
                             value?["user"]?["displayName"]?.ToString() ??
                             value?["value"]?.ToString() ?? ""));

    public static bool IsVerified(JsonNode? attribute, string reportType, string proposed) {
        if (reportType != "attribute") return ValueCount(attribute) == 0;
        var values = attribute?["objectAttributeValues"] as JsonArray;
        return values?.Count == 1 &&
            string.Equals(values[0]?["value"]?.ToString() ?? values[0]?["displayValue"]?.ToString(),
                proposed, StringComparison.Ordinal);
    }

    public static string AuditComment(long reportId, string objectKey, string attributeName,
        string oldValue, string newValue, string reason, string reporter, string approver, string reportType) {
        string H(string? value) => WebUtility.HtmlEncode((value ?? "").Length > 2000 ? value![..2000] : value);
        var label = reportType == "attribute" ? "Korekta atrybutu" : "Aktualizacja przypisania";
        return "<p><strong>" + H(label) + "</strong> | Assets request #" + reportId + "</p>" +
               "<p>Obiekt: " + H(objectKey) + "</p>" +
               "<p>Atrybut: " + H(attributeName) + "</p>" +
               "<p>Poprzednia wartosc: " + H(oldValue) + "</p>" +
               "<p>Nowa wartosc: " + H(reportType == "attribute" ? newValue : "(usunięto przypisanie)") + "</p>" +
               "<p>Uzasadnienie: " + H(reason) + "</p>" +
               "<p>Zglosil: " + H(reporter) + "</p>" +
               "<p>Zatwierdzil: " + H(approver) + "</p>";
    }
}
