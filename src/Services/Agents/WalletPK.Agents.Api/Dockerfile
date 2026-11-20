# WalletPK Agent Network service image. Build from the repository root:
#   docker build -f src/Services/Agents/WalletPK.Agents.Api/Dockerfile -t walletpk/agents .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/BuildingBlocks/ src/BuildingBlocks/
COPY src/Services/Agents/ src/Services/Agents/

RUN dotnet restore src/Services/Agents/WalletPK.Agents.Api/WalletPK.Agents.Api.csproj
RUN dotnet publish src/Services/Agents/WalletPK.Agents.Api/WalletPK.Agents.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

USER $APP_UID
ENTRYPOINT ["dotnet", "WalletPK.Agents.Api.dll"]
