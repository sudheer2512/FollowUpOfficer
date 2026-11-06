<#
.SYNOPSIS
  Hosts FollowUp Officer in IIS (web API + Angular UI) and installs the always-on background processor as a
  Windows service. Run from an elevated PowerShell on the server. See docs/DEPLOY-IIS.md.

.DESCRIPTION
  Prerequisites: IIS with the ASP.NET Core Module - install the ".NET 10 Hosting Bundle" AFTER enabling IIS.
  The script:
    1. builds the UI and publishes the API into -PhysicalPath (taking the running app offline with app_offline.htm);
    2. writes appsettings.Production.json (database, public URL, shared data folder, background jobs on/off);
    3. creates / updates a dedicated application pool: No Managed Code, AlwaysRunning, no idle time-out,
       no periodic recycling, user profile loaded (Data Protection keys);
    4. creates a site (-Port / -HostName) or an application under an existing site (-ApplicationPath "/followup");
       preloadEnabled + Application Initialization (warm-up on /api/health) when that IIS feature is installed;
    5. grants the pool identity access to the data folder;
    6. installs the FollowUpOfficer.Worker Windows service (unless -RunJobsInIis or -SkipWorker), so reminders,
       escalations and sync never depend on IIS keeping the site alive.

.EXAMPLE
  # Own site on port 8060
  .\install-iis.ps1 -ConnectionString "Server=.\SQLEXPRESS;Database=FollowUpOfficer;Trusted_Connection=True;TrustServerCertificate=True" -Port 8060

.EXAMPLE
  # Application under Default Web Site: http://localhost/followupofficer
  .\install-iis.ps1 -ParentSite "Default Web Site" -ApplicationPath "/followupofficer" -ConnectionString "..."

.EXAMPLE
  # The same under a real host name
  .\install-iis.ps1 -ParentSite "Default Web Site" -ApplicationPath "/followupofficer" -PublicAppUrl "https://intranet.contoso.com/followupofficer" -ConnectionString "..."
#>
[CmdletBinding()]
param(
    [string] $SiteName = "FollowUpOfficer",
    [int] $Port = 8060,
    [string] $HostName = "",
    # Set to host as an application under an existing site, e.g. "/followup" under "Default Web Site".
    [string] $ApplicationPath = "",
    [string] $ParentSite = "Default Web Site",
    [string] $PhysicalPath = "C:\inetpub\FollowUpOfficer",
    [string] $AppPoolName = "FollowUpOfficer",
    [string] $ConnectionString = "Server=.\SQLEXPRESS;Database=FollowUpOfficer;Trusted_Connection=True;TrustServerCertificate=True",
    # External URL users open (used in email/Teams links). Default: derived from the binding.
    [string] $PublicAppUrl = "",
    # Data Protection key ring + encrypted attachments, shared with the Worker service.
    [string] $DataDir = "C:\ProgramData\FollowUpOfficer",
    # Run the background job engine inside IIS as well (database leases keep each job single-run).
    [switch] $RunJobsInIis,
    # Do not install the Worker Windows service.
    [switch] $SkipWorker,
    # Where the Worker service is published (install-windows-services.ps1 -WorkerOnly).
    [string] $WorkerInstallRoot = "C:\Services\FollowUpOfficer",
    # Seed the demo organisation on an empty database (evaluation only).
    [switch] $Demo,
    # Enable the IIS WebSocket Protocol and Application Initialization features (Windows optional features).
    [switch] $InstallIisFeatures,
    [switch] $SkipPublish
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Run this script from an elevated (Administrator) PowerShell." }

# -- Prerequisites --------------------------------------------------------------------------------------------
if (-not (Get-Service W3SVC -ErrorAction SilentlyContinue)) { throw "IIS is not installed. Enable 'Internet Information Services' (Web Server) first." }
$ancm = Join-Path $env:ProgramFiles "IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
if (-not (Test-Path $ancm)) { throw "The ASP.NET Core Module is missing. Install the .NET 10 Hosting Bundle (https://dotnet.microsoft.com/download/dotnet/10.0), then run 'iisreset'." }
# Module versions track the Hosting Bundle: 13.1 = .NET Core 3.1, 18.x = .NET 8, 20.x = .NET 10.
$ancmInfo = (Get-Item $ancm).VersionInfo
if ($ancmInfo.FileMajorPart -lt 20) { Write-Warning "The ASP.NET Core Module is version $($ancmInfo.FileVersion) (from an older Hosting Bundle). Install the .NET 10 Hosting Bundle, then run 'iisreset'." }
if ($InstallIisFeatures) {
    Write-Host "Enabling IIS WebSocket Protocol and Application Initialization..." -ForegroundColor Cyan
    Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets, IIS-ApplicationInit -All -NoRestart | Out-Null
}
Import-Module WebAdministration
if ($ConnectionString -match "\(localdb\)") {
    Write-Warning "LocalDB is a per-user instance and does not work for an IIS application pool identity. Use SQL Server (Express or higher) - see docs/DEPLOY-IIS.md."
}

$ApplicationPath = $ApplicationPath.Trim().TrimEnd('/')
if ($ApplicationPath -and -not $ApplicationPath.StartsWith('/')) { $ApplicationPath = "/$ApplicationPath" }
if (-not $PublicAppUrl) {
    $hostPart = if ($HostName) { $HostName } else { "localhost" }
    $PublicAppUrl = if ($ApplicationPath) { "http://localhost$ApplicationPath" } else { "http://${hostPart}:$Port" }
}
$PublicAppUrl = $PublicAppUrl.TrimEnd('/')
$iisLocation = if ($ApplicationPath) { "$ParentSite$ApplicationPath" } else { $SiteName }

# In-process hosting allows one application per pool (HTTP 500.35). Stop here, before the running app is taken offline.
$thisApp = if ($ApplicationPath) { "$ParentSite$ApplicationPath" } else { "$SiteName/" }
$appHost = [xml](Get-Content "$env:windir\System32\inetsrv\config\applicationHost.config" -Raw)
$otherApps = @(foreach ($site in $appHost.configuration.'system.applicationHost'.sites.site) {
    foreach ($app in @($site.application)) {
        $appName = "$($site.name)$($app.path)"
        if ($app.applicationPool -eq $AppPoolName -and $appName -ne $thisApp) { $appName }
    }
})
if ($otherApps) { throw "Application pool '$AppPoolName' already serves $($otherApps -join ', '). In-process hosting allows one application per pool. Remove that site or application, or pass a different -AppPoolName." }

# -- Publish (take the running app offline first so files are not locked) ------------------------------------
New-Item -ItemType Directory -Force $PhysicalPath, (Join-Path $PhysicalPath "logs"), (Join-Path $DataDir "keys"), (Join-Path $DataDir "attachments") | Out-Null
$offline = Join-Path $PhysicalPath "app_offline.htm"
if (-not $SkipPublish) {
    Set-Content $offline "<!doctype html><title>Updating</title><p>FollowUp Officer is being updated. Please try again in a minute.</p>" -Encoding utf8
    Start-Sleep -Seconds 3
    Write-Host "Building the Angular UI..." -ForegroundColor Cyan
    Push-Location (Join-Path $repo "client")
    try { npm ci; if ($LASTEXITCODE -eq 0) { npm run build } } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "The Angular build failed." }
    Write-Host "Publishing the API to $PhysicalPath..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repo "src\FollowUp.Api") -c Release -o $PhysicalPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
}

