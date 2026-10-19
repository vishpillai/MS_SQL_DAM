#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
    exec sudo -- bash "$0" "$@"
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
APP_SOURCE="${SCRIPT_DIR}/app/SqlDam.Web"
INSTALL_DIR="/opt/sqldam"
DATA_DIR="/var/lib/sqldam"
CONFIG_DIR="/etc/sqldam"
CONFIG_FILE="${CONFIG_DIR}/sqldam.env"
SERVICE_FILE="/etc/systemd/system/sqldam.service"

if [[ ! -f "${APP_SOURCE}" ]]; then
    echo "SQL DAM Linux app was not found in ${SCRIPT_DIR}/app. Extract the complete Linux package and rerun this script." >&2
    exit 1
fi
if ! command -v systemctl >/dev/null 2>&1; then
    echo "This installer requires a systemd-based Linux distribution." >&2
    exit 1
fi

escape_env_value() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    printf '"%s"' "${value}"
}

if [[ ! -f "${CONFIG_FILE}" ]]; then
    echo "SQL DAM first-run setup"
    echo "The initial Super Admin must already exist in your OIDC identity provider."

    read -r -p "Organization name: " organization_name
    read -r -p "Organization identifier: " organization_identifier
    read -r -p "Initial Super Admin email: " super_admin_email
    read -r -p "OIDC authority URL (HTTPS): " oidc_authority
    read -r -p "OIDC client ID: " oidc_client_id
    read -r -p "Allowed DNS host name (leave blank for local-only access): " allowed_host
    read -r -s -p "OIDC client secret: " oidc_client_secret
    printf '\n'

    if [[ -z "${organization_name//[[:space:]]/}" || -z "${organization_identifier//[[:space:]]/}" ]]; then
        echo "Organization name and identifier are required." >&2
        exit 1
    fi
    if [[ ! "${super_admin_email}" == *@*.* ]]; then
        echo "Enter a valid initial Super Admin email address." >&2
        exit 1
    fi
    if [[ "${oidc_authority}" != https://* || -z "${oidc_client_id}" || -z "${oidc_client_secret}" ]]; then
        echo "A HTTPS OIDC authority, client ID, and client secret are required for production." >&2
        exit 1
    fi
    if [[ -n "${allowed_host}" && ! "${allowed_host}" =~ ^[A-Za-z0-9.-]+$ ]]; then
        echo "Enter a DNS host name without a scheme, port, or path." >&2
        exit 1
    fi
    allowed_hosts="localhost;127.0.0.1"
    if [[ -n "${allowed_host}" ]]; then
        allowed_hosts="${allowed_hosts};${allowed_host}"
    fi

    install -d -o root -g root -m 0700 "${CONFIG_DIR}"
    {
        printf 'ASPNETCORE_ENVIRONMENT="Production"\n'
        printf 'ASPNETCORE_URLS="http://127.0.0.1:5055"\n'
        printf 'AllowedHosts=%s\n' "$(escape_env_value "${allowed_hosts}")"
        printf 'Storage__DataDirectory="%s"\n' "${DATA_DIR}"
        printf 'Authentication__Authority=%s\n' "$(escape_env_value "${oidc_authority}")"
        printf 'Authentication__ClientId=%s\n' "$(escape_env_value "${oidc_client_id}")"
        printf 'Authentication__ClientSecret=%s\n' "$(escape_env_value "${oidc_client_secret}")"
        printf 'Bootstrap__OrganizationName=%s\n' "$(escape_env_value "${organization_name}")"
        printf 'Bootstrap__OrganizationIdentifier=%s\n' "$(escape_env_value "${organization_identifier}")"
        printf 'Bootstrap__SuperAdminEmail=%s\n' "$(escape_env_value "${super_admin_email}")"
    } > "${CONFIG_FILE}"
    chown root:root "${CONFIG_FILE}"
    chmod 0600 "${CONFIG_FILE}"
    unset oidc_client_secret
else
    echo "Using existing SQL DAM configuration at ${CONFIG_FILE}."
fi

if ! getent group sqldam >/dev/null 2>&1; then
    groupadd --system sqldam
fi
if ! id -u sqldam >/dev/null 2>&1; then
    useradd --system --gid sqldam --home-dir "${DATA_DIR}" --shell /usr/sbin/nologin sqldam
fi

install -d -o root -g root -m 0755 "${INSTALL_DIR}"
install -d -o sqldam -g sqldam -m 0700 "${DATA_DIR}"
install -d -o sqldam -g sqldam -m 0750 "${DATA_DIR}/certs"
systemctl stop sqldam.service 2>/dev/null || true
cp -a "${SCRIPT_DIR}/app/." "${INSTALL_DIR}/"
chown -R root:root "${INSTALL_DIR}"
chmod 0755 "${INSTALL_DIR}/SqlDam.Web"
install -m 0644 "${SCRIPT_DIR}/sqldam.service" "${SERVICE_FILE}"
systemctl daemon-reload
systemctl enable --now sqldam.service

echo "SQL DAM service installed and enabled."
echo "Local service URL: http://127.0.0.1:5055"
echo "For remote access, put it behind an HTTPS reverse proxy. Do not expose port 5055 directly to the network."
echo "Configuration: ${CONFIG_FILE}"
echo "Data and protected key ring: ${DATA_DIR}"

if command -v curl >/dev/null 2>&1; then
    for attempt in {1..30}; do
        if curl --fail --silent http://127.0.0.1:5055/health >/dev/null; then
            echo "Health check passed."
            exit 0
        fi
        sleep 1
    done
    echo "The service was enabled but did not pass its health check. Recent logs:" >&2
    journalctl -u sqldam.service --no-pager -n 60 >&2 || true
    exit 1
fi
