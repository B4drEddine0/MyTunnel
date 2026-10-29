# Release checklist

Status as of 2026-10-29. ✅ = done and verified, ⚠️ = done but not verified in the target environment, ❌ = not done.

## Definition of done (from the project brief)

| # | Requirement | Status | Evidence / gap |
|---|---|---|---|
| 1 | A user can authenticate | ✅ | Register, login, logout, lockout and session revocation are covered by PostgreSQL integration tests. Browser login (`mytunnel login`) is tested end to end. |
| 2 | CLI connects to the deployed gateway using TLS | ⚠️ | The CLI derives `wss://` from `https://` and refuses non-loopback `http://`. No deployed gateway exists yet. |
| 3 | CLI registers a tunnel securely | ✅ | Token required. Revoked, expired and forged tokens are rejected (tests). |
| 4 | Gateway assigns or resolves a public URL | ✅ (local) / ⚠️ (public) | Random or reserved subdomains, Host routing (tests). Public DNS is not set up. |
| 5 | An external client can reach that URL | ❌ | Requires deployment (`docs/deployment.md` §7). |
| 6 | Routing through the correct WebSocket | ✅ | 50 concurrent out-of-order requests are correlated correctly. Responses for unknown request ids are dropped (tests). |
| 7–8 | CLI forwards to the local app; the real response returns | ✅ | End-to-end tests over real sockets. Also checked by hand with separate processes and curl. |
| 9 | Bodies, headers, paths and query strings | ✅ | Methods, JSON and form bodies, 6 MiB upload, 20 MiB download, chunked, cookies, gzip passthrough (tests). |
| 10 | Concurrent requests are safe | ✅ | Tests, plus per-stream flow control. |
| 11 | Disconnections and timeouts are predictable | ✅ | 502 on client disconnect, 504 on timeout, reconnect after gateway restart (tests and manual). |
| 12 | Users cannot claim or access others' tunnels | ✅ | Cross-user API isolation. Reserved subdomain protected while the owner is offline (tests). |
| 13 | Credentials can be revoked | ✅ | Revocation disconnects live tunnels and blocks reconnection (test). |
| 14 | Migrations and a clean install are documented | ✅ | The migration bundle was applied to an empty PostgreSQL 18 database and re-run idempotently. |
| 15 | Dashboard talks to the real backend | ✅ | Verified against a live gateway and PostgreSQL. Screenshots were taken in light and dark mode. |
| 16 | Automated tests pass | ✅ | 167 .NET tests (21 need PostgreSQL) and 9 dashboard tests, all passing locally on 2026-10-29. `.github/workflows/ci.yml` runs them on every push and pull request to `main`, including the PostgreSQL tests on Linux. It fails if any test is skipped. |
| 17 | Deployment guide followed and verified | ❌ | Docker was unavailable. No host, domain or DNS exists. |
| 18 | Known limitations documented | ✅ | `docs/architecture.md` |

## Before the first public deployment

- [ ] Choose a dedicated domain. Create the apex and wildcard DNS records.
- [x] Build the images (`docker compose ... --profile tools build`). Verified in CI only (run 40852273932, commit 8e03c0c): all three images build, the Compose file and Caddyfile validate, migrations apply to an empty PostgreSQL 18 database and re-run cleanly, and the gateway container answers `/ready`, `/health`, the dashboard, `401` on `/api/me`, and a registration. Not yet built on the target host.
- [ ] Run `migrate`, start the stack, and complete every step in `docs/deployment.md` §7 from an external network.
- [ ] Confirm Caddy obtained the wildcard certificate and that WebSocket `wss://<domain>/tunnel` works through Caddy.
- [ ] Confirm client IPs in tunnelled `X-Forwarded-For` are real visitor IPs (trusted-network setting).
- [ ] Schedule database backups and test a restore.
- [ ] Set up an external uptime check on `/ready`.

## Before opening public sign-ups

- [ ] Email verification and password reset (needs an email provider). **Not implemented.**
- [ ] Decide on open versus invite-only registration. Registration is currently open.
- [ ] Separate the tunnel domain from the dashboard domain and consider the Public Suffix List (`docs/deployment.md` §2).
- [ ] Terms of use and an abuse-report contact. Public tunnels will be used for phishing if unattended.
- [ ] Load test: tunnels per instance, and throughput per tunnel.
- [ ] Rotate the Cloudflare token to minimum scope. Keep `deploy/.env` at mode 600 on the host only.

## Dependency and secret hygiene (checked 2026-10-29, including the full Git history)

- `dotnet list package --vulnerable --include-transitive`: no known vulnerabilities.
- `npm audit`: 0 vulnerabilities.
- No real secrets are in the repository:
  * The test tokens are dummy strings.
  * `docker-compose.dev.yml` contains a development-only password, and its database is bound to loopback.
  * `deploy/.env.example` has empty values.
  * The gateway connection string belongs in user secrets or the environment.
