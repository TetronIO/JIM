# syntax=docker/dockerfile:1
# jim-compliance: production-image
# This image ships to customers. Base image digest pinning is enforced by CI
# (see .github/workflows/ci.yml discover-base-images job). Do not remove the
# @sha256: digest from any FROM line or this Dockerfile will fail CI.
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble@sha256:a365ce6a50b09176855d085c69da3fc1204a48432e36087e9a208f6e5860e235 AS base
ARG VERSION

LABEL org.opencontainers.image.title="JIM Scheduler"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Job scheduler service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://junctional.io/license"
WORKDIR /app

# Apply Ubuntu's published fixes at build time. Microsoft rebuilds the base image on its own
# cadence, so a digest can trail the noble archive by weeks; upgrading here is what makes the
# shipped image carry the fixes rather than wait for the rebuild. It runs before the pinned
# installs so a pin is never silently moved: a pin older than what the upgrade installed fails
# the build outright (and apt-pin-check will already have proposed the bump).
# See engineering/DEPENDENCY_PINNING.md.
RUN apt-get update \
    && apt-get upgrade -y \
    && rm -rf /var/lib/apt/lists/*

# Create data directories writeable by non-root user before switching
# Note: no LDAP/Kerberos packages needed; Scheduler only queues WorkerTasks,
# it never communicates with connected systems directly.
RUN mkdir -p /data/keys /var/log/jim \
    && chown -R app:app /data/keys /var/log/jim

# Run as non-root for security (built-in 'app' user, UID 1654)
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
# Corporate networks routinely inspect outbound TLS by re-signing it with their own authority, which
# breaks certificate validation for every package restore inside the build. Supplying that authority
# here (base64 of a PEM bundle) lets the build trust it; empty (the default) is a no-op. Build-stage
# only: nothing from this reaches the final image.
ARG EXTRA_CA_CERTS_BASE64=""
RUN if [ -n "$EXTRA_CA_CERTS_BASE64" ]; then \
        echo "$EXTRA_CA_CERTS_BASE64" | base64 -d > /usr/local/share/ca-certificates/extra-build-ca.crt \
        && update-ca-certificates; \
    fi
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["VERSION", "./"]
COPY ["src/JIM.Scheduler/JIM.Scheduler.csproj", "JIM.Scheduler/"]
COPY ["src/JIM.Scheduler/packages.lock.json", "JIM.Scheduler/"]

RUN dotnet restore "./JIM.Scheduler/JIM.Scheduler.csproj" /p:RestoreLockedMode=true

COPY src/ .
WORKDIR "/src/JIM.Scheduler"

# Publish stage - optimised for production
FROM build AS publish
ARG VERSION_SUFFIX=
RUN dotnet publish "JIM.Scheduler.csproj" -c Release -o /app/publish /p:VersionSuffix="${VERSION_SUFFIX}" /p:RestoreLockedMode=true

# Final stage - use published output
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Third-party licence texts the image is bound to carry (the Oracle managed driver in particular);
# see third-party-notices/README.md.
COPY ["third-party-notices/", "./third-party-notices/"]
ENTRYPOINT ["dotnet", "JIM.Scheduler.dll"]
