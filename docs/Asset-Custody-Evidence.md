# Assets Portal - custody and legal evidence

## Available workflows

- AD managers may assign up to three substitute viewers and revoke access. The substitute
  sees only the manager's team and cannot delegate onward.
- The team is read from AD manager relationships when team.json is absent.
- Jira ownership is queried by each employee's email, not by raw Windows login
  display values. The team UI supports employee and flat hardware views.
- Permissions currently means direct AD memberOf groups only. Effective rights
  across nested groups, Entra, Jira, SaaS and third-party systems are NOT yet mapped.
- A handover can have one leg or two legs, via an intermediate manager.
  Sender confirms dispatch and receiver confirms actual receipt. The second leg
  cannot start until the manager accepts the first.
- A pending second leg remains on the intermediate manager's outstanding-custody
  dashboard. The operator also sees the outstanding manager and elapsed days.
- A PDF of physically signed paper evidence can be uploaded (10 MB maximum) to
  the restricted evidence vault outside IIS root; the server calculates SHA-256.
  A hash entered by the user without a stored PDF is not sufficient.
- Explicit SSO confirmations are stored with Windows identity, UTC, client IP,
  User-Agent, statement, previous event hash, and current SHA-256. An evidence
  endpoint verifies the chain.
- Protocol creation and acknowledgement do NOT yet update ownership in Jira.
  Until a separate verified Jira reconciliation flow exists, a local custody
  record and Jira ownership may disagree.
- Yearly physical inventory and direct AD groups review are separate. The user
  inspects every item and affirms a server-verified SHA-256 snapshot. A source
  change between viewing and confirming returns HTTP 409.
- Signed paper evidence is the alternate annual review route. Admins archive
  the signed PDF in the vault and register its SHA-256.
- Reminders: 30, 14, 1 and 0 days before the configured deadline, suppressed
  after a successful review. Each channel/day/review is idempotent with up to
  three retries on delivery failure.
- SMTP uses STARTTLS, ports 25/587, protected SMTP password. Teams uses
  Power Automate Workflows webhook, protected by Data Protection. Office 365
  Connector webhooks are retired.

## Legal status

Electronic confirmations and logs can be evidence but are not automatically
equivalent to a qualified electronic signature (QES). Article 25 eIDAS does not
allow rejecting a signature solely because of its electronic form, but QES has
the handwritten-signature equivalent effect. Polish Civil Code Article 78(1)
defines electronic form using QES.

Official laws:
- https://eur-lex.europa.eu/eli/reg/2014/910/2024-05-20
- https://eli.gov.pl/api/acts/DU/2024/1061/text.html

The local hash chain detects inconsistent edits, not tampering by administrators
who can change both the SQLite database and hashes. Legal-grade evidence should
also be shipped off-host to independently administered WORM/append-only storage.
Consider a qualified timestamp, a PAdES/QES provider, LTV validation including
OCSP/CRL and archival of the original signed bytes. Choose the provider from
EU trusted lists and obtain legal/HR review of retention, employment policy and
the exact employee declaration. Do not label SSO confirmation as QES.

## Notifications and operational requirements

Settings > SMTP i Teams enables delivery and separate due dates (MM-DD).
A Teams Workflow must route the JSON recipient email and text fields to the
appropriate person; an incoming channel webhook alone does not guarantee DM.
See Microsoft Learn:
https://learn.microsoft.com/microsoftteams/platform/webhooks-and-connectors/how-to/add-incoming-webhook

Annual review rows are currently enrolled on the employee's login and when a
manager opens team review status. A fully proactive enrollment of every active
worker requires a separate AD/HR sync. Background reminders run in the IIS app;
for reliability configure AlwaysRunning/Application Initialization and monitor
the process. A separate scheduler with independent availability is recommended.

Evidence files are stored under ProgramData/Assets/Data/evidence, not the IIS
PhysicalPath. Keep NTFS ACLs restricted, back up the full directory (including
keys and SQLite), and copy or anchor hashes off-host for independent integrity.

## Verification checklist

1. Install the verified GitHub Release with the existing Update-Production.ps1.
2. Check /api/me, /api/assets and manager/substitute team inventory under
   two different Windows sessions.
3. Revoke substitute access and verify the same session loses delegated scope.
4. Test equipment filtering by serial number, and both team list modes.
5. Create a direct protocol and two-leg manager protocol. Use separate Windows
   identities for dispatch, manager receipt and final receipt.
6. Verify pending manager custody after stage one, then its disappearance after
   stage two completion. Export evidence JSON and check chainValid.
7. Upload a signed PDF; compare the server SHA-256 with Get-FileHash.
8. Send SMTP and Teams test notifications and verify their recipient routing.
9. Check annual asset and AD group snapshot screens, and change source data
   between loading and confirmation (expected HTTP 409).
10. Confirm a recent backup of SQLite, Data Protection keys and signed PDFs
    before any binary rollback. Old versions may not understand schema changes.
