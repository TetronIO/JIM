# syntax=docker/dockerfile:1
# jim-compliance: production-image
# This image ships to customers. Base image digest pinning is enforced by CI
# (see .github/workflows/ci.yml scan-base-images job). Do not remove the
# @sha256: digest from any FROM line or this Dockerfile will fail CI.
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble@sha256:ed5d539b27842d656a06a5984dbcb5114d3e885fbada612a49a5a7c3c3a44e1c AS base
ARG VERSION

LABEL org.opencontainers.image.title="JIM Scheduler"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Job scheduler service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://junctional.io/license"
WORKDIR /app

# Create data directories writable by non-root user before switching
# Note: no LDAP/Kerberos packages needed; Scheduler only queues WorkerTasks,
# it never communicates with connected systems directly.
RUN mkdir -p /data/keys /var/log/jim \
    && chown -R app:app /data/keys /var/log/jim

# Run as non-root for security (built-in 'app' user, UID 1654)
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
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
ENTRYPOINT ["dotnet", "JIM.Scheduler.dll"]
