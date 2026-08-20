FROM mcr.microsoft.com/dotnet/sdk:10.0.100-noble AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/AzureBusService.Contracts/AzureBusService.Contracts.csproj src/AzureBusService.Contracts/packages.lock.json src/AzureBusService.Contracts/
COPY src/AzureBusService.Persistence/AzureBusService.Persistence.csproj src/AzureBusService.Persistence/packages.lock.json src/AzureBusService.Persistence/
COPY src/AzureBusService.Api/AzureBusService.Api.csproj src/AzureBusService.Api/packages.lock.json src/AzureBusService.Api/
RUN dotnet restore src/AzureBusService.Api/AzureBusService.Api.csproj --locked-mode

COPY src/ src/
RUN dotnet publish src/AzureBusService.Api/AzureBusService.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble-chiseled-extra AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AzureBusService.Api.dll"]
