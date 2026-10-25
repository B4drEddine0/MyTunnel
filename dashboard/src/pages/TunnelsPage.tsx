import { useEffect, useState } from "react";
import { api, type Tunnel } from "../api";
import { Card, CopyButton, Empty, ErrorBanner, Loading, PageHeader, useAsync } from "../components/ui";
import { formatNumber, formatRelative } from "../format";

export function TunnelTable({ tunnels, onStop }: { tunnels: Tunnel[]; onStop?: (t: Tunnel) => void }) {
  if (tunnels.length === 0) {
    return (
      <Empty title="No tunnels are online">
        Start one with <code>mytunnel http 3000</code>. It appears here as soon as it connects.
      </Empty>
    );
  }

  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th scope="col">Public URL</th>
            <th scope="col">Connected</th>
            <th scope="col" className="num">Requests</th>
            <th scope="col" className="num">In flight</th>
            {onStop && <th scope="col"><span className="visually-hidden">Actions</span></th>}
          </tr>
        </thead>
        <tbody>
          {tunnels.map((t) => (
            <tr key={t.tunnelId}>
              <td>
                <a href={t.publicUrl} target="_blank" rel="noopener noreferrer">{t.publicUrl}</a>{" "}
                <CopyButton value={t.publicUrl} />
              </td>
              <td>{formatRelative(t.connectedAt)}</td>
              <td className="num">{formatNumber(t.requests)}</td>
              <td className="num">{t.inFlight}</td>
              {onStop && (
                <td className="actions">
                  <button className="button button-small button-danger" onClick={() => onStop(t)}>Stop</button>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function TunnelsPage() {
  const { data, error, loading, reload } = useAsync(api.tunnels);
  const [actionError, setActionError] = useState<string | null>(null);

  // Live view: refresh every 5 seconds while the page is open.
  useEffect(() => {
    const id = setInterval(() => void reload(), 5000);
    return () => clearInterval(id);
  }, [reload]);

  async function stop(tunnel: Tunnel) {
    if (!confirm(`Disconnect ${tunnel.publicUrl}? A running CLI will reconnect unless you also revoke its token.`)) return;
    try {
      await api.stopTunnel(tunnel.tunnelId);
      await reload();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : "Could not stop the tunnel.");
    }
  }

  return (
    <>
      <PageHeader title="Tunnels" description="Tunnels currently connected to the gateway. Refreshes every 5 seconds." />
      {actionError && <ErrorBanner message={actionError} />}
      <Card>
        {error ? <ErrorBanner message={error} onRetry={reload} /> : !data && loading ? <Loading /> : <TunnelTable tunnels={data ?? []} onStop={stop} />}
      </Card>
    </>
  );
}
