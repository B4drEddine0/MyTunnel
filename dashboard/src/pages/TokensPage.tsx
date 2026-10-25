import { useState, type FormEvent } from "react";
import { api, ApiError, type CreatedToken, type Token } from "../api";
import { Card, CopyButton, Empty, ErrorBanner, Field, Loading, PageHeader, useAsync } from "../components/ui";
import { formatDate, formatRelative } from "../format";

export function TokensPage() {
  const { data, error, loading, reload } = useAsync(api.tokens);
  const [name, setName] = useState("");
  const [expiry, setExpiry] = useState("90");
  const [formError, setFormError] = useState<string | null>(null);
  const [created, setCreated] = useState<CreatedToken | null>(null);
  const [busy, setBusy] = useState(false);

  async function create(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setFormError(null);
    try {
      setCreated(await api.createToken(name.trim(), expiry === "never" ? null : Number(expiry)));
      setName("");
      await reload();
    } catch (e) {
      setFormError(e instanceof ApiError ? e.message : "Could not create the token.");
    } finally {
      setBusy(false);
    }
  }

  async function revoke(token: Token) {
    if (!confirm(`Revoke "${token.name}"? Tunnels using it disconnect immediately.`)) return;
    try {
      await api.revokeToken(token.id);
      await reload();
    } catch (e) {
      setFormError(e instanceof Error ? e.message : "Could not revoke the token.");
    }
  }

  return (
    <>
      <PageHeader title="Access tokens" description="Tokens let the CLI and automation open tunnels for your account. 'mytunnel login' creates one for you." />

      {created && (
        <div className="banner banner-success secret-banner" role="status">
          <div>
            <strong>Copy your new token now.</strong> It will not be shown again.
            <pre className="secret"><code>{created.secret}</code></pre>
            <span className="muted small">Use it with <code>mytunnel login --token &lt;token&gt;</code> or the <code>MYTUNNEL_TOKEN</code> environment variable.</span>
          </div>
          <div className="row">
            <CopyButton value={created.secret} />
            <button className="button button-small" onClick={() => setCreated(null)}>Done</button>
          </div>
        </div>
      )}

      <Card title="Create a token">
        <form onSubmit={create} className="inline-form">
          <Field label="Name" error={formError ?? undefined}>
            <input required maxLength={100} placeholder="e.g. laptop, ci-runner" value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label="Expires">
            <select value={expiry} onChange={(e) => setExpiry(e.target.value)}>
              <option value="30">in 30 days</option>
              <option value="90">in 90 days</option>
              <option value="366">in 1 year</option>
              <option value="never">never</option>
            </select>
          </Field>
          <button className="button button-primary" disabled={busy || !name.trim()}>Create token</button>
        </form>
      </Card>

      <Card title="Your tokens">
        {error ? <ErrorBanner message={error} onRetry={reload} /> : !data && loading ? <Loading /> : data!.length === 0 ? (
          <Empty title="No tokens yet">Create one above, or run <code>mytunnel login</code>.</Empty>
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th scope="col">Name</th>
                  <th scope="col">Prefix</th>
                  <th scope="col">Created</th>
                  <th scope="col">Last used</th>
                  <th scope="col">Expires</th>
                  <th scope="col">Status</th>
                  <th scope="col"><span className="visually-hidden">Actions</span></th>
                </tr>
              </thead>
              <tbody>
                {data!.map((t) => (
                  <tr key={t.id} className={t.active ? undefined : "inactive"}>
                    <td>{t.name}</td>
                    <td><code>{t.prefix}…</code></td>
                    <td>{formatDate(t.createdAt)}</td>
                    <td>{formatRelative(t.lastUsedAt)}</td>
                    <td>{t.expiresAt ? formatDate(t.expiresAt) : "never"}</td>
                    <td>{t.revokedAt ? "Revoked" : t.active ? "Active" : "Expired"}</td>
                    <td className="actions">
                      {t.active && <button className="button button-small button-danger" onClick={() => revoke(t)}>Revoke</button>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>
    </>
  );
}
