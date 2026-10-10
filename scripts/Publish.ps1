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
& dotnet publish (Join-Path $repoPath 'AssetsPortal.csproj') -c Release -r $Runtime --self-contained true -o $resolvedOutput
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
$configPath = Join-Path $env:ProgramData 'AssetsPortal\appsettings.json'
New-Item -ItemType Directory -Path (Split-Path $configPath -Parent) -Force | Out-Null
if (-not (Test-Path -LiteralPath $configPath)) {
    Copy-Item -LiteralPath (Join-Path $repoPath 'appsettings.org.json') -Destination $configPath
    Write-Host "Created configuration: $configPath"
}
Write-Host "Published to: $resolvedOutput"
Write-Host 'Configure IIS with HTTPS, Windows Authentication and the ASP.NET Core Hosting Bundle.'
