<#
.SYNOPSIS
  Installs FollowUp Officer as two Windows services that start with the machine and restart after a crash
  (spec §59–§61). Run from an elevated PowerShell prompt on the server.

.DESCRIPTION
  * FollowUpOfficer.Worker — the background processor (reminders, escalations, recurrence, mail/Teams sync,
    AI classification, notification delivery). Independent of the web UI.
  * FollowUpOfficer.Api    — web API + Angular UI + SignalR (its in-process job engine is disabled here so the
    Worker is the single always-on processor; the database lease makes it safe either way).
  Both services: Automatic (Delayed Start) so SQL Server is up first, recovery = restart after 30 s / 60 s / 120 s,
  failure counter reset daily, and restart also on non-crash failures. Work resumes from database checkpoints.

.EXAMPLE
  .\install-windows-services.ps1 -InstallRoot "C:\Services\FollowUpOfficer" -ConnectionString "Server=.;Database=FollowUpOfficer;Trusted_Connection=True;TrustServerCertificate=True"
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = "C:\Services\FollowUpOfficer",
    [string] $ConnectionString = "Server=(localdb)\MSSQLLocalDB;Database=FollowUpOfficer;Trusted_Connection=True;TrustServerCertificate=True",
    [string] $Urls = "http://0.0.0.0:5860",
    [string] $PublicAppUrl = "http://localhost:5860",
    [string] $ServiceAccount = "NT AUTHORITY\NetworkService",
    [switch] $SkipPublish
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Run this script from an elevated (Administrator) PowerShell." }

$apiDir = Join-Path $InstallRoot "api"
$workerDir = Join-Path $InstallRoot "worker"
$dataDir = Join-Path $InstallRoot "data"

if (-not $SkipPublish) {
    Write-Host "Building the Angular UI..." -ForegroundColor Cyan
    Push-Location (Join-Path $repo "client"); npm ci; npm run build; Pop-Location
    Write-Host "Publishing API and Worker..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repo "src\FollowUp.Api") -c Release -o $apiDir
    dotnet publish (Join-Path $repo "src\FollowUp.Worker") -c Release -o $workerDir
}
New-Item -ItemType Directory -Force (Join-Path $dataDir "keys"), (Join-Path $dataDir "attachments") | Out-Null

# Shared production settings: one Data Protection key ring for both services, production database, demo seeding off.
$shared = @{
    ConnectionStrings = @{ FollowUp = $ConnectionString }
    DataProtection = @{ KeysPath = (Join-Path $dataDir "keys") }
    FollowUp = @{ PublicAppUrl = $PublicAppUrl; StoragePath = (Join-Path $dataDir "attachments"); SeedDemoData = $false }
}
$apiSettings = $shared.Clone(); $apiSettings.Urls = $Urls; $apiSettings.Jobs = @{ Enabled = $false }
$workerSettings = $shared.Clone(); $workerSettings.Jobs = @{ Enabled = $true }
$apiSettings | ConvertTo-Json -Depth 5 | Out-File (Join-Path $apiDir "appsettings.Production.json") -Encoding utf8
$workerSettings | ConvertTo-Json -Depth 5 | Out-File (Join-Path $workerDir "appsettings.Production.json") -Encoding utf8

# The service account needs to write data (keys, attachments).
icacls $dataDir /grant "${ServiceAccount}:(OI)(CI)M" | Out-Null

function Install-FollowUpService([string] $Name, [string] $DisplayName, [string] $Exe, [string] $Description) {
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        Write-Host "Updating $Name" -ForegroundColor Yellow
        Stop-Service $Name -Force -ErrorAction SilentlyContinue
        sc.exe config $Name binPath= "`"$Exe`"" start= delayed-auto obj= $ServiceAccount | Out-Null
    } else {
        Write-Host "Creating $Name" -ForegroundColor Green
        sc.exe create $Name binPath= "`"$Exe`"" start= delayed-auto obj= $ServiceAccount DisplayName= "$DisplayName" | Out-Null
    }
    sc.exe description $Name "$Description" | Out-Null
    # Restart after crashes: 30 s, 60 s, then every 120 s; reset the failure count after a day.
    sc.exe failure $Name reset= 86400 actions= restart/30000/restart/60000/restart/120000 | Out-Null
    sc.exe failureflag $Name 1 | Out-Null
    [Environment]::SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production", "Machine")
    Start-Service $Name
    Get-Service $Name | Format-Table Name, Status, StartType -AutoSize
}

Install-FollowUpService "FollowUpOfficer.Worker" "FollowUp Officer — Background Processor" (Join-Path $workerDir "FollowUp.Worker.exe") `
    "Reminders, escalations, recurring tasks, Outlook/Teams synchronisation, AI classification and notification delivery. Resumes from database checkpoints after any restart."
Install-FollowUpService "FollowUpOfficer.Api" "FollowUp Officer — Web API & UI" (Join-Path $apiDir "FollowUp.Api.exe") `
    "Web API, Angular UI and real-time updates for FollowUp Officer."

Write-Host "Installed. Open $PublicAppUrl — both services start automatically with Windows and restart after failures." -ForegroundColor Green
