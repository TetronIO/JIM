#!/usr/bin/env bash
# Copyright (c) Tetron Limited. All rights reserved.
# Licensed under the Tetron Commercial License. See LICENSE file in the project root.
# JIM Setup Script
# Installs and configures JIM for production deployment with Docker or Podman, from the internet or from a
# release bundle.
#
# Usage, connected:
#   curl -fsSL https://junctional.io/get | sudo bash
#
# Or download and inspect first:
#   curl -fsSL -o setup.sh https://raw.githubusercontent.com/TetronIO/JIM/main/deploy/setup.sh
#   sudo bash setup.sh
#
# Usage, air-gapped: extract the release bundle and run the copy of this script inside it. It installs from
# the bundle's own files and images and needs no internet connection:
#   tar -xzf jim-release-X.Y.Z.tar.gz && cd jim-release-X.Y.Z && sudo ./setup.sh
#
# The installation keeps a copy of this script, for looking after it later:
#   sudo /opt/jim/setup.sh --renew-certificate
#
# Upgrade a Docker installation to the latest release, or, run inside an extracted release bundle, to that release:
#   curl -fsSL https://junctional.io/get | sudo bash -s -- --upgrade
#   cd jim-release-X.Y.Z && sudo ./setup.sh --upgrade
#
# Options:
#   --runtime docker|podman  The container runtime to install JIM on. Only needed where both are installed:
#                         the script asks rather than guess.
#   --rootless            Podman only: run JIM rootless, under a dedicated account, instead of as root.
#   --renew-certificate   Issue a new server certificate from the certificate authority this script created,
#                         for the same names, and restart JIM's web service.
#   --certificate         Create or install JIM's certificate again, for example to change its names or to
#                         move to your organisation's certificate, and restart JIM's web service.
#   --upgrade             Docker only: upgrade the installation to the latest release, or to the release bundle
#                         this script is in, keeping its settings, and restart JIM.
#   --help                Show this help.
#   The certificate and upgrade options act on the installation the script sits in, or JIM_INSTALL_DIR (by
#   default /opt/jim when run as root).
#
# JIM serves HTTPS. The script either creates a certificate authority (CA) and a server certificate for this
# server, or installs your organisation's certificate and key, in the tls folder of the installation.
#
# On Podman, JIM runs as root by default (rootful), as it does on Docker, so that it records each client's own
# address. With --rootless, it runs under a dedicated account, jim, which the script creates with lingering
# enabled, so that systemd starts JIM at boot with nobody logged in; run with --rootless by any other account,
# the script installs JIM under that account. Podman 4.4 or later and systemd are needed; without systemd, JIM
# still runs, as root, but does not start by itself after a reboot. A reinstall keeps the way JIM runs.
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
#     JIM_INSTALL_DIR       - Installation directory (default: /opt/jim when run as root, otherwise ./jim)
#     JIM_WEB_PORT          - HTTPS port users reach JIM on (default: prompt, suggesting 443)
#     JIM_SETUP_DB_MODE     - "bundled" or "external" (default: prompt)
#     JIM_SETUP_AUTO_START  - "true" to start JIM without prompting
#     JIM_DB_HOSTNAME       - External DB hostname (required if db_mode=external)
#     JIM_DB_NAME           - Database name (default: jim)
#     JIM_DB_USERNAME       - Database username (default: jim)
#     JIM_DB_PASSWORD       - Database password (auto-generated if bundled)
#     JIM_DB_SHARED_BUFFERS, JIM_DB_EFFECTIVE_CACHE_SIZE, JIM_DB_MAINTENANCE_WORK_MEM, JIM_DB_WORK_MEM,
#     JIM_DB_SHM_SIZE       - The bundled database's memory settings (default: sized to this host's memory;
#                             JIM_DB_SHM_SIZE is Docker only)
#     JIM_SETUP_TLS_MODE    - "generate" (create a CA and server certificate) or "provided" (default: prompt)
#     JIM_SETUP_TLS_NAMES   - Comma-separated DNS names and IP addresses users reach JIM at, for a generated
#                             certificate (default: prompt, suggesting this host's name and address)
#     JIM_SETUP_TLS_CERT_FILE - Your organisation's PEM certificate, followed by any intermediate CA
#                             certificates (required if tls_mode=provided)
#     JIM_SETUP_TLS_KEY_FILE  - The certificate's unencrypted PEM private key (required if tls_mode=provided)
#     JIM_TRUSTED_PROXIES   - Address(es) of a reverse proxy or load balancer in front of JIM. Set it empty
#                             for none (default: prompt)
#     JIM_SETUP_RUNTIME     - "docker" or "podman", as --runtime (default: the one installed; prompt if both)
#     JIM_SETUP_PODMAN_ROOTLESS - "true" to run JIM rootless on Podman, as --rootless
#     JIM_SETUP_PODMAN_ACCOUNT  - The account that runs JIM rootless, with --rootless (default: jim)
#     JIM_SETUP_FIX_APPARMOR   - "true" or "false": on a host whose AppArmor profiles for Podman stop JIM using
#                             the network, or stop its containers' processes signalling one another (Ubuntu
#                             24.04), add the rules it needs (default: prompt). Podman only.
#     JIM_SETUP_OPEN_FIREWALL  - "true" or "false": open the HTTPS port in firewalld, when it is running (default:
#                             prompt). Podman only.
#     JIM_SETUP_BACKUP_CONFIRMED - "true", with --upgrade: the database and encryption keys are backed up, so the
#                             upgrade need not ask (default: prompt, and a "no" cancels the upgrade)

set -euo pipefail

# --- Configuration ---
GITHUB_REPO="TetronIO/JIM"
GITHUB_API_URL="https://api.github.com/repos/${GITHUB_REPO}/releases/latest"
RELEASE_DOWNLOAD_BASE="https://github.com/${GITHUB_REPO}/releases/latest/download"
COMPOSE_FILES=(-f docker-compose.yml -f docker-compose.production.yml)
# The files an installation runs from as the release ships them, which an upgrade replaces, and the record of their
# checksums, by which an upgrade tells whether they were edited.
SHIPPED_COMPOSE_FILES=(docker-compose.yml docker-compose.production.yml)
COMPOSE_CHECKSUMS="compose-files.sha256"
DOCS_BASE="https://docs.junctional.io"

# The user JIM runs as inside its containers. Compose mounts the TLS key with its host owner and mode, and
# JIM has every capability dropped, so the key must belong to this user.
JIM_UID=1654

# A generated server certificate lasts a year (renew with --renew-certificate); its CA lasts ten.
TLS_SERVER_DAYS=365
TLS_CA_DAYS=3650

# The standard HTTPS port, so that users and identity provider registrations need no port in JIM's address.
DEFAULT_WEB_PORT=443

# How long the installer waits for JIM to be ready after starting it.
READY_TIMEOUT_SECONDS=600

# Podman: the oldest version with Quadlet, which runs JIM's pods under systemd, and the files a release publishes
# for it (the pod and settings files, then the Quadlet units).
PODMAN_MIN_VERSION="4.4"
PODMAN_FILES=(jim.yaml jim-database.yaml jim-config.yaml)
QUADLET_FILES=(jim.kube jim-database.kube jim.network)
DEFAULT_PODMAN_ACCOUNT="jim"

# AppArmor's policy folder and the kernel's interface to it, and the file of the rule that lets a rootful container's
# processes signal one another (see configure_apparmor).
APPARMOR_DIR="/etc/apparmor.d"
APPARMOR_FS="/sys/kernel/security/apparmor"
APPARMOR_SIGNAL_RULES="abstractions/base.d/jim-podman"

# Where this script is, when it runs from a file rather than from a pipe (curl ... | bash). Run from an extracted
# release bundle, it installs from the bundle instead of downloading; run from an installation, its options act
# on that installation.
SCRIPT_PATH=""
SCRIPT_DIR=""
if [ -n "${BASH_SOURCE[0]:-}" ] && [ -f "${BASH_SOURCE[0]}" ]; then
    SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
    SCRIPT_PATH="${SCRIPT_DIR}/$(basename "${BASH_SOURCE[0]}")"
fi
# Set as the installer runs, read by later steps.
USE_BUNDLED_DB=""
TLS_MODE=""
NEW_CA=""
REPLACED_CA=""
JIM_READY=""
EXISTING_DB_PASSWORD=""  # a reinstall's bundled database password, which the database keeps
RUNTIME=""          # docker or podman
CONFIG_FILE=""      # where the settings go: .env (Docker) or jim-config.yaml (Podman)
PODMAN_ACCOUNT=""   # Podman: the account JIM runs rootless under; empty when rootful
PODMAN_SYSTEMD=""   # Podman: "true" when systemd runs JIM through Quadlet
PODMAN_SOURCE=""    # Podman: the folder the release's Podman files are installed from

BUNDLE_DIR=""
if [ -n "$SCRIPT_DIR" ] && [ -f "${SCRIPT_DIR}/compose/docker-compose.yml" ] && [ -d "${SCRIPT_DIR}/docker-images" ]; then
    BUNDLE_DIR="$SCRIPT_DIR"
fi

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

    # Private from the start: the file replaces .env, which holds secrets.
    (umask 077 && : > "$tmp_file")

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

# --- Podman: the settings file ---
# A value as a double-quoted YAML string, so that any text reaches JIM unchanged.
yaml_quote() {
    local value="$1"
    value="${value//\\/\\\\}"
    value="${value//\"/\\\"}"
    printf '"%s"' "$value"
}

# Sets a value in jim-config.yaml: replaces the key's line, or its commented-out line, or adds it at the end.
update_config_yaml() {
    local key="$1"
    local value="$2"
    local config_file="$3"
    local tmp_file="${config_file}.tmp"
    local pattern="^  ${key}:"
    grep -q "$pattern" "$config_file" 2>/dev/null || pattern="^  # ${key}:"

    local replaced=false line
    : > "$tmp_file"
    while IFS= read -r line || [ -n "$line" ]; do
        if [ "$replaced" = false ] && [[ "$line" =~ $pattern ]]; then
            printf '  %s: %s\n' "$key" "$(yaml_quote "$value")" >> "$tmp_file"
            replaced=true
        else
            printf '%s\n' "$line" >> "$tmp_file"
        fi
    done < "$config_file"
    if [ "$replaced" = false ]; then
        printf '  %s: %s\n' "$key" "$(yaml_quote "$value")" >> "$tmp_file"
    fi

    chmod 644 "$tmp_file"
    mv "$tmp_file" "$config_file"
}

# Records a setting where the chosen runtime reads it. On Podman, the secrets go to Podman's secret store instead
# (store_podman_secrets), and the port to the Quadlet unit (install_podman_units).
set_setting() {
    local key="$1"
    local value="$2"
    if [ "$RUNTIME" = "podman" ]; then
        case "$key" in
            JIM_DB_PASSWORD|JIM_SSO_SECRET|JIM_INFRASTRUCTURE_API_KEY|JIM_WEB_PORT) return ;;
        esac
        update_config_yaml "$key" "$value" "$CONFIG_FILE"
    else
        update_env "$key" "$value" "$CONFIG_FILE"
    fi
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

# --- Choose the container runtime ---

# Docker Engine, and not the docker command some distributions provide as a front end to Podman.
docker_installed() {
    command -v docker >/dev/null 2>&1 && ! docker --version 2>/dev/null | grep -qi podman
}

podman_installed() {
    command -v podman >/dev/null 2>&1
}

choose_runtime() {
    local requested="${JIM_SETUP_RUNTIME:-}"
    if [ -n "$requested" ]; then
        case "$requested" in
            docker) docker_installed || fatal "Docker is not installed on this host (see https://docs.docker.com/engine/install/)" ;;
            podman) podman_installed || fatal "Podman is not installed on this host. On RHEL and its derivatives: dnf install podman" ;;
            *) fatal "Invalid runtime: ${requested} (must be 'docker' or 'podman')" ;;
        esac
        RUNTIME="$requested"
        return
    fi

    local has_docker="" has_podman=""
    docker_installed && has_docker="true"
    podman_installed && has_podman="true"

    if [ -n "$has_docker" ] && [ -n "$has_podman" ]; then
        # Both are here, and either could be the one the organisation supports, so ask rather than guess.
        echo
        info "Docker and Podman are both installed"
        echo "  ${BOLD}1)${RESET} Docker"
        echo "  ${BOLD}2)${RESET} Podman"
        echo
        printf "Which should run JIM? "
        local choice=""
        read -r choice || true
        case "$choice" in
            1) RUNTIME="docker" ;;
            2) RUNTIME="podman" ;;
            *) fatal "Choose 1 or 2, or run this script again with --runtime docker or --runtime podman" ;;
        esac
    elif [ -n "$has_docker" ]; then
        RUNTIME="docker"
    elif [ -n "$has_podman" ]; then
        RUNTIME="podman"
    else
        fatal "JIM runs on Docker or Podman, and neither is installed. On RHEL and its derivatives, install Podman: dnf install podman. Otherwise see https://docs.docker.com/engine/install/"
    fi
}

