# Architecture and security notes

## Components

| Project | Role |
|---|---|
| `MyTunnel.Contracts` | Protocol messages, validation, binary body frames, the WebSocket transport (`TunnelSocket`), flow control and subdomain syntax. Shared by the CLI, gateway and control plane so these rules are defined once. |
| `MyTunnel.ControlPlane` | Domain model, EF Core `DbContext`, migrations, and services for accounts, sessions, access tokens, device login, projects and reservations, usage and audit. It has no HTTP code. |
| `MyTunnel.Gateway` | The one deployable. It runs the tunnel endpoint `/tunnel`, the in-memory `TunnelRegistry`, Host-based routing, the REST API under `/api`, and the built dashboard. |
| `MyTunnel.Cli` | The `mytunnel` .NET tool. `TunnelClient` keeps the tunnel registered with reconnect and forwards requests to the fixed local origin. |
| `dashboard/` | React + TypeScript (Vite) SPA, served by the gateway in production. |
| `MyTunnel.LocalServer` | Sample local application for manual testing. |
| `MyTunnel.Tests` | xUnit tests: unit, protocol abuse, end-to-end over real sockets, and PostgreSQL integration. |

This is a **modular monolith**. The control plane is a separate library, but it runs in the gateway process, which brings three benefits:

* The API reads live tunnel state directly from the registry.
* Revoking a token disconnects its tunnels immediately.
* There is no message broker or second service to operate.

Splitting the API out later only requires replacing those two in-process calls with a small internal channel.

Data path:

```
visitor ──HTTP──▶ gateway ─ TunnelRoutingMiddleware (Host → subdomain → TunnelSession)
                    │
                    └─ TunnelSession.ForwardAsync ── http_request + body frames ──▶ WebSocket
                                                                                       │
local app ◀──HTTP── TunnelClient (LocalRequestBuilder, fixed origin) ◀───────────────┘
          ──resp──▶ TunnelClient ── http_response + body frames ──▶ gateway ──▶ visitor
```

The gateway never opens a connection to the developer's machine. The original `/proxy` endpoint, which fetched `http://localhost:5019` from the gateway, was removed for that reason.

## Decisions

* **One connection, many requests.** Requests are multiplexed by `requestId` with per-stream credit windows (see `docs/protocol.md`). This avoids a WebSocket per request and head-of-line blocking.
* **JSON control messages and binary body frames.** Readable, evolvable control messages without Base64 overhead on bodies.
* **Streaming with bounded buffers.** At most one 256 KiB window per direction per in-flight request. Total size is capped by `MaxRequestBodyBytes` and `MaxResponseBodyBytes`.
* **Authentication inside `register`.** The original unauthenticated `ping`/`pong` keeps working. Unregistered sockets can only ping, are closed after 10 s, and are rate-limited per IP.
* **In-memory registry, single instance.** A disconnect or restart cannot leave stale "active" tunnels. The CLI re-registers the same subdomain on reconnect. Several gateway instances would need shared routing; that is out of scope until there is a measured need.
* **Same-user takeover.** Re-registering a subdomain you already hold replaces the old (likely dead) connection. Another user gets `subdomain_unavailable`.
* **Host rewritten to the local origin.** Dev servers reject unknown Hosts. The public host goes in `X-Forwarded-Host`.
* **Server-side sessions.** The cookie names a `user_sessions` row that is checked on every request. Logout, password change and expiry take effect immediately. A stolen cookie stops working once the session is revoked.
* **Access tokens are `mt_<10-char id>_<43-char secret>`.** The id is stored for lookup. The whole token is stored only as SHA-256, compared in constant time. A fast hash is appropriate for 256-bit random secrets; passwords use ASP.NET Core Identity's PBKDF2 hasher.
* **Browser login uses the RFC 8628 shape.** The CLI holds a secret device code, and the user approves a short code while signed in. The token is minted when the CLI collects it, claimed atomically, so it is issued exactly once and never stored in plaintext.
* **Usage is aggregated in memory.** It is flushed to `usage_daily` every 30 s and at shutdown, as one upsert per (user, day, subdomain). There is no per-request table. A crash can lose up to 30 s of counters, which is acceptable for usage telemetry and documented.

