# Staging without a domain (sslip.io)

**Staging only.** Use this to test MyTunnel over the internet on a single VM before you own a domain. The production setup (`deploy/docker-compose.yml`, `deploy/Caddyfile`, `docs/deployment.md`) is unchanged and still needs a real domain with wildcard DNS.

## What changes

| File | Role |
|---|---|
| `deploy/docker-compose.staging.yml` | Override layered on the production Compose file. It only mounts `Caddyfile.staging` in place of `Caddyfile` (same mount target) and sets `ACME_CA`. |
| `deploy/Caddyfile.staging` | Certificates for three **fixed** hostnames through HTTP-01 / TLS-ALPN-01. There is no DNS provider, no wildcard and no on-demand TLS. The proxy, body limit and log redaction are the same as production. |
| `deploy/.env.staging.example` | Template for `deploy/.env.staging`, which is gitignored. |

Unchanged from production:
* PostgreSQL stays on the internal `backend` network with no published port.
* The gateway is not published either. Only Caddy publishes 80 and 443.
* The gateway runs as `Production`, so it still refuses `AllowAnonymousTunnels` and requires `https`.
* Session cookies are still `__Host-`/Secure, and the CLI still refuses plain `http://` for remote gateways.

The tests in `MyTunnel.Tests/Gateway/StagingHostnameTests.cs` and the `deployment` CI job check these properties. The CI job checks the merged configuration's ports, networks and Caddyfile mount, and runs `caddy validate` on `Caddyfile.staging`.

## Hostnames

`PUBLIC_DOMAIN` is the VM's public IPv4 address written with dashes, under sslip.io. For example, for 203.0.113.10 use `203-0-113-10.sslip.io`. sslip.io (also reachable as nip.io) answers DNS for any name that embeds an IP with that IP.

| Name | Certificate | Served by |
|---|---|---|
| `203-0-113-10.sslip.io` | yes | gateway: dashboard, `/api`, `/tunnel`, `/health`, `/ready` |
| `demo.203-0-113-10.sslip.io` | yes | the tunnel registered as `demo`, or a gateway `404 tunnel_not_found` if none is connected |
| `test.203-0-113-10.sslip.io` | yes | the tunnel registered as `test` (same as `demo`) |
| any other `<name>.203-0-113-10.sslip.io` | **no** | the TLS handshake fails. Caddy has no certificate for it and never requests one on demand. |

Consequences:
* Start tunnels with `--subdomain demo` or `--subdomain test`. A random subdomain (the default) registers fine, but its URL has no certificate and browsers cannot open it.
* `demo` and `test` are first come, first served like any unreserved subdomain. Reserve them in a dashboard project so nobody else can claim them while you are offline.
* Use the dashed form everywhere. `demo.203.0.113.10.sslip.io` resolves to the same IP, but it is a different name: it does not match `PUBLIC_DOMAIN` and has no certificate.
* Use a **static** public IP. On Azure, a dynamic IP can change when the VM is deallocated. The hostnames then change too, and new certificates must be issued.
* To add a fixed name, add it to the site line in `deploy/Caddyfile.staging` and to the expected list in `StagingHostnameTests`. It must be a valid subdomain (3–63 characters) and not in `Gateway:ReservedSubdomains`.

## Certificates and their limits

* On first start, Caddy requests one Let's Encrypt certificate per name, three in total. Let's Encrypt connects to the VM on port 80 (HTTP-01) or 443 (TLS-ALPN-01) from its own servers. **Port 80 must therefore be open to the internet**, even if you restrict 443 to your own IP. Caddy renews automatically and keeps certificates in the `caddy_data` volume.
* **sslip.io and nip.io are not on the Public Suffix List** (checked 2026-10-29). Let's Encrypt therefore counts every user of the service as one "registered domain".
  * The normal limit is 50 certificates per registered domain per 7 days.
  * The sslip.io operator states that Let's Encrypt raised their limit to 250,000 per week. That is their claim and I have not verified it.
  * If the shared limit is reached, issuance fails until it refills. That is outside your control.
* Your own limits:
  * 5 certificates per identical set of names per 7 days.
  * 5 failed validations per name per hour.

  So do not delete `caddy_data` (`down -v`) repeatedly.
* **Dry runs:** set `ACME_CA=https://acme-staging-v02.api.letsencrypt.org/directory` in `deploy/.env.staging`. Its limits are much higher, but the certificates are untrusted: browsers warn and the `mytunnel` CLI refuses to connect. Remove the line and restart Caddy to get real certificates. Assumption, not verified: an explicit `acme_ca` makes Caddy use only that CA, without its usual ZeroSSL fallback.
* sslip.io is a free third-party DNS service without an SLA. If its DNS is unavailable, the names do not resolve.
* The names are not secret. Anyone who knows the IP can guess them.
* Because sslip.io is not a public suffix, sites of other sslip.io users can set cookies scoped to `.sslip.io`. The dashboard session uses a `__Host-` cookie, which sibling hosts cannot overwrite. Apps you expose through a tunnel do not get this protection. This is acceptable for testing, not for real users.

## Commands

Run these on the VM from the repository root. These commands deploy the stack; run them only when you intend to.

```bash
cp deploy/.env.staging.example deploy/.env.staging
chmod 600 deploy/.env.staging
# Edit deploy/.env.staging: PUBLIC_DOMAIN=<your-ip-with-dashes>.sslip.io, ACME_EMAIL, POSTGRES_PASSWORD (openssl rand -hex 32).
# Keep CLOUDFLARE_API_TOKEN=unused-in-staging. The production file requires the variable; staging never reads it.

. deploy/.env.staging && nslookup "demo.$PUBLIC_DOMAIN"    # must return the VM's public IP

C="docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.staging.yml --env-file deploy/.env.staging"

# 1. Validate the merged configuration (no containers are started).
$C --profile tools config --quiet
$C config caddy | grep -B2 'target: /etc/caddy/Caddyfile'   # the source must end in deploy/Caddyfile.staging
$C config | grep -n 'published'                             # only Caddy's 80 and 443 may appear

# 2. Build the images, then validate the staging Caddyfile inside the Caddy image.
$C --profile tools build
$C run --rm --no-deps caddy caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile

# 3. Start: database, migrations, then the gateway and Caddy.
$C up -d postgres
$C run --rm migrate
$C up -d gateway caddy
$C logs -f caddy          # wait for "certificate obtained successfully" for all three names
```

Always pass both `-f` files. Without the override, Caddy loads the production Caddyfile. That file asks Cloudflare for a wildcard certificate and fails with the placeholder token.

## Checking it works

```bash
curl -fsS "https://$PUBLIC_DOMAIN/ready"              # {"status":"Ready"}
curl -sS -o /dev/null "https://nope.$PUBLIC_DOMAIN/"   # expected to FAIL with a TLS error (no certificate, by design)
```

1. Open `https://<PUBLIC_DOMAIN>/` and create your account.
2. Create a project and reserve `demo`.
3. On your own machine, run the CLI:

```powershell
mytunnel config set gateway https://<PUBLIC_DOMAIN>
mytunnel login
mytunnel http 5019 --subdomain demo
curl https://demo.<PUBLIC_DOMAIN>/
```

Until you have tested this, it is unverified. CI validates the configuration and the images but never obtains a real certificate.

## Moving to a real domain

Stop using the override and `deploy/.env.staging`, then follow `docs/deployment.md` with `deploy/.env`. The production Caddyfile issues a new wildcard certificate. The staging certificates stay in `caddy_data` unused. Both setups use the Compose project `mytunnel`, so the database volume is shared. Accounts created during staging carry over, unless you remove the `pgdata` volume first.
