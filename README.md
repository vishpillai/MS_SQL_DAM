# SQL DAM

SQL DAM's monitoring service and web dashboard run on Windows and Linux. The native desktop client and MSI installer are Windows-only; Linux deployments use the browser dashboard and the separate package in `installer-linux/`. SQL DAM provides source management, searchable event history, configurable detection rules, alert triage, an administrative audit trail, and organization-level retention settings.

For the contribution workflow, review [CONTRIBUTING.md](CONTRIBUTING.md). Release notes are maintained in [CHANGELOG.md](CHANGELOG.md).

## Download standalone installers

Download the separately packaged installers from the [SQL DAM GitHub release](https://github.com/vishpillai/MS_SQL_DAM/releases/latest):

- [Windows x64 installer (MSI)](https://github.com/vishpillai/MS_SQL_DAM/releases/latest/download/SqlDam-Setup-v1.0.0.msi)
- [Linux x64 service package](https://github.com/vishpillai/MS_SQL_DAM/releases/latest/download/SQL-DAM-linux-x64.tar.gz)

The Windows MSI installs the monitoring service and desktop client. The Linux archive installs the service; see the platform instructions below before installation.

## Database engine support

Connection validation and read-only catalog browsing are available for Microsoft SQL Server, PostgreSQL, MySQL/MariaDB, and IBM Db2. This provider support lets operators test connectivity and select databases and tables; it does not mean all four engines have activity capture yet. Oracle and other engines are not currently supported.

Activity capture currently has a SQL Server reader for existing Extended Events `.xel` and SQL Server Audit `.sqlaudit` output in the instance's default error-log directory. SQL DAM no longer creates, alters, starts, or stops sessions, audits, or specifications on monitored servers. A DBA must configure the feeds and grant the SQL DAM reader permission to read them. Table selections in SQL DAM are filters/configuration; they do not enable capture on the server.

The HTTPS host-agent ingestion API accepts normalized activity events for remote sources when `AgentApi:Enabled` is enabled with production OIDC authentication. A Super Admin can generate the tenant API key on **Organization**. The API endpoint is not itself a host agent: provider-specific native audit-file readers and a packaged agent service are still needed for PostgreSQL, MySQL/MariaDB, Db2, and other engines. Until those readers exist, those sources can be connected and browsed but cannot capture activity. Do not treat a successful connection test as evidence of audit coverage.

## Collection model

The SQL Server reader understands these existing SQL Server-native feeds:

- **Extended Events:** reads `SqlDam_SecurityEvents*.xel` files for login, error, and schema DDL events when a DBA has configured a session with that file target. The `master` database is available for explicitly selected user-created tables.
- **SQL Server Audit:** reads `SqlDam_SecurityAudit*.sqlaudit` files for permission and principal/role changes and table DML when the DBA has configured an audit with that file target.

Files are read incrementally using file/offset cursors. To bound SQL DAM's added work, collection polls no more often than every 30 seconds, reads at most 500 rows per feed per poll, allows at most two concurrent source polls overall and one poll at a time per configured server, and backs off repeated failures up to five minutes. SQL connections use small pools and short connection timeouts. Feed availability and coverage depend on the DBA's server-side configuration and the monitoring account's permissions; the Sources page reports failures. These limits reduce collector load but cannot guarantee zero impact: the native audit configuration and its event volume determine the database's audit overhead.

The SQL DAM reader does not set buffer, rollover, or delivery options on the server. Review the DBA-configured feed's blocking, retention, and loss behavior before relying on it; any event feed can consume CPU, memory, disk, or I/O and can lose events under pressure.

Sources, rules, alerts, and DAM configuration are stored in local SQLite under `App_Data/sqldam.db` (configurable with `Storage:DataDirectory`). Captured activity and collector cursors can stay in SQLite or use a separate SQL Server database configured by `LogStore:ConnectionString`. SQL login passwords are protected with ASP.NET Core Data Protection. Protect and back up the SQLite database and Data Protection key ring together.

Optional SMTP alert delivery is configured by a Super Admin on **Organization → Email alert delivery**. Configure the relay, sender, recipient list, STARTTLS, optional username/password, and minimum severity; use **Send test email** to verify delivery. SMTP passwords are protected at rest with Data Protection. New alert incidents are sent through a background queue, so SMTP latency does not block event collection; updates to an already-open alert do not send duplicate messages. An SMTP username/password requires STARTTLS.

SQL source connections use TLS encryption and validate certificates by default where the provider supports it. The source form has an explicit **Trust server certificate** option for trusted private development servers with self-signed certificates; it keeps traffic encrypted but skips certificate chain and server-name validation. Db2 connections use SSL and can be given a PEM or DER CA/server certificate file on the SQL DAM host. For production, install trusted certificates and connect with names that match them.

To send captured activity logs to a SQL Server database, set the `LogStore__ConnectionString` environment variable for the service, for example:

```text
Server=sql-log-host,1433;Database=SqlDamLogs;User Id=SqlDamWriter;Password=<secret>;Encrypt=True;TrustServerCertificate=False
```

Use a connection string for any reachable SQL Server database. The database must already exist; on startup SQL DAM creates its `SqlDamActivityEvents` and `SqlDamSourceCursors` tables and indexes. The login needs permission to create those objects, then select, insert, and delete rows for collection and retention. Keep credentials in a protected service secret or environment configuration, not in source control. Existing events in SQLite are not automatically copied when the SQL Server log store is first enabled.

## DAM roles

Production role checks use the OIDC role claim (default claim type `roles`; configurable with `Authentication:RoleClaimType`). Assign these roles in the identity provider:

- **SuperAdmin:** all read and configuration pages, including organization settings.
- **Admin:** read pages plus source, table-tracking, rule, and alert-management actions.
- **Auditor** or **ReadUser:** read-only dashboard, activity, user-access, alert, and audit-trail pages.

The installer asks for the organization name and identifier, plus the initial Super Admin's account email. It stores the organization details and creates that local role assignment once when the service first starts. The email must match an account in the configured identity provider; the identity provider still controls account creation, passwords, and sign-in. Later, Super Admins can create DAM user profiles and assign Admin, Auditor (read only), or SuperAdmin roles by account email on **DAM users**. Local assignments override DAM role claims for that email. The default email claim is `email`; configure `Authentication:EmailClaimType` if the provider uses another claim. Role changes take effect when the user refreshes or signs in again.

Linux hosts connect to Microsoft SQL Server with SQL authentication. Windows Integrated authentication is available only when the SQL DAM service runs on Windows. PostgreSQL, MySQL/MariaDB, and Db2 use username/password authentication on both platforms.

Development mode keeps the local dashboard open for development. Two-step verification is deferred; enforce MFA in the OIDC identity provider when deploying with production authentication.

## Operations and security

Production startup requires OIDC configuration through a protected secret provider:

- `Authentication__Authority`
- `Authentication__ClientId`
- `Authentication__ClientSecret`
- `Authentication__RoleClaimType` (optional; defaults to `roles`)
- `AllowedHosts` (set to approved DNS host names)

The OIDC client needs `/signin-oidc` and `/signout-callback-oidc` callbacks. Terminate TLS at a trusted reverse proxy, forward `X-Forwarded-Proto`, and expose the application only over HTTPS. Development is intentionally unauthenticated and loopback-only when OIDC is absent.

Connections to monitored databases use encrypted transport and certificate validation where the selected provider supports it. Install trusted certificates on the SQL DAM host and use server names that match the certificates. Db2 TLS can use an explicit PEM or DER CA/server certificate path.

Administrative changes and alert resolutions are written to a per-organization SHA-256 hash chain. This makes partial or accidental edits detectable but is not immutable storage: a privileged operator with database access could rewrite and recompute the chain. External log forwarding, protected retention, access roles/separation of duties, recovery testing, and a customer-specific control assessment are still needed for a regulated production deployment. SQL DAM does not assert blanket RBI compliance or certification; retention and control applicability must be set by the deploying organization.

## SaaS direction

The first target remains on-premises. A future SaaS design should use a customer-managed Windows collector that initiates outbound TLS, tenant authorization/membership checks, managed multi-tenant storage, key rotation, and external audit-log anchoring. The current agent ingestion API is disabled unless explicitly configured and has no packaged host-side feed reader; it is not a supported production SaaS collector. Do not expose it publicly as-is.

## Run locally

Requires the .NET 10 SDK.

```powershell
dotnet run --project .\src\SqlDam.Web\SqlDam.Web.csproj
```

Open <http://127.0.0.1:5055>. The dashboard binds to loopback only.

The `/health` endpoint reports application availability, configured sources, and open alerts. It reports `complianceStatus: not-assessed` deliberately.

## Windows MSI & Desktop Application Deployment

SQL DAM provides a complete native Windows deployment package that bundles both the **Background Monitoring Service** and the **Native Windows Desktop Application**:

1. **`SqlDam.Web.exe`**: Runs as an unattended background Windows Service (`builder.Host.UseWindowsService()`) with embedded SQLite storage and activity collection.
2. **`SqlDam.Desktop.exe`**: A native Windows desktop application with quick navigation toolbar, embedded WebView2 dashboard, live service connection monitoring, and system tray integration.
3. **`SqlDam-Setup-v1.0.0.msi`**: Standalone Windows Installer package available from the [GitHub release](https://github.com/vishpillai/MS_SQL_DAM/releases/latest). It installs both executables and creates Start Menu and Desktop shortcuts automatically.

### Installation for Clients

- Download the [Windows x64 MSI](https://github.com/vishpillai/MS_SQL_DAM/releases/latest/download/SqlDam-Setup-v1.0.0.msi) and run it as Administrator.
- In the setup wizard, enter your organization name and identifier, then the email of the first Super Admin. Create that account in your identity provider and configure the OIDC service settings before installing, because production startup requires OIDC.
- The installer installs to `C:\Program Files\SqlDam\`, starts the `SqlDam` Windows Service automatically, and places a **"SQL DAM Desktop"** shortcut on the Desktop and Start Menu.
- Operators can launch the **SQL DAM Desktop** app directly or open `http://127.0.0.1:5055/` in any browser.

## Linux Service Deployment

Download the [Linux x64 service package](https://github.com/vishpillai/MS_SQL_DAM/releases/latest/download/SQL-DAM-linux-x64.tar.gz), extract it on the Linux host, and run `sudo bash install.sh` from the extracted package directory. See [installer-linux/README.md](installer-linux/README.md) for host prerequisites and installation details.

### Build the package from source

Build the standalone Linux x64 package from Windows with:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer-linux\Build-Linux.ps1
```

The package is written to `installer-linux\dist\SQL-DAM-linux-x64.tar.gz`. Copy it to a systemd-based Linux host, extract it, and run `sudo bash install.sh`. First-run setup asks for the organization, initial Super Admin, and production OIDC client settings. The service runs as the unprivileged `sqldam` account, binds to loopback on port 5055, and keeps its database and Data Protection key ring in `/var/lib/sqldam`. Put it behind an HTTPS reverse proxy for remote browser access. The environment file is root-readable only; keep backups of it, the SQLite database, and the protected key ring. Restrict the data directory to the service account and protect its disk with the host's approved at-rest encryption.

The Windows and Linux packages are built separately: Windows files remain under `installer\`, while Linux packaging, systemd configuration, and its archive live under `installer-linux\`.

## Database & Storage Requirements

**Does it require a database to keep the logs?**

- **For SQL DAM itself: a SQL Server is optional.**
  Local SQLite stores application configuration. Set `LogStore__ConnectionString` to keep captured activity logs and collector cursors in a central SQL Server database.
- **For monitored SQL Server instances: SQL DAM does not create server-side audit objects or business tables.**
  Its current reader queries existing SQL Server Extended Events (`.xel`) and SQL Server Audit (`.sqlaudit`) files in the monitored instance's error log directory. A DBA configures these feeds and grants read access. Captured events are stored in local SQLite or the separately configured SQL DAM log database.

## Initial product direction

- Run as a Windows or Linux service with a local web dashboard.
- Collect SQL Server activity into a searchable event history.
- Evaluate activity against configurable alert rules and surface suspicious behavior.
- Keep source setup and monitoring state explicit; unsupported events and unavailable collection must never appear as an all-clear.
