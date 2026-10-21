static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var user = new AccessRule("user", "User", "User", "User", "Allow");
var admin = new AccessRule("admin", "Admin", "Group", "Admin", "Allow");
var deny = new AccessRule("deny", "Blocked", "User", "User", "Deny");
Check(!AccessPolicy.Evaluate(false, true, [admin]).Allowed, "Anonymous bootstrap must not grant access");
Check(!AccessPolicy.Evaluate(true, false, []).Allowed, "Unlisted authenticated user must be denied");
var access=AccessPolicy.Evaluate(true, false, [user]);
Check(access.Allowed && !access.Admin, "User grant must not elevate");
Check(AccessPolicy.Evaluate(true, false, [admin]).Admin, "Admin group must grant admin");
Check(!AccessPolicy.Evaluate(true, false, [user, admin, deny]).Allowed, "Deny must override group and user grants");
Check(AccessPolicy.Evaluate(true, true, [deny]).Admin, "Bootstrap administrator must retain recovery access");
Console.WriteLine("PASS: anonymous, default deny, user role, admin role, deny precedence, bootstrap recovery");
