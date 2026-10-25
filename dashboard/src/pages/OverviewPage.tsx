import { Link } from "react-router-dom";
import { api } from "../api";
import { useAuth } from "../auth";
import { Card, ErrorBanner, Loading, PageHeader, useAsync } from "../components/ui";
import { RequestsChart } from "../components/RequestsChart";
import { TunnelTable } from "./TunnelsPage";
import { dailyTotals, formatNumber } from "../format";

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="stat">
      <span className="stat-label">{label}</span>
      <span className="stat-value">{value}</span>
    </div>
  );
}

export function OverviewPage() {
  const { user } = useAuth();
  const { data, error, loading, reload } = useAsync(() => Promise.all([api.tunnels(), api.usage(14)]));

  const [tunnels, usage] = data ?? [[], []];
  const total = usage.reduce((sum, u) => sum + u.requests, 0);
  const errors = usage.reduce((sum, u) => sum + u.errors, 0);
  const today = new Date().toISOString().slice(0, 10);
  const todayRequests = usage.filter((u) => u.day === today).reduce((sum, u) => sum + u.requests, 0);

  return (
    <>
      <PageHeader title={`Welcome${user?.displayName ? `, ${user.displayName}` : ""}`} description="Your tunnels at a glance." />
      {error ? <ErrorBanner message={error} onRetry={reload} /> : !data && loading ? <Loading /> : (
        <>
          <div className="stats">
            <Stat label="Tunnels online" value={formatNumber(tunnels.length)} />
            <Stat label="Requests today" value={formatNumber(todayRequests)} />
            <Stat label="Requests, 14 days" value={formatNumber(total)} />
            <Stat label="Server errors, 14 days" value={formatNumber(errors)} />
          </div>
          <Card title="Online tunnels" actions={<Link to="/tunnels">Manage</Link>}>
            <TunnelTable tunnels={tunnels} />
          </Card>
          <Card title="Requests per day, last 14 days" actions={<Link to="/usage">Details</Link>}>
            <RequestsChart points={dailyTotals(usage, 14)} title="Requests per day, last 14 days" hideTitle />
          </Card>
          <Card title="Get started">
            <ol className="steps">
              <li>Sign in from the CLI: <code>mytunnel login</code></li>
              <li>Start your app, then expose it: <code>mytunnel http 3000</code></li>
              <li>Reserve a stable subdomain under <Link to="/projects">Projects</Link>.</li>
            </ol>
          </Card>
        </>
      )}
    </>
  );
}
