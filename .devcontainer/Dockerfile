FROM mcr.microsoft.com/devcontainers/dotnet:1-10.0-noble

# Install pinned .NET SDK version to match global.json's rollForward target.
# Two reasons this is an explicit install rather than relying on the base image:
#  1. The base image can ship with an SDK that predates global.json's pin
#     (e.g. 10.0.100-rc.2 on arm64 at time of writing), which fails
#     rollForward: latestFeature against 10.0.200. Layer the pinned SDK in
#     explicitly so builds are deterministic across architectures and image
#     refresh cycles, regardless of what the base image happens to bundle.
#  2. The C# Dev Kit's Roslyn LSP hardcodes a runtime version lookup
#     (currently 10.0.5). If /usr/share/dotnet doesn't satisfy that lookup,
#     ms-dotnettools.vscode-dotnet-runtime falls back to acquiring its own
#     sandbox runtime in globalStorage, which has produced MEF composition
#     crashes (FileNotFoundException for System.Numerics.Vectors 10.0.0.0).
#     SDK 10.0.203 ships .NET runtime + ASP.NET Core runtime 10.0.7, which
#     covers the Dev Kit's 10.0.5 lookup with margin. Pair this with the
#     dotnetAcquisitionExtension.existingDotnetPath setting in
#     devcontainer.json so both ms-dotnettools.csharp and .csdevkit use
#     this install instead of acquiring their own. Bump in lockstep when
#     the Dev Kit's required runtime version moves past what 10.0.203 ships.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --version 10.0.203 --install-dir /usr/share/dotnet --no-path \
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
#
# Install ripgrep so Claude Code can use the distro binary instead of its
# bundled vendor/ripgrep/arm64-linux/rg. The bundled binary assumes a 4K
# memory page size and SIGABRTs on hosts running a 16K page-size kernel
# (notably Asahi Linux on Apple Silicon, kernel suffix `+16k`), which crashes
# Claude Code at startup when it scans for slash commands. Pairing this with
# USE_BUILTIN_RIPGREP=0 in devcontainer.json forces every environment
# (Codespaces, Docker Desktop on Windows/macOS, native Linux) onto the same
# distro-built ripgrep, which is page-size agnostic.
RUN apt-get install -y --no-install-recommends \
    socat \
    python3-pip \
    ripgrep \
    libglib2.0-0 libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
    libxkbcommon0 libxcomposite1 libxdamage1 libxrandr2 libgbm1 libpango-1.0-0 \
    libcairo2 libasound2t64 libxshmfence1 libxfixes3 \
    && rm -rf /var/lib/apt/lists/*
