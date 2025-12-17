# syntax=docker/dockerfile:1
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev
ARG TARGET=release

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base

LABEL org.opencontainers.image.title="JIM Worker"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Background worker service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://tetron.io/jim/#licensing"
WORKDIR /app

RUN apt-get update \
    && apt-get install -y libldap-common \
    && apt-get install -y libldap-2.5-0 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["JIM.Worker/JIM.Worker.csproj", "JIM.Worker/"]
COPY ["JIM.Models/JIM.Models.csproj", "JIM.Models/"]
COPY ["JIM.Application/JIM.Application.csproj", "JIM.Application/"]
COPY ["JIM.PostgresData/JIM.PostgresData.csproj", "JIM.PostgresData/"]
COPY ["JIM.Utilities/JIM.Utilities.csproj", "JIM.Utilities/"]
COPY ["JIM.Connectors/JIM.Connectors.csproj", "JIM.Connectors/"]
COPY ["JIM.Data/JIM.Data.csproj", "JIM.Data/"]

# Restore with BuildKit cache mount for NuGet packages
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "./JIM.Worker/JIM.Worker.csproj"

COPY . .
WORKDIR "/src/JIM.Worker"

# Build stage for dev - Debug configuration
FROM build AS build-debug
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "JIM.Worker.csproj" -c Debug -o /app/build-debug

# Build stage for release - Release configuration
FROM build AS build-release
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "JIM.Worker.csproj" -c Release -o /app/build-release

# Dev target - use Debug build output directly (faster, ~10-15s saved, better debugging)
FROM base AS dev
WORKDIR /app
COPY --from=build-debug /app/build-debug .
ENTRYPOINT ["dotnet", "JIM.Worker.dll"]

# Publish stage - optimised for production
FROM build-release AS publish
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "JIM.Worker.csproj" -c Release -o /app/publish

# Release target - use published output (production-ready)
FROM base AS release
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Worker.dll"]

# Default target is release for backwards compatibility
FROM ${TARGET} AS final
