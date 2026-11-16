# Publish startup smoke test using isolated Windows-local SQLite.
[CmdletBinding()]
param(
    [string]$PublishPath = 'out/AssetsPortal',
    [int]$TimeoutSeconds = 35
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publish = [IO.Path]::GetFullPath($PublishPath)
$dll = Join-Path $publish 'AssetsPortal.dll'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
    throw "Published DLL missing: $dll"
}
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$work = Join-Path ([IO.Path]::GetTempPath()) ('AssetsSmoke_' + [guid]::NewGuid().ToString('N'))
New-Item -Path $work -ItemType Directory -Force | Out-Null
$stdout = Join-Path $work 'stdout.log'
$stderr = Join-Path $work 'stderr.log'
$db = Join-Path $work 'data\portal.db'
$listener = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$uri = "http://127.0.0.1:$port/"
$previousDb = [Environment]::GetEnvironmentVariable('Portal__DatabasePath','Process')
$previousUrls = [Environment]::GetEnvironmentVariable('ASPNETCORE_URLS','Process')
$process = $null

try {
    [Environment]::SetEnvironmentVariable('Portal__DatabasePath', $db, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', $uri, 'Process')
    $process = Start-Process -FilePath $dotnet -ArgumentList ('"{0}"' -f $dll) -WorkingDirectory $publish -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            throw "Application exited during startup (code $($process.ExitCode))."
        }
        try {
            $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $ready) {
        throw "Application did not respond HTTP 200 at $uri after $TimeoutSeconds seconds."
    }
    if (-not (Test-Path -LiteralPath $db -PathType Leaf)) {
        throw 'Startup did not initialize its isolated SQLite database.'
    }
    Write-Host "PASS: Published app initialized SQLite and served HTTP 200 on $uri"
}
catch {
    Write-Host '=== Startup stderr ==='
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Tail 100 }
    Write-Host '=== Startup stdout ==='
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Tail 100 }
    throw
}
finally {
    if ($null -ne $process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                $process.WaitForExit(10000) | Out-Null
            }
        } catch {
            Write-Warning "Failed to stop isolated smoke process: $($_.Exception.Message)"
        }
    }
    [Environment]::SetEnvironmentVariable('Portal__DatabasePath', $previousDb, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_URLS', $previousUrls, 'Process')
    try { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop }
    catch { Write-Warning "Could not clean startup test directory: $work" }
}
