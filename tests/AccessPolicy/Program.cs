using System.Text.Json.Nodes;
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

var fullPage = JsonNode.Parse("""{"values":[{"id":"1"},{"id":"2"}],"totalFilterCount":1000,"hasMoreResults":true}""")!;
Check(JiraAssetWorkflow.Entries(fullPage).Count == 2, "AQL values parsing");
Check(JiraAssetWorkflow.HasMore(fullPage, 998, 2, 2), "Capped total must not truncate pagination");
var finalPage = JsonNode.Parse("""{"values":[{"id":"3"}],"isLast":true}""")!;
Check(!JiraAssetWorkflow.HasMore(finalPage, 1000, 1, 100), "Last page detection");
var before = JsonNode.Parse("""{"objectAttributeValues":[{"value":"abc","referencedObject":{"id":"21"}}]}""")!;
var same = JsonNode.Parse("""{"objectAttributeValues":[{"referencedObject":{"id":"21"},"value":"abc"}]}""")!;
var changed = JsonNode.Parse("""{"objectAttributeValues":[{"value":"abc","referencedObject":{"id":"22"}}]}""")!;
Check(JiraAssetWorkflow.AttributeFingerprint(before) == JiraAssetWorkflow.AttributeFingerprint(same), "Fingerprint must ignore JSON property ordering");
Check(JiraAssetWorkflow.AttributeFingerprint(before) != JiraAssetWorkflow.AttributeFingerprint(changed), "Fingerprint must detect changed owner");
Check(!JiraAssetWorkflow.IsVerified(before, "attribute", "unexpected"), "Verify must detect mismatch");
Check(JiraAssetWorkflow.IsVerified(JsonNode.Parse("""{"objectAttributeValues":[]}"""), "not-held", ""), "Owner removal verification");
var comment = JiraAssetWorkflow.AuditComment(7, "EQ-7", "Model", "<script>", "new", "reason", "DOMAIN\\user", "DOMAIN\\admin", "attribute");
Check(!comment.Contains("<script>") && comment.Contains("&lt;script&gt;") && comment.Contains("Assets request #7"), "Comment must escape HTML and carry audit ID");
Console.WriteLine("PASS: Jira pagination, owner snapshots, readback verification and comment escaping");
