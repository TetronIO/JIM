# syntax=docker/dockerfile:1
# jim-compliance: production-image
# This image ships to customers. Base image digest pinning is enforced by CI
# (see .github/workflows/ci.yml scan-base-images job). Do not remove the
# @sha256: digest from any FROM line or this Dockerfile will fail CI.
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev
# OPENAPI_STAGE selects the source for the final image: defaults to openapi-gen
# (which generates the static OpenAPI doc); pass --build-arg OPENAPI_STAGE=publish
# to skip the expensive generation step in local dev builds.
ARG OPENAPI_STAGE=openapi-gen

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:55e37c7795bfaf6b9cc5d77c155811d9569f529d86e20647704bc1d7dd9741d4 AS base
ARG VERSION

LABEL org.opencontainers.image.title="JIM Web"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Web and API service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://tetron.io/jim/#licensing"
WORKDIR /app
EXPOSE 80
EXPOSE 443

# Pin functional dependency versions for reproducible builds
# Update these versions when bumping base image digests
# Note: libldap-2.5-0 was renamed to libldap2 in Ubuntu Noble (OpenLDAP 2.6)
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libldap-common=2.6.10+dfsg-0ubuntu0.24.04.1 \
        libldap2=2.6.10+dfsg-0ubuntu0.24.04.1 \
    && apt-get install -y --no-install-recommends \
        iputils-ping \
        curl \
    && rm -rf /var/lib/apt/lists/* \
    # Symlink for System.DirectoryServices.Protocols compatibility
    # .NET 10 managed LDAP code loads libldap-2.5.so.0 by name; Noble ships libldap.so.2
    # See: https://github.com/dotnet/runtime/issues/123676
    && ln -sf /usr/lib/$(uname -m)-linux-gnu/libldap.so.2 /usr/lib/$(uname -m)-linux-gnu/libldap-2.5.so.0

# Create data directories writable by non-root user before switching.
# /connector-files is the formal File Connector mount point; JIM.Web needs
# read access so the file browser dialog (FileBrowserDialog.razor) can list
# files for admins configuring connectors. Creating it here with app:app
# ownership ensures Docker inherits the correct ownership when the
# jim-connector-files-volume is mounted on first container start.
RUN mkdir -p /data/keys /var/log/jim /connector-files \
    && chown -R app:app /data/keys /var/log/jim /connector-files

# Run as non-root for security (built-in 'app' user, UID 1654)
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:8a90a473da5205a16979de99d2fc20975e922c68304f5c79d564e666dc3982fc AS build
WORKDIR /src
COPY ["Directory.Build.props", "./"]
COPY ["VERSION", "./"]
COPY ["src/JIM.Web/JIM.Web.csproj", "JIM.Web/"]
COPY ["src/JIM.Models/JIM.Models.csproj", "JIM.Models/"]
COPY ["src/JIM.Application/JIM.Application.csproj", "JIM.Application/"]
COPY ["src/JIM.PostgresData/JIM.PostgresData.csproj", "JIM.PostgresData/"]
COPY ["src/JIM.Utilities/JIM.Utilities.csproj", "JIM.Utilities/"]
COPY ["src/JIM.Connectors/JIM.Connectors.csproj", "JIM.Connectors/"]
COPY ["src/JIM.Data/JIM.Data.csproj", "JIM.Data/"]

RUN dotnet restore "./JIM.Web/JIM.Web.csproj"

COPY src/ .
WORKDIR "/src/JIM.Web"

# Publish stage - optimised for production
FROM build AS publish
ARG VERSION_SUFFIX=
RUN dotnet publish "JIM.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:VersionSuffix="${VERSION_SUFFIX}"

# Generate OpenAPI document in lightweight mode (no DB or IdP required).
# The app starts, generates the schema from controller metadata, writes the
# JSON file into the publish output, and exits.
#
# This stage is expensive (full publish output COPY + .NET cold start) and the
# publish layer cache is invalidated on every build by VERSION_SUFFIX, so a
# build arg lets local dev builds bypass it. CI/release builds always run it
# (OPENAPI_STAGE defaults to openapi-gen). Local dev builds can pass
# --build-arg OPENAPI_STAGE=publish to skip; run jim-openapi-generate to
# refresh the static doc on disk when API surface changes.
#
# The output is written under /app/publish so that the final stage can pull
# from either openapi-gen or publish using the same path.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:55e37c7795bfaf6b9cc5d77c155811d9569f529d86e20647704bc1d7dd9741d4 AS openapi-gen
WORKDIR /app/publish
COPY --from=publish /app/publish .
ENV JIM_OPENAPI_GENERATE=true \
    JIM_OPENAPI_OUTPUT_PATH=/app/publish/wwwroot/api/openapi/v1.json \
    JIM_LOG_LEVEL=Warning \
    JIM_LOG_PATH=/tmp \
    JIM_SSO_AUTHORITY=http://localhost:8181/realms/jim \
    JIM_SSO_CLIENT_ID=jim-web \
    JIM_SSO_SECRET=placeholder \
    JIM_SSO_API_SCOPE=jim-api \
    JIM_SSO_CLAIM_TYPE=sub \
    JIM_SSO_MV_ATTRIBUTE="Subject Identifier" \
    JIM_SSO_INITIAL_ADMIN=placeholder \
    JIM_DB_HOSTNAME=localhost \
    JIM_DB_NAME=jim \
    JIM_DB_USERNAME=jim \
    JIM_DB_PASSWORD=placeholder
RUN dotnet JIM.Web.dll

# Final stage - copy from either openapi-gen (default) or publish (when the
# expensive doc-generation step is bypassed). Both stages expose the app
# under /app/publish so the COPY path is identical.
FROM ${OPENAPI_STAGE} AS final-source

FROM base AS final
WORKDIR /app
COPY --from=final-source /app/publish .
ENTRYPOINT ["dotnet", "JIM.Web.dll"]
