#!/usr/bin/env bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
# JIM Setup Script
# Downloads and configures JIM for production deployment.
#
# Usage:
#   curl -fsSL https://junctional.io/get | bash
#
# Or download and inspect first:
#   curl -fsSL -o setup.sh https://raw.githubusercontent.com/TetronIO/JIM/main/deploy/setup.sh
#   bash setup.sh
#
# Options:
#   --renew-certificate   Issue a new server certificate from the certificate authority an earlier run of
#                         this script created, for the same names, and restart jim.web. Uses JIM_INSTALL_DIR.
#   --help                Show this help.
#
# JIM serves HTTPS. The script either creates a certificate authority (CA) and a server certificate for this
# server, or installs your organisation's certificate and key, in the tls folder of the installation.
#
# Non-interactive mode (for automation):
#   Set all required environment variables before running. The script will skip
#   prompts for any variable that is already set.
#
#   Required env vars for non-interactive:
#     JIM_SSO_AUTHORITY, JIM_SSO_CLIENT_ID, JIM_SSO_SECRET, JIM_SSO_API_SCOPE,
#     JIM_SSO_CLAIM_TYPE, JIM_SSO_MV_ATTRIBUTE, JIM_SSO_INITIAL_ADMIN
#
#   Optional env vars:
#     JIM_INSTALL_DIR       - Installation directory (default: ./jim)
#     JIM_SETUP_DB_MODE     - "bundled" or "external" (default: prompt)
#     JIM_SETUP_AUTO_START  - "true" to start JIM without prompting
#     JIM_DB_HOSTNAME       - External DB hostname (required if db_mode=external)
#     JIM_DB_NAME           - Database name (default: jim)
#     JIM_DB_USERNAME       - Database username (default: jim)
#     JIM_DB_PASSWORD       - Database password (auto-generated if bundled)
#     JIM_SETUP_TLS_MODE    - "generate" (create a CA and server certificate) or "provided" (default: prompt)
#     JIM_SETUP_TLS_NAMES   - Comma-separated DNS names and IP addresses users reach JIM at, for a generated
#                             certificate (default: prompt, suggesting this host's name and address)
#     JIM_SETUP_TLS_CERT_FILE - Your organisation's PEM certificate, followed by any intermediate CA
#                             certificates (required if tls_mode=provided)
#     JIM_SETUP_TLS_KEY_FILE  - The certificate's unencrypted PEM private key (required if tls_mode=provided)
#     JIM_TRUSTED_PROXIES   - Address(es) of a reverse proxy or load balancer in front of JIM. Set it empty
#                             for none (default: prompt)

set -euo pipefail

# --- Configuration ---
GITHUB_REPO="TetronIO/JIM"
GITHUB_API_URL="https://api.github.com/repos/${GITHUB_REPO}/releases/latest"
RELEASE_DOWNLOAD_BASE="https://github.com/${GITHUB_REPO}/releases/latest/download"
COMPOSE_FILES=(-f docker-compose.yml -f docker-compose.production.yml)
DOCS_BASE="https://docs.junctional.io"

# The user JIM runs as inside its containers. Compose mounts the TLS key with its host owner and mode, and
# JIM has every capability dropped, so the key must belong to this user.
JIM_UID=1654

# A generated server certificate lasts a year (renew with --renew-certificate); its CA lasts ten.
TLS_SERVER_DAYS=365
TLS_CA_DAYS=3650

# --- Colour support ---
setup_colours() {
    if [ -t 1 ] && command -v tput >/dev/null 2>&1 && [ "$(tput colors 2>/dev/null || echo 0)" -ge 8 ]; then
        BOLD=$(tput bold)
        DIM=$(tput dim)
        RED=$(tput setaf 1)
        GREEN=$(tput setaf 2)
        YELLOW=$(tput setaf 3)
        BLUE=$(tput setaf 4)
        CYAN=$(tput setaf 6)
        RESET=$(tput sgr0)
    else
        BOLD="" DIM="" RED="" GREEN="" YELLOW="" BLUE="" CYAN="" RESET=""
    fi
}

# --- Output helpers ---
info()    { echo "${BLUE}${BOLD}[INFO]${RESET} $*"; }
success() { echo "${GREEN}${BOLD}[OK]${RESET}   $*"; }
warn()    { echo "${YELLOW}${BOLD}[WARN]${RESET} $*"; }
error()   { echo "${RED}${BOLD}[ERR]${RESET}  $*" >&2; }
fatal()   { error "$*"; exit 1; }

# --- Prompt helper ---
# Prompts for a value if not already set via environment variable.
# Usage: prompt_value "VAR_NAME" "Prompt text" "default_value"
prompt_value() {
    local var_name="$1"
    local prompt_text="$2"
    local default_value="${3:-}"

    # If the variable is already set and non-empty, skip the prompt
    local current_value="${!var_name:-}"
    if [ -n "$current_value" ]; then
        return
    fi

    if [ -n "$default_value" ]; then
        printf "%s [%s]: " "$prompt_text" "$default_value"
    else
        printf "%s: " "$prompt_text"
    fi

    local input
    read -r input
    input="${input:-$default_value}"

    if [ -z "$input" ]; then
        fatal "$var_name is required"
    fi

    printf -v "$var_name" '%s' "$input"
}

