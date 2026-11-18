# WalletPK Bill Payment service image. Build from the repository root:
#   docker build -f src/Services/Bills/WalletPK.Bills.Api/Dockerfile -t walletpk/bills .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Bills/ src/Services/Bills/

RUN dotnet restore src/Services/Bills/WalletPK.Bills.Api/WalletPK.Bills.Api.csproj
RUN dotnet publish src/Services/Bills/WalletPK.Bills.Api/WalletPK.Bills.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.Bills.Api.dll"]
