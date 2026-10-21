[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$PortalUrl,
    [string]$SiteName = 'Assets'
)
$ErrorActionPreference = 'Stop'
if ($PortalUrl.Scheme -notin @('http','https')) { throw 'Use an HTTP or HTTPS portal URL.' }
Add-Type -AssemblyName System.Net.Http
$baseUrl = $PortalUrl.AbsoluteUri.TrimEnd('/') + '/'
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseDefaultCredentials = $true
$handler.AllowAutoRedirect = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)
try {
    $requestUrl = New-Object uri([uri]$baseUrl, 'api/me')
    $response = $client.GetAsync($requestUrl).GetAwaiter().GetResult()
    try {
        Write-Output ('URL: ' + $requestUrl.AbsoluteUri)
        Write-Output ('HTTP: ' + [int]$response.StatusCode)
        Write-Output ('WWW-Authenticate: ' + ($response.Headers.WwwAuthenticate -join ', '))
        if ($response.Headers.Location) { Write-Output ('Redirect: ' + $response.Headers.Location) }
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($response.IsSuccessStatusCode) {
            try {
                $data = $text | ConvertFrom-Json
                $data | Select-Object name, authenticationType, allowed, admin, bootstrap, accessReason, manager | Format-List | Out-String | Write-Output
            } catch { Write-Output 'Response is not JSON. Check ASP.NET Core hosting and physical application path.' }
        } else { Write-Output 'Request failed. Correlate the status with IIS sc-status, sc-substatus and sc-win32-status.' }
        $response.Dispose()
    } catch { throw }
} finally { $client.Dispose(); $handler.Dispose() }
$appCmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'
if (Test-Path -LiteralPath $appCmd) {
    Write-Output 'Effective IIS authentication (run this on the web server as administrator):'
    & $appCmd list config "$SiteName/" /section:system.webServer/security/authentication/windowsAuthentication
    & $appCmd list config "$SiteName/" /section:system.webServer/security/authentication/anonymousAuthentication
    & $appCmd list app "$SiteName/"
} else { Write-Output 'IIS configuration is available only when this script runs on the web server.' }
