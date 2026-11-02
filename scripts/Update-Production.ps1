[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$SiteName = 'Assets-KL',
    [string]$DeployRoot = 'C:\Sites\AssetsDeploy',
    [string]$ConfigSourcePath,
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
try {
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/Eris92/Assets/releases/latest' -Headers $headers -TimeoutSec 30
} catch {
    throw "No public GitHub Release available. Check the CI release job and retry. $($_.Exception.Message)"
}
$tag = 'assets-' + $commit
if ($release.tag_name -ne $tag -or $release.draft -or $release.prerelease) {
    throw "Release $($release.tag_name) does not match main ($commit). Retry after CI release succeeds."
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
$backupRoot = Join-Path $env:ProgramData 'AssetsPortal-Deploy\Backups'
$backupName = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$backupDir = Join-Path $backupRoot $backupName
$dataPath = Join-Path $env:ProgramData 'AssetsPortal'
$liveConfig = Join-Path $oldPath 'appsettings.json'
$newConfig = Join-Path $dataPath 'appsettings.json'
# Never switch IIS to a package without the existing production Jira settings.
# The earlier manual deployment used C:\Sites\AssetsPortal while IIS pointed at C:\Sites\Assets.
$configCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($ConfigSourcePath)) {
    $configCandidates += [IO.Path]::GetFullPath($ConfigSourcePath)
}
$configCandidates += $liveConfig
$configCandidates += 'C:\Sites\AssetsPortal\appsettings.json'
$configSource = $null
if (-not (Test-Path -LiteralPath $newConfig -PathType Leaf)) {
    $configSource = @($configCandidates | Select-Object -Unique | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
    if ($configSource.Count -eq 0) {
        throw "Production configuration missing. Restore C:\ProgramData\AssetsPortal\appsettings.json or pass -ConfigSourcePath 'C:\path\appsettings.json'. IIS was not changed."
    }
    $configSource = [string]$configSource[0]
} else {
    $configSource = $newConfig
}
try {
    $cfg = Get-Content -LiteralPath $configSource -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ($null -eq $cfg.Jira -or $null -eq $cfg.Portal) {
        throw 'Missing Jira or Portal section.'
    }
} catch {
    throw "Invalid JSON configuration at $configSource. IIS was not changed. $($_.Exception.Message)"
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
        Copy-Item -LiteralPath $dataPath -Destination (Join-Path $backupDir 'AssetsPortalData') -Recurse -Force
    }
    if (-not (Test-Path -LiteralPath $newConfig -PathType Leaf)) {
        Copy-Item -LiteralPath $configSource -Destination (Join-Path $backupDir 'previous-appsettings.json') -Force
        New-Item -Path $dataPath -ItemType Directory -Force | Out-Null
        Copy-Item -LiteralPath $configSource -Destination $newConfig -ErrorAction Stop
        Write-Host "Preserved production configuration from $configSource in ProgramData."
    }
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
