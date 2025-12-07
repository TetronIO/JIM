#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
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
RUN dotnet restore "./JIM.Worker/JIM.Worker.csproj"
COPY . .
WORKDIR "/src/JIM.Worker"
RUN dotnet build "JIM.Worker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "JIM.Worker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Worker.dll"]