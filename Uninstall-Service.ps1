# Uninstall-Service.ps1
# Run as Administrator

param(
    [string]$ServiceName = "JiraGitLabSync"
)

Write-Host "Uninstalling Windows Service: $ServiceName" -ForegroundColor Cyan

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Service '$ServiceName' not found." -ForegroundColor Yellow
    exit 0
}

if ($svc.Status -ne 'Stopped') {
    Write-Host "Stopping service..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force
    Start-Sleep -Seconds 3
}

sc.exe delete $ServiceName | Out-Null
Write-Host "Service '$ServiceName' removed." -ForegroundColor Green
