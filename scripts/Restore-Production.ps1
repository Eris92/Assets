#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param([string]$SiteName = 'Assets-KL', [string]$DeployRoot = 'C:\Sites\AssetsDeploy')
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
$site = Get-Website -Name $SiteName
if (-not $site) { throw "IIS site not found: $SiteName" }
$active = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath))
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $DeployRoot 'releases')).TrimEnd('\') + '\'
if (-not $active.StartsWith($releaseRoot,[StringComparison]::OrdinalIgnoreCase)) {
    throw "Active IIS directory is not a managed Assets release: $active"
}
$backupRoot = Join-Path $env:ProgramData 'Assets-Deploy\Backups'
$candidates = @(Get-ChildItem -LiteralPath $backupRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
$previous = $null
foreach ($dir in $candidates) {
    $metaFile = Join-Path $dir.FullName 'deployment.json'
    if (-not (Test-Path -LiteralPath $metaFile)) { continue }
    try { $meta = Get-Content -LiteralPath $metaFile -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { continue }
    if ($meta.SiteName -ne $SiteName) { continue }
    $new = [IO.Path]::GetFullPath([string]$meta.NewPath)
    if ($new.Equals($active,[StringComparison]::OrdinalIgnoreCase)) {
        $previous = [IO.Path]::GetFullPath([string]$meta.PreviousPath)
        break
    }
}
if (-not $previous) { throw 'No saved prior version for current IIS release.' }
if (-not $previous.StartsWith($releaseRoot,[StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath (Join-Path $previous 'AssetsPortal.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $previous 'web.config'))) {
    throw 'Previous version is not a verified runnable release; restore manually from backup.'
}
if (-not $PSCmdlet.ShouldProcess($SiteName, "Switch IIS to $previous")) { return }
$poolName = [string]$site.ApplicationPool
$filter = "system.applicationHost/sites/site[@name='$SiteName']/application[@path='/']/virtualDirectory[@path='/']"
function Wait-Pool {
    param([string]$Expected)
    for ($i=0; $i -lt 50; $i++) {
        if ((Get-WebAppPoolState -Name $poolName).Value -eq $Expected) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "IIS pool failed to reach state $Expected."
}
try {
    if ((Get-WebAppPoolState -Name $poolName).Value -eq 'Started') { Stop-WebAppPool -Name $poolName }
    Wait-Pool -Expected 'Stopped'
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name physicalPath -Value $previous
    Start-WebAppPool -Name $poolName
    Wait-Pool -Expected 'Started'
    Write-Host "Rollback completed: $active -> $previous"
    Write-Warning 'SQLite and keys were preserved; verify schema compatibility manually.'
} catch {
    $failure = $_
    try {
        Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name physicalPath -Value $active
        if ((Get-WebAppPoolState -Name $poolName).Value -eq 'Stopped') { Start-WebAppPool -Name $poolName }
    } catch { Write-Warning "Rollback recovery failed: $($_.Exception.Message)" }
    throw $failure
}
