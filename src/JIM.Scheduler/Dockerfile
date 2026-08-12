# syntax=docker/dockerfile:1
# jim-compliance: production-image
# This image ships to customers. Base image digest pinning is enforced by CI
# (see .github/workflows/ci.yml scan-base-images job). Do not remove the
# @sha256: digest from any FROM line or this Dockerfile will fail CI.
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble@sha256:acad02eb5c4fbf57d15296f9c08d56cd4036e915bdae5b4dd48a06523d452617 AS base
ARG VERSION

LABEL org.opencontainers.image.title="JIM Scheduler"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Job scheduler service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://junctional.io/license"
WORKDIR /app

# Create data directories writeable by non-root user before switching
# Note: no LDAP/Kerberos packages needed; Scheduler only queues WorkerTasks,
# it never communicates with connected systems directly.
RUN mkdir -p /data/keys /var/log/jim \
    && chown -R app:app /data/keys /var/log/jim

# Run as non-root for security (built-in 'app' user, UID 1654)
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80 AS build
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
ENTRYPOINT ["dotnet", "JIM.Scheduler.dll"]
