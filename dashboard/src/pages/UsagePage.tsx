import { useState } from "react";
import { api, type Usage } from "../api";
import { RequestsChart } from "../components/RequestsChart";
import { Card, Empty, ErrorBanner, Loading, PageHeader, useAsync } from "../components/ui";
import { dailyTotals, formatBytes, formatNumber } from "../format";

export function summarize(rows: Usage[]) {
  const bySubdomain = new Map<string, { requests: number; errors: number; bytesIn: number; bytesOut: number }>();
  for (const r of rows) {
    const s = bySubdomain.get(r.subdomain) ?? { requests: 0, errors: 0, bytesIn: 0, bytesOut: 0 };
    s.requests += r.requests;
    s.errors += r.errors;
    s.bytesIn += r.bytesIn;
    s.bytesOut += r.bytesOut;
    bySubdomain.set(r.subdomain, s);
  }
  return [...bySubdomain.entries()].map(([subdomain, s]) => ({ subdomain, ...s })).sort((a, b) => b.requests - a.requests);
}

export function UsagePage() {
  const [days, setDays] = useState(30);
  const { data, error, loading, reload } = useAsync(() => api.usage(days), [days]);
  const rows = summarize(data ?? []);

  return (
    <>
      <PageHeader
        title="Usage"
        description="Requests served through your tunnels. Server errors are responses with status 500 or higher."
        actions={
          <label className="row">
            <span className="muted small">Range</span>
            <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
              <option value={7}>Last 7 days</option>
              <option value={30}>Last 30 days</option>
              <option value={90}>Last 90 days</option>
            </select>
          </label>
        }
      />
      {error ? <ErrorBanner message={error} onRetry={reload} /> : !data && loading ? <Loading /> : (
        <>
          <Card><RequestsChart points={dailyTotals(data ?? [], days)} /></Card>
          <Card title="By subdomain">
            {rows.length === 0 ? <Empty title="No traffic in this period" /> : (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th scope="col">Subdomain</th>
                      <th scope="col" className="num">Requests</th>
                      <th scope="col" className="num">Server errors</th>
                      <th scope="col" className="num">Received</th>
                      <th scope="col" className="num">Sent</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rows.map((r) => (
                      <tr key={r.subdomain}>
                        <td>{r.subdomain}</td>
                        <td className="num">{formatNumber(r.requests)}</td>
                        <td className="num">{formatNumber(r.errors)}</td>
                        <td className="num">{formatBytes(r.bytesIn)}</td>
                        <td className="num">{formatBytes(r.bytesOut)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>
        </>
      )}
    </>
  );
}
