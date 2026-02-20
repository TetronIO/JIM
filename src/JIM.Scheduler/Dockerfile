# syntax=docker/dockerfile:1
# See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

ARG VERSION=dev

FROM mcr.microsoft.com/dotnet/runtime:9.0@sha256:1cc59a29be89c6d42acf0e1191f458c20034c0ab213b2646b8af94c836c9e9bf AS base

LABEL org.opencontainers.image.title="JIM Scheduler"
LABEL org.opencontainers.image.description="JIM (Junctional Identity Manager) - Job scheduler service"
LABEL org.opencontainers.image.version="${VERSION}"
LABEL org.opencontainers.image.vendor="Tetron"
LABEL org.opencontainers.image.source="https://github.com/TetronIO/JIM"
LABEL org.opencontainers.image.licenses="See https://tetron.io/jim/#licensing"
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:03a7d87512a743b3d53416adae79eb89d2a7163de78bb58e7bc7b7e262e25750 AS build
WORKDIR /src
COPY ["JIM.Scheduler/JIM.Scheduler.csproj", "JIM.Scheduler/"]

RUN dotnet restore "./JIM.Scheduler/JIM.Scheduler.csproj"

COPY . .
WORKDIR "/src/JIM.Scheduler"

# Publish stage - optimised for production
FROM build AS publish
RUN dotnet publish "JIM.Scheduler.csproj" -c Release -o /app/publish

# Final stage - use published output
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Scheduler.dll"]
