# Gateway + control-plane API + dashboard in one image.
#   docker build -t mytunnel-gateway .                 (runtime image)
#   docker build --target migrations -t mytunnel-migrate .  (one-shot schema migration)

# ---- Dashboard -------------------------------------------------------------------------------
FROM node:24-alpine AS dashboard
WORKDIR /src/dashboard
COPY dashboard/package.json dashboard/package-lock.json ./
RUN npm ci
COPY dashboard/ ./
RUN npm run build

# ---- .NET build ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY MyTunnel.slnx ./
COPY MyTunnel.Contracts/MyTunnel.Contracts.csproj MyTunnel.Contracts/
COPY MyTunnel.ControlPlane/MyTunnel.ControlPlane.csproj MyTunnel.ControlPlane/
COPY MyTunnel.Gateway/MyTunnel.Gateway.csproj MyTunnel.Gateway/
RUN dotnet restore MyTunnel.Gateway/MyTunnel.Gateway.csproj
COPY MyTunnel.Contracts/ MyTunnel.Contracts/
COPY MyTunnel.ControlPlane/ MyTunnel.ControlPlane/
COPY MyTunnel.Gateway/ MyTunnel.Gateway/
RUN dotnet publish MyTunnel.Gateway/MyTunnel.Gateway.csproj -c Release -o /app --no-restore

# Self-contained migration executable (no SDK needed where it runs), for the platform being built.
FROM build AS bundle
ARG TARGETARCH
RUN dotnet tool install --global dotnet-ef --version 10.0.14 \
 && rid="linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)" \
 && /root/.dotnet/tools/dotnet-ef migrations bundle \
      --project MyTunnel.ControlPlane --startup-project MyTunnel.Gateway \
      --self-contained -r "$rid" -o /efbundle --configuration Release

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS migrations
COPY --from=bundle /efbundle /efbundle
USER app
# Reads ConnectionStrings__MyTunnel from the environment (see DesignTimeDbContextFactory).
ENTRYPOINT ["/efbundle"]

# ---- Runtime ---------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
COPY --from=dashboard /src/dashboard/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
EXPOSE 8080
USER app
# No HEALTHCHECK: the image has no HTTP client. Caddy actively probes /ready (see deploy/Caddyfile).
ENTRYPOINT ["dotnet", "MyTunnel.Gateway.dll"]
