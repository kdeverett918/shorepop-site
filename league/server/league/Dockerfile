# Build from repository root: docker build -f server/league/Dockerfile -t shorepop-league:review .
# PostgreSQL storage is configured with environment values; image filesystem holds no player state.
FROM mcr.microsoft.com/dotnet/sdk:8.0.413 AS build
WORKDIR /src
COPY Assets/Scripts/Core/ Assets/Scripts/Core/
COPY server/league/*.cs server/league/
COPY server/league/Shorepop.League.csproj server/league/packages.lock.json server/league/appsettings.json server/league/
RUN dotnet publish server/league/Shorepop.League.csproj -c Release -o /out -p:RestoreLockedMode=true
FROM mcr.microsoft.com/dotnet/aspnet:8.0.31
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shorepop.League.dll"]
