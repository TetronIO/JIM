#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

RUN apt-get update \
    && apt-get install -y --no-install-recommends libldap-2.4-2 \
    && apt-get install -y --no-install-recommends iputils-ping \
    && apt-get install -y --no-install-recommends netcat \
    && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
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
RUN dotnet build "JIM.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "JIM.Web.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Web.dll"]