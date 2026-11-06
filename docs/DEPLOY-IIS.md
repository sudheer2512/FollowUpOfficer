# Hosting FollowUp Officer in IIS

The web API, the Angular UI and the SignalR hub run in IIS through the ASP.NET Core Module (in-process). The
background processor keeps running as the `FollowUpOfficer.Worker` Windows service. Reminders, escalations,
recurrence and Microsoft 365 sync therefore never depend on IIS keeping the site warm.

```
Browser ──► IIS (app pool "FollowUpOfficer", No Managed Code, AlwaysRunning)
              └─ FollowUp.Api.dll   UI + /api + /hubs/events + /graph-emulator
SQL Server ◄─┤
              └─ FollowUpOfficer.Worker (Windows service)   durable job engine
Shared data folder (C:\ProgramData\FollowUpOfficer): Data Protection keys + encrypted attachments
```

## Prerequisites

1. **IIS** (Windows Server role or Windows feature "Internet Information Services"). Optional features:
   - *WebSocket Protocol* gives real-time updates over WebSockets. Without it, SignalR falls back to Server-Sent Events or long polling and still works.
   - *Application Initialization* warms the app up after an app pool start or recycle.
2. **.NET 10 Hosting Bundle.** Install it *after* IIS, then run `iisreset`. It provides the ASP.NET Core Module V2 and the runtime. An older bundle is not enough. The module file `C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll` should be version 20.x. Version 13.1 comes from .NET Core 3.1.
3. **SQL Server** (Express or higher). LocalDB is per-user and does not work for an app pool identity.
4. To build on the server: **Node.js 22+** and the **.NET 10 SDK**. Alternatively, publish on a build machine and copy the folder (see *Manual deployment*).

## Automated install

From an elevated PowerShell on the server:

```powershell
# Own site on port 8060  ->  http://server:8060
.\deploy\install-iis.ps1 -Port 8060 `
    -ConnectionString "Server=.\SQLEXPRESS;Database=FollowUpOfficer;Trusted_Connection=True;TrustServerCertificate=True" `
    -PublicAppUrl "http://server:8060"

# Application under an existing site  ->  https://intranet.contoso.com/followup
.\deploy\install-iis.ps1 -ParentSite "Default Web Site" -ApplicationPath "/followup" `
    -PublicAppUrl "https://intranet.contoso.com/followup" -ConnectionString "..."
```

The script performs these steps:

| Step | What it does |
| --- | --- |
| Publish | Builds the UI. Publishes the API to `-PhysicalPath` (default `C:\inetpub\FollowUpOfficer`). Writes `app_offline.htm` first so the running app releases its files. |
| Settings | Writes `appsettings.Production.json` with the connection string, `FollowUp:PublicAppUrl`, `Microsoft365:PublicBaseUrl`, the shared key and attachment folders, and `Jobs:Enabled`. |
| App pool | Creates a dedicated pool: *No Managed Code*, `startMode=AlwaysRunning`, idle time-out 0, no periodic recycle, user profile loaded. |
| Site / application | Creates a new site with a binding, or an application under `-ParentSite`. Sets `preloadEnabled`. When the feature is installed, adds Application Initialization on `/api/health`. |
| Permissions | Gives `IIS AppPool\FollowUpOfficer` read access to the site, modify access to `logs`, and modify access to the data folder. |
| Worker | Runs `install-windows-services.ps1 -WorkerOnly` with the same database and data folder (Automatic (Delayed Start), restart on failure). |

Useful switches:

- `-InstallIisFeatures` enables the WebSocket Protocol and Application Initialization features.
- `-RunJobsInIis` runs the job engine inside IIS as well. Database leases keep each job single-run.
- `-SkipWorker` also turns the in-process engine on, so IIS is the only processor.
- `-Demo` seeds the demo organisation (evaluation only).
- `-SkipPublish` reconfigures without rebuilding.

Re-running the script updates an existing installation in place.

## Database access for the pool identity

With Windows authentication, the app pool runs as `IIS AppPool\FollowUpOfficer`. On a remote SQL Server it runs as
the computer account `DOMAIN\SERVER$`. The app applies EF Core migrations at start-up, so it needs `db_owner`
(or DDL rights) on its database:

```sql
CREATE LOGIN [IIS AppPool\FollowUpOfficer] FROM WINDOWS;
USE FollowUpOfficer;
CREATE USER [IIS AppPool\FollowUpOfficer] FOR LOGIN [IIS AppPool\FollowUpOfficer];
ALTER ROLE db_owner ADD MEMBER [IIS AppPool\FollowUpOfficer];
-- The Worker service (NETWORK SERVICE by default) needs the same:
CREATE LOGIN [NT AUTHORITY\NETWORK SERVICE] FROM WINDOWS;
CREATE USER [NT AUTHORITY\NETWORK SERVICE] FOR LOGIN [NT AUTHORITY\NETWORK SERVICE];
ALTER ROLE db_owner ADD MEMBER [NT AUTHORITY\NETWORK SERVICE];
```

You can use a SQL login instead (`User Id=...;Password=...` in the connection string). In that case, protect
`appsettings.Production.json` with NTFS permissions.

## Hosting under a virtual path

The same build works at a site root and under a path such as `http://localhost/followupofficer`. No rebuild is needed.