# --- Prompt for secret (no echo) ---
prompt_secret() {
    local var_name="$1"
    local prompt_text="$2"

    local current_value="${!var_name:-}"
    if [ -n "$current_value" ]; then
        return
    fi

    printf "%s: " "$prompt_text"
    read -rs input
    echo

    if [ -z "$input" ]; then
        fatal "$var_name is required"
    fi

    printf -v "$var_name" '%s' "$input"
}

# --- Generate a secure random password ---
generate_password() {
    if [ -r /dev/urandom ]; then
        # Read enough random bytes and filter, avoiding SIGPIPE with pipefail
        local result
        result=$(dd if=/dev/urandom bs=256 count=1 2>/dev/null | tr -dc 'A-Za-z0-9' | head -c 32)
        if [ ${#result} -ge 32 ]; then
            printf '%s' "$result"
        else
            fatal "Failed to generate secure password"
        fi
    else
        fatal "Cannot generate secure password: /dev/urandom is not available"
    fi
}

# --- Yes/No prompt ---
prompt_yn() {
    local prompt_text="$1"
    local default="${2:-y}"

    if [ "$default" = "y" ]; then
        printf "%s [Y/n]: " "$prompt_text"
    else
        printf "%s [y/N]: " "$prompt_text"
    fi

    local input
    read -r input
    input="${input:-$default}"

    case "$input" in
        [yY]|[yY][eE][sS]) return 0 ;;
        *) return 1 ;;
    esac
}

