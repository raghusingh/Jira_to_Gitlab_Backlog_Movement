# Install-Service.ps1
# Run as Administrator

param(
    [string]$ServiceName   = "JiraGitLabSync",
    [string]$DisplayName   = "Jira to GitLab Sync Service",
    [string]$Description   = "Replicates Jira tickets (Stories, Bugs, Tasks) to GitLab milestones on a schedule.",
    [string]$BinaryPath    = "$PSScriptRoot\JiraGitLabSync.exe",
    [string]$StartupType   = "Automatic",   # Automatic | Manual | Disabled
    [string]$RunAsAccount  = "LocalSystem"  # or "DOMAIN\ServiceAccount"
)

Write-Host "Installing Windows Service: $ServiceName" -ForegroundColor Cyan

# Stop and remove any existing instance
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Stopping existing service..." -ForegroundColor Yellow
    Stop-Service  -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# Create the service
New-Service `
    -Name        $ServiceName `
    -DisplayName $DisplayName `
    -Description $Description `
    -BinaryPathName $BinaryPath `
    -StartupType $StartupType

# Set recovery actions: restart after 1st and 2nd failures, reboot after 3rd
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/reboot/60000 | Out-Null

Write-Host "Service '$ServiceName' installed successfully." -ForegroundColor Green
Write-Host "Starting service..." -ForegroundColor Cyan
Start-Service -Name $ServiceName
Write-Host "Service status: $((Get-Service -Name $ServiceName).Status)" -ForegroundColor Green