**In IIS the path must be an Application, not a plain Virtual Directory.** Both give the same URL. But the ASP.NET Core
Module needs its own application and app pool. A plain virtual directory, or a folder under the site root, runs in the
parent's pool and fails with HTTP 500.

**To set it up in IIS Manager:**

1. Create the app pool (see *Manual deployment*).
2. Right-click **Default Web Site** → **Add Application**.
3. Enter the alias `followupofficer`, choose the pool, and enter the physical path. If the folder is already under the site root, right-click it and choose **Convert to Application** instead.
4. In **Advanced Settings**, set **Preload Enabled = True**.
5. Set both URLs in `appsettings.Production.json`, **including** the path, in the website and Worker copies:
   - `FollowUp:PublicAppUrl` (links in emails and Teams)
   - `Microsoft365:PublicBaseUrl` (the Graph emulator in Emulator mode)

**Rules to keep in mind:**

- **One application per pool.** In-process hosting allows only one app in a pool. A second one fails with HTTP 500.35. Give each app its own pool.
- **One spelling of the path.** IIS ignores case in the path, but browsers treat each spelling as a separate place for the sign-in cookie. So page loads under any other spelling redirect to the spelling in `FollowUp:PublicAppUrl`.

**How it works:**

- The ASP.NET Core Module passes the application path to the app as `PathBase`.
- The server writes `<base href="/followupofficer/">` into `index.html` on every request.
- The client builds every API, SignalR and attachment URL from that base.
- Deep links such as `/followupofficer/tasks/42` survive a browser refresh.

Behind a reverse proxy that strips the prefix, set `"PathBase": "/followupofficer"` in `appsettings.Production.json`.

## Manual deployment

```powershell
cd client; npm ci; npm run build; cd ..          # writes the UI into src/FollowUp.Api/wwwroot
dotnet publish src/FollowUp.Api -c Release -o out\FollowUpOfficer
```

Publishing from Visual Studio (*Publish → Folder*) produces the same output in `bin\Release\net10.0\publish`. Run
`npm run build` in `client` first, because Visual Studio does not build the UI. If you forget, the publish warns
that `wwwroot\index.html` is missing.

Copy `out\FollowUpOfficer` to the server and add `appsettings.Production.json` (same keys as the script writes).
In IIS Manager:

1. Create an app pool with *.NET CLR version = No Managed Code*, *Start Mode = AlwaysRunning* and *Idle Time-out = 0*.
2. Create a site or application that points to the folder and uses that pool. Set *Preload Enabled = True*.
3. Keep only *Anonymous Authentication* enabled. The app does its own sign-in (cookie or Microsoft Entra ID).
4. Grant the pool identity access to the data folder.

The published `web.config` does the following:

- Runs the app in-process.
- Removes the WebDAV handler. It has no `<modules>` section, because IIS locks that section by default.
- Allows 50 MB request bodies (attachments are capped at 25 MB by the API).
- Hides the `Server` and `X-Powered-By` headers.
- Sets `ASPNETCORE_ENVIRONMENT=Production`.

## First administrator

With demo data off, a new database has no users, and only an administrator can add them. The `Bootstrap` settings create the first one.

1. Add this section to the website's `appsettings.Production.json`. Use a password of at least 8 characters.

   ```json
   "Bootstrap": {
     "AdminUserName": "admin",
     "AdminEmail": "you@yourcompany.com",
     "AdminPassword": "choose-a-strong-password",
     "AdminDisplayName": "Your Name"
   }
   ```

2. Recycle the app pool. `AdminDisplayName` is optional.
3. Sign in over HTTPS with that user name and password.
4. Delete the `Bootstrap` section from the file.
5. Add people under **Admin → Organisation**: **New employee**, then **Login** on that employee.

The app only creates this account while no administrator exists, enabled or disabled. It never changes an existing account, and it stores the password only as a hash. The Windows *Application* event log records the result. If nothing was created, it shows why.

## Updating

To update, re-run `install-iis.ps1`. It takes the app offline and republishes.

To update by hand:

1. Drop an `app_offline.htm` into the site folder.
2. Copy the new files, keeping `appsettings.Production.json`.
3. Delete `app_offline.htm`.

Data Protection keys live outside the site folder, so sign-in sessions and encrypted attachments survive updates
and recycles.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| **Nobody can sign in on a new install** | With demo data off, there are no users yet. See *First administrator*. |
| **HTTP 500 with an empty page under a virtual path** | The path is a plain folder or virtual directory. Make it an Application (see *Hosting under a virtual path*). |
| **HTTP 500.35** | Two applications share one app pool. Give each its own pool, or stop the other site and recycle the pool. |
| **Worker service stops or won't start** | Read the Windows *Application* event log. It now shows the real error, usually the database login or the data folder permissions. |
| **HTTP 500.19, code 0x8007000d** | The ASP.NET Core Module is missing. Install the .NET 10 Hosting Bundle, then run `iisreset`. |
| **HTTP 500.19, code 0x80070021** ("cannot be used at this path") | `web.config` uses a section that IIS locks at server level. The current `web.config` avoids `<modules>` for this reason. If an older copy still has a `<modules>` block, delete it. If `<handlers>` is reported, unlock it once, elevated: `%windir%\system32\inetsrv\appcmd unlock config -section:system.webServer/handlers`. |
| **HTTP 500.30 / 500.31** | The app failed to start. Set `stdoutLogEnabled="true"` in `web.config`, recycle, and read `logs\stdout_*.log`. The usual cause is the database login. The Windows *Application* event log also has the details. |
| **HTTP 405 on save / delete** | The WebDAV Publishing feature intercepted the request. `install-iis.ps1` disables its module for the site. By hand, run this elevated: `%windir%\system32\inetsrv\appcmd set config "FollowUpOfficer" -section:system.webServer/modules /-"[name='WebDAVModule']" /commit:apphost`. Or uninstall WebDAV Publishing, which this app does not need. |
| **HTTP 413 on upload** | Raise `maxAllowedContentLength` in `web.config`. |
| **Real-time updates are slow** | The WebSocket Protocol feature is not installed, so SignalR uses long polling. Install the feature. |
| **Blank page under `/followup`** | `index.html` was served as a static file by an old build. Rebuild the UI and republish. |
| **Links in emails point to the wrong host** | `FollowUp:PublicAppUrl` is wrong. Set it to the external URL, including the virtual path. |
| **Reminders stop at night** | The background engine was running in IIS and the pool was idle. Use the Worker service (the default), or keep `AlwaysRunning`, idle time-out 0 and Application Initialization. |
| **`Cannot open database` / LocalDB errors** | LocalDB does not work under IIS. Use SQL Server and grant the pool identity a login (see above). |

Use `GET /api/health` (anonymous) to check that the app is reachable. The Integration monitor in the UI shows job
and Microsoft 365 health.
