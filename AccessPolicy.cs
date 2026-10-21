public record AccessRule(string Sid, string Name, string Kind, string Role, string Effect);
public record PortalAccess(bool Allowed, bool Admin, string Reason, bool Bootstrap);
public static class AccessPolicy {
    public static PortalAccess Evaluate(bool authenticated, bool bootstrap, IEnumerable<AccessRule> matching) {
        if (!authenticated) return new(false, false, "NotAuthenticated", false);
        if (bootstrap) return new(true, true, "BootstrapAdministrator", true);
        var rules = matching.ToArray();
        if (rules.Any(r => r.Effect == "Deny")) return new(false, false, "ExplicitDeny", false);
        var allow = rules.Where(r => r.Effect == "Allow").ToArray();
        return new(allow.Length > 0, allow.Any(r => r.Role == "Admin"), allow.Length > 0 ? "AccessGranted" : "NoAccessRule", false);
    }
}
