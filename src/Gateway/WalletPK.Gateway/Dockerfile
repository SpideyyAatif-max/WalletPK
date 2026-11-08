# WalletPK API Gateway image. Build from the repository root:
#   docker build -f src/Gateway/WalletPK.Gateway/Dockerfile -t walletpk/gateway .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Shared build settings first, then only the projects the gateway needs.
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Gateway/ src/Gateway/

RUN dotnet restore src/Gateway/WalletPK.Gateway/WalletPK.Gateway.csproj
RUN dotnet publish src/Gateway/WalletPK.Gateway/WalletPK.Gateway.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# Run as the non-root user that ships with the .NET images.
USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.Gateway.dll"]
