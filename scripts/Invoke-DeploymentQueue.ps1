#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$SiteName = 'Assets-KL', [string]$RepoRoot = 'C:\Sites\Assets', [string]$DeployRoot = 'C:\Sites\AssetsDeploy')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$queue = Join-Path $env:ProgramData 'Assets\Data\deployment-queue'
$pending = Join-Path $queue 'pending.json'
$processing = Join-Path $queue 'processing.json'
$statusPath = Join-Path $queue 'status.json'
if (-not (Test-Path -LiteralPath $pending -PathType Leaf)) { return }
if (-not (Get-ScheduledTask -TaskName 'AssetsWeb-Production-Updates' -ErrorAction SilentlyContinue)) {
    throw 'Authorized scheduled task is missing.'
}
if (Test-Path -LiteralPath $processing) { throw 'Unresolved interrupted request: processing.json. Reconcile before retry.' }
try { Move-Item -LiteralPath $pending -Destination $processing -ErrorAction Stop }
catch { return }
$id = ''
$action = ''
$actor = ''
function Write-WorkerStatus {
    param([string]$State, [string]$Detail)
    $value = [ordered]@{
        schema = 1; id = $id; action = $action; requestedBy = $actor
        state = $State; updatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
        message = $Detail
    }
    $temp = $statusPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temp, (ConvertTo-Json -InputObject $value -Depth 5 -Compress), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $statusPath -Force
}
try {
    $raw = Get-Content -LiteralPath $processing -Raw -Encoding UTF8
    if ($raw.Length -gt 2048) { throw 'Request exceeded the allowed size.' }
    $job = $raw | ConvertFrom-Json -ErrorAction Stop
    $id = [string]$job.id
    $action = [string]$job.action
    $actor = [string]$job.actor
    $at = [DateTimeOffset]::Parse([string]$job.requestedAtUtc)
    $delta = ([DateTimeOffset]::UtcNow - $at.ToUniversalTime()).TotalMinutes
    if ($job.schema -ne 1 -or $action -notin @('update','rollback') -or
        $id -notmatch '^[0-9a-fA-F-]{36}$' -or $actor.Length -gt 256 -or
        $delta -gt 120 -or $delta -lt -5) { throw 'Invalid, expired or unsupported deployment request.' }
    Write-WorkerStatus -State 'Running' -Detail "Executing $action"
    if ($action -eq 'update') {
        $updater = Join-Path $RepoRoot 'scripts\Update-Production.ps1'
        if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) { throw 'Update script is missing from source checkout.' }
        & $updater -SiteName $SiteName -DeployRoot $DeployRoot
    } else {
        & (Join-Path $PSScriptRoot 'Restore-Production.ps1') -SiteName $SiteName -DeployRoot $DeployRoot
    }
    Import-Module WebAdministration
    $site = Get-Website -Name $SiteName
    $live = [string]$site.PhysicalPath
    $state = (Get-WebAppPoolState -Name ([string]$site.ApplicationPool)).Value
    if ($state -ne 'Started' -or -not (Test-Path -LiteralPath (Join-Path $live 'AssetsPortal.dll'))) {
        throw 'IIS pool or release DLL verification failed.'
    }
    Write-WorkerStatus -State 'Succeeded' -Detail "Completed $action; IIS PhysicalPath=$live"
} catch {
    $detail = [string]$_.Exception.Message
    if ($detail.Length -gt 1000) { $detail = $detail.Substring(0,1000) }
    Write-WorkerStatus -State 'Failed' -Detail $detail
    throw
} finally {
    # Never replay a deployment after an ambiguous or partial execution.
    Remove-Item -LiteralPath $processing -Force -ErrorAction SilentlyContinue
}
