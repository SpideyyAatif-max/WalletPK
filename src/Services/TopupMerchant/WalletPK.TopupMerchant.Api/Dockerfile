# WalletPK Top-up & Merchant service image. Build from the repository root:
#   docker build -f src/Services/TopupMerchant/WalletPK.TopupMerchant.Api/Dockerfile -t walletpk/topup-merchant .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/TopupMerchant/ src/Services/TopupMerchant/

RUN dotnet restore src/Services/TopupMerchant/WalletPK.TopupMerchant.Api/WalletPK.TopupMerchant.Api.csproj
RUN dotnet publish src/Services/TopupMerchant/WalletPK.TopupMerchant.Api/WalletPK.TopupMerchant.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.TopupMerchant.Api.dll"]