# --- Prerequisites ---
check_prerequisites() {
    info "Checking prerequisites..."

    if [ -z "$BUNDLE_DIR" ] && ! command -v curl >/dev/null 2>&1; then
        fatal "curl is required but not installed. Install it with your package manager."
    fi

    if ! command -v openssl >/dev/null 2>&1; then
        fatal "openssl is required for JIM's HTTPS certificate but not installed. Install it with your package manager."
    fi

    if [ "$RUNTIME" = "podman" ]; then
        check_podman
        success "All prerequisites met"
        return
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
    # .env will hold the database password and the identity provider's client secret.
    chmod 600 "${install_dir}/.env"
    success "Downloaded .env (from default.env.example)"
}

# --- Install from a release bundle ---
read_bundle_version() {
    local verb="${1:-Installing}"
    [ -f "${BUNDLE_DIR}/VERSION" ] || fatal "No VERSION file in ${BUNDLE_DIR}; this does not look like a complete JIM release bundle."
    JIM_RELEASE_VERSION=$(tr -d '[:space:]' < "${BUNDLE_DIR}/VERSION")
    success "${verb} JIM v${JIM_RELEASE_VERSION} from the release bundle in ${BUNDLE_DIR} (no internet connection needed)"
}

copy_bundle_files() {
    local install_dir="$1"

    info "Copying JIM files to ${install_dir}..."
    mkdir -p "$install_dir"
    cp "${BUNDLE_DIR}/compose/docker-compose.yml" "${BUNDLE_DIR}/compose/docker-compose.production.yml" "$install_dir/"
    cp "${BUNDLE_DIR}/compose/.env.example" "${install_dir}/.env"
    # .env will hold the database password and the identity provider's client secret.
    chmod 600 "${install_dir}/.env"
    success "Copied the compose files and .env"
}

# Loads an image archive into the runtime that runs JIM. Podman reads it from standard input, so that the account
# running JIM needs no access to the bundle's folder.
load_image_archive() {
    local archive="$1"
    if [ "$RUNTIME" = "podman" ]; then
        as_jim_account podman load < "$archive" >/dev/null
    else
        docker load -i "$archive" >/dev/null
    fi
}

# Loads the images the installation will run. The PostgreSQL image is needed only for the bundled database.
load_bundle_images() {
    info "Loading JIM's images from the bundle (this takes a minute or two)..."
    local name
    for name in jim-web jim-worker jim-scheduler; do
        [ -f "${BUNDLE_DIR}/docker-images/${name}.tar" ] || fatal "The bundle is missing docker-images/${name}.tar"
        load_image_archive "${BUNDLE_DIR}/docker-images/${name}.tar" || fatal "Failed to load docker-images/${name}.tar"
    done
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        [ -f "${BUNDLE_DIR}/docker-images/postgres-18.tar" ] \
            || fatal "This bundle has no PostgreSQL image (docker-images/postgres-18.tar). Run the installer again and choose an external PostgreSQL server."
        load_image_archive "${BUNDLE_DIR}/docker-images/postgres-18.tar" || fatal "Failed to load docker-images/postgres-18.tar"
    fi
    success "Loaded the images"
}

# The PostgreSQL image a compose file pins, the default of its JIM_DB_IMAGE.
pinned_database_image() {
    local compose_file="$1"
    # The second sed reads all its input, where head would stop early and fail the pipeline under pipefail.
    sed -n 's/^[[:space:]]*image:[[:space:]]*\${JIM_DB_IMAGE:-\([^}]*\)}[[:space:]]*$/\1/p' "$compose_file" | sed -n 1p
}

# Docker only, bundled database only. Sets DATABASE_IMAGE_ID to what JIM_DB_IMAGE must be for the compose file's
# pinned PostgreSQL image to run: empty when Docker finds the pinned image, or the image's ID when it cannot.
# Docker's classic image store drops the registry digest when it loads an image archive, so on that store the pin
# never resolves for the bundle's image. The bundle records the ID of the PostgreSQL image it ships; this checks
# the image loaded under the pinned name against it, so that Compose runs the image by its ID. An ID is the digest
# of the image's content, so, like the pin, it names only the image the bundle shipped, however the name is later
# retagged.
DATABASE_IMAGE_ID=""
check_database_image() {
    local compose_file="$1"
    local pinned
    pinned=$(pinned_database_image "$compose_file")
    [ -n "$pinned" ] || fatal "Could not find the PostgreSQL image in ${compose_file}"

    DATABASE_IMAGE_ID=""
    # Docker's containerd image store keeps the digest, and a host that pulled the image has it.
    if docker image inspect "$pinned" >/dev/null 2>&1; then
        return
    fi
    local name="${pinned%@*}"
    [ "$name" != "$pinned" ] \
        || fatal "The PostgreSQL image ${pinned} is not available, and this installation cannot download it."
    [ -n "$BUNDLE_DIR" ] \
        || fatal "Docker cannot find the PostgreSQL image ${pinned} after downloading it."

    local ids_file="${BUNDLE_DIR}/docker-images/postgres-18.image-ids"
    [ -f "$ids_file" ] \
        || fatal "Docker cannot find ${pinned} by its digest, and this bundle does not record the PostgreSQL image's ID to check it by instead. Enable Docker's containerd image store (https://docs.docker.com/engine/storage/containerd/), or choose an external PostgreSQL server."
    local id
    id=$(docker image inspect -f '{{.Id}}' "$name" 2>/dev/null) \
        || fatal "The PostgreSQL image ${name} is not available after loading the bundle, and this installation cannot download it."
    grep -qxF "$id" "$ids_file" \
        || fatal "The image named ${name} on this host (${id}) is not the PostgreSQL image this bundle ships. Check the bundle is intact (sha256sum -c checksums.sha256), and extract it again if not."

    DATABASE_IMAGE_ID="$id"
    success "Checked the PostgreSQL image against the bundle; JIM runs it by its ID, ${id}"
}

# Confirms every image the installation runs is present, so that a gap shows here rather than as Compose trying
# to download it.
verify_bundle_images() {
    local install_dir="$1"
    local -a profile=()
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        profile=(--profile with-db)
    fi
    local image
    for image in $(cd "$install_dir" && docker compose "${COMPOSE_FILES[@]}" ${profile[@]+"${profile[@]}"} config --images); do
        docker image inspect "$image" >/dev/null 2>&1 \
            || fatal "The image ${image} is not available after loading the bundle, and this installation cannot download it."
    done
}

# The command that runs the installation's copy of this script, or the download when there is no copy.
installer_command() {
    local install_dir="$1"
    local absolute_dir
    absolute_dir=$(cd "$install_dir" && pwd)
    local sudo_prefix=""
    [ "$(id -u)" -eq 0 ] && sudo_prefix="sudo "

    if [ -x "${absolute_dir}/setup.sh" ]; then
        printf '%s%s/setup.sh' "$sudo_prefix" "$absolute_dir"
    else
        printf 'curl -fsSL https://junctional.io/get | %sJIM_INSTALL_DIR=%s bash -s --' "$sudo_prefix" "$absolute_dir"
    fi
}

# Keeps a copy of this script in the installation, so that looking after it later needs nothing else.
save_installer_copy() {
    local install_dir="$1"
    local target="${install_dir}/setup.sh"

    if [ -n "$SCRIPT_PATH" ]; then
        if [ "$SCRIPT_PATH" != "$(cd "$install_dir" && pwd)/setup.sh" ]; then
            cp "$SCRIPT_PATH" "$target"
        fi
    elif ! curl -fsSL -o "$target" "${RELEASE_DOWNLOAD_BASE}/setup.sh" 2>/dev/null; then
        rm -f "$target"
        warn "Could not save a copy of this script in ${install_dir}; download it from ${RELEASE_DOWNLOAD_BASE}/setup.sh when you need it"
        return
    fi
    chmod 755 "$target"
}

# The installation an option acts on, or the folder a new installation goes in.
resolve_install_dir() {
    if [ -n "${JIM_INSTALL_DIR:-}" ]; then
        printf '%s' "$JIM_INSTALL_DIR"
    elif [ -n "$SCRIPT_DIR" ] && is_installation "$SCRIPT_DIR"; then
        printf '%s' "$SCRIPT_DIR"
    elif [ "$(id -u)" -eq 0 ]; then
        printf '/opt/jim'
    else
        printf './jim'
    fi
}

# Whether a folder holds a JIM installation this script made, on either runtime.
is_installation() {
    local dir="$1"
    { [ -f "${dir}/docker-compose.production.yml" ] && [ -f "${dir}/.env" ]; } \
        || { [ -f "${dir}/jim-config.yaml" ] && [ -f "${dir}/install.conf" ]; }
}

