#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param([string]$SiteName = 'Assets-KL', [string]$RepoRoot = 'C:\Sites\Assets', [string]$DeployRoot = 'C:\Sites\AssetsDeploy')
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
Import-Module ScheduledTasks
if (-not (Get-Website -Name $SiteName -ErrorAction SilentlyContinue)) { throw "IIS site not found: $SiteName" }
$source = Join-Path $RepoRoot 'scripts'
$protected = Join-Path $env:ProgramFiles 'Assets-Web-Deployment'
$queue = Join-Path $env:ProgramData 'Assets\Data\deployment-queue'
$poolName = [string](Get-Website -Name $SiteName).ApplicationPool
foreach ($file in @('Invoke-DeploymentQueue.ps1', 'Restore-Production.ps1', 'Update-Production.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file))) { throw "Script missing: $file. Pull latest Git first." }
}
if (-not $PSCmdlet.ShouldProcess($SiteName, 'Install protected Assets deployment worker and scheduled task')) { return }
New-Item -ItemType Directory -Path $protected -Force | Out-Null
foreach ($name in @('Invoke-DeploymentQueue.ps1', 'Restore-Production.ps1')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $protected $name) -Force
}
& icacls.exe $protected '/inheritance:r' '/grant:r' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot secure protected worker scripts.' }
New-Item -ItemType Directory -Path $queue -Force | Out-Null
$appPoolSid = "IIS AppPool\$poolName"
& icacls.exe $queue '/inheritance:r' '/grant:r' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' ($appPoolSid + ':(OI)(CI)M') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Cannot secure deployment queue for $appPoolSid" }
$worker = Join-Path $protected 'Invoke-DeploymentQueue.ps1'
$argument = '-NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File "' + $worker + '" -SiteName "' + $SiteName + '" -RepoRoot "' + $RepoRoot + '" -DeployRoot "' + $DeployRoot + '"'
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument $argument
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1) -RepetitionDuration (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -StartWhenAvailable
Register-ScheduledTask -TaskName 'Production-Updates' -TaskPath '\Assets-Web\' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Fixed-actions Assets GitHub Release update or rollback' -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $queue 'enabled.flag'), 'v1', (New-Object Text.UTF8Encoding($false)))
Write-Host 'Installed task: \Assets-Web\Production-Updates (SYSTEM, once per minute).'
Write-Host "Queue: $queue"
Write-Host "Protected worker scripts: $protected"
