# WalletPK mock providers image. Build from the repository root:
#   docker build -f src/MockProviders/WalletPK.MockProviders/Dockerfile -t walletpk/mock-providers .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/MockProviders/ src/MockProviders/

RUN dotnet restore src/MockProviders/WalletPK.MockProviders/WalletPK.MockProviders.csproj
RUN dotnet publish src/MockProviders/WalletPK.MockProviders/WalletPK.MockProviders.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.MockProviders.dll"]
