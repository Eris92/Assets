#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$Account = [Security.Principal.WindowsIdentity]::GetCurrent().Name)
$ErrorActionPreference = 'Stop'
$identity = New-Object Security.Principal.NTAccount($Account)
$sid = $identity.Translate([Security.Principal.SecurityIdentifier]).Value
if (-not $env:ProgramData) { throw 'ProgramData is unavailable. Run on the Windows web server.' }
$configPath = Join-Path $env:ProgramData 'AssetsPortal\appsettings.json'
$template = Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.org.json'
if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
} else {
    if (-not (Test-Path -LiteralPath $template)) { throw 'Configuration template not found.' }
    $config = Get-Content -LiteralPath $template -Raw -Encoding UTF8 | ConvertFrom-Json
}
if (-not $config.Portal) { $config | Add-Member -NotePropertyName Portal -NotePropertyValue ([pscustomobject]@{}) -Force }
$admins = @($config.Portal.AdminUsers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($admins -contains $sid) { Write-Host "Recovery administrator already configured: $Account ($sid)"; return }
$admins += $sid
$config.Portal | Add-Member -NotePropertyName AdminUsers -NotePropertyValue @($admins) -Force
New-Item -ItemType Directory -Path (Split-Path $configPath -Parent) -Force | Out-Null
if (Test-Path -LiteralPath $configPath) {
    $backup = $configPath + '.' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.bak'
    Copy-Item -LiteralPath $configPath -Destination $backup -ErrorAction Stop
    Write-Host "Configuration backup: $backup"
}
$json = $config | ConvertTo-Json -Depth 50
$json | ConvertFrom-Json | Out-Null
[IO.File]::WriteAllText($configPath, $json, (New-Object Text.UTF8Encoding($false)))
Write-Host "Recovery administrator configured: $Account ($sid)"
Write-Host 'Reload the portal. Current configuration and Jira settings were preserved.'
