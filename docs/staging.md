# Staging without a domain (sslip.io)

**Staging only.** Use this to test MyTunnel over the internet on a single VM before you own a domain. The production setup (`deploy/docker-compose.yml`, `deploy/Caddyfile`, `docs/deployment.md`) is unchanged and still needs a real domain with wildcard DNS.

## What changes

| File | Role |
|---|---|
| `deploy/docker-compose.staging.yml` | Override layered on the production Compose file. It mounts `Caddyfile.staging` in place of `Caddyfile` (same mount target) and **requires** `ACME_CA`, which has no default. |
| `deploy/Caddyfile.staging` | Certificates for three **fixed** hostnames through HTTP-01 / TLS-ALPN-01, from exactly one explicit ACME issuer (`ACME_CA`). There is no DNS provider, no wildcard and no on-demand TLS. The proxy, body limit and log redaction are the same as production. |
| `deploy/.env.staging.example` | Template for `deploy/.env.staging`, which is gitignored. It starts on the Let's Encrypt **staging** CA. |

Unchanged from production:
* PostgreSQL stays on the internal `backend` network with no published port.
* The gateway is not published either. Only Caddy publishes 80 and 443.
* The gateway runs as `Production`, so it still refuses `AllowAnonymousTunnels` and requires `https`.
* Session cookies are still `__Host-`/Secure, and the CLI still refuses plain `http://` for remote gateways.

The tests in `MyTunnel.Tests/Gateway/StagingHostnameTests.cs` and the `deployment` CI job check these properties. The CI job checks:
* The merged configuration's ports, networks and Caddyfile mount.
* `caddy validate` on `Caddyfile.staging`.
* The adapted Caddy JSON, for both CA values: every staging hostname has exactly one issuer, it is the configured ACME directory, and there is no ZeroSSL.

## Hostnames

`PUBLIC_DOMAIN` is the VM's public IPv4 address written with dashes, under sslip.io. For example, for 203.0.113.10 use `203-0-113-10.sslip.io`. sslip.io (also reachable as nip.io) answers DNS for any name that embeds an IP with that IP.

| Name | Certificate | Served by |
|---|---|---|
| `203-0-113-10.sslip.io` | yes | gateway: dashboard, `/api`, `/tunnel`, `/health`, `/ready`. Also the CLI's gateway URL. |
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

* Caddy requests one certificate per name, three in total. The CA connects to the VM on port 80 (HTTP-01) or 443 (TLS-ALPN-01) from its own servers, so **ports 80 and 443 must be open to the internet while certificates are issued**. Certificates are kept in the `caddy_data` volume and renewed automatically.
* **sslip.io and nip.io are not on the Public Suffix List** (checked 2026-10-29). Let's Encrypt therefore counts every user of the service as one "registered domain".
  * The normal limit is 50 certificates per registered domain per 7 days.
  * The sslip.io operator states that Let's Encrypt raised their limit to 250,000 per week. That is their claim and has not been verified.
  * If the shared limit is reached, issuance fails until it refills. That is outside your control.
* Your own Let's Encrypt production limits:
  * 5 certificates per identical set of names per 7 days.
  * 5 failed validations per name per hour.

  Prove issuance on the staging CA first, and do not delete `caddy_data` (`down -v`) or keep recreating Caddy.
* sslip.io is a free third-party DNS service without an SLA. If its DNS is unavailable, the names do not resolve.
* The names are not secret. Anyone who knows the IP can guess them.
* Because sslip.io is not a public suffix, sites of other sslip.io users can set cookies scoped to `.sslip.io`. The dashboard session uses a `__Host-` cookie, which sibling hosts cannot overwrite. Apps you expose through a tunnel do not get this protection. This is acceptable for testing, not for real users.

### Which CA is used (and why there is no ZeroSSL fallback)