# Reads how an existing installation runs, for the options that look after it.
load_installation() {
    local install_dir="$1"
    if [ -f "${install_dir}/install.conf" ] && [ -f "${install_dir}/jim-config.yaml" ]; then
        RUNTIME="podman"
        CONFIG_FILE="${install_dir}/jim-config.yaml"
        PODMAN_ACCOUNT=$(install_state JIM_PODMAN_ACCOUNT "$install_dir")
        PODMAN_SYSTEMD=$(install_state JIM_PODMAN_SYSTEMD "$install_dir")
        JIM_WEB_PORT=$(install_state JIM_WEB_PORT "$install_dir")
        if [ -n "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -ne 0 ] && [ "$(id -un)" != "$PODMAN_ACCOUNT" ]; then
            fatal "This installation runs as ${PODMAN_ACCOUNT}. Run this as root (sudo), or as ${PODMAN_ACCOUNT}."
        fi
        if [ -z "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -ne 0 ]; then
            fatal "This installation runs as root. Run this as root (sudo)."
        fi
    elif [ -f "${install_dir}/.env" ]; then
        RUNTIME="docker"
        CONFIG_FILE="${install_dir}/.env"
    else
        fatal "No JIM installation at ${install_dir}. Run this from the installation's copy of setup.sh, or set JIM_INSTALL_DIR."
    fi
}

install_state() {
    local key="$1"
    local install_dir="$2"
    grep "^${key}=" "${install_dir}/install.conf" 2>/dev/null | head -1 | cut -d= -f2- || true
}

# --- Podman ---

version_at_least() {
    local actual="$1"
    local minimum="$2"
    [ "$(printf '%s\n%s\n' "$minimum" "$actual" | sort -V | head -1)" = "$minimum" ]
}

quadlet_available() {
    [ -x /usr/libexec/podman/quadlet ] || [ -x /usr/lib/podman/quadlet ]
}

check_podman() {
    local version
    version=$(podman version --format '{{.Client.Version}}' 2>/dev/null) \
        || fatal "Podman is installed but not working: podman version failed"
    version_at_least "$version" "$PODMAN_MIN_VERSION" \
        || fatal "JIM needs Podman ${PODMAN_MIN_VERSION} or later, and this host has ${version}. Update Podman with your package manager."
    success "Podman ${version} detected"

    if [ -d /run/systemd/system ] && quadlet_available; then
        PODMAN_SYSTEMD="true"
    else
        PODMAN_SYSTEMD="false"
        warn "Without systemd and Podman's Quadlet, JIM runs but will not start by itself after a reboot"
    fi

    if ! command -v base64 >/dev/null 2>&1; then
        fatal "base64 is required but not installed. Install coreutils with your package manager."
    fi
}

# Decides which account runs JIM: root by default (rootful); with --rootless, a dedicated account, or the account
# running this script when that is not root. A reinstall keeps the way the installation runs.
choose_podman_account() {
    local install_dir="$1"
    local rootless="${JIM_SETUP_PODMAN_ROOTLESS:-false}"
    local account="${JIM_SETUP_PODMAN_ACCOUNT:-}"
    if [ -n "$account" ] && [ "$rootless" != "true" ]; then
        fatal "JIM_SETUP_PODMAN_ACCOUNT names the account for a rootless installation: pass --rootless too, or leave it unset to run JIM as root"
    fi

    # An installation's images, secrets and data are in the Podman storage of the account that runs it, so a
    # reinstall that moved it to another account would leave them all behind.
    if [ -f "${install_dir}/install.conf" ]; then
        local recorded
        recorded=$(install_state JIM_PODMAN_ACCOUNT "$install_dir")
        if [ -z "$recorded" ] && [ "$rootless" = "true" ]; then
            fatal "The installation at ${install_dir} runs as root, and a reinstall cannot move it to rootless: run this without --rootless, or install into another folder (JIM_INSTALL_DIR)"
        fi
        if [ -n "$recorded" ] && [ -n "$account" ] && [ "$account" != "$recorded" ]; then
            fatal "The installation at ${install_dir} runs as ${recorded}, and a reinstall cannot move it to ${account}: leave JIM_SETUP_PODMAN_ACCOUNT unset, or install into another folder (JIM_INSTALL_DIR)"
        fi
        if [ -n "$recorded" ]; then
            rootless="true"
            account="$recorded"
            info "Keeping this installation rootless, as ${recorded}"
        fi
    fi

    if [ "$rootless" != "true" ]; then
        [ "$(id -u)" -eq 0 ] \
            || fatal "On Podman, JIM runs as root unless you choose otherwise: run this script as root (sudo). To run JIM rootless under your own account instead, pass --rootless."
        PODMAN_ACCOUNT=""
        info "Installing JIM to run as root (rootful)"
        return
    fi

    if [ "$(id -u)" -ne 0 ]; then
        if [ -n "$account" ] && [ "$account" != "$(id -un)" ]; then
            fatal "JIM runs as ${account} here: run this script as root (sudo), or as ${account}"
        fi
        PODMAN_ACCOUNT=$(id -un)
        if [ "$PODMAN_SYSTEMD" = "true" ] && ! lingering_enabled "$PODMAN_ACCOUNT"; then
            fatal "JIM would stop when you log out, and not start at boot, because lingering is not enabled for ${PODMAN_ACCOUNT}. Ask your administrators to run: loginctl enable-linger ${PODMAN_ACCOUNT}"
        fi
    else
        # Without systemd there is no manager to keep JIM running under another account.
        [ "$PODMAN_SYSTEMD" = "true" ] \
            || fatal "Running JIM rootless under a dedicated account needs systemd with Podman's Quadlet, which this host lacks: run this without --rootless to run JIM as root"
        PODMAN_ACCOUNT="${account:-$DEFAULT_PODMAN_ACCOUNT}"
    fi
    info "Installing JIM to run rootless as ${PODMAN_ACCOUNT}"
}

lingering_enabled() {
    local account="$1"
    [ -f "/var/lib/systemd/linger/${account}" ] \
        || [ "$(loginctl show-user "$account" --property=Linger --value 2>/dev/null)" = "yes" ]
}

account_home() {
    getent passwd "$PODMAN_ACCOUNT" | cut -d: -f6
}

nologin_shell() {
    local shell
    for shell in /usr/sbin/nologin /sbin/nologin; do
        if [ -x "$shell" ]; then
            printf '%s' "$shell"
            return
        fi
    done
    printf '/bin/false'
}

# Gives the account a range of subordinate user and group IDs, which rootless containers map their users into,
# if it has none. useradd assigns them on current distributions; older ones, and accounts created elsewhere, may
# lack them.
ensure_subordinate_ids() {
    local account="$1"
    local uid
    uid=$(id -u "$account")
    local file option start
    for file in /etc/subuid /etc/subgid; do
        if grep -q -e "^${account}:" -e "^${uid}:" "$file" 2>/dev/null; then
            continue
        fi
        start=$(awk -F: 'BEGIN { next_free = 100000 } { if ($2 + $3 > next_free) next_free = $2 + $3 } END { print next_free }' "$file" 2>/dev/null || echo 100000)
        option="--add-subuids"
        [ "$file" = "/etc/subgid" ] && option="--add-subgids"
        usermod "$option" "${start}-$((start + 65535))" "$account" \
            || fatal "Failed to give ${account} subordinate IDs in ${file}"
    done
}

# Creates the account that runs JIM rootless, if needed, and enables lingering for it, so that its systemd
# manager runs JIM with nobody logged in and starts it at boot.
prepare_podman_account() {
    [ -n "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -eq 0 ] || return 0
    local account="$PODMAN_ACCOUNT"

    if ! id "$account" >/dev/null 2>&1; then
        useradd --create-home --comment "JIM (Junctional Identity Manager)" --shell "$(nologin_shell)" "$account" \
            || fatal "Failed to create the account ${account}"
        success "Created the account ${account}, which runs JIM"
    else
        info "Using the existing account ${account}"
    fi
    ensure_subordinate_ids "$account"

    loginctl enable-linger "$account" || fatal "Failed to enable lingering for ${account}"
    local uid
    uid=$(id -u "$account")
    local deadline=$((SECONDS + 60))
    until [ -S "/run/user/${uid}/bus" ]; do
        [ "$SECONDS" -lt "$deadline" ] \
            || fatal "The systemd manager of ${account} did not start. See: systemctl status user@${uid}.service"
        sleep 1
    done
    success "Enabled lingering for ${account}: JIM runs with nobody logged in, and starts at boot"
}

# Stops a rootless installation whose systemd manager would look for JIM in the wrong folders. The manager runs
# Quadlet and Podman with its own environment, which takes settings made for every account (in /etc/environment,
# say); a folder setting that points elsewhere leaves Quadlet generating no units from the ones this script
# installs, or Podman using storage or a runtime folder that is not the account's, and JIM never starts.
check_account_environment() {
    [ -n "$PODMAN_ACCOUNT" ] && [ "$PODMAN_SYSTEMD" = "true" ] || return 0
    local uid home
    uid=$(id -u "$PODMAN_ACCOUNT")
    home=$(account_home)

    local environment
    environment=$(jim_systemctl show-environment) \
        || fatal "Could not read the environment of the systemd manager of ${PODMAN_ACCOUNT}. See: systemctl status user@${uid}.service"

    local problems=() name expected actual
    for name in XDG_CONFIG_HOME XDG_DATA_HOME XDG_RUNTIME_DIR; do
        case "$name" in
            XDG_CONFIG_HOME) expected="${home}/.config" ;;
            XDG_DATA_HOME) expected="${home}/.local/share" ;;
            XDG_RUNTIME_DIR) expected="/run/user/${uid}" ;;
        esac
        actual=$(printf '%s\n' "$environment" | sed -n "s/^${name}=//p" | tail -n 1)
        if [ -n "$actual" ] && [ "$actual" != "$expected" ]; then
            problems+=("${name}=${actual}, where JIM needs ${expected} or nothing")
        fi
    done
    [ "${#problems[@]}" -eq 0 ] && return 0

    error "The systemd manager of ${PODMAN_ACCOUNT}, which runs JIM, has folder settings meant for another account:"
    local problem
    for problem in "${problems[@]}"; do
        error "  ${problem}"
    done
    error "They are usually set for every account in /etc/environment, /etc/environment.d/ or"
    error "/etc/security/pam_env.conf, or for ${PODMAN_ACCOUNT} alone in ${home}/.config/environment.d/. Remove them,"
    error "or limit them to the accounts they are meant for, then run: systemctl restart user@${uid}.service"
    fatal "Then run this again. Or run JIM as root, which they do not affect: leave out --rootless."
}

