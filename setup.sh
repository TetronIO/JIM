#!/usr/bin/env bash
# JIM Setup Script
# Downloads and configures JIM for production deployment.
#
# Usage:
#   curl -fsSL https://raw.githubusercontent.com/TetronIO/JIM/main/setup.sh | bash
#
# Or download and inspect first:
#   curl -fsSL -o setup.sh https://raw.githubusercontent.com/TetronIO/JIM/main/setup.sh
#   bash setup.sh
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

set -euo pipefail

# --- Configuration ---
GITHUB_REPO="TetronIO/JIM"
GITHUB_API_URL="https://api.github.com/repos/${GITHUB_REPO}/releases/latest"
RELEASE_DOWNLOAD_BASE="https://github.com/${GITHUB_REPO}/releases/latest/download"

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

    curl -fsSL -o "${install_dir}/.env" "${base_url}/.env.example" \
        || fatal "Failed to download .env.example"
    success "Downloaded .env (from .env.example)"
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
    echo "${DIM}  See: https://github.com/${GITHUB_REPO}/blob/main/docs/SSO_SETUP_GUIDE.md${RESET}"
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

# --- Launch JIM ---
launch_jim() {
    local install_dir="$1"
    local auto_start="${JIM_SETUP_AUTO_START:-}"

    echo

    local compose_cmd="docker compose -f docker-compose.yml -f docker-compose.production.yml"
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
    echo "  ${CYAN}http://localhost:5200${RESET}"
    echo
}

# --- Summary ---
show_summary() {
    local install_dir="$1"

    echo "${BOLD}--------------------------------------------------${RESET}"
    echo "${BOLD}  Installation complete${RESET}"
    echo "${BOLD}--------------------------------------------------${RESET}"
    echo
    echo "  ${BOLD}Location:${RESET}  ${install_dir}"
    echo "  ${BOLD}Version:${RESET}   ${JIM_RELEASE_VERSION}"
    if [ "$USE_BUNDLED_DB" = "true" ]; then
        echo "  ${BOLD}Database:${RESET}  Bundled PostgreSQL"
    else
        echo "  ${BOLD}Database:${RESET}  External (${JIM_DB_HOSTNAME})"
    fi
    echo
    echo "  ${BOLD}Management commands:${RESET}"
    echo "    cd ${install_dir}"
    echo "    docker compose -f docker-compose.yml -f docker-compose.production.yml ps"
    echo "    docker compose -f docker-compose.yml -f docker-compose.production.yml logs -f"
    echo "    docker compose -f docker-compose.yml -f docker-compose.production.yml down"
    echo
    echo "  ${BOLD}Documentation:${RESET}"
    echo "    SSO Setup:  https://github.com/${GITHUB_REPO}/blob/main/docs/SSO_SETUP_GUIDE.md"
    echo "    Releases:   https://github.com/${GITHUB_REPO}/releases"
    echo
}

# --- Main ---
main() {
    # When piped (curl ... | bash), stdin is the pipe, not the terminal.
    # Reopen stdin from /dev/tty so interactive prompts work.
    if [ ! -t 0 ] && [ -r /dev/tty ] && (echo < /dev/tty) 2>/dev/null; then
        exec < /dev/tty
    fi

    setup_colours
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

    launch_jim "$install_dir"
    show_summary "$install_dir"
}

main "$@"