# -- Production settings -------------------------------------------------------------------------------------
$settings = @{
    ConnectionStrings = @{ FollowUp = $ConnectionString }
    DataProtection = @{ KeysPath = (Join-Path $DataDir "keys") }
    FollowUp = @{ PublicAppUrl = $PublicAppUrl; StoragePath = (Join-Path $DataDir "attachments"); SeedDemoData = [bool]$Demo; ShowDemoUsers = [bool]$Demo }
    Microsoft365 = @{ PublicBaseUrl = $PublicAppUrl }
    Jobs = @{ Enabled = [bool]($RunJobsInIis -or $SkipWorker) }
    Cors = @{ Origins = @(([Uri]$PublicAppUrl).GetLeftPart([UriPartial]::Authority)) }
}
$settings | ConvertTo-Json -Depth 5 | Out-File (Join-Path $PhysicalPath "appsettings.Production.json") -Encoding utf8

# -- Application pool ----------------------------------------------------------------------------------------
$pool = "IIS:\AppPools\$AppPoolName"
if (-not (Test-Path $pool)) { New-WebAppPool -Name $AppPoolName | Out-Null; Write-Host "Created application pool $AppPoolName" -ForegroundColor Green }
Set-ItemProperty $pool -Name managedRuntimeVersion -Value ""                       # No Managed Code (ASP.NET Core)
Set-ItemProperty $pool -Name startMode -Value "AlwaysRunning"
Set-ItemProperty $pool -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name processModel.loadUserProfile -Value $true
Set-ItemProperty $pool -Name recycling.periodicRestart.time -Value ([TimeSpan]::Zero)
Set-ItemProperty $pool -Name enable32BitAppOnWin64 -Value $false

# -- Site or application -------------------------------------------------------------------------------------
if ($ApplicationPath) {
    if (-not (Test-Path "IIS:\Sites\$ParentSite")) { throw "Parent site '$ParentSite' does not exist." }
    $appName = $ApplicationPath.TrimStart('/')
    if (-not (Get-WebApplication -Site $ParentSite -Name $appName)) {
        New-WebApplication -Site $ParentSite -Name $appName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName | Out-Null
        Write-Host "Created application $ParentSite$ApplicationPath" -ForegroundColor Green
    } else {
        Set-ItemProperty "IIS:\Sites\$ParentSite\$appName" -Name physicalPath -Value $PhysicalPath
        Set-ItemProperty "IIS:\Sites\$ParentSite\$appName" -Name applicationPool -Value $AppPoolName
    }
    Set-ItemProperty "IIS:\Sites\$ParentSite\$appName" -Name preloadEnabled -Value $true
} else {
    if (-not (Test-Path "IIS:\Sites\$SiteName")) {
        New-Website -Name $SiteName -Port $Port -HostHeader $HostName -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName | Out-Null
        Write-Host "Created site $SiteName on port $Port" -ForegroundColor Green
    } else {
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath -Value $PhysicalPath
        Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $AppPoolName
    }
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationDefaults.preloadEnabled -Value $true
}