By default Caddy chooses its issuers itself: Let's Encrypt production, and, when an email is configured, ZeroSSL as a fallback. The global `acme_ca` option narrows that to one CA in current Caddy source (`caddyconfig/httpcaddyfile/tlsapp.go`, checked 2026-10-29). There is a catch, though. A policy that ends up with *no* issuers falls back at runtime to `DefaultIssuersProvisioned` (`modules/caddytls/automation.go`), which is Let's Encrypt **production**. Relying on that global option is therefore not a safe guarantee for a staging test.

`deploy/Caddyfile.staging` therefore sets the issuer explicitly in the site's `tls` block (`issuer acme { dir {$ACME_CA} ... }`). An automation policy with explicit issuers uses only those (`automation.go`: defaults apply only when the issuer list is empty).

* `ACME_CA` is required by the override (`${ACME_CA:?...}`). Forgetting it stops Compose with an error instead of silently choosing a CA.
* Retries: the ACME issuer's `test_dir` is used when retrying failed challenges. It defaults to the Let's Encrypt staging directory. Retries therefore stay on `ACME_CA` or Let's Encrypt staging and never go to ZeroSSL.
* CI evidence: the `deployment` job adapts `Caddyfile.staging` inside the built Caddy image with both the staging and the production URL. It fails unless every staging hostname is covered by a policy whose only issuer is `acme` with exactly that `ca`. This checks the configuration Caddy actually loads. Seeing which CA really issues the certificates still requires a live VM (see "Check the staging certificates" below).

### The CLI cannot use staging certificates

Let's Encrypt staging certificates chain to roots that are deliberately untrusted. The `mytunnel` CLI uses .NET's default certificate validation, for its WebSocket (`MyTunnel.Cli/Tunnel/TunnelClient.cs`) and its HTTP calls (`MyTunnel.Cli/CliApp.cs`). It has no option to skip validation. Against a staging certificate:
* `mytunnel login` fails.
* `mytunnel http` keeps reconnecting.

Browsers show a certificate warning. Test *issuance* on staging with the `openssl` checks below. Test the *CLI and dashboard* only after switching to production certificates.

## Safe first start

