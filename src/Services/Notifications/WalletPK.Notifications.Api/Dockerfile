# WalletPK Notifications service image. Build from the repository root:
#   docker build -f src/Services/Notifications/WalletPK.Notifications.Api/Dockerfile -t walletpk/notifications .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Notifications/ src/Services/Notifications/

RUN dotnet restore src/Services/Notifications/WalletPK.Notifications.Api/WalletPK.Notifications.Api.csproj
RUN dotnet publish src/Services/Notifications/WalletPK.Notifications.Api/WalletPK.Notifications.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.Notifications.Api.dll"]
