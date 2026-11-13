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
        var sam=login.Trim().Split('\\').Last();
        if(string.IsNullOrWhiteSpace(sam)||sam.Length>128)
            throw new ArgumentException("Invalid AD samAccountName");
        var filter=sam.Contains('@') ? "(userprincipalname=" + EscapeFilter(sam) + ")" :
            "(samaccountname=" + EscapeFilter(sam) + ")";
        var result=Find(filter,2);
        if (result.Count != 1) throw new InvalidOperationException("User not found or ambiguous in Active Directory");
        return result[0];
    }

    // Direct-report lookup from AD 'manager' DN. Used if no team.json is configured.
    // Operations are bounded and read-only; no LDAP filter values originate from the caller unescaped.
    public static TeamMember[] ManagedTeam(string managerLogin) {
        if (string.IsNullOrWhiteSpace(managerLogin) || managerLogin.Length > 256)
            return [];
        var domain = managerLogin.Contains('\\') ? managerLogin.Split('\\')[0] : "";
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queued = new Queue<(string Login, int Depth)>();
        queued.Enqueue((managerLogin.Split('\\').Last(), 0));
        visited.Add(managerLogin.Split('\\').Last());
        var items = new List<TeamMember>();
        using var rootDse = new DirectoryEntry("LDAP://RootDSE", null, null, SecureBinding);
        var domainDn = Convert.ToString(rootDse.Properties["defaultNamingContext"].Value);
        if (string.IsNullOrWhiteSpace(domainDn)) return [];
        using var root = new DirectoryEntry("LDAP://" + domainDn, null, null, SecureBinding);
        while (queued.Count != 0 && items.Count < 1500) {
            var (manager, depth) = queued.Dequeue();
            if (depth >= 8) continue;
            using var parentSearcher = new DirectorySearcher(root) {
                Filter = "(&(objectCategory=person)(objectClass=user)(samaccountname=" + EscapeFilter(manager) + "))",
                SizeLimit = 2, ClientTimeout = TimeSpan.FromSeconds(7)
            };
            parentSearcher.PropertiesToLoad.Add("distinguishedname");
            var parent = parentSearcher.FindOne();
            if (parent is null || !parent.Properties.Contains("distinguishedname")) continue;
            var managerDn = Convert.ToString(parent.Properties["distinguishedname"][0]);
            if (string.IsNullOrWhiteSpace(managerDn)) continue;
            using var children = new DirectorySearcher(root) {
                Filter = "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))(manager=" + EscapeFilter(managerDn) + "))",
                SizeLimit = Math.Min(200,1500-items.Count), PageSize = 100,
                ClientTimeout = TimeSpan.FromSeconds(10), ServerTimeLimit = TimeSpan.FromSeconds(8),
                ReferralChasing = ReferralChasingOption.None
            };
            foreach (var property in new[]{"samaccountname","displayname","title"}) children.PropertiesToLoad.Add(property);
            using var results = children.FindAll();
            foreach (SearchResult row in results) {
                var sam = Property(row, "samaccountname");
                if (string.IsNullOrWhiteSpace(sam) || !visited.Add(sam)) continue;
                var name = Property(row, "displayname");
                var full = string.IsNullOrWhiteSpace(domain) ? sam : domain + "\\" + sam;
                var parentFull = string.IsNullOrWhiteSpace(domain) ? manager : domain + "\\" + manager;
                items.Add(new TeamMember(full, string.IsNullOrWhiteSpace(name) ? sam : name, parentFull, Property(row, "title")));
                queued.Enqueue((sam, depth+1));
            }
        }
        return items.ToArray();
    }

    // Direct membership only. It is displayed as AD groups, not as effective
    // transitive app permissions (which require separate source integrations).
    public static string[] DirectGroups(string login) {
        var sam = login.Split('\\').Last();
        if (sam.Length is < 1 or > 128 || sam.Any(char.IsControl)) return [];
        using var rootDse = new DirectoryEntry("LDAP://RootDSE", null, null, SecureBinding);
        var dn = Convert.ToString(rootDse.Properties["defaultNamingContext"].Value);
        if (string.IsNullOrWhiteSpace(dn)) return [];
        using var root = new DirectoryEntry("LDAP://" + dn, null, null, SecureBinding);
        using var searcher = new DirectorySearcher(root) {
            Filter = "(&(objectCategory=person)(objectClass=user)(samaccountname=" + EscapeFilter(sam) + "))",
            SizeLimit = 2, ClientTimeout = TimeSpan.FromSeconds(8),
            ReferralChasing = ReferralChasingOption.None
        };
        searcher.PropertiesToLoad.Add("memberof");
        var entry = searcher.FindOne();
        if (entry is null) return [];
        if (!entry.Properties.Contains("memberof")) return [];
        return entry.Properties["memberof"].OfType<object>()
            .Select(x => Convert.ToString(x) ?? "")
            .Where(x => x.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            .Select(x => x[3..].Split(',')[0].Replace("\\,", ",", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(300).ToArray();
    }
}