# --- Update a value in the .env file ---
# Uses line-by-line rewrite to avoid sed delimiter collisions and macOS sed incompatibilities.
# Replaces the first uncommented match, or the first commented match if no uncommented line exists.
update_env() {
    local key="$1"
    local value="$2"
    local env_file="$3"
    local tmp_file="${env_file}.tmp"
    local replaced=false

    : > "$tmp_file"

    # First pass: try to replace an uncommented line (^KEY=...)
    if grep -q "^${key}=" "$env_file" 2>/dev/null; then
        while IFS= read -r line || [ -n "$line" ]; do
            if [ "$replaced" = false ] && [[ "$line" =~ ^${key}= ]]; then
                printf '%s=%s\n' "$key" "$value" >> "$tmp_file"
                replaced=true
            else
                printf '%s\n' "$line" >> "$tmp_file"
            fi
        done < "$env_file"
    # Second pass: try to replace a commented line (#KEY=... or # KEY=...)
    elif grep -q "^#.*${key}=" "$env_file" 2>/dev/null; then
        while IFS= read -r line || [ -n "$line" ]; do
            if [ "$replaced" = false ] && [[ "$line" =~ ^#.*${key}= ]]; then
                printf '%s=%s\n' "$key" "$value" >> "$tmp_file"
                replaced=true
            else
                printf '%s\n' "$line" >> "$tmp_file"
            fi
        done < "$env_file"
    fi

    # Fallback: append if not found at all
    if [ "$replaced" = false ]; then
        cp "$env_file" "$tmp_file"
        printf '%s=%s\n' "$key" "$value" >> "$tmp_file"
    fi

    mv "$tmp_file" "$env_file"
}

# --- Banner ---
show_banner() {
    echo
    echo "${CYAN}${BOLD}     ██╗██╗███╗   ███╗${RESET}"
    echo "${CYAN}${BOLD}     ██║██║████╗ ████║${RESET}"
    echo "${CYAN}${BOLD}     ██║██║██╔████╔██║${RESET}"
    echo "${CYAN}${BOLD}██   ██║██║██║╚██╔╝██║${RESET}"
    echo "${CYAN}${BOLD}╚█████╔╝██║██║ ╚═╝ ██║${RESET}"
    echo "${CYAN}${BOLD} ╚════╝ ╚═╝╚═╝     ╚═╝${RESET}"
    echo "${DIM}Junctional Identity Manager${RESET}"
    echo
}

# --- Prerequisites ---
check_prerequisites() {
    info "Checking prerequisites..."

    if ! command -v curl >/dev/null 2>&1; then
        fatal "curl is required but not installed. Install it with your package manager."
    fi

    if ! command -v openssl >/dev/null 2>&1; then
        fatal "openssl is required for JIM's HTTPS certificate but not installed. Install it with your package manager."
    fi

    if ! command -v docker >/dev/null 2>&1; then
        fatal "Docker is required but not installed. See https://docs.docker.com/engine/install/"
    fi

    # Check for Docker Compose v2 (docker compose, not docker-compose)
    if ! docker compose version >/dev/null 2>&1; then
        fatal "Docker Compose v2 is required. See https://docs.docker.com/compose/install/"
    fi

    local compose_version
    compose_version=$(docker compose version --short 2>/dev/null || echo "unknown")
    success "Docker Compose ${compose_version} detected"

    # Check Docker daemon is running
    if ! docker info >/dev/null 2>&1; then
        # Provide context-appropriate advice based on installed Docker variant
        if systemctl --user list-unit-files docker-desktop.service >/dev/null 2>&1; then
            fatal "Docker daemon is not running. Start Docker Desktop from your applications menu, or run: systemctl --user start docker-desktop"
        elif systemctl list-unit-files docker.service >/dev/null 2>&1; then
            fatal "Docker daemon is not running. Start it with: sudo systemctl start docker"
        else
            fatal "Docker daemon is not running. Please start your Docker service (Docker Desktop or Docker Engine)."
        fi
    fi

    success "All prerequisites met"
}

# --- Detect latest release ---
detect_latest_version() {
    info "Detecting latest JIM release..."

    local response
    response=$(curl -fsSL "$GITHUB_API_URL" 2>/dev/null) || fatal "Failed to query GitHub API. Check your internet connection."

    JIM_RELEASE_VERSION=$(echo "$response" | grep -o '"tag_name": "v[^"]*"' | head -1 | cut -d'"' -f4 | sed 's/^v//')

    if [ -z "$JIM_RELEASE_VERSION" ]; then
        fatal "No stable release found. Check https://github.com/${GITHUB_REPO}/releases for pre-release versions and install manually."
    fi

    success "Latest release: v${JIM_RELEASE_VERSION}"
}

# --- Download files ---
download_files() {
    local install_dir="$1"

    info "Downloading JIM files to ${install_dir}..."
    mkdir -p "$install_dir"

    local base_url="$RELEASE_DOWNLOAD_BASE"

    curl -fsSL -o "${install_dir}/docker-compose.yml" "${base_url}/docker-compose.yml" \
        || fatal "Failed to download docker-compose.yml"
    success "Downloaded docker-compose.yml"

    curl -fsSL -o "${install_dir}/docker-compose.production.yml" "${base_url}/docker-compose.production.yml" \
        || fatal "Failed to download docker-compose.production.yml"
    success "Downloaded docker-compose.production.yml"

    # Published as default.env.example: GitHub renames an asset whose name starts with a dot.
    curl -fsSL -o "${install_dir}/.env" "${base_url}/default.env.example" \
        || fatal "Failed to download default.env.example"
    success "Downloaded .env (from default.env.example)"
}

# --- Configure database ---
configure_database() {
    local env_file="$1"

    echo
    info "Database configuration"
    echo

    local db_mode="${JIM_SETUP_DB_MODE:-}"

    if [ -z "$db_mode" ]; then
        echo "  ${BOLD}1)${RESET} Bundled PostgreSQL (recommended for getting started)"
        echo "  ${BOLD}2)${RESET} External PostgreSQL (recommended for production)"
        echo
        printf "Select database topology [1]: "
        local choice
        read -r choice
        choice="${choice:-1}"

        case "$choice" in
            1) db_mode="bundled" ;;
            2) db_mode="external" ;;
            *) fatal "Invalid choice: $choice" ;;
        esac
    fi

    if [ "$db_mode" = "bundled" ]; then
        info "Using bundled PostgreSQL"

        # Auto-generate a secure password if not set
        if [ -z "${JIM_DB_PASSWORD:-}" ]; then
            JIM_DB_PASSWORD=$(generate_password)
            success "Generated secure database password"
        fi

        JIM_DB_NAME="${JIM_DB_NAME:-jim}"
        JIM_DB_USERNAME="${JIM_DB_USERNAME:-jim}"
        USE_BUNDLED_DB="true"

    elif [ "$db_mode" = "external" ]; then
        info "Using external PostgreSQL"

        prompt_value "JIM_DB_HOSTNAME" "  Database hostname"
        prompt_value "JIM_DB_NAME" "  Database name" "jim"
        prompt_value "JIM_DB_USERNAME" "  Database username" "jim"
        prompt_secret "JIM_DB_PASSWORD" "  Database password"
        USE_BUNDLED_DB="false"

    else
        fatal "Invalid JIM_SETUP_DB_MODE: $db_mode (must be 'bundled' or 'external')"
    fi

    update_env "JIM_DB_NAME" "$JIM_DB_NAME" "$env_file"
    update_env "JIM_DB_USERNAME" "$JIM_DB_USERNAME" "$env_file"
    update_env "JIM_DB_PASSWORD" "$JIM_DB_PASSWORD" "$env_file"

    if [ "$db_mode" = "external" ]; then
        update_env "JIM_DB_HOSTNAME" "$JIM_DB_HOSTNAME" "$env_file"
    fi
}

