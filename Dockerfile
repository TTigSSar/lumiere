# Lumière receptionist API — demo image (ADR-008).
# Build context: repo root (see .dockerignore — only src/ is sent).

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /build

# Restore first so the package layer is cached across source-only changes.
COPY src/Lumiere.Api/Lumiere.Api.csproj src/Lumiere.Api/
RUN dotnet restore src/Lumiere.Api/Lumiere.Api.csproj

COPY src/ src/
RUN dotnet publish src/Lumiere.Api/Lumiere.Api.csproj \
        -c Release -o /app --no-restore /p:UseAppHost=false

# ---- runtime ----
# Debian bookworm-slim based. The upstream runtime-deps layer already installs tzdata
# (Europe/Zurich, ADR-009) and libicu, and creates the non-root user "app" (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1 \
    DATA_DIR=/data

WORKDIR /app
COPY --from=build /app ./

# Fail the build if the salon time zone is missing from the image.
RUN test -f /usr/share/zoneinfo/Europe/Zurich \
    && mkdir -p /data && chown app:app /data

USER app
EXPOSE 8080
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "Lumiere.Api.dll"]
