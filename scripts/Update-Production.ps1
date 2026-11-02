[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SiteName = 'Assets-KL',
    [string]$DeployRoot = 'C:\Sites\AssetsDeploy',
    [string]$HealthUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$deployRootFull = [IO.Path]::GetFullPath($DeployRoot)
$separator = [IO.Path]::DirectorySeparatorChar
if ($deployRootFull.TrimEnd($separator).Equals($repoRoot.TrimEnd($separator), [StringComparison]::OrdinalIgnoreCase) -or
    $deployRootFull.StartsWith($repoRoot.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase) -or
    $repoRoot.StartsWith($deployRootFull.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DeployRoot must be separate from the Git checkout.'
}
if ($WhatIfPreference) {
    Write-Host "Would pull main, download a verified release, back up state and switch IIS site $SiteName."
    return
}
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { throw 'Git is not installed.' }
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run Windows PowerShell elevated.' }
Import-Module WebAdministration -ErrorAction Stop
$site = Get-Website -Name $SiteName
if (-not $site) { throw "IIS site not found: $SiteName" }
$poolName = [string]$site.ApplicationPool
$pool = Get-Item "IIS:\AppPools\$poolName"
if ($pool.enable32BitAppOnWin64) { throw "App pool $poolName must use x64." }
if (-not (Get-WebGlobalModule -Name AspNetCoreModuleV2)) { throw 'Hosting Bundle / ANCM V2 missing.' }
$runtimes = & (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') --list-runtimes
if ($LASTEXITCODE -ne 0 -or -not ($runtimes -match '^Microsoft\.NETCore\.App 10\.') -or
    -not ($runtimes -match '^Microsoft\.AspNetCore\.App 10\.')) {
    throw 'ASP.NET Core shared runtimes 10.x not found.'
}
# IIS operations are asynchronous; do not start a pool while it is Stopping.
function Wait-AppPoolState {
    param([string]$Name, [string]$Expected, [int]$Attempts = 60)
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        try {
            $state = [string](Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value
            if ($state -eq $Expected) { return }
        } catch {
            Write-Verbose "Reading app pool state: $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 500
    }
    throw "App pool $Name did not reach state $Expected."
}
function Stop-AppPoolSafely {
    param([string]$Name, [int]$Attempts = 40)
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        try {
            $state = [string](Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value
            if ($state -eq 'Stopped') { return }
            if ($state -eq 'Started') {
                Stop-WebAppPool -Name $Name -ErrorAction Stop
            }
        } catch {
            Write-Verbose "App pool stop attempt $($attempt + 1): $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 500
    }
    throw "App pool $Name could not stop. Check WAS/W3SVC services and IIS event logs."
}
function Start-AppPoolSafely {
    param([string]$Name, [int]$Attempts = 40)
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        try {
            $state = [string](Get-WebAppPoolState -Name $Name -ErrorAction Stop).Value
            if ($state -eq 'Started') { return }
            if ($state -eq 'Stopped') {
                Start-WebAppPool -Name $Name -ErrorAction Stop
            }
        } catch {
            # WAS often returns 0x80070425 while a previous stop is completing.
            Write-Verbose "App pool start attempt $($attempt + 1): $($_.Exception.Message)"
        }
        Start-Sleep -Milliseconds 500
    }
    throw "App pool $Name could not start. Check WAS/W3SVC services and IIS event logs."
}

Write-Host 'Pulling source...'
# Windows PowerShell 5.1 converts native Git stderr (including normal fetch status)
# into NativeCommandError when ErrorActionPreference is Stop. Run Git as a
# separate process and check the real exit code instead.
$gitExe = (Get-Command git.exe -ErrorAction Stop).Source
$gitArgs = '-C "{0}" pull --ff-only origin main' -f $repoRoot
$gitProcess = Start-Process -FilePath $gitExe -ArgumentList $gitArgs -NoNewWindow -Wait -PassThru
if ($gitProcess.ExitCode -ne 0) {
    throw "git pull failed with exit code $($gitProcess.ExitCode). No IIS changes were made."
}
$commit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid HEAD SHA.' }

$headers = @{ 'User-Agent' = 'AssetsPortal-ProductionUpdater'; 'Accept' = 'application/vnd.github+json' }
$tag = 'assets-' + $commit
$releaseUrl = 'https://api.github.com/repos/Eris92/Assets/releases/tags/' + $tag
$release = $null
# Use the exact commit-tagged release, never the global "latest" alias.
# CI may finish shortly after git pull. Retry only missing releases, bounded.
for ($attempt = 1; $attempt -le 6; $attempt++) {
    try {
        $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -TimeoutSec 30 -ErrorAction Stop
        break
    } catch {
        $response = $_.Exception.Response
        $status = if ($null -ne $response) { [int]$response.StatusCode } else { 0 }
        if ($status -ne 404) {
            throw "GitHub release API failed (HTTP $status): $($_.Exception.Message)"
        }
        if ($attempt -eq 6) {
            throw "Release $tag is not published. Check the CI release job; no IIS changes were made."
        }
        Write-Host "Release for $commit is not published yet ($attempt/6). Retrying..."
        Start-Sleep -Seconds 10
    }
}
if ($null -eq $release -or $release.tag_name -ne $tag -or
    $release.draft -or $release.prerelease -or
    $release.target_commitish -ne $commit) {
    throw "Release metadata does not match commit $commit. No IIS changes were made."
}
$assets = @($release.assets)
$zipName = 'AssetsPortal-win-x64.zip'
$sumName = $zipName + '.sha256'
$zip = @($assets | Where-Object { $_.name -eq $zipName })
$sum = @($assets | Where-Object { $_.name -eq $sumName })
if ($zip.Count -ne 1 -or $sum.Count -ne 1) { throw 'Release ZIP or SHA256 asset missing.' }
foreach ($asset in @($zip[0], $sum[0])) {
    if (-not $asset.browser_download_url.StartsWith("https://github.com/Eris92/Assets/releases/download/$tag/", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Invalid release asset URL.'
    }
}
$oldPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath))
# Refuse to deploy into a site pointing at an unrelated application.
$releaseRoot = [IO.Path]::GetFullPath((Join-Path $deployRootFull 'releases')).TrimEnd($separator) + $separator
if (-not $oldPath.TrimEnd($separator).Equals($repoRoot.TrimEnd($separator), [StringComparison]::OrdinalIgnoreCase) -and
    -not $oldPath.StartsWith($releaseRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "IIS site $SiteName points outside the Assets checkout/deploy root: $oldPath. No changes were made."
}
$newPath = Join-Path $deployRootFull (Join-Path 'releases' $commit)
if ($oldPath.TrimEnd($separator).Equals($newPath.TrimEnd($separator), [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "Already active: $commit"
    return
}
if (Test-Path -LiteralPath $newPath) {
    # An earlier interrupted deployment may have left an unused release dir.
    # Move it aside instead of silently deleting or trusting stale files.
    $recovered = $newPath + '.interrupted-' + (Get-Date -Format 'yyyyMMddHHmmssfff')
    Rename-Item -LiteralPath $newPath -NewName (Split-Path $recovered -Leaf) -ErrorAction Stop
    Write-Warning "Previous staged directory was preserved at: $recovered"
}

$temp = Join-Path $env:TEMP ('AssetsPortal_' + [guid]::NewGuid().ToString('N'))
$backupRoot = Join-Path $env:ProgramData 'Assets-Deploy\Backups'
$backupName = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$backupDir = Join-Path $backupRoot $backupName
$dataPath = Join-Path $env:ProgramData 'Assets'
$dataDirectory = Join-Path $dataPath 'Data'
$newConfig = Join-Path $dataPath 'appsettings.json'
$legacyAssetsConfig = Join-Path $repoRoot 'appsettings.json'
$configTemplate = Join-Path $repoRoot 'appsettings.org.json'
# Configuration, SQLite and DPAPI keys belong only to Eris92/Assets.
# Do not import settings from a different IIS project or application.
$configSource = $null
if (Test-Path -LiteralPath $newConfig -PathType Leaf) {
    # Never silently replace an established production configuration.
    $configSource = $newConfig
} elseif (Test-Path -LiteralPath $legacyAssetsConfig -PathType Leaf) {
    # A checkout may retain an old appsettings.json from an unrelated project.
    # Import it only when it references the isolated Assets data directory.
    try {
        $legacy = Get-Content -LiteralPath $legacyAssetsConfig -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $legacy.Jira -or $null -eq $legacy.Portal) {
            throw 'Missing Jira or Portal section.'
        }
        $legacyDb = [string]$legacy.Portal.DatabasePath
        if ([string]::IsNullOrWhiteSpace($legacyDb)) {
            $configSource = $legacyAssetsConfig
        } else {
            $legacyDbFull = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($legacyDb))
            if ($legacyDbFull.StartsWith($dataPath.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
                $configSource = $legacyAssetsConfig
            } else {
                Write-Warning "Ignoring checkout appsettings.json: its DatabasePath is outside the Assets data directory. The original file was not modified."
            }
        }
    } catch {
        Write-Warning "Ignoring invalid checkout appsettings.json. The original file was not modified: $($_.Exception.Message)"
    }
}
if ($null -eq $configSource) {
    if (-not (Test-Path -LiteralPath $configTemplate -PathType Leaf)) {
        throw 'Assets configuration template is missing from the checkout. IIS was not changed.'
    }
    $configSource = $configTemplate
}
try {
    $cfg = Get-Content -LiteralPath $configSource -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $cfg.Jira -or $null -eq $cfg.Portal) {
        throw 'Missing Jira or Portal section.'
    }
    $dbConfig = [string]$cfg.Portal.DatabasePath
    if (-not [string]::IsNullOrWhiteSpace($dbConfig)) {
        $dbFull = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($dbConfig))
        if (-not $dbFull.StartsWith($dataPath.TrimEnd($separator) + $separator, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Portal:DatabasePath must be inside $dataPath, not $dbFull."
        }
    }
} catch {
    throw "Invalid Assets configuration at $configSource. IIS was not changed. $($_.Exception.Message)"
}
$poolWasStarted = (Get-WebAppPoolState -Name $poolName).Value -eq 'Started'
$stopped = $false
$changed = $false
$oldWindows = $null
$oldAnonymous = $null
$pathFilter = "system.applicationHost/sites/site[@name='$SiteName']/application[@path='/']/virtualDirectory[@path='/']"
$winFilter = 'system.webServer/security/authentication/windowsAuthentication'
$anonFilter = 'system.webServer/security/authentication/anonymousAuthentication'
try {
    New-Item -Path $temp -ItemType Directory -Force | Out-Null
    $zipPath = Join-Path $temp $zipName
    $sumPath = Join-Path $temp $sumName
    Write-Host "Downloading $tag..."
    Invoke-WebRequest -Uri $zip[0].browser_download_url -OutFile $zipPath -UseBasicParsing -TimeoutSec 300
    Invoke-WebRequest -Uri $sum[0].browser_download_url -OutFile $sumPath -UseBasicParsing -TimeoutSec 60
    $expected = (Get-Content -LiteralPath $sumPath -Raw).Trim()
    if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or
        -not (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Release ZIP SHA256 verification failed.'
    }
    if (-not $PSCmdlet.ShouldProcess($SiteName, "Deploy verified $tag")) { return }
    New-Item -Path (Split-Path $newPath -Parent) -ItemType Directory -Force | Out-Null
    Expand-Archive -LiteralPath $zipPath -DestinationPath $newPath -ErrorAction Stop
    foreach ($required in @('web.config','AssetsPortal.dll','AssetsPortal.deps.json','AssetsPortal.runtimeconfig.json','wwwroot\index.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $newPath $required) -PathType Leaf)) {
            throw "Invalid release: $required missing."
        }
    }

    New-Item -Path $backupRoot -ItemType Directory -Force | Out-Null
    & icacls.exe $backupRoot '/inheritance:r' '/grant:r' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to protect backup directory ACL.' }
    New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
    $oldWindows = [bool](Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter $winFilter -Name enabled).Value
    $oldAnonymous = [bool](Get-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter $anonFilter -Name enabled).Value
    $iisBackup = 'AssetsPortal_' + $backupName
    Backup-WebConfiguration -Name $iisBackup | Out-Null
    @{
        Commit = $commit
        SiteName = $SiteName
        Pool = $poolName
        PreviousPath = $oldPath
        NewPath = $newPath
        IisBackup = $iisBackup
        WindowsAuthentication = $oldWindows
        AnonymousAuthentication = $oldAnonymous
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupDir 'deployment.json') -Encoding UTF8

    # Quiesce IIS before backing up SQLite, even if the pool is Starting/Stopping.
    $stopped = $true
    Stop-AppPoolSafely -Name $poolName
    Wait-AppPoolState -Name $poolName -Expected 'Stopped'
    if (Test-Path -LiteralPath $dataPath) {
        Copy-Item -LiteralPath $dataPath -Destination (Join-Path $backupDir 'AssetsData') -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath $newConfig -PathType Leaf)) {
        if ($configSource -eq $configTemplate) {
            # Bootstrap only on the first deployment. The elevated operator
            # becomes the recovery admin, without granting broad AD access.
            & (Join-Path $repoRoot 'scripts\Initialize-PortalAdmin.ps1') -Account ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
            if (-not (Test-Path -LiteralPath $newConfig -PathType Leaf)) {
                throw 'Initial Assets config bootstrap did not create appsettings.json.'
            }
            Write-Host "Initialized separate Assets settings at $newConfig. Configure Jira using the web admin panel."
        } else {
            New-Item -Path $dataPath -ItemType Directory -Force | Out-Null
            Copy-Item -LiteralPath $configSource -Destination $newConfig -ErrorAction Stop
            Write-Host "Migrated Assets settings from Git checkout into $newConfig."
        }
    }
    New-Item -Path $dataDirectory -ItemType Directory -Force | Out-Null
    # Keep the config file read-only for IIS. DB and DP keys are under Data.
    $poolSid = "IIS AppPool\$poolName"
    # Remove inherited Users permissions from the directory containing Jira credentials.
    # The app pool gets read-only access to configuration and Modify only for Data.
    & icacls.exe $dataPath '/inheritance:r' '/grant:r' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' ($poolSid + ':(OI)(CI)RX') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to secure the Assets configuration directory." }
    & icacls.exe $dataDirectory '/inheritance:r' '/grant:r' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' ($poolSid + ':(OI)(CI)M') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unable to secure the Assets SQLite and DP keys directory." }
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter $winFilter -Name enabled -Value $true
    $changed = $true
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter $anonFilter -Name enabled -Value $false
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $pathFilter -Name physicalPath -Value $newPath
    # Explicit deployment requests the application to be online even when a
    # previous failed deployment left its app pool stopped.
    Start-AppPoolSafely -Name $poolName
    Wait-AppPoolState -Name $poolName -Expected 'Started'
    $stopped = $false
    $actualPath = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables([string](Get-Website -Name $SiteName).PhysicalPath))
    if (-not $actualPath.Equals($newPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'IIS PhysicalPath verification failed.' }
    if ((Get-WebAppPoolState -Name $poolName).Value -ne 'Started') {
        throw 'Application pool did not start.'
    }
    if ($HealthUrl) {
        $health = Invoke-RestMethod -Uri $HealthUrl -UseDefaultCredentials -TimeoutSec 30
        if ($null -eq $health -or $health.allowed -ne $true) { throw 'Authenticated health check failed.' }
    }
    Write-Host "Deployed commit: $commit"
    Write-Host "IIS $SiteName PhysicalPath: $newPath"
    Write-Host "Backup: $backupDir"
    Write-Host 'Check /api/me and /api/assets using your Windows account.'
    if (-not $poolWasStarted) { Write-Host "App pool $poolName recovered from its previously stopped state." }
} catch {
    $failure = $_
    if ($changed) {
        try {
            Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $pathFilter -Name physicalPath -Value $oldPath
            if ($null -ne $oldWindows) {
                Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter $winFilter -Name enabled -Value $oldWindows
            }
            if ($null -ne $oldAnonymous) {
                Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter $anonFilter -Name enabled -Value $oldAnonymous
            }
            Write-Warning "Restored previous IIS path: $oldPath"
        } catch { Write-Warning "IIS rollback failed: $($_.Exception.Message)" }
    }
    if ($stopped -and $poolWasStarted) {
        try {
            Start-AppPoolSafely -Name $poolName
        } catch {
            Write-Warning "App pool start failed during rollback: $($_.Exception.Message)"
        }
    }
    throw $failure
} finally {
    if (Test-Path -LiteralPath $temp) {
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
