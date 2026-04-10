# syntax=docker/dockerfile:1
# jim-compliance: production-image
# This image ships to customers. Base image digest pinning is enforced by CI
# (see .github/workflows/ci.yml scan-base-images job). Do not remove the
# @sha256: digest from any FROM line or this Dockerfile will fail CI.
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:c433886fdfe33c6427966a412328867b2be9a64f540a105d08943c2dc6fba39b AS base
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

# Create data directories writable by non-root user before switching
RUN mkdir -p /data/keys /var/log/jim \
    && chown -R app:app /data/keys /var/log/jim

# Run as non-root for security (built-in 'app' user, UID 1654)
USER app

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:127d7d4d601ae26b8e04c54efb37e9ce8766931bded0ee59fcd799afd21d6850 AS build
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

# Final stage - use published output
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Web.dll"]
