# Build context = server/verifier (Render rootDir). Self-contained: no files outside this folder.
#   docker build -t shorepop-verifier server/verifier
FROM mcr.microsoft.com/dotnet/sdk:8.0.413 AS build
WORKDIR /src
COPY Shorepop.Verifier.csproj packages.lock.json ./
RUN dotnet restore Shorepop.Verifier.csproj --locked-mode
COPY . .
RUN dotnet publish Shorepop.Verifier.csproj -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:8.0.31
WORKDIR /app
COPY --from=build /out .
# SQLite lives on the container disk: EPHEMERAL on Render free (resets every deploy/restart).
RUN mkdir -p /home/app/data && chown -R $APP_UID /home/app/data
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 VERIFIER_DB_PATH=/home/app/data/verifier.db
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shorepop.Verifier.dll"]
