# WalletPK Fraud & Risk service image. Build from the repository root:
#   docker build -f src/Services/Fraud/WalletPK.Fraud.Api/Dockerfile -t walletpk/fraud .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Fraud/ src/Services/Fraud/

RUN dotnet restore src/Services/Fraud/WalletPK.Fraud.Api/WalletPK.Fraud.Api.csproj
RUN dotnet publish src/Services/Fraud/WalletPK.Fraud.Api/WalletPK.Fraud.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
# 8080: REST (through the gateway). 8081: internal gRPC (HTTP/2), see docker-compose.yml.
EXPOSE 8080 8081

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.Fraud.Api.dll"]