# --- Configure SSO ---
configure_sso() {
    local env_file="$1"

    echo
    info "SSO/OIDC configuration"
    echo "${DIM}  JIM requires an OIDC identity provider (e.g., Microsoft Entra ID, AD FS, Keycloak).${RESET}"
    echo "${DIM}  See: ${DOCS_BASE}/administration/sso-setup/${RESET}"
    echo

    prompt_value "JIM_SSO_AUTHORITY" "  OIDC Authority URL (e.g., https://login.microsoftonline.com/{tenant}/v2.0)"
    prompt_value "JIM_SSO_CLIENT_ID" "  Client/Application ID"
    prompt_secret "JIM_SSO_SECRET" "  Client secret"
    prompt_value "JIM_SSO_API_SCOPE" "  API scope (e.g., api://{client-id}/access_as_user)"
    prompt_value "JIM_SSO_CLAIM_TYPE" "  JWT claim type for user identity" "sub"
    prompt_value "JIM_SSO_MV_ATTRIBUTE" "  Metaverse attribute to match claim against" "Subject Identifier"
    prompt_value "JIM_SSO_INITIAL_ADMIN" "  Initial admin claim value (identifies the first admin user)"

    update_env "JIM_SSO_AUTHORITY" "$JIM_SSO_AUTHORITY" "$env_file"
    update_env "JIM_SSO_CLIENT_ID" "$JIM_SSO_CLIENT_ID" "$env_file"
    update_env "JIM_SSO_SECRET" "$JIM_SSO_SECRET" "$env_file"
    update_env "JIM_SSO_API_SCOPE" "$JIM_SSO_API_SCOPE" "$env_file"
    update_env "JIM_SSO_CLAIM_TYPE" "$JIM_SSO_CLAIM_TYPE" "$env_file"
    update_env "JIM_SSO_MV_ATTRIBUTE" "$JIM_SSO_MV_ATTRIBUTE" "$env_file"
    update_env "JIM_SSO_INITIAL_ADMIN" "$JIM_SSO_INITIAL_ADMIN" "$env_file"
}

# --- Configure Docker registry ---
configure_registry() {
    local env_file="$1"

    update_env "DOCKER_REGISTRY" "ghcr.io/tetronio/" "$env_file"
    update_env "JIM_VERSION" "$JIM_RELEASE_VERSION" "$env_file"
    success "Configured image registry: ghcr.io/tetronio/ (v${JIM_RELEASE_VERSION})"
}

# --- TLS certificate ---

# Reads a value from an installation's .env file.
env_value() {
    local key="$1"
    local env_file="$2"
    grep "^${key}=" "$env_file" 2>/dev/null | head -1 | cut -d= -f2- || true
}

# This host's fully qualified name and first address: the names suggested for a generated certificate.
default_tls_names() {
    local fqdn address
    fqdn=$(hostname -f 2>/dev/null || hostname 2>/dev/null || true)
    address=$(hostname -I 2>/dev/null | awk '{print $1}' || true)
    if [ -n "$fqdn" ] && [ -n "$address" ]; then
        printf '%s,%s' "$fqdn" "$address"
    else
        printf '%s' "${fqdn}${address}"
    fi
}

