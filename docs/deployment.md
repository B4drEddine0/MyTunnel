# Deploying MyTunnel

> **No domain yet?** Test on a single VM first with [`docs/staging.md`](staging.md). It uses sslip.io hostnames and HTTP-01 certificates for fixed names, and leaves this production setup unchanged.
>
> **Status: not yet verified on a real host.** CI builds the images, validates the Compose files and Caddyfiles, migrates an empty database and smoke-tests the gateway container. Nothing has run on a real host with DNS and TLS. These parts **were** also verified locally:
>
> * The gateway's Release publish.
> * The EF migration bundle, applied to a brand-new PostgreSQL 18 database. It is idempotent on re-run.
> * The full stack (PostgreSQL, gateway, dashboard, CLI) running as local processes.
>
> Treat the first deployment as a test: follow the [verification steps](#7-verify-the-deployment) before announcing the service.

## Topology

```
Internet ──443──▶ Caddy (TLS: apex + *.domain wildcard cert)
                    │  reverse_proxy (HTTP + WebSocket)
                    ▼
                 gateway:8080  (tunnels, API, dashboard)  ──▶  postgres:5432 (internal network only)
                    ▲
developer CLI ──wss://domain/tunnel (outbound only)
```

* `https://<domain>/` serves the dashboard and API. `wss://<domain>/tunnel` accepts CLI connections.
* `https://<sub>.<domain>/` serves tunnels. The gateway routes by Host header.
* Only Caddy publishes ports (80, 443). The gateway and PostgreSQL are not reachable from outside.

## 1. Host

A small VPS is enough to start: 1–2 vCPU and 2 GB RAM (for example a Hetzner CX22 or a DigitalOcean/Lightsail 2 GB instance, roughly 5–12 USD per month). The gateway is I/O-bound. Each tunnel needs one WebSocket, and each in-flight request buffers at most about 256 KiB per direction.

Install Docker Engine and the Compose plugin. Firewall:

```bash
ufw default deny incoming
ufw allow 22/tcp      # restrict to your IP if possible
ufw allow 80/tcp      # ACME HTTP challenge + redirect to HTTPS
ufw allow 443/tcp
ufw enable
```

## 2. Domain and DNS

Pick a domain dedicated to tunnels, written as `tunnel.example.com` below. Create:

| Record | Name | Value |
|---|---|---|
| A (and AAAA if IPv6) | `tunnel.example.com` | VPS IP |
| A (and AAAA) | `*.tunnel.example.com` | VPS IP |

**Security recommendation:** tunnels serve arbitrary third-party content on sibling subdomains of the dashboard. Before opening sign-ups to the public:

* Serve tunnels from a registrable domain separate from anything else you run.
* Consider submitting it to the [Public Suffix List](https://publicsuffix.org/), as ngrok did for `ngrok-free.app`, so browsers isolate cookies between tunnels.

Session cookies already use the `__Host-` prefix, so tunnels cannot read or overwrite them.

## 3. TLS certificates

A wildcard certificate requires the ACME **DNS-01** challenge. `deploy/caddy.Dockerfile` builds Caddy with the Cloudflare DNS plugin, and the token needs **Zone → DNS → Edit** on the zone only. For another DNS provider, replace the plugin (`github.com/caddy-dns/<provider>`) and the `dns` line in `deploy/Caddyfile`.

Caddy obtains and renews certificates automatically and stores them in the `caddy_data` volume. Back that volume up so a rebuild doesn't hit Let's Encrypt rate limits.

## 4. Configure

```bash
git clone <repo> mytunnel && cd mytunnel
cp deploy/.env.example deploy/.env
chmod 600 deploy/.env
# edit deploy/.env: PUBLIC_DOMAIN, ACME_EMAIL, CLOUDFLARE_API_TOKEN, POSTGRES_PASSWORD (openssl rand -hex 32)
```

Gateway settings use environment variables (`Gateway__Name`). See the README table for all settings. Production-relevant ones:

| Variable | Value |
|---|---|
| `ConnectionStrings__MyTunnel` | set by compose from `POSTGRES_PASSWORD` |
| `Gateway__PublicDomain` | your domain |
| `Gateway__PublicScheme` | `https` (startup fails in Production otherwise) |
| `Gateway__TrustedNetworks__0` | the Caddy network (`172.30.0.0/24` in compose) |
| `Database__MigrateOnStartup` | `false`; migrations are an explicit step |
| `Gateway__AllowAnonymousTunnels` | must stay unset (startup fails outside Development) |

Data Protection keys (which sign session cookies) are stored in PostgreSQL. Sessions therefore survive restarts.

## 5. Build, migrate, start

```bash
C="docker compose -f deploy/docker-compose.yml --env-file deploy/.env"
$C --profile tools build   # include the migrate image (it is in the "tools" profile)
$C up -d postgres
$C run --rm migrate          # applies EF Core migrations (idempotent)
$C up -d gateway caddy
$C logs -f gateway caddy
```

## 6. Upgrades and rollback

1. Back up the database first (section 8).
2. Run `git pull`, then `$C --profile tools build`. Without `--profile tools` the migrations image is not rebuilt, and the next step would apply the previous release's migrations.
3. Run `$C run --rm migrate`. Migrations are additive. Review each new migration for destructive operations before running it in production.
4. Run `$C up -d gateway`. Connected CLIs reconnect automatically with backoff and keep their subdomains.

Rollback: check out the previous tag, rebuild, and run `$C up -d gateway`. If the release included a migration that the old version cannot run against, restore the pre-upgrade backup.

## 7. Verify the deployment

```bash
curl -fsS https://tunnel.example.com/health     # {"status":"Healthy",...}
curl -fsS https://tunnel.example.com/ready      # {"status":"Ready"} (checks DB)
curl -sI https://nothing.tunnel.example.com/   # 404, X-MyTunnel-Error: tunnel_not_found
```

Then, from a different network than the server:

1. Register in the dashboard at `https://tunnel.example.com/`.
2. Run `mytunnel config set gateway https://tunnel.example.com`, then `mytunnel login`.
3. Start something locally and run `mytunnel http 3000`.
4. Open the printed `https://<sub>.tunnel.example.com` URL from another device, for example a phone on mobile data.
5. Revoke the token in the dashboard. The tunnel should drop and refuse to reconnect.

Only after these steps succeed can the tunnel be called publicly usable.

## 8. Backups

```bash
$C exec -T postgres pg_dump -U mytunnel -Fc mytunnel > backup-$(date +%F).dump    # daily via cron; keep 14+
$C exec -T postgres pg_restore -U mytunnel -d mytunnel --clean < backup.dump     # restore
```

Copy backups off the host. Test a restore at least once.

## 9. Logs and monitoring

* Both containers log to stdout (`docker compose logs`). The Caddyfile drops URIs and headers from access logs, and the gateway never logs tokens, request bodies or query strings.
* External uptime check: `GET /ready` every minute.
* Metrics: the `MyTunnel.Gateway` meter (`mytunnel.requests`, `mytunnel.request.duration`, `mytunnel.registrations`, `mytunnel.tunnels.active`). Add an OpenTelemetry exporter when you have a metrics backend. Until then, run `dotnet-counters` inside the container for ad-hoc inspection.
* Retention runs hourly inside the gateway: usage 400 days, audit 365 days, expired device codes and old sessions are purged.

## 10. Troubleshooting

| Symptom | Check |
|---|---|
| CLI: `Refusing to connect to a remote gateway over unencrypted http://` | Use `https://` for the gateway URL. |
| CLI loops on "Reconnecting" | `$C logs caddy gateway`. Verify 443 is reachable and DNS points to the VPS. |
| Visitors get 404 `tunnel_not_found` | The CLI isn't connected, or the subdomain differs. Check the dashboard's Tunnels page. |
| Visitors get 502 `local_unreachable` | The developer's local app isn't running on the forwarded port. |
| Certificate errors | Check the Cloudflare token scope and that the wildcard DNS record exists. Caddy logs ACME details. |
| `/ready` returns 503 `DatabaseUnavailable` | Check that PostgreSQL is healthy (`$C ps`) and that the password in `.env` matches the volume's original password. |
| Client IPs appear as 172.30.0.x | `Gateway__TrustedNetworks__0` must match the Caddy network. |
