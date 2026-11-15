# WalletPK Payments service image. Build from the repository root:
#   docker build -f src/Services/Payments/WalletPK.Payments.Api/Dockerfile -t walletpk/payments .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Payments/ src/Services/Payments/

RUN dotnet restore src/Services/Payments/WalletPK.Payments.Api/WalletPK.Payments.Api.csproj
RUN dotnet publish src/Services/Payments/WalletPK.Payments.Api/WalletPK.Payments.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.Payments.Api.dll"]