# Reads a comma-separated list of DNS names and IP addresses, and sets:
#   TLS_FIRST_NAME  - the first DNS name, or the first address when there is none
#   TLS_SUBJECT     - the server certificate's common name
#   TLS_SAN         - the server certificate's subjectAltName
#   TLS_CONSTRAINTS - the certificate authority's nameConstraints, limiting it to exactly these names
parse_tls_names() {
    local list="$1"
    local name
    local -a entries san=() constraints=() dns=() addresses=()

    IFS=',' read -r -a entries <<< "$list"
    for name in "${entries[@]}"; do
        name="${name//[[:space:]]/}"
        [ -z "$name" ] && continue
        if [[ "$name" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then
            addresses+=("$name")
            san+=("IP:${name}")
            constraints+=("permitted;IP:${name}/255.255.255.255")
        elif [[ "$name" =~ ^[0-9A-Fa-f:]+$ ]] && [[ "$name" == *:* ]]; then
            addresses+=("$name")
            san+=("IP:${name}")
            constraints+=("permitted;IP:${name}/ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")
        elif [[ "$name" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$ ]]; then
            dns+=("$name")
            san+=("DNS:${name}")
            constraints+=("permitted;DNS:${name}")
        else
            fatal "Not a DNS name or IP address: ${name}"
        fi
    done

    [ ${#san[@]} -gt 0 ] || fatal "No names given for the certificate"

    # A kind of name with no permitted entry would be unconstrained, so rule each kind out when it is unused:
    # every address, or every DNS name outside the reserved .invalid domain (RFC 6761).
    if [ ${#addresses[@]} -eq 0 ]; then
        constraints+=("excluded;IP:0.0.0.0/0.0.0.0" "excluded;IP:::/::")
    fi
    if [ ${#dns[@]} -eq 0 ]; then
        constraints+=("permitted;DNS:invalid")
        TLS_FIRST_NAME="${addresses[0]}"
        # Not the address: OpenSSL checks a common name that looks like a host name against the DNS
        # constraint when a certificate has no DNS names, so an address there fails verification.
        TLS_SUBJECT="JIM"
    else
        TLS_FIRST_NAME="${dns[0]}"
        TLS_SUBJECT="${dns[0]}"
    fi

    TLS_SAN=$(IFS=,; echo "${san[*]}")
    TLS_CONSTRAINTS=$(IFS=,; echo "${constraints[*]}")
}

# Creates a certificate authority (CA) for this JIM server, as tls/ca.crt and tls/ca.key. Its name constraints
# limit it to the names given, so that a browser trusting it will not accept a certificate it signs for any
# other site, even if its key were stolen. They are not marked critical, so that a client which cannot apply
# them still accepts JIM's certificate; current browsers and operating systems apply them.
create_certificate_authority() {
    local tls_dir="$1"
    local work
    work=$(mktemp -d)

    cat > "${work}/ca.cnf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
O = JIM
CN = JIM certificate authority for ${TLS_FIRST_NAME} ($(date -u +%Y-%m-%d))
[ext]
basicConstraints = critical,CA:TRUE,pathlen:0
keyUsage = critical,keyCertSign,cRLSign
subjectKeyIdentifier = hash
nameConstraints = ${TLS_CONSTRAINTS}
EOF

    if ! { (umask 077 && openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "${work}/ca.key") \
        && openssl req -new -x509 -config "${work}/ca.cnf" -key "${work}/ca.key" -sha256 -days "$TLS_CA_DAYS" \
            -out "${work}/ca.crt"; } 2> "${work}/openssl.log"; then
        cat "${work}/openssl.log" >&2
        rm -rf "$work"
        fatal "Failed to create the certificate authority"
    fi

    rm -f "${tls_dir}/ca.key"
    mv "${work}/ca.key" "${tls_dir}/ca.key"
    chmod 400 "${tls_dir}/ca.key"
    mv "${work}/ca.crt" "${tls_dir}/ca.crt"
    chmod 644 "${tls_dir}/ca.crt"
    rm -rf "$work"

    # The names are kept for --renew-certificate, which must issue for exactly the names the CA permits.
    printf '%s\n' "$TLS_NAMES" > "${tls_dir}/names"
}

# Issues JIM's server certificate from the CA, for TLS_SAN, and installs it as tls/tls.crt and tls/tls.key.
issue_server_certificate() {
    local install_dir="$1"
    local tls_dir="${install_dir}/tls"
    local work
    work=$(mktemp -d)

    cat > "${work}/csr.cnf" <<EOF
[req]
distinguished_name = dn
prompt = no
[dn]
CN = ${TLS_SUBJECT}
EOF

    cat > "${work}/ext.cnf" <<EOF
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = serverAuth
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid:always
subjectAltName = ${TLS_SAN}
EOF

    if ! { (umask 077 && openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "${work}/tls.key") \
        && openssl req -new -config "${work}/csr.cnf" -key "${work}/tls.key" -out "${work}/tls.csr" \
        && openssl x509 -req -in "${work}/tls.csr" -CA "${tls_dir}/ca.crt" -CAkey "${tls_dir}/ca.key" \
            -set_serial "0x$(openssl rand -hex 16)" -days "$TLS_SERVER_DAYS" -sha256 \
            -extfile "${work}/ext.cnf" -out "${work}/tls.crt"; } 2> "${work}/openssl.log"; then
        cat "${work}/openssl.log" >&2
        rm -rf "$work"
        fatal "Failed to issue the server certificate"
    fi

    install_tls_pair "$install_dir" "${work}/tls.crt" "${work}/tls.key"
    rm -rf "$work"
}

# Checks an organisation's certificate and key before installing them, so that a mistake shows here rather
# than as jim.web failing to start.
check_provided_certificate() {
    local certificate="$1"
    local key="$2"

    [ -r "$certificate" ] || fatal "Cannot read the certificate file: ${certificate}"
    [ -r "$key" ] || fatal "Cannot read the private key file: ${key}"
    openssl x509 -in "$certificate" -noout 2>/dev/null || fatal "${certificate} is not a PEM certificate"

    # The empty passphrase makes an encrypted key fail here, instead of openssl stopping to ask for one.
    openssl pkey -in "$key" -passin pass: -noout 2>/dev/null \
        || fatal "${key} is not an unencrypted PEM private key. JIM cannot use an encrypted key; decrypt it with: openssl pkey -in <encrypted key> -out <new key file>"

    [ "$(openssl x509 -in "$certificate" -noout -pubkey)" = "$(openssl pkey -in "$key" -passin pass: -pubout)" ] \
        || fatal "The private key does not belong to the certificate"

    openssl x509 -in "$certificate" -noout -checkend 0 >/dev/null || fatal "The certificate has expired"
    openssl x509 -in "$certificate" -noout -checkend $((30 * 86400)) >/dev/null \
        || warn "The certificate expires within 30 days, on $(certificate_expiry "$certificate")"
}

# Installs a certificate and key as tls/tls.crt and tls/tls.key, with the key readable only by JIM.
install_tls_pair() {
    local install_dir="$1"
    local certificate="$2"
    local key="$3"
    local tls_dir="${install_dir}/tls"

    # Each file is replaced rather than overwritten: an existing key belongs to JIM's user, so it may not be
    # writable here, while the folder is.
    cp "$certificate" "${tls_dir}/tls.crt.new"
    chmod 644 "${tls_dir}/tls.crt.new"
    mv -f "${tls_dir}/tls.crt.new" "${tls_dir}/tls.crt"

    (umask 077 && cp "$key" "${tls_dir}/tls.key.new")
    chmod 400 "${tls_dir}/tls.key.new"
    set_tls_key_owner "$install_dir" "${tls_dir}/tls.key.new"
    mv -f "${tls_dir}/tls.key.new" "${tls_dir}/tls.key"
}

# Makes the key belong to JIM's user.
set_tls_key_owner() {
    local install_dir="$1"
    local key="$2"

    if [ "$(id -u)" -eq 0 ]; then
        chown "${JIM_UID}:${JIM_UID}" "$key" || fatal "Failed to make ${key} belong to UID ${JIM_UID}"
        return
    fi

    # Without root, change the owner from inside a container, using the jim.web image: anyone who can run
    # containers can do that, and the container runtime maps the owner correctly even when it runs rootless.
    local env_file="${install_dir}/.env"
    # Named as docker-compose.yml names it, so that this is the image Compose runs.
    local version image
    version=$(env_value JIM_VERSION "$env_file")
    image="$(env_value DOCKER_REGISTRY "$env_file")jim-web${version:+:${version}}"
    local folder
    folder=$(cd "$(dirname "$key")" && pwd)

    if ! docker image inspect "$image" >/dev/null 2>&1; then
        info "Pulling ${image} to set the key's owner..."
        docker pull -q "$image" >/dev/null || fatal "Failed to pull ${image}"
    fi
    docker run --rm --network none --user 0:0 --entrypoint chown -v "${folder}:/tls" "$image" \
        "${JIM_UID}:${JIM_UID}" "/tls/$(basename "$key")" \
        || fatal "Failed to make ${key} belong to UID ${JIM_UID}. As root, run: chown ${JIM_UID}:${JIM_UID} ${key}"
}

certificate_expiry() {
    openssl x509 -in "$1" -noout -enddate | cut -d= -f2
}

# The first DNS name in a certificate, or its first IP address; empty if it has neither.
certificate_first_name() {
    local names
    names=$(openssl x509 -in "$1" -noout -ext subjectAltName 2>/dev/null | tail -n +2 | tr -d ' ') || true
    local first
    first=$(printf '%s' "$names" | tr ',' '\n' | grep '^DNS:' | head -1 | cut -d: -f2-) || true
    if [ -z "$first" ]; then
        first=$(printf '%s' "$names" | tr ',' '\n' | grep '^IPAddress:' | head -1 | cut -d: -f2-) || true
    fi
    printf '%s' "$first"
}

# --- Configure the HTTPS certificate ---
configure_certificate() {
    local install_dir="$1"
    local tls_dir="${install_dir}/tls"

    echo
    info "HTTPS certificate"
    echo "${DIM}  JIM serves HTTPS, which browsers on other machines need in order to sign in.${RESET}"
    echo

    local mode="${JIM_SETUP_TLS_MODE:-}"

    if [ -z "$mode" ]; then
        echo "  ${BOLD}1)${RESET} Create a certificate for this server (recommended for getting started)"
        echo "  ${BOLD}2)${RESET} Use your organisation's certificate and key (recommended for production)"
        echo
        printf "Select certificate [1]: "
        local choice
        read -r choice
        choice="${choice:-1}"

        case "$choice" in
            1) mode="generate" ;;
            2) mode="provided" ;;
            *) fatal "Invalid choice: $choice" ;;
        esac
    fi

    mkdir -p "$tls_dir"
    chmod 700 "$tls_dir"

    if [ "$mode" = "generate" ]; then
        local previous_names=""
        if [ -f "${tls_dir}/names" ]; then
            previous_names=$(cat "${tls_dir}/names")
        fi

        echo "${DIM}  The certificate covers the DNS names and IP addresses users will type to reach JIM, and the${RESET}"
        echo "${DIM}  name any reverse proxy or load balancer connects to it at.${RESET}"
        prompt_value "JIM_SETUP_TLS_NAMES" "  Names and addresses, comma-separated" "${previous_names:-$(default_tls_names)}"
        TLS_NAMES="$JIM_SETUP_TLS_NAMES"
        parse_tls_names "$TLS_NAMES"

        # A CA made earlier for the same names is kept, so that browsers which already trust it carry on.
        if [ -f "${tls_dir}/ca.key" ] && [ "$TLS_NAMES" = "$previous_names" ]; then
            info "Keeping the existing certificate authority"
        else
            create_certificate_authority "$tls_dir"
            success "Created a certificate authority: ${tls_dir}/ca.crt"
        fi

        issue_server_certificate "$install_dir"
        TLS_MODE="generate"

    elif [ "$mode" = "provided" ]; then
        prompt_value "JIM_SETUP_TLS_CERT_FILE" "  Certificate file (PEM, followed by any intermediate CA certificates)"
        prompt_value "JIM_SETUP_TLS_KEY_FILE" "  Private key file (PEM, unencrypted)"
        check_provided_certificate "$JIM_SETUP_TLS_CERT_FILE" "$JIM_SETUP_TLS_KEY_FILE"
        install_tls_pair "$install_dir" "$JIM_SETUP_TLS_CERT_FILE" "$JIM_SETUP_TLS_KEY_FILE"

        # --renew-certificate renews only a certificate this script issued.
        rm -f "${tls_dir}/names"
        TLS_MODE="provided"

    else
        fatal "Invalid JIM_SETUP_TLS_MODE: $mode (must be 'generate' or 'provided')"
    fi

    success "Installed the certificate; it expires on $(certificate_expiry "${tls_dir}/tls.crt")"
}

# --- Configure a reverse proxy or load balancer ---
configure_proxy() {
    local env_file="$1"

    # Set, even when empty, means already answered: empty is no proxy.
    if [ -z "${JIM_TRUSTED_PROXIES+set}" ]; then
        echo
        info "Reverse proxy or load balancer"
        echo "${DIM}  Only needed if one sits in front of JIM. JIM then trusts it to report each client's address.${RESET}"
        echo
        if prompt_yn "Will users reach JIM through a reverse proxy or load balancer?" "n"; then
            prompt_value "JIM_TRUSTED_PROXIES" "  Address or network it connects to JIM from (e.g. 10.0.0.5 or 10.0.0.0/24)"
        else
            JIM_TRUSTED_PROXIES=""
        fi
    fi

    if [ -n "$JIM_TRUSTED_PROXIES" ]; then
        update_env "JIM_TRUSTED_PROXIES" "$JIM_TRUSTED_PROXIES" "$env_file"
        success "Trusting the proxy at ${JIM_TRUSTED_PROXIES}"
    fi
}

# --- Renew the server certificate ---
renew_certificate() {
    local install_dir="${JIM_INSTALL_DIR:-./jim}"
    local tls_dir="${install_dir}/tls"

    [ -f "${install_dir}/.env" ] || fatal "No JIM installation at ${install_dir}. Set JIM_INSTALL_DIR to its folder."

    if [ ! -f "${tls_dir}/ca.key" ] || [ ! -f "${tls_dir}/names" ]; then
        fatal "This script did not create this installation's certificate, so it cannot renew it. Replace ${tls_dir}/tls.crt and ${tls_dir}/tls.key with your renewed certificate and key, then restart jim.web (see the Deployment Guide, \"Replacing the certificate\")."
    fi

    command -v openssl >/dev/null 2>&1 || fatal "openssl is required but not installed."

    TLS_NAMES=$(cat "${tls_dir}/names")
    parse_tls_names "$TLS_NAMES"

    if ! openssl x509 -in "${tls_dir}/ca.crt" -noout -checkend $((TLS_SERVER_DAYS * 86400)) >/dev/null; then
        warn "The certificate authority expires on $(certificate_expiry "${tls_dir}/ca.crt"), before the new certificate would. Create a new one by running this script again without --renew-certificate, then distribute ${tls_dir}/ca.crt again."
    fi

    info "Issuing a new certificate for ${TLS_NAMES}..."
    issue_server_certificate "$install_dir"
    success "Installed the new certificate; it expires on $(certificate_expiry "${tls_dir}/tls.crt")"

    info "Restarting jim.web to load it..."
    (cd "$install_dir" && docker compose "${COMPOSE_FILES[@]}" restart jim.web) \
        || fatal "Failed to restart jim.web. Restart it yourself: cd ${install_dir} && docker compose ${COMPOSE_FILES[*]} restart jim.web"
    success "jim.web restarted with the new certificate"
}

# --- Launch JIM ---
launch_jim() {
    local install_dir="$1"
    local auto_start="${JIM_SETUP_AUTO_START:-}"

    echo

    local compose_cmd="docker compose ${COMPOSE_FILES[*]}"
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        compose_cmd="${compose_cmd} --profile with-db"
    fi
    compose_cmd="${compose_cmd} up -d"

    if [ "$auto_start" = "true" ]; then
        info "Starting JIM..."
        (cd "$install_dir" && eval "$compose_cmd")
    else
        if prompt_yn "Start JIM now?"; then
            info "Starting JIM..."
            (cd "$install_dir" && eval "$compose_cmd")
        else
            echo
            info "To start JIM later, run:"
            echo "  cd ${install_dir}"
            echo "  ${compose_cmd}"
            return
        fi
    fi

    echo
    success "JIM is starting up!"
    echo
    echo "  ${BOLD}Wait a moment for services to become healthy, then visit:${RESET}"
    echo "  ${CYAN}$(jim_url "$install_dir")${RESET}"
    echo
}

# The address users open JIM at: the certificate's first name, on the published port.
jim_url() {
    local install_dir="$1"
    local port
    port=$(env_value JIM_WEB_PORT "${install_dir}/.env")
    port="${port:-5200}"
    # JIM_WEB_PORT may carry an address prefix, such as 127.0.0.1:5200.
    port="${port##*:}"
    local name
    name=$(certificate_first_name "${install_dir}/tls/tls.crt")
    printf 'https://%s:%s' "${name:-<this server>}" "$port"
}

# --- Summary ---
show_summary() {
    local install_dir="$1"
    local tls_dir="${install_dir}/tls"
    local url
    url=$(jim_url "$install_dir")
    local absolute_dir
    absolute_dir=$(cd "$install_dir" && pwd)

    # Every command names the bundled database's profile too, or "down" would leave it running.
    local compose="docker compose ${COMPOSE_FILES[*]}"
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        compose="${compose} --profile with-db"
    fi

    echo "${BOLD}--------------------------------------------------${RESET}"
    echo "${BOLD}  Installation complete${RESET}"
    echo "${BOLD}--------------------------------------------------${RESET}"
    echo
    echo "  ${BOLD}Location:${RESET}     ${install_dir}"
    echo "  ${BOLD}Version:${RESET}      ${JIM_RELEASE_VERSION}"
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        echo "  ${BOLD}Database:${RESET}     Bundled PostgreSQL"
    else
        echo "  ${BOLD}Database:${RESET}     External (${JIM_DB_HOSTNAME})"
    fi
    echo "  ${BOLD}Address:${RESET}      ${url}"
    echo "  ${BOLD}Certificate:${RESET}  expires on $(certificate_expiry "${tls_dir}/tls.crt")"
    echo
    echo "  ${BOLD}Next steps:${RESET}"
    echo "    Register ${url}/signin-oidc as a redirect URI at your identity provider."
    if [ "$TLS_MODE" = "generate" ]; then
        echo "    Add this certificate authority to the trusted root certificate authorities of every machine"
        echo "    whose browser or tools use JIM (for example by Group Policy); until then, browsers warn:"
        echo "      ${absolute_dir}/tls/ca.crt"
        echo "    Keep ${absolute_dir}/tls/ca.key secret: it signs JIM's certificates."
        echo "    Renew the certificate before it expires:"
        echo "      curl -fsSL https://junctional.io/get | JIM_INSTALL_DIR=${absolute_dir} bash -s -- --renew-certificate"
    fi
    echo
    echo "  ${BOLD}Management commands:${RESET}"
    echo "    cd ${install_dir}"
    echo "    ${compose} ps"
    echo "    ${compose} logs -f"
    echo "    ${compose} down"
    echo
    echo "  ${BOLD}Documentation:${RESET}"
    echo "    Deployment: ${DOCS_BASE}/administration/deployment/"
    echo "    SSO Setup:  ${DOCS_BASE}/administration/sso-setup/"
    echo "    Releases:   https://github.com/${GITHUB_REPO}/releases"
    echo
}

show_help() {
    echo "Usage: bash setup.sh [--renew-certificate | --help]"
    echo
    echo "Installs JIM with Docker Compose, asking for anything not set in the environment."
    echo
    echo "  --renew-certificate  Issue a new server certificate from the certificate authority this"
    echo "                       script created, for the same names, and restart jim.web."
    echo "  --help               Show this help."
    echo
    echo "Both act on the installation in JIM_INSTALL_DIR (default: ./jim)."
    echo "Documentation: ${DOCS_BASE}/administration/deployment/"
}

# --- Main ---
main() {
    # When piped (curl ... | bash), stdin is the pipe, not the terminal.
    # Reopen stdin from /dev/tty so interactive prompts work.
    if [ ! -t 0 ] && [ -r /dev/tty ] && (echo < /dev/tty) 2>/dev/null; then
        exec < /dev/tty
    fi

    setup_colours

    case "${1:-}" in
        "") ;;
        --renew-certificate)
            renew_certificate
            exit 0
            ;;
        --help|-h)
            show_help
            exit 0
            ;;
        *)
            fatal "Unknown option: $1 (see --help)"
            ;;
    esac

    show_banner

    # Determine install directory
    local install_dir="${JIM_INSTALL_DIR:-./jim}"

    # Check for existing installation
    if [ -f "${install_dir}/.env" ]; then
        warn "Existing installation detected at ${install_dir}"
        if ! prompt_yn "Overwrite configuration?" "n"; then
            info "Aborting. Existing installation left untouched."
            exit 0
        fi
    fi

    check_prerequisites
    detect_latest_version
    download_files "$install_dir"

    local env_file="${install_dir}/.env"

    configure_database "$env_file"
    configure_sso "$env_file"
    configure_registry "$env_file"
    configure_certificate "$install_dir"
    configure_proxy "$env_file"

    launch_jim "$install_dir"
    show_summary "$install_dir"
}

main "$@"
