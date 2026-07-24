# syntax=docker/dockerfile:1
# jim-compliance: production-image
# This image ships to customers. Base image digest pinning is enforced by CI
# (see .github/workflows/ci.yml scan-base-images job). Do not remove the
# @sha256: digest from any FROM line or this Dockerfile will fail CI.
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble@sha256:ed5d539b27842d656a06a5984dbcb5114d3e885fbada612a49a5a7c3c3a44e1c AS base
ARG VERSION

LABEL org.opencontainers.image.title="JIM Worker"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Background worker service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://junctional.io/license"
WORKDIR /app

# Pin apt package versions for reproducible builds
# Update these versions when bumping base image digests
# Note: libldap-2.5-0 was renamed to libldap2 in Ubuntu Noble (OpenLDAP 2.6)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libldap-common=2.6.10+dfsg-0ubuntu0.24.04.1 \
        libldap2=2.6.10+dfsg-0ubuntu0.24.04.1 \
        cifs-utils=2:7.0-2ubuntu0.5 \
        libgssapi-krb5-2=1.20.1-6ubuntu2.7 \
    && rm -rf /var/lib/apt/lists/* \
    # Symlink for System.DirectoryServices.Protocols compatibility
    # .NET 10 managed LDAP code loads libldap-2.5.so.0 by name; Noble ships libldap.so.2
    # See: https://github.com/dotnet/runtime/issues/123676
    && ln -sf /usr/lib/$(uname -m)-linux-gnu/libldap.so.2 /usr/lib/$(uname -m)-linux-gnu/libldap-2.5.so.0

# Create data directories writable by non-root user before switching.
# /connector-files is the formal File Connector mount point; creating it here
# with app:app ownership ensures Docker inherits the correct ownership when
# the jim-connector-files-volume is mounted on first container start.
RUN mkdir -p /data/keys /var/log/jim /connector-files \
    && chown -R app:app /data/keys /var/log/jim /connector-files

# Run as non-root for security (built-in 'app' user, UID 1654)
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS build
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["VERSION", "./"]
COPY ["src/JIM.Worker/JIM.Worker.csproj", "JIM.Worker/"]
COPY ["src/JIM.Worker/packages.lock.json", "JIM.Worker/"]
COPY ["src/JIM.Models/JIM.Models.csproj", "JIM.Models/"]
COPY ["src/JIM.Models/packages.lock.json", "JIM.Models/"]
COPY ["src/JIM.Application/JIM.Application.csproj", "JIM.Application/"]
COPY ["src/JIM.Application/packages.lock.json", "JIM.Application/"]
COPY ["src/JIM.PostgresData/JIM.PostgresData.csproj", "JIM.PostgresData/"]
COPY ["src/JIM.PostgresData/packages.lock.json", "JIM.PostgresData/"]
COPY ["src/JIM.Utilities/JIM.Utilities.csproj", "JIM.Utilities/"]
COPY ["src/JIM.Utilities/packages.lock.json", "JIM.Utilities/"]
COPY ["src/JIM.Connectors/JIM.Connectors.csproj", "JIM.Connectors/"]
COPY ["src/JIM.Connectors/packages.lock.json", "JIM.Connectors/"]
COPY ["src/JIM.Data/JIM.Data.csproj", "JIM.Data/"]
COPY ["src/JIM.Data/packages.lock.json", "JIM.Data/"]

RUN dotnet restore "./JIM.Worker/JIM.Worker.csproj" /p:RestoreLockedMode=true

COPY src/ .
WORKDIR "/src/JIM.Worker"

# Publish stage - optimised for production
FROM build AS publish
ARG VERSION_SUFFIX=
RUN dotnet publish "JIM.Worker.csproj" -c Release -o /app/publish /p:VersionSuffix="${VERSION_SUFFIX}" /p:RestoreLockedMode=true

# Final stage - use published output
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Worker.dll"]
