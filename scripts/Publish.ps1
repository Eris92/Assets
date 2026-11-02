[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\out\AssetsPortal'),
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install .NET 10 SDK before publishing.'
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repoPath 'out'))
$prefix = $buildRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutput.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputPath must be inside $buildRoot. Never publish directly to the IIS production directory."
}
if (Test-Path -LiteralPath $resolvedOutput) {
    foreach ($protected in @('appsettings.json', 'portal.db', 'team.json', 'keys', 'DataProtection-Keys', 'logs')) {
        if (Test-Path -LiteralPath (Join-Path $resolvedOutput $protected)) {
            throw "Output directory contains production data ($protected). Refusing to delete it."
        }
    }
    Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
}
& dotnet publish (Join-Path $repoPath 'AssetsPortal.csproj') -c Release -r $Runtime --self-contained false -p:UseAppHost=false -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
$runtimeConfig = Get-Content -LiteralPath (Join-Path $resolvedOutput 'AssetsPortal.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.includedFrameworks -or
    -not (@($runtimeConfig.runtimeOptions.frameworks.name) -contains 'Microsoft.AspNetCore.App')) {
    throw 'Publish output is not framework-dependent on ASP.NET Core.'
}
foreach ($runtimeFile in @('coreclr.dll','hostfxr.dll','AssetsPortal.exe')) {
    if (Test-Path -LiteralPath (Join-Path $resolvedOutput $runtimeFile)) {
        throw "Unexpected bundled runtime/apphost: $runtimeFile"
    }
}
Write-Host "Framework-dependent .NET 10 package ready: $resolvedOutput"
Write-Host 'No runtime, credentials, local database or Data Protection keys are bundled.'
Write-Host 'Server prerequisite: ASP.NET Core Hosting Bundle 10 with IIS Windows Authentication.'
