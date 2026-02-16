# syntax=docker/dockerfile:1
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/runtime:9.0@sha256:9916fb650c83b13231e4ec91d10e76a520209a0caeeeb46c507a9d98db8612a2 AS base

LABEL org.opencontainers.image.title="JIM Worker"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Background worker service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://tetron.io/jim/#licensing"
WORKDIR /app

# Pin apt package versions for reproducible builds
# Update these versions when bumping base image digests
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        libldap-common=2.5.13+dfsg-5 \
        libldap-2.5-0=2.5.13+dfsg-5 \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:03a7d87512a743b3d53416adae79eb89d2a7163de78bb58e7bc7b7e262e25750 AS build
WORKDIR /src
COPY ["JIM.Worker/JIM.Worker.csproj", "JIM.Worker/"]
COPY ["JIM.Models/JIM.Models.csproj", "JIM.Models/"]
COPY ["JIM.Application/JIM.Application.csproj", "JIM.Application/"]
COPY ["JIM.PostgresData/JIM.PostgresData.csproj", "JIM.PostgresData/"]
COPY ["JIM.Utilities/JIM.Utilities.csproj", "JIM.Utilities/"]
COPY ["JIM.Connectors/JIM.Connectors.csproj", "JIM.Connectors/"]
COPY ["JIM.Data/JIM.Data.csproj", "JIM.Data/"]

RUN dotnet restore "./JIM.Worker/JIM.Worker.csproj"

COPY . .
WORKDIR "/src/JIM.Worker"

# Publish stage - optimised for production
FROM build AS publish
RUN dotnet publish "JIM.Worker.csproj" -c Release -o /app/publish

# Final stage - use published output
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Worker.dll"]
