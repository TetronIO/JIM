FROM mcr.microsoft.com/devcontainers/dotnet:1-9.0-bookworm

# Remove stale Yarn apt repo that breaks feature installs (expired GPG key)
RUN rm -f /etc/apt/sources.list.d/yarn.list 2>/dev/null; \
    rm -f /etc/apt/keyrings/yarn.gpg 2>/dev/null; \
    apt-get update || true

# Install socat for Keycloak port-forwarding bridge.
# Docker-in-Docker proxy ports aren't auto-forwarded by VS Code Dev Containers;
# socat provides a userspace bridge that VS Code detects and forwards to the host.
#
# Install Chromium shared libraries for Puppeteer (diagram export via jim-diagrams).
# Puppeteer downloads its own Chrome binary, but needs these OS libraries at runtime.
RUN apt-get install -y --no-install-recommends \
    socat \
    libglib2.0-0 libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
    libxkbcommon0 libxcomposite1 libxdamage1 libxrandr2 libgbm1 libpango-1.0-0 \
    libcairo2 libasound2 libxshmfence1 libxfixes3 \
    && rm -rf /var/lib/apt/lists/*
