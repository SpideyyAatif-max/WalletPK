# WalletPK Identity service image. Build from the repository root:
#   docker build -f src/Services/Identity/WalletPK.Identity.Api/Dockerfile -t walletpk/identity .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Identity/ src/Services/Identity/

RUN dotnet restore src/Services/Identity/WalletPK.Identity.Api/WalletPK.Identity.Api.csproj
RUN dotnet publish src/Services/Identity/WalletPK.Identity.Api/WalletPK.Identity.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
# 8080: REST (through the gateway). 8081: internal gRPC (HTTP/2), see docker-compose.yml.
EXPOSE 8080 8081

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.Identity.Api.dll"]
