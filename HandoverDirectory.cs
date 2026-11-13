using System.DirectoryServices;

internal sealed record HandoverPerson(string Login, string Name, string Email, string Department);

internal static class HandoverDirectory {
    private const AuthenticationTypes SecureBinding =
        AuthenticationTypes.Secure | AuthenticationTypes.Signing | AuthenticationTypes.Sealing;

    // Escaping RFC4515 filter metacharacters prevents directory filter injection.
    private static string EscapeFilter(string text) => text
        .Replace("\\", "\\5c", StringComparison.Ordinal)
        .Replace("*", "\\2a", StringComparison.Ordinal)
        .Replace("(", "\\28", StringComparison.Ordinal)
        .Replace(")", "\\29", StringComparison.Ordinal)
        .Replace("\0", "\\00", StringComparison.Ordinal);

    private static string Property(SearchResult row, string name) =>
        row.Properties.Contains(name) && row.Properties[name].Count > 0
            ? Convert.ToString(row.Properties[name][0]) ?? "" : "";

    private static List<HandoverPerson> Find(string filter, int size) {
        using var rootDse = new DirectoryEntry("LDAP://RootDSE", null, null, SecureBinding);
        var domainDn = Convert.ToString(rootDse.Properties["defaultNamingContext"].Value);
        if (string.IsNullOrWhiteSpace(domainDn))
            throw new InvalidOperationException("Active Directory defaultNamingContext is unavailable.");
        using var root = new DirectoryEntry("LDAP://" + domainDn, null, null, SecureBinding);
        using var search = new DirectorySearcher(root) {
            Filter = "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))" + filter + ")",
            SearchScope = SearchScope.Subtree, SizeLimit = size, PageSize = Math.Min(size, 50),
            ClientTimeout = TimeSpan.FromSeconds(8), ServerTimeLimit = TimeSpan.FromSeconds(6),
            ReferralChasing = ReferralChasingOption.None
        };
        foreach (var field in new[] {"samaccountname","displayname","mail","department"})
            search.PropertiesToLoad.Add(field);
        using var results = search.FindAll();
        var people = new List<HandoverPerson>();
        foreach (SearchResult row in results) {
            var login = Property(row, "samaccountname");
            var email = Property(row, "mail");
            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(email) ||
                email.Length > 254 || email.Count(c => c == '@') != 1) continue;
            people.Add(new HandoverPerson(login, Property(row, "displayname") is { Length: > 0 } n ? n : login,
                email, Property(row, "department")));
        }
        return people.DistinctBy(p => p.Login, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<HandoverPerson> Search(string query) {
        if (query.Length is < 2 or > 80 || query.Any(char.IsControl))
            throw new ArgumentException("Search must contain 2-80 visible characters");
        var value = EscapeFilter(query.Trim());
        return Find("(|(samaccountname=*" + value + "*)(displayname=*" + value + "*)(mail=*" + value + "*))", 30);
    }

    public static HandoverPerson Resolve(string login) {
        if (string.IsNullOrWhiteSpace(login) || login.Length > 128 || login.Any(char.IsControl))
            throw new ArgumentException("Invalid AD login");
        var result = Find("(samaccountname=" + EscapeFilter(login.Trim()) + ")", 2);
        if (result.Count != 1) throw new InvalidOperationException("User not found or ambiguous in Active Directory");
        return result[0];
    }
}
