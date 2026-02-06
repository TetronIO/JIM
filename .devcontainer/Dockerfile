FROM mcr.microsoft.com/devcontainers/dotnet:1-9.0-bookworm

# Remove stale Yarn apt repo that breaks feature installs (expired GPG key)
RUN rm -f /etc/apt/sources.list.d/yarn.list 2>/dev/null; \
    rm -f /etc/apt/keyrings/yarn.gpg 2>/dev/null; \
    apt-get update || true
