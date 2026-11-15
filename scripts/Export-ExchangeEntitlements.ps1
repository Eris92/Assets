#requires -Version 5.1
<#
Read-only Exchange Online mail/delegation snapshot for Assets Portal.
CSV required columns: Login,UserPrincipalName
Connect-ExchangeOnline before execution.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UsersCsv,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1,10000)][int]$MaxMailboxes=3000,
    [ValidateRange(1,10000)][int]$MaxGroups=3000,
    [uri]$PortalUrl
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
foreach($n in @('Get-EXORecipientPermission','Get-Mailbox','Get-MailboxPermission','Get-DistributionGroup','Get-DistributionGroupMember')){
    if(-not(Get-Command $n -ErrorAction SilentlyContinue)){throw "Missing EXO cmdlet: $n"}
}
$users=@(Import-Csv -LiteralPath $UsersCsv)
if($users.Count -lt 1 -or $users.Count -gt 1000){throw 'CSV user count must be 1..1000'}
if(-not(Test-Path -LiteralPath $OutputDirectory)){[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null}
$out=(Resolve-Path -LiteralPath $OutputDirectory).Path
$index=@{};$rows=@{};$seen=@{}
foreach($u in $users){
    if(-not $u.PSObject.Properties['Login'] -or -not $u.PSObject.Properties['UserPrincipalName']){throw 'CSV needs Login and UserPrincipalName'}
    $login=([string]$u.Login).Trim()
    $upn=([string]$u.UserPrincipalName).Trim()
    if(-not $login -or -not $upn){throw 'Empty Login or UPN'}
    $rows[$login]=New-Object 'System.Collections.Generic.List[object]'
    foreach($alias in @($login,$upn,($login -split '\\')[-1])){
        $key=$alias.ToLowerInvariant()
        if($index.ContainsKey($key) -and $index[$key] -ne $login){throw "Ambiguous recipient: $key"}
        $index[$key]=$login
    }
}
function Add-Grant {
    param([string]$Principal,[string]$Resource,[string]$Display,[string]$Kind,[string]$Via,[string]$Evidence)
    if([string]::IsNullOrWhiteSpace($Principal) -or [string]::IsNullOrWhiteSpace($Resource)){return}
    $principalKey=$Principal.Trim().ToLowerInvariant()
    if(-not $index.ContainsKey($principalKey)){return}
    $user=$index[$principalKey]
    $key=$Resource.Trim().ToLowerInvariant()+'/'+$Kind
    if($key.Length -gt 2048){return}
    $dedupe=($user+'|'+$key).ToLowerInvariant()
    if($seen.ContainsKey($dedupe)){return}
    $seen[$dedupe]=$true
    $rows[$user].Add([pscustomobject]@{
        key=$key;name="$Kind - $Display";kind=$Kind
        details="Recipient: $Resource";via=$Via;evidence=$Evidence
    })
}
foreach($p in @(Get-EXORecipientPermission -AccessRights SendAs -ResultSize Unlimited)){
    if(-not $p.Deny){
        Add-Grant -Principal ([string]$p.Trustee) -Resource ([string]$p.Identity) -Display ([string]$p.Identity) -Kind 'SendAs' -Via 'Explicit' -Evidence 'Get-EXORecipientPermission'
    }
}
$mailboxes=@(Get-Mailbox -ResultSize ($MaxMailboxes+1))
if($mailboxes.Count -gt $MaxMailboxes){throw 'Mailbox safety limit exceeded'}
foreach($m in $mailboxes){
    $smtp=[string]$m.PrimarySmtpAddress
    if(-not $smtp){continue}
    foreach($d in @($m.GrantSendOnBehalfTo)){
        Add-Grant -Principal ([string]$d) -Resource $smtp -Display ([string]$m.DisplayName) -Kind 'SendOnBehalf' -Via 'Explicit' -Evidence 'Get-Mailbox'
    }
    foreach($p in @(Get-MailboxPermission -Identity $m.Identity -ErrorAction Stop)){
        if($p.Deny -or $p.IsInherited -or @($p.AccessRights) -notcontains 'FullAccess'){continue}
        Add-Grant -Principal ([string]$p.User) -Resource $smtp -Display ([string]$m.DisplayName) -Kind 'FullAccess' -Via 'Explicit' -Evidence 'Get-MailboxPermission'
    }
}
$groups=@(Get-DistributionGroup -ResultSize ($MaxGroups+1))
if($groups.Count -gt $MaxGroups){throw 'Mail group safety limit exceeded'}
foreach($g in $groups){
    $smtp=[string]$g.PrimarySmtpAddress
    if(-not $smtp){continue}
    foreach($d in @($g.GrantSendOnBehalfTo)){
        Add-Grant -Principal ([string]$d) -Resource $smtp -Display ([string]$g.DisplayName) -Kind 'SendOnBehalf' -Via 'Group delegation' -Evidence 'Get-DistributionGroup'
    }
    foreach($m in @(Get-DistributionGroupMember -Identity $g.Identity -ResultSize Unlimited)){
        $principal=if($m.PrimarySmtpAddress){[string]$m.PrimarySmtpAddress}else{[string]$m.Identity}
        Add-Grant -Principal $principal -Resource $smtp -Display ([string]$g.DisplayName) -Kind 'DistributionMember' -Via 'Direct member' -Evidence 'Get-DistributionGroupMember'
    }
}
$root=$null;$webSession=$null;$csrf=$null
if($PortalUrl){
    if($PortalUrl.Scheme -ne 'https' -or $PortalUrl.UserInfo){throw 'PortalUrl must be HTTPS without credentials'}
    $root=$PortalUrl.AbsoluteUri.TrimEnd('/')
    $me=Invoke-RestMethod -Method Get -Uri "$root/api/me" -UseDefaultCredentials -SessionVariable webSession
    if(-not $me.admin -or -not $me.csrf){throw 'Current Windows session is not an Assets administrator'}
    $csrf=[string]$me.csrf
}
foreach($u in $users){
    $login=([string]$u.Login).Trim()
    $itemList=@($rows[$login].ToArray())
    $obj=[pscustomobject]@{source='Exchange';userLogin=$login;items=$itemList}
    $json=ConvertTo-Json -InputObject $obj -Depth 6 -Compress
    $safe=$login -replace '[^a-zA-Z0-9_.-]','_'
    $file=Join-Path $out ("exchange-"+$safe+".json")
    [IO.File]::WriteAllText($file,$json,(New-Object System.Text.UTF8Encoding($false)))
    if($root){
        $body=[Text.Encoding]::UTF8.GetBytes($json)
        Invoke-RestMethod -Method Post -Uri "$root/api/admin/entitlements/import" -UseDefaultCredentials -WebSession $webSession -Headers @{'X-CSRF-TOKEN'=$csrf} -ContentType 'application/json; charset=utf-8' -Body $body | Out-Null
    }
    Write-Output ([pscustomobject]@{Login=$login;Entries=$itemList.Count;File=$file;Uploaded=[bool]$root})
}
# Limitations: unresolved DN/SID trustees, nested mail security groups,
# and dynamic distribution membership are not expanded. Imported results are
# snapshots and must not be treated as complete effective access.