# Warm-up after start / recycle (only when the Application Initialization feature is installed; the setting lives
# in applicationHost.config so re-publishing never overwrites it).
$appInit = Get-WindowsOptionalFeature -Online -FeatureName IIS-ApplicationInit -ErrorAction SilentlyContinue
if ($appInit -and $appInit.State -eq "Enabled") {
    Set-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Location $iisLocation -Filter "system.webServer/applicationInitialization" -Name "doAppInitAfterRestart" -Value $true
    $existing = Get-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Location $iisLocation -Filter "system.webServer/applicationInitialization" -Name "."
    if (-not ($existing.Collection | Where-Object { $_.initializationPage -eq "/api/health" })) {
        Add-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Location $iisLocation -Filter "system.webServer/applicationInitialization" -Name "." -Value @{ initializationPage = "/api/health" }
    }
} else {
    Write-Warning "IIS Application Initialization is not installed: the app starts on the first request after a restart. Re-run with -InstallIisFeatures, or rely on the Worker service for background processing."
}
$ws = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets -ErrorAction SilentlyContinue
if (-not $ws -or $ws.State -ne "Enabled") { Write-Warning "IIS WebSocket Protocol is not installed: real-time updates fall back to Server-Sent Events / long polling." }

# WebDAV Publishing (when installed) answers PUT / DELETE with 405 before the app sees them. Remove its module for this
# site here: web.config cannot, because IIS locks the <modules> section by default (HTTP 500.19 / 0x80070021).
if (Test-Path "$env:windir\System32\inetsrv\webdav.dll") {
    $webDav = Get-WebConfiguration -PSPath "MACHINE/WEBROOT/APPHOST" -Location $iisLocation -Filter "system.webServer/modules/add[@name='WebDAVModule']"
    if ($webDav) {
        Remove-WebConfigurationProperty -PSPath "MACHINE/WEBROOT/APPHOST" -Location $iisLocation -Filter "system.webServer/modules" -Name "." -AtElement @{ name = "WebDAVModule" }
        Write-Host "Disabled the WebDAV module for $iisLocation" -ForegroundColor Green
    }
}

# -- Permissions ---------------------------------------------------------------------------------------------
$identity = "IIS AppPool\$AppPoolName"
icacls $PhysicalPath /grant "${identity}:(OI)(CI)RX" | Out-Null
icacls (Join-Path $PhysicalPath "logs") /grant "${identity}:(OI)(CI)M" | Out-Null
icacls $DataDir /grant "${identity}:(OI)(CI)M" | Out-Null

# -- Background processor ------------------------------------------------------------------------------------
if (-not $RunJobsInIis -and -not $SkipWorker) {
    & (Join-Path $PSScriptRoot "install-windows-services.ps1") -WorkerOnly -InstallRoot $WorkerInstallRoot `
        -ConnectionString $ConnectionString -PublicAppUrl $PublicAppUrl -DataDir $DataDir
}

# -- Start ---------------------------------------------------------------------------------------------------
if (Test-Path $offline) { Remove-Item $offline -Force }
Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
if (-not $ApplicationPath) { Start-Website -Name $SiteName -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "Database access for the pool identity (run once in SQL Server if you use Windows authentication):" -ForegroundColor Yellow
Write-Host "  CREATE LOGIN [$identity] FROM WINDOWS;"
Write-Host "  -- in the FollowUpOfficer database (db_owner lets the app apply migrations at start-up):"
Write-Host "  CREATE USER [$identity] FOR LOGIN [$identity]; ALTER ROLE db_owner ADD MEMBER [$identity];"
if (-not $RunJobsInIis -and -not $SkipWorker) {
    Write-Host "  -- and the same for the Worker service account: [NT AUTHORITY\NETWORK SERVICE] (or the computer account on a remote SQL Server)."
}
Write-Host ""
try {
    $health = Invoke-WebRequest "$PublicAppUrl/api/health" -UseBasicParsing -TimeoutSec 60
    Write-Host "Health check: $($health.StatusCode) $($health.Content)" -ForegroundColor Green
} catch {
    Write-Warning "Health check failed ($($_.Exception.Message)). Check the database login, then enable stdout logging in web.config (logs\stdout) if the app does not start."
}
Write-Host "Open $PublicAppUrl" -ForegroundColor Green
