export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

export function formatNumber(value: number): string {
  return new Intl.NumberFormat().format(value);
}

export function formatDate(iso: string | null): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

export function formatRelative(iso: string | null, now: Date = new Date()): string {
  if (!iso) return "never";
  const seconds = Math.round((now.getTime() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "just now";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `${hours} h ago`;
  return `${Math.round(hours / 24)} days ago`;
}

/** Sums usage rows per day, filling missing days with zero so the time axis is continuous. */
export function dailyTotals(rows: { day: string; requests: number }[], days: number, today: Date = new Date()): { day: string; requests: number }[] {
  const totals = new Map<string, number>();
  for (const row of rows) totals.set(row.day, (totals.get(row.day) ?? 0) + row.requests);
  const result: { day: string; requests: number }[] = [];
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate() - i));
    const key = d.toISOString().slice(0, 10);
    result.push({ day: key, requests: totals.get(key) ?? 0 });
  }
  return result;
}

const auditLabels: Record<string, string> = {
  "user.registered": "Account created",
  "user.login_succeeded": "Signed in",
  "user.login_failed": "Failed sign-in",
  "user.logout": "Signed out",
  "user.password_changed": "Password changed",
  "token.created": "Access token created",
  "token.revoked": "Access token revoked",
  "device.approved": "CLI login approved",
  "device.denied": "CLI login denied",
  "project.created": "Project created",
  "project.deleted": "Project deleted",
  "subdomain.reserved": "Subdomain reserved",
  "subdomain.released": "Subdomain released",
  "tunnel.stopped": "Tunnel stopped",
};

export function auditLabel(type: string): string {
  return auditLabels[type] ?? type;
}
