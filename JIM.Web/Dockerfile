# syntax=docker/dockerfile:1
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base

LABEL org.opencontainers.image.title="JIM Web"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Web and API service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://tetron.io/jim/#licensing"
WORKDIR /app
EXPOSE 80
EXPOSE 443

RUN apt-get update \
    && apt-get install -y libldap-common \
    && apt-get install -y libldap-2.5-0 \
    && apt-get install -y cifs-utils \
    && apt-get install -y --no-install-recommends iputils-ping \
    && apt-get install -y --no-install-recommends netcat-traditional \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["JIM.Web/JIM.Web.csproj", "JIM.Web/"]
COPY ["JIM.Models/JIM.Models.csproj", "JIM.Models/"]
COPY ["JIM.Application/JIM.Application.csproj", "JIM.Application/"]
COPY ["JIM.PostgresData/JIM.PostgresData.csproj", "JIM.PostgresData/"]
COPY ["JIM.Utilities/JIM.Utilities.csproj", "JIM.Utilities/"]
COPY ["JIM.Connectors/JIM.Connectors.csproj", "JIM.Connectors/"]
COPY ["JIM.Data/JIM.Data.csproj", "JIM.Data/"]

# Restore with BuildKit cache mount for NuGet packages
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "./JIM.Web/JIM.Web.csproj"

COPY . .
WORKDIR "/src/JIM.Web"

# Build with BuildKit cache mount
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet build "JIM.Web.csproj" -c Release -o /app/build

# Dev target - use build output directly (faster, ~10-15s saved)
FROM base AS dev
WORKDIR /app
COPY --from=build /app/build .
ENTRYPOINT ["dotnet", "JIM.Web.dll"]

# Publish stage - optimised for production
FROM build AS publish
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "JIM.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Release target - use published output (production-ready)
FROM base AS release
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Web.dll"]

# Default target is release for backwards compatibility
FROM release AS final