## Data model (PostgreSQL)

`users`, `user_sessions`, `access_tokens`, `device_authorizations`, `projects`, `reserved_subdomains`, `usage_daily`, `audit_events`, `data_protection_keys`.

* Unique: email, token prefix, reserved subdomain, (user, project name), device user code, device code hash.
* Deleting a user cascades to everything they own. Audit rows keep the event with `user_id` set to NULL.
* Retention (hourly job): usage 400 days, audit 365 days, device requests 1 day after expiry, sessions 30 days after expiry.
* Migrations: `MyTunnel.ControlPlane/Data/Migrations`. They are applied automatically only in Development (`Database:MigrateOnStartup`). In production, run them as an explicit step (`docs/deployment.md`).

## Trust boundaries and threat model

| Boundary | Threats | Controls in place (tested unless noted) |
|---|---|---|
| Internet → tunnel hosts | Host spoofing, open proxy, abuse, resource exhaustion | Only registered subdomains of `PublicDomain` are routed. Tunnel hosts never fall through to gateway endpoints (`/tunnel`, `/api`). Unknown → 404. Size limits → 413 / abort. Per-tunnel concurrency → 429. Per-tunnel request rate → 429. Header timeout → 504. Incoming `X-Forwarded-*`/`Forwarded` are stripped and replaced. Proxy headers are honoured only from `TrustedProxies`/`TrustedNetworks`. |
| CLI → `/tunnel` | Unauthenticated or forged registration, subdomain hijack, malformed or oversized frames, floods | Token required (anonymous only in Development, enforced at startup). Revoked, expired and forged tokens are rejected. Reserved subdomains are owner-only, even while the owner is offline. Cross-user claims are rejected. Per-user tunnel cap. Strict validation and size limits. Flow-control enforcement. Registration timeout. Per-IP connection rate limit. |
| Browser → API | CSRF, session theft, credential stuffing, IDOR | `SameSite=Strict`, `HttpOnly`, `__Host-` cookie in production. A custom-header CSRF check on every unsafe method. Server-side session revocation. Per-IP auth rate limit plus per-account lockout (10 failures / 15 min). Constant-work login for unknown emails. Every query is scoped to the caller's user id; cross-user requests get 404. |
| Dashboard rendering | XSS, clickjacking | React escaping (no `dangerouslySetInnerHTML`). CSP `script-src 'self'`, `frame-ancestors 'none'`, `nosniff`, `Referrer-Policy`. No secrets in browser storage. The new token secret is held only in component state. |
| Gateway → CLI | Malicious gateway steering the CLI (SSRF) | Origin-form targets only. The URL is rebuilt against the fixed `localhost` origin and the authority is re-verified. No redirects, proxy, cookies or decompression. |
| Credentials at rest | Theft from disk, DB or logs | CLI: DPAPI (Windows) or an owner-only file. DB: SHA-256 verifiers and PBKDF2 password hashes. Tokens are redacted from `ToString`. Logs never include tokens, bodies or query strings. Audit details are short labels only. |

## Known limitations

* **No email verification or password reset.** There is no email provider yet. Accounts are verified only by the operator's trust in sign-ups. Before opening public registration, add an email sender and the verification and reset flows.
* **Open registration.** Anyone who can reach the dashboard can create an account.
* Visitor WebSocket upgrades through tunnels (for example Vite HMR) are not supported and return 501. HTTP trailers are not forwarded.
* No idle timeout on a response body that has started streaming; this only affects the owning tunnel's concurrency slots.
* Single gateway instance (see Decisions).
* Tunnel subdomains are siblings of the dashboard host. See the domain and Public Suffix List recommendation in `docs/deployment.md`.
* Usage counters can lose up to 30 s on a crash.
* The deployment stack (Docker, Caddy, DNS, TLS) has not been exercised on a real host.
