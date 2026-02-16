# syntax=docker/dockerfile:1
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/aspnet:9.0@sha256:302f77633b8d9a944bd98ff249079c8a353506b83084579216b46a264992025f AS base

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
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libldap-common=2.5.13+dfsg-5 \
        libldap-2.5-0=2.5.13+dfsg-5 \
        cifs-utils=2:7.0-2 \
    && apt-get install -y --no-install-recommends \
        iputils-ping \
        curl \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:a574e6212c98ccf9cf2c6588201b1c71d93094f6f26c21375f75aa909a9e88f0 AS build
WORKDIR /src
COPY ["JIM.Web/JIM.Web.csproj", "JIM.Web/"]
COPY ["JIM.Models/JIM.Models.csproj", "JIM.Models/"]
COPY ["JIM.Application/JIM.Application.csproj", "JIM.Application/"]
COPY ["JIM.PostgresData/JIM.PostgresData.csproj", "JIM.PostgresData/"]
COPY ["JIM.Utilities/JIM.Utilities.csproj", "JIM.Utilities/"]
COPY ["JIM.Connectors/JIM.Connectors.csproj", "JIM.Connectors/"]
COPY ["JIM.Data/JIM.Data.csproj", "JIM.Data/"]

RUN dotnet restore "./JIM.Web/JIM.Web.csproj"

COPY . .
WORKDIR "/src/JIM.Web"

# Publish stage - optimised for production
FROM build AS publish
RUN dotnet publish "JIM.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Final stage - use published output
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Web.dll"]
