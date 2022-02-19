#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["TIM.Service/TIM.Service.csproj", "TIM.Service/"]
COPY ["TIM.Models/TIM.Models.csproj", "TIM.Models/"]
RUN dotnet restore "TIM.Service/TIM.Service.csproj"
COPY . .
WORKDIR "/src/TIM.Service"
RUN dotnet build "TIM.Service.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "TIM.Service.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TIM.Service.dll"]