Run these on the VM from the repository root. They deploy the stack; run them only when you intend to. Complete the [Azure checklist](#azure-checklist) first. No command below prints a secret.

### 1. Environment file

```bash
cp deploy/.env.staging.example deploy/.env.staging
chmod 600 deploy/.env.staging
IP=<your static public IP, e.g. 20.1.2.3>
sed -i "s/^PUBLIC_DOMAIN=.*/PUBLIC_DOMAIN=${IP//./-}.sslip.io/" deploy/.env.staging
sed -i "s/^ACME_EMAIL=.*/ACME_EMAIL=<a real address you own>/" deploy/.env.staging
# Generated straight into the file and never displayed. Set it once: see "Database password" in the checklist.
sed -i "s/^POSTGRES_PASSWORD=.*/POSTGRES_PASSWORD=$(openssl rand -hex 32)/" deploy/.env.staging
D=$(grep -oP '^PUBLIC_DOMAIN=\K.*' deploy/.env.staging)   # reads only this value; do not source the whole file
C="docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.staging.yml --env-file deploy/.env.staging"
```

Always pass both `-f` files and `--env-file`, as `$C` does. Without the override, Caddy loads the production Caddyfile. That file asks Cloudflare for a wildcard certificate and fails with the placeholder token.

### 2. Check DNS and the effective configuration, before Caddy starts

```bash
getent hosts "$D" "demo.$D" "test.$D"                       # all three must print the VM's public IP
grep -c '^ACME_EMAIL=REPLACE' deploy/.env.staging           # must print 0
grep -c '^POSTGRES_PASSWORD=.\+' deploy/.env.staging        # must print 1 (set, value not shown)
$C --profile tools config --quiet                            # merged config is valid
$C config caddy | grep -B2 'target: /etc/caddy/Caddyfile'   # source must end in deploy/Caddyfile.staging
$C config caddy | grep 'ACME_CA'                             # must show acme-staging-v02.api.letsencrypt.org
$C config | grep 'published'                                 # only Caddy's 80 and 443 may appear
```

Do not continue unless every line matches.

### 3. Build, validate, start on the staging CA

```bash
$C --profile tools build
$C run --rm --no-deps caddy caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
$C up -d postgres
$C --profile tools run --rm migrate
$C up -d gateway caddy            # Caddy starts requesting certificates from ACME_CA (staging) now
$C logs -f caddy                  # wait for "certificate obtained successfully" for all three names
```

### 4. Check the staging certificates (TLS verification stays on)

```bash
for h in "$D" "demo.$D" "test.$D"; do
  echo | openssl s_client -connect "$h:443" -servername "$h" 2>/dev/null | openssl x509 -noout -subject -issuer -enddate
done
# Each subject must be that exact hostname, and each issuer must contain "(STAGING)".
curl -sS -o /dev/null -w '%{http_code}\n' "http://$D/"     # 3xx: port 80 reaches Caddy, which redirects to https
curl -sS -o /dev/null "https://$D/"                         # expected to FAIL certificate verification: staging is untrusted
```

`openssl x509` only prints the certificate and does not trust it. Do not use `curl -k` or any other option that disables verification.

## Switching to production certificates

Only do this once every check in step 4 passes.

```bash
sed -i 's|^ACME_CA=https://acme-staging-v02|# ACME_CA=https://acme-staging-v02|; s|^# ACME_CA=https://acme-v02|ACME_CA=https://acme-v02|' deploy/.env.staging
$C config caddy | grep 'ACME_CA'  # must now show acme-v02.api.letsencrypt.org (no "staging")
$C up -d caddy                    # recreates Caddy with the new environment (three production certificate orders)
$C logs -f caddy                  # wait for "certificate obtained successfully" for all three names
```

Use `up -d caddy`, not `docker compose restart`. `restart` reuses the existing container and its old environment, so Caddy would keep the staging CA.

Then check with full verification:

```bash
curl -fsS "https://$D/ready"                          # {"status":"Ready"}: trusted certificate, gateway and database up
curl -sS -o /dev/null "https://nope.$D/"              # expected to FAIL with a TLS error (no certificate, by design)
```

Now test the dashboard and the CLI:
1. Open `https://<PUBLIC_DOMAIN>/` and create your account.
2. Create a project and reserve `demo`.
3. On your own machine, run the CLI:

```powershell
mytunnel config set gateway https://<PUBLIC_DOMAIN>
mytunnel login
mytunnel http 5019 --subdomain demo
curl https://demo.<PUBLIC_DOMAIN>/
```

Until you have done this, it is unverified. CI validates the configuration and images but never obtains a certificate.

## If issuance fails or the hostname is wrong

Avoid repeated certificate requests:

1. Stop new attempts: `$C stop caddy`. Do not delete `caddy_data`, do not run `down -v`, and do not keep recreating Caddy. Each start makes fresh attempts.
2. Read the reason: `$C logs caddy | grep -iE 'error|challenge|rateLimited|urn:ietf:params:acme'`. Common causes:
   * DNS does not point at the VM.
   * Port 80 or 443 is closed in the NSG.
   * `ACME_EMAIL` is a placeholder.
   * A rate limit was hit.
3. DNS:
   * Run `getent hosts "demo.$D"` on the VM and resolve the same name from your own machine. Both must match the static IP shown in the Azure portal.
   * If they differ, fix `PUBLIC_DOMAIN` with the `sed` line from step 1, then run `$C up -d gateway caddy` (both read it).
   * Only the dashed form works.
4. Ports: from your own machine, `curl -sS -o /dev/null -w '%{http_code}\n' http://<PUBLIC_DOMAIN>/` must give a 3xx.
5. Retry on the **staging** CA: set `ACME_CA` back to the staging URL, run `$C up -d caddy`, and repeat step 4 of the first start. Return to production only after it passes.
6. Rate-limited: stop and wait for the limit to refill (see "Certificates and their limits"). The staging CA keeps working meanwhile.

## Azure checklist

The setup is one Ubuntu 24.04 x64 VM; see `docs/deployment.md` for sizing. Building the images needs about 4 GiB of RAM, or add swap.

| Item | Do this | Check |
|---|---|---|
| Resource group | Put **everything** in one dedicated group, e.g. `mytunnel-staging-rg`. | `az resource list -g mytunnel-staging-rg -o table` lists only staging resources |
| Static public IP | Use a Standard-SKU public IP with static allocation, attached to the VM's NIC. | The IP stays the same after the VM is stopped and deallocated |
| DNS | `PUBLIC_DOMAIN` is that IP in dashed form under sslip.io. | `getent hosts "$D" "demo.$D" "test.$D"` returns the IP, on the VM and from your machine |
| NSG inbound | Allow 22/tcp from **your IP only**. Allow 80/tcp and 443/tcp from **Any** (both are needed while certificates are issued). Allow nothing else. After issuance you may restrict 443 to your own IPs. Port 80 must stay open for renewals. | `az network nsg rule list -g mytunnel-staging-rg --nsg-name <nsg> -o table` |
| Private PostgreSQL and gateway | Add no NSG rule or `ports:` entry for 5432 or 8080. Compose publishes only Caddy's 80 and 443. | `$C config \| grep published` shows only 80 and 443. CI checks the merged configuration too. |
| Database password | Generate it into `deploy/.env.staging` **before the first start** (step 1). PostgreSQL applies `POSTGRES_PASSWORD` only when it creates the `pgdata` volume. Changing it later does not change the database password; the gateway and migrations then fail to log in. | `grep -c '^POSTGRES_PASSWORD=.\+' deploy/.env.staging` prints 1 |
| Persistent volumes | `pgdata` (database) and `caddy_data` / `caddy_config` (certificates, ACME account) are named Docker volumes on the VM's OS disk. They survive container recreation and `down` (without `-v`). They are lost with the disk, and nothing backs them up. | `docker volume ls` shows `mytunnel_pgdata`, `mytunnel_caddy_data`, `mytunnel_caddy_config` |
| Secrets | `deploy/.env.staging` stays on the VM with mode 600. It is gitignored; never commit or copy it elsewhere. | `stat -c %a deploy/.env.staging` prints 600 |
| Open registration | Anyone who can reach 443 can create an account. Keep the test window short, or restrict 443 to your IPs once certificates are issued. | |
| Cost | Set a budget alert. Note that **deallocating stops compute charges only**: the disk and the static IP are billed until deleted. | Cost Management |

### Cleanup

```bash
az group list -o table                                # find the staging group
az resource list -g mytunnel-staging-rg -o table      # check exactly what will be deleted
az group delete --name mytunnel-staging-rg --yes      # VM, OS disk (with all volumes), NIC, public IP, NSG, VNet
az group list -o table                                # confirm it is gone
```

* Also check for groups Azure created for you, such as `NetworkWatcherRG`, and anything you created outside the staging group.
* Check Cost Management a day later.
* On your machine, run `mytunnel logout` to remove the stored token.
* sslip.io needs no cleanup, and the certificates simply expire.

## Moving to a real domain

Stop using the override and `deploy/.env.staging`, then follow `docs/deployment.md` with `deploy/.env`. The production Caddyfile issues a new wildcard certificate. The staging certificates stay in `caddy_data` unused. Both setups use the Compose project `mytunnel`, so the database volume is shared. Accounts created during staging carry over, unless you remove the `pgdata` volume first. `POSTGRES_PASSWORD` in `deploy/.env` must then equal the one the volume was created with.