# Runs a command as the account that runs JIM: that account's Podman sees JIM's images, secrets and containers.
as_jim_account() {
    if [ -z "$PODMAN_ACCOUNT" ] || [ "$(id -un)" = "$PODMAN_ACCOUNT" ]; then
        "$@"
        return
    fi
    local uid
    uid=$(id -u "$PODMAN_ACCOUNT")
    # With a clean environment: the caller's own XDG_CONFIG_HOME, TMPDIR and the like would point Podman at
    # folders the account cannot read. Proxy settings are kept, for a connected install behind a proxy.
    local keep=() var
    for var in LANG http_proxy https_proxy no_proxy HTTP_PROXY HTTPS_PROXY NO_PROXY; do
        if [ -n "${!var:-}" ]; then
            keep+=("${var}=${!var}")
        fi
    done
    # From the root folder, which the account can always read, rather than wherever this script was started.
    (cd / && runuser -u "$PODMAN_ACCOUNT" -- env -i PATH="$PATH" HOME="$(account_home)" USER="$PODMAN_ACCOUNT" \
        LOGNAME="$PODMAN_ACCOUNT" XDG_RUNTIME_DIR="/run/user/${uid}" DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/${uid}/bus" \
        ${keep[@]+"${keep[@]}"} "$@")
}

# systemctl for the systemd manager that runs JIM: the system's when rootful, the account's when rootless.
jim_systemctl() {
    if [ -z "$PODMAN_ACCOUNT" ]; then
        systemctl "$@"
    else
        as_jim_account systemctl --user "$@"
    fi
}

# The same command, as an administrator types it. For the account that runs JIM, it uses the account's own bus, as
# jim_systemctl does: systemctl --user -M jim@ fails on a minimal RHEL-family host, which lacks systemd-container.
jim_systemctl_command() {
    if [ -z "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -eq 0 ]; then
        printf 'sudo systemctl'
    elif [ -z "$PODMAN_ACCOUNT" ]; then
        printf 'systemctl'
    elif [ "$(id -u)" -eq 0 ]; then
        local uid
        uid=$(id -u "$PODMAN_ACCOUNT")
        printf 'sudo -u %s XDG_RUNTIME_DIR=/run/user/%s DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/%s/bus systemctl --user' \
            "$PODMAN_ACCOUNT" "$uid" "$uid"
    else
        printf 'systemctl --user'
    fi
}

# A podman command as an administrator types it to see JIM's containers. For the account that runs JIM, it starts
# from the root folder: rootless Podman fails in a folder the account cannot read, such as root's home.
jim_podman_command() {
    if [ -n "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -eq 0 ]; then
        printf '(cd / && sudo -u %s XDG_RUNTIME_DIR=/run/user/%s podman %s)' "$PODMAN_ACCOUNT" "$(id -u "$PODMAN_ACCOUNT")" "$*"
    elif [ "$(id -u)" -eq 0 ]; then
        printf 'sudo podman %s' "$*"
    else
        printf 'podman %s' "$*"
    fi
}

podman_unit_dir() {
    if [ -z "$PODMAN_ACCOUNT" ]; then
        printf '/etc/containers/systemd'
    else
        printf '%s/.config/containers/systemd' "$(account_home)"
    fi
}

# Downloads the latest release's Podman files to a folder of their own.
download_podman_files() {
    local target="$1"
    local name
    info "Downloading JIM's Podman files..."
    for name in "${PODMAN_FILES[@]}" "${QUADLET_FILES[@]}"; do
        curl -fsSL -o "${target}/${name}" "${RELEASE_DOWNLOAD_BASE}/${name}" \
            || fatal "Failed to download ${name}. The latest release may predate JIM's Podman support; see ${DOCS_BASE}/administration/deployment/"
    done
    success "Downloaded the Podman files"
}

# Puts the pod files and the settings file in the installation.
install_podman_files() {
    local install_dir="$1"
    mkdir -p "$install_dir"
    chmod 755 "$install_dir"
    cp "${PODMAN_SOURCE}/jim.yaml" "${PODMAN_SOURCE}/jim-database.yaml" "${PODMAN_SOURCE}/jim-config.yaml" "$install_dir/"
    # Readable by the account that runs JIM. None holds a secret.
    chmod 644 "${install_dir}/jim.yaml" "${install_dir}/jim-database.yaml" "${install_dir}/jim-config.yaml"
    success "Copied the pod files and jim-config.yaml"
}

# The images a pod file runs.
pod_images() {
    local pod_file="$1"
    awk '$1 == "image:" { print $2 }' "$pod_file"
}

podman_images_needed() {
    local install_dir="$1"
    pod_images "${install_dir}/jim.yaml"
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        pod_images "${install_dir}/jim-database.yaml"
    fi
}

# Downloads the images before starting JIM, so that the first start is not a long download under systemd's
# start-up time limit, and a failed download shows here.
pull_podman_images() {
    local install_dir="$1"
    local image
    info "Downloading JIM's images (this takes a few minutes)..."
    for image in $(podman_images_needed "$install_dir"); do
        as_jim_account podman pull -q "$image" >/dev/null || fatal "Failed to download ${image}"
    done
    success "Downloaded the images"
}

verify_podman_images() {
    local install_dir="$1"
    local image
    for image in $(podman_images_needed "$install_dir"); do
        as_jim_account podman image exists "$image" \
            || fatal "The image ${image} is not available after loading the bundle, and this installation cannot download it."
    done
}

# Stores the secrets in the Podman secret store of the account that runs JIM, from a Kubernetes Secret document
# passed on standard input, so that no secret is written to a file or shown in the process list.
store_podman_secrets() {
    {
        printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: jim-secrets\nstringData:\n'
        printf '  JIM_DB_PASSWORD: %s\n' "$(yaml_quote "$JIM_DB_PASSWORD")"
        printf '  JIM_SSO_SECRET: %s\n' "$(yaml_quote "$JIM_SSO_SECRET")"
        if [ -n "${JIM_INFRASTRUCTURE_API_KEY:-}" ]; then
            printf '  JIM_INFRASTRUCTURE_API_KEY: %s\n' "$(yaml_quote "$JIM_INFRASTRUCTURE_API_KEY")"
        fi
    } | as_jim_account podman kube play --replace - >/dev/null || fatal "Failed to store JIM's secrets in Podman"
    success "Stored the database password and client secret as the Podman secret jim-secrets"
}

# Stores JIM's certificate and key as the Podman secret jim-tls, which the pod mounts for jim.web.
store_tls_secret() {
    local install_dir="$1"
    local tls_dir="${install_dir}/tls"
    printf 'apiVersion: v1\nkind: Secret\nmetadata:\n  name: jim-tls\ndata:\n  tls.crt: %s\n  tls.key: %s\n' \
        "$(base64 -w0 "${tls_dir}/tls.crt")" "$(base64 -w0 "${tls_dir}/tls.key")" \
        | as_jim_account podman kube play --replace - >/dev/null || fatal "Failed to store JIM's certificate in Podman"
}

# Copies a Quadlet unit, pointing it at this installation's files and the chosen port.
render_unit() {
    local unit="$1"
    local absolute_dir="$2"
    local line
    while IFS= read -r line || [ -n "$line" ]; do
        case "$line" in
            Yaml=/opt/jim/*) printf 'Yaml=%s/%s\n' "$absolute_dir" "${line#Yaml=/opt/jim/}" ;;
            ConfigMap=/opt/jim/*) printf 'ConfigMap=%s/%s\n' "$absolute_dir" "${line#ConfigMap=/opt/jim/}" ;;
            PublishPort=*) printf 'PublishPort=%s:8443\n' "$JIM_WEB_PORT" ;;
            *) printf '%s\n' "$line" ;;
        esac
    done < "$unit"
}

install_podman_units() {
    local install_dir="$1"
    local absolute_dir
    absolute_dir=$(cd "$install_dir" && pwd)
    local unit_source="$PODMAN_SOURCE"
    [ -d "${PODMAN_SOURCE}/quadlet" ] && unit_source="${PODMAN_SOURCE}/quadlet"
    local unit_dir
    unit_dir=$(podman_unit_dir)

    if [ -n "$PODMAN_ACCOUNT" ]; then
        as_jim_account mkdir -p "$unit_dir" || fatal "Failed to create ${unit_dir}"
    else
        mkdir -p "$unit_dir"
    fi

    local unit
    for unit in jim.network jim.kube jim-database.kube; do
        if [ "$unit" = "jim-database.kube" ] && [ "$USE_BUNDLED_DB" != "true" ]; then
            rm -f "${unit_dir}/${unit}"
            continue
        fi
        render_unit "${unit_source}/${unit}" "$absolute_dir" > "${unit_dir}/${unit}"
        chmod 644 "${unit_dir}/${unit}"
        if [ -n "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -eq 0 ]; then
            chown "${PODMAN_ACCOUNT}:" "${unit_dir}/${unit}"
        fi
    done
    success "Installed the systemd units in ${unit_dir}"
}

# Records how this installation runs, for the options that look after it later.
write_install_state() {
    local install_dir="$1"
    {
        echo "# Written by setup.sh, whose maintenance options read it. Rerun setup.sh rather than edit it."
        echo "JIM_RUNTIME=podman"
        echo "JIM_PODMAN_ACCOUNT=${PODMAN_ACCOUNT}"
        echo "JIM_PODMAN_SYSTEMD=${PODMAN_SYSTEMD}"
        echo "JIM_WEB_PORT=${JIM_WEB_PORT}"
    } > "${install_dir}/install.conf"
    chmod 644 "${install_dir}/install.conf"
}

# The port a rootless container may publish from: the kernel's unprivileged-port threshold.
unprivileged_port_start() {
    sysctl -n net.ipv4.ip_unprivileged_port_start 2>/dev/null || echo 1024
}

# Running rootless, JIM cannot publish a port below the unprivileged-port threshold. Run as root, the installer
# lowers the threshold to the chosen port; Red Hat's own rootless installations do the same.
allow_unprivileged_port() {
    local port="$1"
    [ -n "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -eq 0 ] || return 0
    [ "$port" -lt "$(unprivileged_port_start)" ] || return 0

    { mkdir -p /etc/sysctl.d && echo "net.ipv4.ip_unprivileged_port_start=${port}" > /etc/sysctl.d/90-jim.conf; } \
        || fatal "Failed to write /etc/sysctl.d/90-jim.conf"
    sysctl -q -w "net.ipv4.ip_unprivileged_port_start=${port}" >/dev/null \
        || fatal "Failed to allow rootless containers to use port ${port}"
    info "Allowed unprivileged programs to use ports from ${port} up, so that JIM can publish port ${port} rootless (/etc/sysctl.d/90-jim.conf)"
}

# Offers to open the HTTPS port where firewalld is running, which blocks it by default on RHEL.
configure_firewall() {
    command -v firewall-cmd >/dev/null 2>&1 || return 0
    [ "$(firewall-cmd --state 2>/dev/null)" = "running" ] || return 0
    # Bound to one address, such as 127.0.0.1 behind a proxy on this host, JIM is not for other machines.
    [[ "$JIM_WEB_PORT" == *:* ]] && return 0

    local port="$JIM_WEB_PORT"
    if firewall-cmd --query-port="${port}/tcp" >/dev/null 2>&1 \
        || { [ "$port" = "443" ] && firewall-cmd --query-service=https >/dev/null 2>&1; }; then
        success "firewalld already allows port ${port}"
        return
    fi

    local open="${JIM_SETUP_OPEN_FIREWALL:-}"
    if [ -z "$open" ]; then
        echo
        if prompt_yn "firewalld is running. Allow HTTPS to JIM on port ${port}?" "y"; then
            open="true"
        else
            open="false"
        fi
    fi

    local command="firewall-cmd --permanent --add-port=${port}/tcp && firewall-cmd --reload"
    if [ "$open" != "true" ]; then
        warn "firewalld blocks port ${port}, so other machines cannot reach JIM until you allow it: ${command}"
    elif [ "$(id -u)" -ne 0 ]; then
        warn "Opening the firewall needs root. Ask your administrators to run: ${command}"
    else
        { firewall-cmd --permanent --add-port="${port}/tcp" && firewall-cmd --reload; } >/dev/null \
            || fatal "Failed to open port ${port} in firewalld"
        success "Allowed port ${port} in firewalld"
    fi
}

# Ubuntu 24.04 gives crun and podman AppArmor profiles of their own. A container that sets no-new-privileges, as
# JIM's do, cannot leave them for its own profile, so AppArmor stacks the two: a rootful container's processes run
# under Podman's profile for containers stacked with crun's, such as containers-default-0.57.4-apparmor1//&crun.
# The stack breaks two things, each fixed in the place AppArmor provides for site additions; the containers keep
# their own profile. Rootless containers, which Podman confines with no profile, and hosts without these profiles or
# already with both rules, are unaffected.
#
# - The network: the stack allows none, so JIM could reach neither its database nor its identity provider. A network
#   rule in the local overrides of crun's and podman's profiles restores it.
# - Signals: Ubuntu's base abstraction, which every profile includes, lets a profile signal itself, and the stacked
#   label is not itself, so a process cannot signal its own threads or the other processes in its container. The
#   .NET runtime then aborts, and JIM's services crash and restart over and over (#1953); PostgreSQL cannot signal
#   its own processes either. A rule in the base abstraction's folder for site additions lets a profile signal itself
#   stacked with crun's or podman's too, and allows nothing between different profiles.
apparmor_enabled() {
    [ "$(cat /sys/module/apparmor/parameters/enabled 2>/dev/null)" = "Y" ]
}

# Whether this host stacks a rootful JIM's containers' profiles with the profiles of Podman's runtimes.
apparmor_stacks_podman() {
    [ -z "$PODMAN_ACCOUNT" ] || return 1
    apparmor_enabled || return 1
    [ -f "${APPARMOR_DIR}/crun" ] || [ -f "${APPARMOR_DIR}/podman" ]
}

apparmor_blocks_network() {
    local profile
    for profile in crun podman; do
        if [ -f "${APPARMOR_DIR}/${profile}" ] \
            && ! grep -qsE '^[[:space:]]*network[[:space:],]' "${APPARMOR_DIR}/${profile}" "${APPARMOR_DIR}/local/${profile}"; then
            return 0
        fi
    done
    return 1
}

# A signal rule for the stacked label in any file of the base abstraction's folder counts, so one added by hand does.
apparmor_blocks_signals() {
    ! grep -qsE '^[[:space:]]*signal[[:space:]].*peer=@\{profile_name\}//&' "${APPARMOR_DIR}/abstractions/base.d/"*
}

# Whether a process runs under the loaded AppArmor profile, alone or stacked with another.
apparmor_profile_in_use() {
    { cat /proc/[0-9]*/attr/apparmor/current /proc/[0-9]*/attr/current 2>/dev/null || true; } | grep -qF -e "$1"
}

configure_apparmor() {
    apparmor_stacks_podman || return 0
    local network="" signals=""
    if apparmor_blocks_network; then
        network="true"
    fi
    if apparmor_blocks_signals; then
        signals="true"
    fi
    [ -n "${network}${signals}" ] || return 0

    local fix="${JIM_SETUP_FIX_APPARMOR:-}"
    if [ -z "$fix" ]; then
        echo
        info "This host's AppArmor profiles for Podman stop JIM's containers working:"
        if [ -n "$network" ]; then
            echo "  - They block the network. A network rule in their local overrides, ${APPARMOR_DIR}/local/crun and podman, fixes it."
        fi
        if [ -n "$signals" ]; then
            echo "  - They stop a container's processes signalling one another, so JIM's services crash and restart. A rule"
            echo "    letting each profile signal itself under Podman's, in ${APPARMOR_DIR}/${APPARMOR_SIGNAL_RULES}, fixes it."
        fi
        if prompt_yn "Add the rules?" "y"; then
            fix="true"
        else
            fix="false"
        fi
    fi
    if [ "$fix" != "true" ]; then
        local -a missing=()
        if [ -n "$network" ]; then
            missing+=("a network rule, without which JIM cannot reach its database or identity provider (a line reading network, in ${APPARMOR_DIR}/local/crun and ${APPARMOR_DIR}/local/podman, reloaded with apparmor_parser -r)")
        fi
        if [ -n "$signals" ]; then
            missing+=("a signal rule, without which JIM's services crash and restart (a line reading signal peer=@{profile_name}//&{crun,podman}, in ${APPARMOR_DIR}/${APPARMOR_SIGNAL_RULES})")
        fi
        local joined
        printf -v joined '%s; and ' "${missing[@]}"
        fatal "JIM needs ${joined%; and }. Add them as ${DOCS_BASE}/administration/podman/#firewall-selinux-and-apparmor shows, or install JIM rootless (--rootless)."
    fi

    if [ -n "$network" ]; then
        local profile
        for profile in crun podman; do
            [ -f "${APPARMOR_DIR}/${profile}" ] || continue
            mkdir -p "${APPARMOR_DIR}/local"
            if ! grep -qsE '^[[:space:]]*network[[:space:],]' "${APPARMOR_DIR}/local/${profile}"; then
                echo "network," >> "${APPARMOR_DIR}/local/${profile}" \
                    || fatal "Failed to write ${APPARMOR_DIR}/local/${profile}"
            fi
            apparmor_parser -r "${APPARMOR_DIR}/${profile}" \
                || fatal "Failed to reload the AppArmor profile ${APPARMOR_DIR}/${profile}"
        done
        success "Allowed JIM's containers to use the network under the AppArmor profiles for Podman (${APPARMOR_DIR}/local/crun and podman)"
    fi
    if [ -n "$signals" ]; then
        allow_stacked_signals
    fi
}

# Adds the signal rule, and has Podman load its profile for containers afresh, with the rule.
allow_stacked_signals() {
    local file="${APPARMOR_DIR}/${APPARMOR_SIGNAL_RULES}"
    mkdir -p "$(dirname "$file")" || fatal "Failed to create $(dirname "$file")"
    cat > "$file" <<EOF || fatal "Failed to write ${file}"
# Written by JIM's installer; see ${DOCS_BASE}/administration/podman/#firewall-selinux-and-apparmor
#
# The base abstraction lets a profile signal itself. A rootful Podman container that sets no-new-privileges runs
# under its profile stacked with crun's, which is not itself, so its processes could not signal one another or their
# own threads. This lets a profile signal itself stacked with crun's or podman's too, and allows nothing more.
  signal peer=@{profile_name}//&{crun,podman},
EOF

    # Every profile on the host includes the base abstraction, so a rule AppArmor could not compile would stop each of
    # them loading.
    local error
    if ! error=$(printf 'include <tunables/global>\nprofile jim-check {\n  include <abstractions/base>\n}\n' \
            | apparmor_parser -QK 2>&1 >/dev/null); then
        rm -f "$file"
        fatal "AppArmor cannot compile the signal rule in ${file}, so the installer took it back out: ${error}"
    fi

    # Podman loads its profile for containers, which includes the base abstraction, when a container first needs it,
    # and keeps it loaded until the server restarts. Unloaded, it is loaded afresh, with the rule, by the next container
    # to start. Unloading a profile a process runs under would leave that process unconfined, so one in use stays.
    local name in_use=""
    while read -r name _; do
        [[ "$name" == containers-default* ]] || continue
        if apparmor_profile_in_use "$name" || ! printf '%s' "$name" > "${APPARMOR_FS}/.remove"; then
            in_use="true"
        fi
    done < <(cat "${APPARMOR_FS}/profiles" 2>/dev/null)
    success "Allowed the processes in each of JIM's containers to signal one another under the AppArmor profiles for Podman (${file})"
    if [ -n "$in_use" ]; then
        warn "Containers running now use Podman's AppArmor profile without the rule, so it applies to JIM once this server restarts. Restart the server after installing; until then JIM's services may crash and restart."
    fi
}

# Starts JIM's pods, under systemd where it can.
start_podman() {
    local install_dir="$1"
    local absolute_dir
    absolute_dir=$(cd "$install_dir" && pwd)

    if [ "$PODMAN_SYSTEMD" = "true" ]; then
        jim_systemctl daemon-reload || fatal "Failed to reload systemd"
        if [ "$USE_BUNDLED_DB" = "true" ]; then
            jim_systemctl restart jim-database.service \
                || fatal "Failed to start the database. See: $(jim_systemctl_command) status jim-database.service"
        fi
        jim_systemctl restart jim.service || fatal "Failed to start JIM. See: $(jim_systemctl_command) status jim.service"
        return
    fi

    as_jim_account podman network create --ignore jim >/dev/null || fatal "Failed to create the network jim"
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        as_jim_account podman kube play --replace --network jim --configmap "${absolute_dir}/jim-config.yaml" \
            "${absolute_dir}/jim-database.yaml" >/dev/null || fatal "Failed to start the database"
    fi
    as_jim_account podman kube play --replace --network jim --configmap "${absolute_dir}/jim-config.yaml" \
        --publish "${JIM_WEB_PORT}:8443" "${absolute_dir}/jim.yaml" >/dev/null || fatal "Failed to start JIM"
}

# The commands that start JIM, for an administrator who chose not to start it yet.
podman_start_commands() {
    local install_dir="$1"
    local absolute_dir
    absolute_dir=$(cd "$install_dir" && pwd)
    if [ "$PODMAN_SYSTEMD" = "true" ]; then
        if [ "$USE_BUNDLED_DB" = "true" ]; then
            echo "  $(jim_systemctl_command) start jim-database.service"
        fi
        echo "  $(jim_systemctl_command) start jim.service"
    else
        echo "  $(jim_podman_command network create --ignore jim)"
        if [ "$USE_BUNDLED_DB" = "true" ]; then
            echo "  $(jim_podman_command kube play --replace --network jim --configmap "${absolute_dir}/jim-config.yaml" "${absolute_dir}/jim-database.yaml")"
        fi
        echo "  $(jim_podman_command kube play --replace --network jim --configmap "${absolute_dir}/jim-config.yaml" --publish "${JIM_WEB_PORT}:8443" "${absolute_dir}/jim.yaml")"
    fi
}

# --- Size the bundled database ---
# The bundled PostgreSQL's memory settings, which it reads from the settings file. JIM_DB_SHM_SIZE is the database
# container's /dev/shm on Docker; Podman cannot size a pod's, and its database pod does without (jim-database.yaml).
DATABASE_MEMORY_SETTINGS=(JIM_DB_SHARED_BUFFERS JIM_DB_EFFECTIVE_CACHE_SIZE JIM_DB_MAINTENANCE_WORK_MEM JIM_DB_WORK_MEM JIM_DB_SHM_SIZE)

# This host's memory in MB, as the kernel reports it; empty when it cannot be read.
host_memory_mb() {
    awk '/^MemTotal:/ { printf "%d", $2 / 1024 }' /proc/meminfo 2>/dev/null || true
}

# A PostgreSQL memory setting in MB, rounded down; empty when it is not a size the installer can read. As PostgreSQL
# reads them, the units are case-sensitive, and a number without one counts in the setting's own unit, given in bytes
# (8192 for shared_buffers, whose unit is the 8 kB page).
postgres_size_mb() {
    local value="$1" unit="$2" bytes
    [[ "$value" =~ ^([0-9]+(\.[0-9]+)?)(B|kB|MB|GB|TB)?$ ]] || return 0
    case "${BASH_REMATCH[3]}" in
        B) bytes=1 ;;
        kB) bytes=1024 ;;
        MB) bytes=1048576 ;;
        GB) bytes=1073741824 ;;
        TB) bytes=1099511627776 ;;
        *) bytes="$unit" ;;
    esac
    awk -v n="${BASH_REMATCH[1]}" -v b="$bytes" 'BEGIN { printf "%d", n * b / 1048576 }'
}

