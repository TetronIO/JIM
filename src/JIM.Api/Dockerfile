#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["JIM.Api/JIM.Api.csproj", "JIM.Api/"]
COPY ["JIM.Application/JIM.Application.csproj", "JIM.Application/"]
COPY ["JIM.Models/JIM.Models.csproj", "JIM.Models/"]
RUN dotnet restore "JIM.Api/JIM.Api.csproj"
COPY . .
WORKDIR "/src/JIM.Api"
RUN dotnet build "JIM.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "JIM.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "JIM.Api.dll"]