# syntax=docker/dockerfile:1
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev
ARG TARGET=release

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS base

LABEL org.opencontainers.image.title="JIM Scheduler"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Job scheduler service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://tetron.io/jim/#licensing"
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["JIM.Scheduler/JIM.Scheduler.csproj", "JIM.Scheduler/"]

# Restore with BuildKit cache mount for NuGet packages
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "./JIM.Scheduler/JIM.Scheduler.csproj"

COPY . .
WORKDIR "/src/JIM.Scheduler"

# Build stage for dev - Debug configuration
FROM build AS build-debug
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "JIM.Scheduler.csproj" -c Debug -o /app/build-debug

# Build stage for release - Release configuration
FROM build AS build-release
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "JIM.Scheduler.csproj" -c Release -o /app/build-release

# Dev target - use Debug build output directly (faster, ~10-15s saved, better debugging)
FROM base AS dev
WORKDIR /app
COPY --from=build-debug /app/build-debug .
ENTRYPOINT ["dotnet", "JIM.Scheduler.dll"]

# Publish stage - optimised for production
FROM build-release AS publish
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "JIM.Scheduler.csproj" -c Release -o /app/publish

# Release target - use published output (production-ready)
FROM base AS release
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Scheduler.dll"]

# Default target is release for backwards compatibility
FROM ${TARGET} AS final