# Whether the installation's settings file sets a setting, other than commented out or empty.
config_sets() {
    local key="$1"
    if [ "$RUNTIME" = "podman" ]; then
        # A value, quoted as update_config_yaml writes it, or bare.
        grep -Eq "^  ${key}: *(\"[^\"]|[^ \"#])" "$CONFIG_FILE" 2>/dev/null
    else
        grep -q "^${key}=." "$CONFIG_FILE" 2>/dev/null
    fi
}

# Sizes the bundled PostgreSQL to this host's memory, and writes its settings to the settings file. Until #1943 the
# compose and pod files fixed them for a 64 GB host, so the database could not start on one with less than about
# 10 GB. A setting given in the environment is used as given, for automation; with "keep", as an upgrade passes,
# a setting the installation already has is left alone. The Configuration Reference documents the rules:
#   shared_buffers        a quarter of memory, at most 8 GB: JIM's own services share the host, and the largest
#                         deployments JIM has been validated on ran with 8 GB
#   effective_cache_size  half of memory: the page cache PostgreSQL can expect, beside JIM's services
#   maintenance_work_mem  a sixteenth of memory, at most 2 GB
#   work_mem              memory less shared_buffers, over three for each of the 200 connections allowed, and at
#                         least PostgreSQL's own default, 4 MB
#   shm_size (Docker)     shared_buffers and a quarter more
size_database() {
    local mode="${1:-}"
    local memory
    memory=$(host_memory_mb)
    if ! [[ "$memory" =~ ^[0-9]+$ ]] || [ "$memory" -eq 0 ]; then
        warn "Could not read this host's memory, so the bundled PostgreSQL keeps its default settings, which suit a 4 GB host. To size it, see ${DOCS_BASE}/administration/configuration/#bundled-postgresql-memory"
        return
    fi

    local shared=$((memory / 4))
    [ "$shared" -le 8192 ] || shared=8192
    local maintenance=$((memory / 16))
    [ "$maintenance" -le 2048 ] || maintenance=2048
    local work=$(((memory - shared) / 600))
    [ "$work" -ge 4 ] || work=4
    local -A sized=(
        [JIM_DB_SHARED_BUFFERS]="${shared}MB"
        [JIM_DB_EFFECTIVE_CACHE_SIZE]="$((memory / 2))MB"
        [JIM_DB_MAINTENANCE_WORK_MEM]="${maintenance}MB"
        [JIM_DB_WORK_MEM]="${work}MB"
        [JIM_DB_SHM_SIZE]="$((shared + shared / 4))mb"
    )

    local key
    local -a keys=()
    for key in "${DATABASE_MEMORY_SETTINGS[@]}"; do
        if [ "$RUNTIME" = "podman" ] && [ "$key" = "JIM_DB_SHM_SIZE" ]; then
            continue
        fi
        if [ "$mode" = "keep" ] && config_sets "$key"; then
            continue
        fi
        keys+=("$key")
    done
    [ ${#keys[@]} -gt 0 ] || return 0

    # A shared_buffers given in the environment is checked before anything is written: PostgreSQL allocates it in full
    # when it starts, so one the host cannot give stops the database, which would otherwise surface only at the end of
    # the wait for JIM to be ready (#1948).
    local gb caution=""
    gb=$(awk -v m="$memory" 'BEGIN { printf "%.1f", m / 1024 }')
    if [ -n "${JIM_DB_SHARED_BUFFERS:-}" ] && [[ " ${keys[*]} " == *" JIM_DB_SHARED_BUFFERS "* ]]; then
        local given advice="Leave JIM_DB_SHARED_BUFFERS unset for the installer to choose, which on this host is ${shared}MB; see ${DOCS_BASE}/administration/configuration/#bundled-postgresql-memory"
        given=$(postgres_size_mb "$JIM_DB_SHARED_BUFFERS" 8192)
        if [ -z "$given" ]; then
            caution="JIM_DB_SHARED_BUFFERS is ${JIM_DB_SHARED_BUFFERS}, which is not a size the installer can read, so it could not check that this host can give it. PostgreSQL refuses a size it cannot read too: its units are kB, MB, GB and TB, and the case matters. ${advice}"
        elif [ "$given" -ge "$memory" ]; then
            fatal "JIM_DB_SHARED_BUFFERS is ${JIM_DB_SHARED_BUFFERS}, which this host, with ${gb} GB of memory, cannot give: PostgreSQL allocates shared_buffers in full when it starts, so the database would not start. ${advice}"
        elif [ "$given" -gt $((memory / 2)) ]; then
            caution="JIM_DB_SHARED_BUFFERS is ${JIM_DB_SHARED_BUFFERS}, more than half this host's ${gb} GB of memory, which leaves little for JIM's services and PostgreSQL's other memory, so they may run out of it under load. ${advice}"
        fi
    fi

    local value name label
    local -a chosen=()
    for key in "${keys[@]}"; do
        value="${!key:-${sized[$key]}}"
        label=""
        [ -z "${!key:-}" ] || label=" (given)"
        set_setting "$key" "$value"
        name="${key#JIM_DB_}"
        chosen+=("${name,,} ${value}${label}")
    done
    local summary
    printf -v summary '%s, ' "${chosen[@]}"
    success "Sized the bundled PostgreSQL for this host's ${gb} GB of memory: ${summary%, }"
    [ -z "$caution" ] || warn "$caution"
}

# --- Configure database ---
configure_database() {
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

        # A reinstall keeps the password: the database was created with it, and a new one would lock JIM out.
        if [ -z "${JIM_DB_PASSWORD:-}" ] && [ -n "$EXISTING_DB_PASSWORD" ]; then
            JIM_DB_PASSWORD="$EXISTING_DB_PASSWORD"
            success "Kept the existing database password"
        elif [ -z "${JIM_DB_PASSWORD:-}" ] && bundled_database_exists; then
            fatal "The bundled database already exists, with a password this script cannot read. Run it again with JIM_DB_PASSWORD set to that password, or remove the volume jim-db-volume to start afresh, which deletes JIM's data."
        fi

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

    set_setting "JIM_DB_NAME" "$JIM_DB_NAME"
    set_setting "JIM_DB_USERNAME" "$JIM_DB_USERNAME"
    set_setting "JIM_DB_PASSWORD" "$JIM_DB_PASSWORD"

    # Always written: the Docker template's value, localhost, suits running JIM outside containers in development,
    # and would point every JIM container at itself rather than at the bundled database. The bundled database is
    # the container jim.database on Docker, and the pod jim-database on Podman.
    if [ "$db_mode" = "external" ]; then
        set_setting "JIM_DB_HOSTNAME" "$JIM_DB_HOSTNAME"
    elif [ "$RUNTIME" = "podman" ]; then
        set_setting "JIM_DB_HOSTNAME" "jim-database"
    else
        set_setting "JIM_DB_HOSTNAME" "jim.database"
    fi

    if [ "$db_mode" = "bundled" ]; then
        size_database
    fi
}

# The bundled database's password in an existing installation, read before the reinstall replaces its settings.
existing_database_password() {
    local install_dir="$1"
    local password=""
    if [ "$RUNTIME" = "podman" ]; then
        [ -f "${install_dir}/jim-config.yaml" ] || return 0
        # The secret holds the Kubernetes Secret document it was stored from, as Podman writes it back.
        password=$(as_jim_account podman secret inspect --showsecret --format '{{.SecretData}}' jim-secrets 2>/dev/null \
            | sed -n 's/^  JIM_DB_PASSWORD: //p' | head -1) || true
    elif [ -f "${install_dir}/.env" ]; then
        password=$(env_value JIM_DB_PASSWORD "${install_dir}/.env")
    fi
    # Stored quoted when it contains characters YAML treats specially.
    password="${password#\"}"
    password="${password%\"}"
    password="${password#\'}"
    password="${password%\'}"
    printf '%s' "$password"
}

bundled_database_exists() {
    if [ "$RUNTIME" = "podman" ]; then
        as_jim_account podman volume exists jim-db-volume 2>/dev/null
    else
        docker volume inspect jim-db-volume >/dev/null 2>&1
    fi
}

# --- Configure SSO ---
configure_sso() {
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

    set_setting "JIM_SSO_AUTHORITY" "$JIM_SSO_AUTHORITY"
    set_setting "JIM_SSO_CLIENT_ID" "$JIM_SSO_CLIENT_ID"
    set_setting "JIM_SSO_SECRET" "$JIM_SSO_SECRET"
    set_setting "JIM_SSO_API_SCOPE" "$JIM_SSO_API_SCOPE"
    set_setting "JIM_SSO_CLAIM_TYPE" "$JIM_SSO_CLAIM_TYPE"
    set_setting "JIM_SSO_MV_ATTRIBUTE" "$JIM_SSO_MV_ATTRIBUTE"
    set_setting "JIM_SSO_INITIAL_ADMIN" "$JIM_SSO_INITIAL_ADMIN"
}

# --- Configure Docker registry ---
# Docker only: on Podman, the pod file names JIM's images with their registry and version.
configure_registry() {
    set_setting "DOCKER_REGISTRY" "ghcr.io/tetronio/"
    set_setting "JIM_VERSION" "$JIM_RELEASE_VERSION"
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

# Makes the key belong to JIM's user. Docker only: Podman hands the key to JIM from its secret store instead
# (store_tls_secret), so the file stays readable by its owner alone.
set_tls_key_owner() {
    local install_dir="$1"
    local key="$2"

    [ "$RUNTIME" = "podman" ] && return

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

# --- Configure the HTTPS port ---

# Whether something on this host other than JIM already listens on the port.
port_in_use() {
    local port="$1"
    if command -v ss >/dev/null 2>&1; then
        [ -n "$(ss -Hltn "sport = :${port}" 2>/dev/null)" ] || return 1
    else
        # Without ss, try connecting: whatever accepts on the loopback address holds the port.
        timeout 2 bash -c "exec 3<>/dev/tcp/127.0.0.1/${port}" 2>/dev/null || return 1
    fi
    # A reinstall finds JIM itself on the port, which is no conflict.
    ! jim_holds_port "$port"
}

jim_holds_port() {
    local port="$1"
    if [ "$RUNTIME" = "podman" ]; then
        # Before the account that runs JIM exists, JIM cannot be running. The pod holds the port whether or not
        # jim.web is running.
        [ -z "$PODMAN_ACCOUNT" ] || id "$PODMAN_ACCOUNT" >/dev/null 2>&1 || return 1
        as_jim_account podman pod inspect jim --format '{{json .InfraConfig.PortBindings}}' 2>/dev/null \
            | grep -q "\"HostPort\":\"${port}\""
    else
        docker port jim.web 8443/tcp 2>/dev/null | grep -q ":${port}$"
    fi
}

# Why the port cannot be used, or nothing if it can.
port_problem() {
    local port="$1"
    if ! [[ "$port" =~ ^[0-9]+$ ]] || [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
        printf '%s is not a port number' "$port"
    elif port_in_use "$port"; then
        printf 'Port %s is already in use on this host' "$port"
    elif [ "$RUNTIME" = "podman" ]; then
        # Rootless Podman cannot publish a port below the kernel's unprivileged-port threshold. Run as root, the
        # installer lowers the threshold (allow_unprivileged_port); run as the account itself, it cannot.
        local threshold
        threshold=$(unprivileged_port_start)
        if [ -n "$PODMAN_ACCOUNT" ] && [ "$(id -u)" -ne 0 ] && [ "$port" -lt "$threshold" ]; then
            printf 'Podman runs rootless here, so it cannot publish port %s. Choose a port of %s or above, or have root run: echo net.ipv4.ip_unprivileged_port_start=%s > /etc/sysctl.d/90-jim.conf && sysctl --system' "$port" "$threshold" "$port"
        fi
    elif [ "$port" -lt 1024 ] && docker info --format '{{join .SecurityOptions " "}}' 2>/dev/null | grep -q rootless; then
        # Rootless Docker cannot publish a port below the kernel's unprivileged-port threshold.
        local threshold
        threshold=$(unprivileged_port_start)
        if [ "$port" -lt "$threshold" ]; then
            printf 'Docker runs rootless here, so it cannot publish port %s. Choose a port of %s or above, or have root run: echo net.ipv4.ip_unprivileged_port_start=%s > /etc/sysctl.d/90-jim.conf && sysctl --system' "$port" "$threshold" "$port"
        fi
    fi
}

configure_port() {
    local from_environment="${JIM_WEB_PORT:+true}"

    echo
    info "HTTPS port"
    echo "${DIM}  ${DEFAULT_WEB_PORT} is the standard HTTPS port, so users and your identity provider need no port in JIM's address.${RESET}"
    echo

    local problem
    while true; do
        prompt_value "JIM_WEB_PORT" "  HTTPS port" "$DEFAULT_WEB_PORT"
        # JIM_WEB_PORT set in the environment may carry an address prefix, such as 127.0.0.1:443.
        problem=$(port_problem "${JIM_WEB_PORT##*:}")
        [ -z "$problem" ] && break
        [ "$from_environment" = "true" ] && fatal "$problem"
        warn "$problem"
        JIM_WEB_PORT=""
    done

    set_setting "JIM_WEB_PORT" "$JIM_WEB_PORT"
    if [ "$RUNTIME" = "podman" ]; then
        allow_unprivileged_port "${JIM_WEB_PORT##*:}"
    fi
    success "JIM will serve HTTPS on port ${JIM_WEB_PORT##*:}"
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
            if [ -f "${tls_dir}/ca.key" ]; then
                REPLACED_CA="true"
            fi
            create_certificate_authority "$tls_dir"
            NEW_CA="true"
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
        set_setting "JIM_TRUSTED_PROXIES" "$JIM_TRUSTED_PROXIES"
        success "Trusting the proxy at ${JIM_TRUSTED_PROXIES}"
    fi
}

# --- Renew the server certificate ---
renew_certificate() {
    local install_dir
    install_dir=$(resolve_install_dir)
    local tls_dir="${install_dir}/tls"

    load_installation "$install_dir"

    if [ ! -f "${tls_dir}/ca.key" ] || [ ! -f "${tls_dir}/names" ]; then
        fatal "This installation uses your organisation's certificate, which this script cannot renew. Once your certificate authority has issued the renewed one, install it with: $(installer_command "$install_dir") --certificate"
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

    restart_web "$install_dir"
}

# Restarts jim.web so that it loads a new certificate; a JIM that is not running picks it up when it starts.
restart_web() {
    local install_dir="$1"

    if [ "$RUNTIME" = "podman" ]; then
        restart_podman_web "$install_dir"
        return
    fi

    if [ -z "$(docker ps -q --filter name=^jim.web$ 2>/dev/null)" ]; then
        info "jim.web is not running; it will use the new certificate when JIM starts"
        return
    fi
    info "Restarting jim.web to load it..."
    (cd "$install_dir" && docker compose "${COMPOSE_FILES[@]}" restart jim.web) \
        || fatal "Failed to restart jim.web. Restart it yourself: cd ${install_dir} && docker compose ${COMPOSE_FILES[*]} restart jim.web"
    success "jim.web restarted with the new certificate"
}

# Podman: stores the new certificate as the jim-tls secret, then restarts JIM's pod, which mounts the secret when
# it starts. The pod restarts as a whole, so the worker and scheduler restart too.
restart_podman_web() {
    local install_dir="$1"

    store_tls_secret "$install_dir"
    success "Stored the new certificate as the Podman secret jim-tls"

    if [ "$(as_jim_account podman pod exists jim >/dev/null 2>&1 && echo yes)" != "yes" ]; then
        info "JIM is not running; it will use the new certificate when it starts"
        return
    fi
    info "Restarting JIM to load it (its web, worker and scheduler services restart together)..."
    if [ "$PODMAN_SYSTEMD" = "true" ]; then
        jim_systemctl restart jim.service \
            || fatal "Failed to restart JIM. Restart it yourself: $(jim_systemctl_command) restart jim.service"
    else
        local absolute_dir
        absolute_dir=$(cd "$install_dir" && pwd)
        as_jim_account podman kube play --replace --network jim --configmap "${absolute_dir}/jim-config.yaml" \
            --publish "${JIM_WEB_PORT}:8443" "${absolute_dir}/jim.yaml" >/dev/null || fatal "Failed to restart JIM"
    fi
    success "JIM restarted with the new certificate"
}

# --- Create or install the certificate again ---
change_certificate() {
    local install_dir
    install_dir=$(resolve_install_dir)

    load_installation "$install_dir"
    command -v openssl >/dev/null 2>&1 || fatal "openssl is required but not installed."

    configure_certificate "$install_dir"
    restart_web "$install_dir"

    if [ "$REPLACED_CA" = "true" ]; then
        echo
        warn "This is a new certificate authority. Browsers trust JIM again only once ${install_dir}/tls/ca.crt replaces the old one in their trusted root certificate authorities."
    elif [ "$NEW_CA" = "true" ]; then
        echo
        info "Add ${install_dir}/tls/ca.crt to the trusted root certificate authorities of every machine whose browser or tools use JIM; until then, browsers warn about JIM's certificate."
    fi
}

# --- Launch JIM ---
launch_jim() {
    local install_dir="$1"
    local auto_start="${JIM_SETUP_AUTO_START:-}"

    echo

    if [ "$RUNTIME" = "podman" ]; then
        if [ "$auto_start" != "true" ] && ! prompt_yn "Start JIM now?"; then
            echo
            if [ "$PODMAN_SYSTEMD" = "true" ]; then
                info "JIM will start at the next boot. To start it sooner, run:"
            else
                info "To start JIM later, run:"
            fi
            podman_start_commands "$install_dir"
            return
        fi
        info "Starting JIM..."
        start_podman "$install_dir"
        wait_for_jim "$install_dir"
        return
    fi

    local compose_cmd="docker compose ${COMPOSE_FILES[*]}"
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        compose_cmd="${compose_cmd} --profile with-db"
    fi
    compose_cmd="${compose_cmd} up -d"
    # From a bundle every image is already loaded, so Compose must fail rather than try the internet.
    if [ -n "$BUNDLE_DIR" ]; then
        compose_cmd="${compose_cmd} --pull never"
    fi

    if [ "$auto_start" != "true" ] && ! prompt_yn "Start JIM now?"; then
        echo
        info "To start JIM later, run:"
        echo "  cd ${install_dir}"
        echo "  ${compose_cmd}"
        return
    fi

    info "Starting JIM..."
    (cd "$install_dir" && eval "$compose_cmd") || fatal "Failed to start JIM. See the messages above."
    wait_for_jim "$install_dir"
}

# Waits until jim.web reports healthy, which its health check does once JIM is ready to serve.
wait_for_jim() {
    local install_dir="$1"
    local deadline=$((SECONDS + READY_TIMEOUT_SECONDS))

    info "Waiting for JIM to be ready (the first start prepares the database, which takes a few minutes)..."
    while [ "$SECONDS" -lt "$deadline" ]; do
        if jim_is_healthy; then
            JIM_READY="true"
            success "JIM is ready"
            return
        fi
        sleep 5
    done
    JIM_READY="false"
    report_unready_containers "$install_dir"
}

# JIM's containers, the database's first: the others wait for it.
jim_containers() {
    if [ "$RUNTIME" = "podman" ]; then
        if [ "$USE_BUNDLED_DB" = "true" ]; then
            echo jim-database-postgres
        fi
        echo jim-worker jim-scheduler jim-web
    else
        if [ "$USE_BUNDLED_DB" = "true" ]; then
            echo jim.database
        fi
        echo jim.worker jim.scheduler jim.web
    fi
}

# What is wrong with one of JIM's containers, such as "restarting, restarted 9 times"; nothing when it runs properly.
container_problem() {
    local name="$1"
    local state status restarts health="" code
    if [ "$RUNTIME" = "podman" ]; then
        state=$(as_jim_account podman inspect --format '{{.State.Status}}|{{.RestartCount}}|{{.State.ExitCode}}' "$name" 2>/dev/null) \
            || { echo "not created"; return; }
        IFS='|' read -r status restarts code <<< "$state"
        # Podman runs health checks on a timer only under systemd, so run the check rather than read its last result.
        # It exits 1 when the check fails, and otherwise for a container without one.
        local check=0
        as_jim_account podman healthcheck run "$name" >/dev/null 2>&1 || check=$?
        if [ "$check" -eq 1 ]; then
            health="unhealthy"
        fi
    else
        state=$(docker inspect -f '{{.State.Status}}|{{.RestartCount}}|{{if .State.Health}}{{.State.Health.Status}}{{end}}|{{.State.ExitCode}}' "$name" 2>/dev/null) \
            || { echo "not created"; return; }
        IFS='|' read -r status restarts health code <<< "$state"
    fi

    local -a problems=()
    if [ "$status" != "running" ]; then
        if [ "$status" = "exited" ] && [ -n "$code" ]; then
            problems+=("exited (code ${code})")
        else
            problems+=("$status")
        fi
    fi
    if [ "${restarts:-0}" -gt 0 ] 2>/dev/null; then
        problems+=("restarted ${restarts} times")
    fi
    case "$health" in
        unhealthy) problems+=("unhealthy") ;;
        starting) problems+=("not yet healthy") ;;
    esac
    if [ ${#problems[@]} -gt 0 ]; then
        local joined
        printf -v joined '%s, ' "${problems[@]}"
        echo "${joined%, }"
    fi
}

# Says JIM is not ready, naming each of its containers that is not running properly, with the end of its log, so
# that the message names the failure itself (#1944). Until then it named the web and worker logs only, which show
# just their side of a database that never started, and Compose starts them regardless of the bundled database.
report_unready_containers() {
    local install_dir="$1"
    local name problem database_failed=""
    local -a failing=()
    for name in $(jim_containers); do
        problem=$(container_problem "$name")
        [ -n "$problem" ] || continue
        if [ ${#failing[@]} -eq 0 ]; then
            warn "JIM is not ready after 10 minutes. These containers are not running properly:"
        fi
        failing+=("$name")
        case "$name" in
            jim.database|jim-database-postgres) database_failed="true" ;;
        esac
        echo "  ${BOLD}${name}${RESET}: ${problem}"
        [ "$problem" != "not created" ] || continue
        echo "    The end of its log:"
        if [ "$RUNTIME" = "podman" ]; then
            as_jim_account podman logs --tail 20 "$name" 2>&1 | sed 's/^/      /' || true
        else
            docker logs --tail 20 "$name" 2>&1 | sed 's/^/      /' || true
        fi
    done

    local -a shown=()
    if [ ${#failing[@]} -gt 0 ]; then
        shown=("${failing[@]}")
        if [ -n "$database_failed" ]; then
            warn "JIM's services wait for the bundled database, so fix the database first: see ${DOCS_BASE}/administration/troubleshooting/"
        fi
    else
        warn "JIM is not ready after 10 minutes, though each of its containers is running."
        read -r -a shown <<< "$(jim_containers | tr '\n' ' ')"
    fi

    if [ "$RUNTIME" = "podman" ]; then
        local -a commands=()
        for name in "${shown[@]}"; do
            commands+=("$(jim_podman_command logs "$name")")
        done
        local joined
        printf -v joined '%s; ' "${commands[@]}"
        warn "See what it is doing with: ${joined%; }"
    else
        local compose="docker compose ${COMPOSE_FILES[*]}"
        if [ "$USE_BUNDLED_DB" = "true" ]; then
            compose="${compose} --profile with-db"
        fi
        warn "See what it is doing with: cd ${install_dir} && ${compose} logs ${shown[*]}"
    fi
}

# Whether jim.web's health check passes, which it does once JIM is ready to serve. On Podman the check is run here
# rather than read, because Podman runs health checks on a timer only under systemd.
jim_is_healthy() {
    if [ "$RUNTIME" = "podman" ]; then
        as_jim_account podman healthcheck run jim-web >/dev/null 2>&1
    else
        [ "$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{end}}' jim.web 2>/dev/null)" = "healthy" ]
    fi
}

# The address users open JIM at: the certificate's first name, on the published port.
jim_url() {
    local install_dir="$1"
    local port
    if [ "$RUNTIME" = "podman" ]; then
        port="$JIM_WEB_PORT"
    else
        port=$(env_value JIM_WEB_PORT "${install_dir}/.env")
    fi
    port="${port:-$DEFAULT_WEB_PORT}"
    # JIM_WEB_PORT may carry an address prefix, such as 127.0.0.1:443.
    port="${port##*:}"
    local name
    name=$(certificate_first_name "${install_dir}/tls/tls.crt")
    if [ "$port" = "443" ]; then
        printf 'https://%s' "${name:-<this server>}"
    else
        printf 'https://%s:%s' "${name:-<this server>}" "$port"
    fi
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

    local heading="Installation complete"
    if [ "$JIM_READY" = "false" ]; then
        heading="Installed, but JIM is not ready yet: see the warning above"
    fi
    echo "${BOLD}--------------------------------------------------${RESET}"
    echo "${BOLD}  ${heading}${RESET}"
    echo "${BOLD}--------------------------------------------------${RESET}"
    echo
    echo "  ${BOLD}Location:${RESET}     ${install_dir}"
    echo "  ${BOLD}Version:${RESET}      ${JIM_RELEASE_VERSION}"
    if [ "$RUNTIME" = "podman" ] && [ -n "$PODMAN_ACCOUNT" ]; then
        echo "  ${BOLD}Runtime:${RESET}      Podman, rootless as ${PODMAN_ACCOUNT}"
    elif [ "$RUNTIME" = "podman" ]; then
        echo "  ${BOLD}Runtime:${RESET}      Podman, rootful"
    else
        echo "  ${BOLD}Runtime:${RESET}      Docker"
    fi
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        echo "  ${BOLD}Database:${RESET}     Bundled PostgreSQL"
    else
        echo "  ${BOLD}Database:${RESET}     External (${JIM_DB_HOSTNAME})"
    fi
    echo "  ${BOLD}Address:${RESET}      ${url}"
    echo "  ${BOLD}Certificate:${RESET}  expires on $(certificate_expiry "${tls_dir}/tls.crt")"
    echo
    local installer
    installer=$(installer_command "$install_dir")

    echo "  ${BOLD}Next steps:${RESET}"
    echo "    At your identity provider, register these redirect URIs for JIM's client:"
    echo "      ${url}/signin-oidc"
    echo "      ${url}/signout-callback-oidc"
    if [ "$TLS_MODE" = "generate" ]; then
        echo "    Add this certificate authority to the trusted root certificate authorities of every machine"
        echo "    whose browser or tools use JIM (for example by Group Policy); until then, browsers warn:"
        echo "      ${absolute_dir}/tls/ca.crt"
        echo "    Keep ${absolute_dir}/tls/ca.key secret: it signs JIM's certificates."
    fi
    echo
    echo "  ${BOLD}Looking after JIM:${RESET}"
    if [ "$TLS_MODE" = "generate" ]; then
        echo "    ${installer} --renew-certificate    before the certificate expires"
    fi
    echo "    ${installer} --certificate          to change its names, or use your organisation's certificate"
    if [ "$RUNTIME" = "podman" ]; then
        local systemctl_command
        systemctl_command=$(jim_systemctl_command)
        if [ "$PODMAN_SYSTEMD" = "true" ]; then
            echo "    ${systemctl_command} status jim.service"
            echo "    ${systemctl_command} restart jim.service"
        else
            echo "    Without systemd, JIM does not start by itself after a reboot. Start it with:"
            podman_start_commands "$install_dir" | sed 's/^  /      /'
        fi
        echo "    $(jim_podman_command ps)"
        echo "    $(jim_podman_command logs -f jim-web)"
        if [ -n "$PODMAN_ACCOUNT" ]; then
            echo "    Volumes and secrets are in the Podman storage of ${PODMAN_ACCOUNT}: $(account_home)/.local/share/containers"
        fi
    else
        echo "    cd ${absolute_dir}"
        echo "    ${compose} ps"
        echo "    ${compose} logs -f"
        echo "    ${compose} down"
    fi
    echo
    echo "  ${BOLD}Documentation:${RESET}"
    echo "    Deployment: ${DOCS_BASE}/administration/deployment/"
    echo "    SSO Setup:  ${DOCS_BASE}/administration/sso-setup/"
    echo "    Releases:   https://github.com/${GITHUB_REPO}/releases"
    echo
}

show_help() {
    echo "Usage: setup.sh [--runtime docker|podman] [--rootless]"
    echo "       setup.sh --renew-certificate | --certificate | --upgrade | --help"
    echo
    echo "Installs JIM with Docker or Podman, asking for anything not set in the environment. Run from an"
    echo "extracted release bundle, it installs from the bundle and needs no internet connection."
    echo
    echo "  --runtime docker|podman  The container runtime to install JIM on. Only needed where both are"
    echo "                       installed: the script asks rather than guess."
    echo "  --rootless           Podman only: run JIM rootless, under a dedicated account, instead of as root."
    echo "  --renew-certificate  Issue a new server certificate from the certificate authority this"
    echo "                       script created, for the same names, and restart JIM's web service."
    echo "  --certificate        Create or install JIM's certificate again, for example to change its"
    echo "                       names or to use your organisation's certificate, and restart JIM's web service."
    echo "  --upgrade            Docker only: upgrade the installation to the latest release, or to the release"
    echo "                       bundle this script is in, keeping its settings, and restart JIM."
    echo "  --help               Show this help."
    echo
    echo "The certificate and upgrade options act on the installation this script sits in, or on JIM_INSTALL_DIR."
    echo "Documentation: ${DOCS_BASE}/administration/deployment/"
}

# --- Upgrade (Docker) ---
# Records the checksums of the compose files as the release ships them.
record_compose_checksums() {
    local install_dir="$1"
    (cd "$install_dir" && sha256sum "${SHIPPED_COMPOSE_FILES[@]}" > "$COMPOSE_CHECKSUMS") \
        || fatal "Failed to record the compose files' checksums in ${install_dir}/${COMPOSE_CHECKSUMS}"
    chmod 644 "${install_dir}/${COMPOSE_CHECKSUMS}"
}

# Stops the upgrade if a compose file was edited since it was installed: the upgrade replaces them, and an edit
# would be lost without anyone noticing. Edits belong in a compose file of the administrator's own.
check_compose_edits() {
    local install_dir="$1"
    if [ ! -f "${install_dir}/${COMPOSE_CHECKSUMS}" ]; then
        warn "This installation has no record of its compose files as installed, so the upgrade cannot tell whether they were edited. It keeps the current ones as ${SHIPPED_COMPOSE_FILES[*]/%/.previous}; move any edits of yours into a compose file of your own."
        return
    fi
    # sha256sum fails when a file differs, which is the case being looked for, not an error.
    local edited
    edited=$( (cd "$install_dir" && sha256sum -c "$COMPOSE_CHECKSUMS" 2>/dev/null || true) | sed -n 's/: FAILED.*$//p' | tr '\n' ' ')
    [ -z "$edited" ] \
        || fatal "These compose files were edited after they were installed: ${edited}The upgrade replaces them with the new release's, which would lose the edits. Move the edits into a compose file of your own next to them (for example docker-compose.local.yml), start JIM once with it added (-f docker-compose.local.yml) so the upgrade uses it too, put the edited files back as they were, and run the upgrade again. See ${DOCS_BASE}/administration/upgrading/"
}

# The compose files the deployment was last started with, as -f arguments in UPGRADE_COMPOSE_FILES, so that the
# upgraded JIM starts the same way, with any compose file of the administrator's own. Compose records them on
# each container it creates, a stopped one included; without a container, the files the installer starts JIM with.
UPGRADE_COMPOSE_FILES=()
read_deployment_compose_files() {
    local install_dir="$1"
    local recorded
    recorded=$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project.config_files"}}' jim.web 2>/dev/null || true)
    UPGRADE_COMPOSE_FILES=()
    if [ -z "$recorded" ]; then
        UPGRADE_COMPOSE_FILES=("${COMPOSE_FILES[@]}")
        return
    fi
    local file
    local -a files
    IFS=',' read -r -a files <<< "$recorded"
    for file in "${files[@]}"; do
        [ -f "$file" ] || fatal "JIM was started with ${file}, which no longer exists. Start JIM once without it, or put it back, then run the upgrade again."
        UPGRADE_COMPOSE_FILES+=(-f "$file")
    done
}

# Asks the administrator to confirm the backup the upgrade documentation asks for: an upgrade can change the
# database, and going back needs the database and the encryption keys from before it.
confirm_backup() {
    [ "${JIM_SETUP_BACKUP_CONFIRMED:-}" = "true" ] && return
    echo
    warn "Before upgrading: disable JIM's Schedules, let running Activities finish, and back up JIM's database and its encryption keys together. Going back to this version needs both. See ${DOCS_BASE}/administration/upgrading/"
    if ! prompt_yn "Have you backed up the database and the encryption keys?" "n"; then
        info "Upgrade cancelled; nothing has changed."
        exit 0
    fi
}

# Lists the settings the new release's template has that .env does not mention; their defaults apply.
report_new_settings() {
    local template="$1"
    local env_file="$2"
    local key
    local -a new_keys=()
    for key in $(sed -n 's/^#\{0,1\} \{0,1\}\([A-Z][A-Z0-9_]*\)=.*/\1/p' "$template" | sort -u); do
        grep -Eq "^#? ?${key}=" "$env_file" || new_keys+=("$key")
    done
    if [ ${#new_keys[@]} -gt 0 ]; then
        info "This release adds settings your .env does not mention, which keep their defaults: ${new_keys[*]}. See .env.example beside it, and ${DOCS_BASE}/administration/configuration/"
    fi
}

UPGRADE_STAGE=""
UPGRADE_INSTALL_DIR=""
UPGRADE_SWAPPED=""
UPGRADE_STARTED=""
# On leaving before JIM is restarted, puts the previous compose files and .env back, so that a failed upgrade leaves
# the installation as it was.
finish_upgrade() {
    if [ "$UPGRADE_SWAPPED" = "true" ] && [ "$UPGRADE_STARTED" != "true" ]; then
        local name
        for name in "${SHIPPED_COMPOSE_FILES[@]}" .env; do
            if [ -f "${UPGRADE_INSTALL_DIR}/${name}.previous" ]; then
                cp -p "${UPGRADE_INSTALL_DIR}/${name}.previous" "${UPGRADE_INSTALL_DIR}/${name}"
            fi
        done
        warn "Put the previous compose files and .env back; JIM is as it was before the upgrade."
    fi
    if [ -n "$UPGRADE_STAGE" ]; then
        rm -rf "$UPGRADE_STAGE"
    fi
}

upgrade_installation() {
    local install_dir
    install_dir=$(resolve_install_dir)
    is_installation "$install_dir" \
        || fatal "No JIM installation at ${install_dir}. Set JIM_INSTALL_DIR to the folder JIM is installed in."
    load_installation "$install_dir"
    [ "$RUNTIME" = "docker" ] \
        || fatal "--upgrade upgrades JIM on Docker. To upgrade JIM on Podman, see ${DOCS_BASE}/administration/upgrading/"
    check_prerequisites

    local current target
    current=$(env_value JIM_VERSION "$CONFIG_FILE")
    if [ -n "$BUNDLE_DIR" ]; then
        read_bundle_version "Upgrading to"
    else
        detect_latest_version
    fi
    target="$JIM_RELEASE_VERSION"
    if [ -z "$current" ]; then
        warn "${CONFIG_FILE} does not set JIM_VERSION, so the upgrade cannot check that v${target} is newer than the version JIM runs."
    else
        if [ "$current" = "$target" ]; then
            success "JIM at ${install_dir} already runs v${target}; there is nothing to upgrade."
            return
        fi
        if version_at_least "$current" "$target"; then
            fatal "JIM at ${install_dir} runs v${current}, which is newer than v${target}. To go back to an earlier version, restore the backup taken before upgrading: ${DOCS_BASE}/administration/upgrading/#rolling-back"
        fi
    fi
    info "Upgrading JIM at ${install_dir}${current:+ from v${current}} to v${target}"

    check_compose_edits "$install_dir"
    confirm_backup
    if [ "$(env_value JIM_DB_HOSTNAME "$CONFIG_FILE")" = "jim.database" ]; then
        USE_BUNDLED_DB="true"
    else
        USE_BUNDLED_DB="false"
    fi
    read_deployment_compose_files "$install_dir"

    UPGRADE_INSTALL_DIR="$install_dir"
    UPGRADE_STAGE=$(mktemp -d)
    trap finish_upgrade EXIT

    # The new release's files, gathered before anything in the installation changes.
    if [ -n "$BUNDLE_DIR" ]; then
        cp "${BUNDLE_DIR}/compose/docker-compose.yml" "${BUNDLE_DIR}/compose/docker-compose.production.yml" \
            "${BUNDLE_DIR}/compose/.env.example" "$UPGRADE_STAGE/"
    else
        local name
        for name in "${SHIPPED_COMPOSE_FILES[@]}"; do
            curl -fsSL -o "${UPGRADE_STAGE}/${name}" "${RELEASE_DOWNLOAD_BASE}/${name}" || fatal "Failed to download ${name}"
        done
        # Published as default.env.example: GitHub renames an asset whose name starts with a dot.
        curl -fsSL -o "${UPGRADE_STAGE}/.env.example" "${RELEASE_DOWNLOAD_BASE}/default.env.example" \
            || fatal "Failed to download default.env.example"
    fi

    # The previous files stay beside the new ones, for going back.
    local name
    for name in "${SHIPPED_COMPOSE_FILES[@]}" .env; do
        cp -p "${install_dir}/${name}" "${install_dir}/${name}.previous"
    done
    UPGRADE_SWAPPED="true"
    cp "${UPGRADE_STAGE}/docker-compose.yml" "${UPGRADE_STAGE}/docker-compose.production.yml" "$install_dir/"
    cp "${UPGRADE_STAGE}/.env.example" "${install_dir}/.env.example"
    success "Installed the v${target} compose files; the previous ones are beside them, ending .previous"

    update_env "JIM_VERSION" "$target" "$CONFIG_FILE"
    # A PostgreSQL image run by its ID is the previous release's: the new compose file's pin replaces it.
    if grep -q '^JIM_DB_IMAGE=' "$CONFIG_FILE"; then
        update_env "JIM_DB_IMAGE" "" "$CONFIG_FILE"
    fi
    # The bundled PostgreSQL's memory, where .env does not size it: the compose file's defaults now suit a 4 GB host,
    # where until #1943 they suited a 64 GB one, so a larger host would otherwise lose its tuning.
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        size_database keep
    fi

    local -a profile=()
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        profile=(--profile with-db)
    fi
    if [ -n "$BUNDLE_DIR" ]; then
        load_bundle_images
    else
        info "Downloading JIM's v${target} images (this takes a few minutes)..."
        (cd "$install_dir" && docker compose "${UPGRADE_COMPOSE_FILES[@]}" ${profile[@]+"${profile[@]}"} pull -q) \
            || fatal "Failed to download JIM's v${target} images"
        success "Downloaded the images"
    fi
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        check_database_image "${install_dir}/docker-compose.yml"
        if [ -n "$DATABASE_IMAGE_ID" ]; then
            update_env "JIM_DB_IMAGE" "$DATABASE_IMAGE_ID" "$CONFIG_FILE"
        fi
    fi
    local image
    for image in $(cd "$install_dir" && docker compose "${UPGRADE_COMPOSE_FILES[@]}" ${profile[@]+"${profile[@]}"} config --images); do
        docker image inspect "$image" >/dev/null 2>&1 \
            || fatal "The image ${image} is not available, so JIM cannot start on v${target}."
    done
    report_new_settings "${install_dir}/.env.example" "$CONFIG_FILE"

    # From here the installation is the new release's: a failure leaves it to be started again, not undone.
    UPGRADE_STARTED="true"
    record_compose_checksums "$install_dir"
    # A copy of the new release's installer, for looking after JIM from now on. Run from the installation's own
    # copy, this script is the previous release's, so fetch the new one rather than keep it.
    [ -n "$BUNDLE_DIR" ] || SCRIPT_PATH=""
    save_installer_copy "$install_dir"

    info "Restarting JIM on v${target}; any database upgrade the release brings is applied as it starts..."
    (cd "$install_dir" && docker compose "${UPGRADE_COMPOSE_FILES[@]}" ${profile[@]+"${profile[@]}"} up -d --pull never) \
        || fatal "Failed to start JIM on v${target}. See the messages above; once the cause is fixed, start it with: cd ${install_dir} && docker compose ${UPGRADE_COMPOSE_FILES[*]} ${profile[*]} up -d --pull never"
    wait_for_jim "$install_dir"
    if [ "$JIM_READY" = "true" ]; then
        success "JIM is upgraded to v${target}. Re-enable the Schedules you disabled for the upgrade."
    else
        exit 1
    fi
}

# --- Main ---
main() {
    # When piped (curl ... | bash), stdin is the pipe, not the terminal.
    # Reopen stdin from /dev/tty so interactive prompts work.
    if [ ! -t 0 ] && [ -r /dev/tty ] && (echo < /dev/tty) 2>/dev/null; then
        exec < /dev/tty
    fi

    setup_colours

    local action="install"
    while [ $# -gt 0 ]; do
        case "$1" in
            --runtime)
                [ $# -ge 2 ] || fatal "--runtime needs a value: docker or podman"
                JIM_SETUP_RUNTIME="$2"
                shift 2
                ;;
            --runtime=*)
                JIM_SETUP_RUNTIME="${1#--runtime=}"
                shift
                ;;
            --rootless)
                JIM_SETUP_PODMAN_ROOTLESS="true"
                shift
                ;;
            --renew-certificate)
                action="renew-certificate"
                shift
                ;;
            --certificate)
                action="certificate"
                shift
                ;;
            --upgrade)
                action="upgrade"
                shift
                ;;
            --help|-h)
                show_help
                exit 0
                ;;
            *)
                fatal "Unknown option: $1 (see --help)"
                ;;
        esac
    done

    case "$action" in
        renew-certificate)
            renew_certificate
            exit 0
            ;;
        certificate)
            change_certificate
            exit 0
            ;;
        upgrade)
            show_banner
            upgrade_installation
            exit 0
            ;;
    esac

    show_banner
    choose_runtime

    local install_dir
    install_dir=$(resolve_install_dir)

    # Check for existing installation
    if [ -f "${install_dir}/.env" ] || [ -f "${install_dir}/jim-config.yaml" ]; then
        warn "Existing installation detected at ${install_dir}"
        if ! prompt_yn "Overwrite configuration?" "n"; then
            info "Aborting. Existing installation left untouched."
            exit 0
        fi
    fi

    check_prerequisites
    if [ "$RUNTIME" = "podman" ]; then
        choose_podman_account "$install_dir"
        prepare_podman_account
        check_account_environment
    fi

    if [ -n "$BUNDLE_DIR" ]; then
        read_bundle_version
    else
        detect_latest_version
    fi

    EXISTING_DB_PASSWORD=$(existing_database_password "$install_dir")

    if [ "$RUNTIME" = "podman" ]; then
        if [ -n "$BUNDLE_DIR" ]; then
            PODMAN_SOURCE="${BUNDLE_DIR}/podman"
            [ -f "${PODMAN_SOURCE}/jim.yaml" ] || fatal "This bundle has no Podman files (podman/jim.yaml). Podman installations need a bundle of a release that supports Podman."
        else
            PODMAN_SOURCE=$(mktemp -d)
            # shellcheck disable=SC2064 # Expanded now, deliberately: the folder to remove is this one.
            trap "rm -rf '${PODMAN_SOURCE}'" EXIT
            download_podman_files "$PODMAN_SOURCE"
        fi
        install_podman_files "$install_dir"
        CONFIG_FILE="${install_dir}/jim-config.yaml"
    else
        if [ -n "$BUNDLE_DIR" ]; then
            copy_bundle_files "$install_dir"
        else
            download_files "$install_dir"
        fi
        record_compose_checksums "$install_dir"
        CONFIG_FILE="${install_dir}/.env"
    fi
    save_installer_copy "$install_dir"

    configure_database
    configure_sso
    if [ "$RUNTIME" = "docker" ]; then
        configure_registry
    fi
    if [ -n "$BUNDLE_DIR" ]; then
        load_bundle_images
        if [ "$RUNTIME" = "podman" ]; then
            verify_podman_images "$install_dir"
        else
            if [ "$USE_BUNDLED_DB" = "true" ]; then
                check_database_image "${install_dir}/docker-compose.yml"
                if [ -n "$DATABASE_IMAGE_ID" ]; then
                    set_setting "JIM_DB_IMAGE" "$DATABASE_IMAGE_ID"
                fi
            fi
            verify_bundle_images "$install_dir"
        fi
    elif [ "$RUNTIME" = "podman" ]; then
        pull_podman_images "$install_dir"
    fi
    configure_port
    configure_certificate "$install_dir"
    configure_proxy

    if [ "$RUNTIME" = "podman" ]; then
        store_podman_secrets
        store_tls_secret "$install_dir"
        if [ "$PODMAN_SYSTEMD" = "true" ]; then
            install_podman_units "$install_dir"
        fi
        write_install_state "$install_dir"
        configure_apparmor
        configure_firewall
    fi

    launch_jim "$install_dir"
    show_summary "$install_dir"

    # Automation can tell a JIM that never became ready from a successful installation.
    if [ "$JIM_READY" = "false" ]; then
        exit 1
    fi
}

# Run, it installs JIM; sourced, as its tests do, it only defines its functions.
if ! (return 0 2>/dev/null); then
    main "$@"
fi
