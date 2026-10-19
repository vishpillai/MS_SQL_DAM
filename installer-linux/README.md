# SQL DAM Linux service package

This directory is separate from the Windows MSI and desktop package in `installer/`. It builds a self-contained Linux x64 service package. Linux users access the same web dashboard in a browser; the WinForms/WebView2 desktop client is Windows-only.

**Publisher:** Vishal Pillai · [pillaivishal91@gmail.com](mailto:pillaivishal91@gmail.com) · [LinkedIn](https://www.linkedin.com/in/vishal-pillai-dba/)

Users can download the standalone [Linux x64 service package](https://github.com/vishpillai/MS_SQL_DAM/releases/latest/download/SQL-DAM-linux-x64.tar.gz) from GitHub Releases. Extract it on a supported systemd-based Linux host and follow the bundled `README.md` and `install.sh` instructions below.

## Build on Windows

Install the .NET 10 SDK and run this from the repository root in PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File .\installer-linux\Build-Linux.ps1
```

The standalone archive is created at `installer-linux\dist\SQL-DAM-linux-x64.tar.gz`. To target another .NET Linux runtime identifier supported by the SDK, pass `-Runtime linux-arm64`.

## Install on Linux

The target host must use systemd and provide `bash`, `sudo`, `curl`, and the standard `useradd`, `groupadd`, and `install` utilities. The `linux-x64` package targets x86_64 Red Hat Enterprise Linux 8, 9, and 10 as well as other supported Linux distributions. On RHEL, install the .NET runtime's native OS dependencies first (the app is self-contained, but these system libraries are provided by the OS):

```bash
sudo dnf install glibc libgcc ca-certificates openssl-libs libstdc++ libicu tzdata krb5-libs
```

RHEL may require an active subscription to access the package repositories. Extract the archive and run:

```bash
tar -xzf SQL-DAM-linux-x64.tar.gz
cd SQL-DAM-linux-x64
sudo bash install.sh
```

On first installation the script prompts for the organization name and identifier, initial Super Admin email, OIDC authority/client ID/client secret, and an optional allowed DNS host name. The initial administrator must already exist in the identity provider. The secret is hidden while entered and the environment file is created with root-only permissions. If remote browser access is needed, enter the name used by the HTTPS reverse proxy; the installer adds it to the host allow-list.

The installer creates a locked-down `sqldam` system account and installs a systemd unit. App files go to `/opt/sqldam`, configuration to `/etc/sqldam/sqldam.env`, and the SQLite database and Data Protection key ring to `/var/lib/sqldam`. Place optional Db2 CA certificates under `/var/lib/sqldam/certs/` so the service account can read them. The app binds to `127.0.0.1:5055`; check `systemctl status sqldam` and `journalctl -u sqldam` if startup fails.

Db2 sources use TLS. Use the Db2 SSL listener (normally port `50001`) and provide the server CA certificate when it is not already trusted by the host. The service unit sets the IBM Linux CLI driver's library path automatically.

Keep port 5055 on loopback. For remote users, configure an HTTPS reverse proxy and forward the original scheme and client address. The service runs as an unprivileged user with a private writable data directory. On Linux, filesystem permissions protect the Data Protection key ring; use approved full-disk encryption to protect it and database backups when the host is powered off.

The SQL DAM web service and dashboard are shared across Windows and Linux. Windows Integrated authentication for monitored SQL Server sources is available only on Windows; Linux SQL Server connections use SQL logins. Collection coverage remains based on the existing audit feeds and configured host-agent support.
