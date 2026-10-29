# MyTunnel

Expose a local HTTP application through a public URL, without opening inbound ports. The `mytunnel` CLI opens an outbound WebSocket to the gateway. The gateway routes requests for `https://<subdomain>.<your-domain>` through that connection to your local app and streams the response back.

> **Status:** feature-complete for local and single-machine use, with automated tests, but **not yet deployed**.
>
> The tunnel protocol, HTTP forwarding, CLI, accounts, access tokens, browser login, dashboard and PostgreSQL persistence work and are tested.
>
> These are **not done or not verified**:
> * A real public deployment with a domain, wildcard DNS and TLS. The configuration is written but untested.
> * Email verification and password reset.
>
> See [`docs/release-checklist.md`](docs/release-checklist.md).

## Repository layout

| Path | What it is |
|---|---|
| `MyTunnel.Contracts/` | Shared wire protocol: messages, validation, binary body frames, transport, flow control |
| `MyTunnel.ControlPlane/` | Accounts, sessions, access tokens, device login, projects, usage, audit (EF Core + PostgreSQL) |
| `MyTunnel.Gateway/` | The deployable: tunnel gateway, REST API (`/api`), dashboard hosting |
| `MyTunnel.Cli/` | The `mytunnel` command (packaged as a .NET tool) |
| `dashboard/` | React + TypeScript dashboard (Vite) |
| `MyTunnel.LocalServer/` | Sample local app for trying things out |
| `MyTunnel.Tests/` | Unit, protocol-abuse, end-to-end and PostgreSQL integration tests |
| `deploy/`, `Dockerfile` | Production stack: Caddy (TLS) → gateway → PostgreSQL |
| `docs/` | [protocol](docs/protocol.md), [architecture & threat model](docs/architecture.md), [deployment](docs/deployment.md), [release checklist](docs/release-checklist.md) |

## Prerequisites

* .NET SDK 10.0.302+
* Node.js 24+ (dashboard only)
* PostgreSQL 16+ (accounts, tokens and dashboard only; the tunnel itself runs without it)

## Quick start: tunnels only, no database

```powershell
dotnet run --project MyTunnel.Gateway --launch-profile http        # gateway on :5018 (anonymous tunnels allowed in Development)
dotnet run --project MyTunnel.LocalServer --launch-profile http    # sample app on :5019
dotnet run --project MyTunnel.Cli -- http 5019
```

```
Tunnel status    online
Forwarding       http://k3x9q2m7ab1c.localhost:5018 -> http://localhost:5019
```

Open that URL in Chrome, Edge or Firefox, or use curl. All three resolve `*.localhost` to your machine. This URL is local only; a public URL needs a deployed gateway (`docs/deployment.md`). Ctrl+C stops the tunnel. If the gateway restarts, the CLI reconnects with backoff and keeps its subdomain.

## Full local stack: accounts, dashboard, browser login

1. Get a PostgreSQL database. Either run `docker compose -f docker-compose.dev.yml up -d`, or use any local PostgreSQL with an empty database.
2. Give the gateway the connection string through user secrets, so it never lands in a file in the repo:

   ```powershell
   dotnet user-secrets --project MyTunnel.Gateway set "ConnectionStrings:MyTunnel" "Host=127.0.0.1;Port=5432;Database=mytunnel;Username=mytunnel;Password=<password>"
   ```

3. Start the gateway (it migrates the database on startup in Development) and the dashboard:

   ```powershell
   dotnet run --project MyTunnel.Gateway --launch-profile http
   cd dashboard; npm install; npm run dev       # http://localhost:5173, proxies /api to the gateway
   ```

4. Register at http://localhost:5173, then sign in from the CLI:

   ```powershell
   dotnet run --project MyTunnel.Cli -- login     # opens the browser; approve the code shown in the terminal
   dotnet run --project MyTunnel.Cli -- http 5019
   ```

In the dashboard you can see live tunnels, create and revoke access tokens, reserve subdomains in projects, and view usage and security activity.

## Installing the CLI

```powershell
dotnet pack MyTunnel.Cli -c Release -o ./nupkg
dotnet tool install --global MyTunnel.Cli --add-source ./nupkg     # then: mytunnel --help
```

This works from a local package. Nothing is published to nuget.org.

### CLI reference

```
mytunnel http <port> [--subdomain <name>] [--https] [--gateway <url>] [--token <token>] [--timeout <seconds>]
mytunnel login [--no-browser] | login --token <token> | login --paste
mytunnel logout
mytunnel status [--gateway <url>]          gateway health + whether the stored token is still valid
mytunnel config | config set gateway <url>
mytunnel version
```

* Gateway resolution order: `--gateway`, `MYTUNNEL_GATEWAY`, `config.json`, then `http://localhost:5018`.
* Token resolution order: `--token`, `MYTUNNEL_TOKEN` (for CI), then the stored credential (DPAPI-encrypted on Windows).
* The CLI refuses plain `http://` for any non-loopback gateway.

## Gateway configuration

Settings are under `Gateway:` (environment variables use the form `Gateway__Name`).

| Setting | Default | Meaning |
|---|---|---|
| `PublicDomain` | `localhost` | Tunnels are served at `<subdomain>.<PublicDomain>` |
| `PublicScheme` / `PublicPort` | `http` / unset | Builds public URLs. Must be `https` in Production. |
| `AllowAnonymousTunnels` | `false` (`true` in Development) | Startup fails if enabled outside Development |
| `StaticTokens` | empty | `[{UserId, TokenSha256}]` for tests or a gateway without a database |
| `RegistrationTimeout` / `ResponseHeadersTimeout` | 10 s / 30 s | |
| `MaxRequestBodyBytes` / `MaxResponseBodyBytes` | 10 MiB / 100 MiB | |
| `MaxConcurrentRequestsPerTunnel` / `RequestsPerMinutePerTunnel` | 64 / 1200 | |
| `MaxTunnelsPerUser` | 5 | |
| `TunnelConnectionsPerMinutePerIp` / `AuthRequestsPerMinutePerIp` | 30 / 10 | |
| `ReservedSubdomains` | www, api, app, admin, … | Never assignable |
| `TrustedProxies` / `TrustedNetworks` | empty | Proxies whose `X-Forwarded-*` headers are honoured |
| `DashboardPath` | wwwroot | Built dashboard to serve |

Other settings:

* `ConnectionStrings:MyTunnel` enables the control plane.
* `Database:MigrateOnStartup` is `true` in Development.
* `ControlPlane:*` covers session, token and device-code lifetimes, per-user limits and retention.

Gateway-host endpoints: `/health`, `/ready` (checks the database), `/tunnel`, `/api/*`, and the dashboard.

## Tests

```powershell
dotnet test MyTunnel.slnx                         # PostgreSQL tests are skipped (reported as skipped)
$env:MYTUNNEL_TEST_POSTGRES = "Host=127.0.0.1;Port=5432;Username=postgres;Password=..."
dotnet test MyTunnel.slnx                         # runs everything; each test uses a throwaway database
cd dashboard; npm test; npm run build
```

If the gateway or CLI is running from Visual Studio, their `.exe` files are locked. Stop them, or build elsewhere with `dotnet test --artifacts-path <dir>`.

Continuous integration (`.github/workflows/ci.yml`) runs on every push and pull request to `main`:

* .NET build and tests on Linux (with a PostgreSQL 18 service, so no test is skipped) and on Windows.
* Dashboard `npm ci`, tests, production build and `npm audit`.
* A deployment check: builds the Docker images, validates the Compose file and Caddyfile, migrates an empty database and smoke-tests the gateway container.
