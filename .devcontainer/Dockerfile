FROM mcr.microsoft.com/devcontainers/dotnet:1-9.0-bookworm

# Remove stale Yarn apt repo that breaks feature installs (expired GPG key)
RUN rm -f /etc/apt/sources.list.d/yarn.list 2>/dev/null; \
    rm -f /etc/apt/keyrings/yarn.gpg 2>/dev/null; \
    apt-get update || true

# Install socat for Keycloak port-forwarding bridge.
# Docker-in-Docker proxy ports aren't auto-forwarded by VS Code Dev Containers;
# socat provides a userspace bridge that VS Code detects and forwards to the host.
RUN apt-get install -y --no-install-recommends socat \
    && rm -rf /var/lib/apt/lists/*
