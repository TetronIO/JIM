#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends libldap-2.4-2 \
    && apt-get install -y --no-install-recommends libldap-common \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["JIM.Worker/JIM.Worker.csproj", "JIM.Worker/"]
COPY ["JIM.Models/JIM.Models.csproj", "JIM.Models/"]
RUN dotnet restore "JIM.Service/JIM.Worker.csproj"
COPY . .
WORKDIR "/src/JIM.Worker"
RUN dotnet build "JIM.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "JIM.Worker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Worker.dll"]