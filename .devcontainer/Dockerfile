# Digest-pinned per the dependency pinning policy (engineering/DEPENDENCY_PINNING.md);
# Dependabot's docker ecosystem watches this Dockerfile and proposes digest bumps.
# The 2.x image line is the maintained GA .NET 10 family: the previous 1-10.0-noble
# tag was frozen upstream on an October 2025, RC-era image and never rebuilt after
# .NET 10 went GA, so it received no SDK, runtime, or OS updates.
FROM mcr.microsoft.com/devcontainers/dotnet:2-10.0-noble@sha256:be5bd898d964c0d081cb22285c2318e488882103702c8831418fdd59218fd5dd

# Install pinned .NET SDK version to match global.json's rollForward target.
# Reasons this is an explicit install rather than relying on the base image:
#  1. The base image bundles whatever SDK was current when its digest was built.
#     Layering the pinned SDK explicitly keeps builds deterministic across
#     architectures and image refresh cycles, regardless of the base's bundle.
#  2. CRITICAL SYNC POINT: this pin must ship at least the ASP.NET Core runtime
#     that src/JIM.Web/JIM.Web.csproj pins via <RuntimeFrameworkVersion> (which
#     itself tracks the production base image digests). If it lags, native
#     JIM.Web runs (jim-build-light, scripts/Generate-OpenApiDoc.ps1) fail at
#     launch with "framework not found" while Docker builds keep working; this
#     bit on 2026-07-14 when the product moved to runtime 10.0.9 and this pin
#     still shipped 10.0.7. Bump this pin in the same commit as any
#     RuntimeFrameworkVersion bump (the review-dependabot skill's checklist
#     includes it), then rebuild the devcontainer.
#  3. The C# Dev Kit's Roslyn LSP hardcodes a runtime version lookup. If
#     /usr/share/dotnet doesn't satisfy that lookup,
#     ms-dotnettools.vscode-dotnet-runtime falls back to acquiring its own
#     sandbox runtime in globalStorage, which has produced MEF composition
#     crashes (FileNotFoundException for System.Numerics.Vectors 10.0.0.0).
#     Pair this with the dotnetAcquisitionExtension.existingDotnetPath setting
#     in devcontainer.json so both ms-dotnettools.csharp and .csdevkit use this
#     install instead of acquiring their own.
# SDK 10.0.301 ships .NET runtime + ASP.NET Core runtime 10.0.9, matching
# JIM.Web's current RuntimeFrameworkVersion.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --version 10.0.301 --install-dir /usr/share/dotnet --no-path \
    && rm /tmp/dotnet-install.sh

# Remove the PowerShell bundled in the base image (stable 7.6.x in the 2.x image
# line). We standardise on the PowerShell version installed as a .NET global tool
# in setup.sh, which we control and pin; removing the base copy leaves that as
# the single, unambiguous `pwsh` on PATH instead of letting the base's copy
# shadow it (/usr/bin precedes ~/.dotnet/tools).
RUN rm -rf /usr/share/powershell /usr/bin/pwsh /usr/local/bin/pwsh

# Hold moby-containerd on the 2.2.x line so the docker-in-docker Feature picks
# a working version when it runs (Features execute after the Dockerfile, so
# this pin is in place before apt sees the Feature's install request).
# moby-containerd 2.3.0 on packages.microsoft.com/microsoft-ubuntu-noble-prod
# ships without the io.containerd.transfer.v1 plugin that dockerd's libcontainerd
# client requires; dockerd fails to start with "failed to start containerd:
# timeout waiting for containerd to start" and Unimplemented errors for the
# events.v1.Events, introspection.v1.Introspection, and grpc.health.v1.Health
# gRPC services. The DinD Feature's "version" option only governs moby-engine,
# not moby-containerd, so the pin has to live here. Priority 1001 forces the
# downgrade even if 2.3.x somehow lands first during the Feature's apt run.
# Re-evaluate when moby-containerd 2.3.x exposes transfer.v1 again upstream.
RUN printf 'Package: moby-containerd\nPin: version 2.2.*\nPin-Priority: 1001\n' \
    > /etc/apt/preferences.d/moby-containerd-pin

# Install socat for Keycloak port-forwarding bridge.
# Docker-in-Docker proxy ports aren't auto-forwarded by VS Code Dev Containers;
# socat provides a userspace bridge that VS Code detects and forwards to the host.
#
# Install Chromium shared libraries for the Playwright MCP browser (in-IDE UI
# validation). setup.sh downloads the Chromium binary itself, but it needs these
# OS libraries at runtime (avoids a --with-deps / sudo install step).
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
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
    socat \
    python3-pip \
    ripgrep \
    libglib2.0-0 libnss3 libatk1.0-0 libatk-bridge2.0-0 libcups2 libdrm2 \
    libxkbcommon0 libxcomposite1 libxdamage1 libxrandr2 libgbm1 libpango-1.0-0 \
    libcairo2 libasound2t64 libxshmfence1 libxfixes3 \
    && rm -rf /var/lib/apt/lists/*
