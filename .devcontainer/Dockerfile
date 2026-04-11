FROM mcr.microsoft.com/devcontainers/dotnet:1-10.0-noble

# Install pinned .NET SDK version to match global.json.
# The base image can ship with an SDK that predates global.json's pin
# (e.g. 10.0.100-rc.2 on arm64 at time of writing), which fails
# rollForward: latestFeature against 10.0.200. Layer the pinned SDK in
# explicitly so builds are deterministic across architectures and image
# refresh cycles, regardless of what the base image happens to bundle.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --version 10.0.200 --install-dir /usr/share/dotnet --no-path \
    && rm /tmp/dotnet-install.sh

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
#
# Install python3-pip for MkDocs Material install in setup.sh. The Noble base
# image only ships python3-minimal, which lacks pip/ensurepip - so pip install
# during setup silently fails without this. (Regression from the bookworm->noble
# base image bump; bookworm shipped pip by default.)
RUN apt-get install -y --no-install-recommends \
    socat \
    python3-pip \
    libglib2.0-0 libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
    libxkbcommon0 libxcomposite1 libxdamage1 libxrandr2 libgbm1 libpango-1.0-0 \
    libcairo2 libasound2t64 libxshmfence1 libxfixes3 \
    && rm -rf /var/lib/apt/lists/*
