import { useState, type FormEvent } from "react";
import { api, ApiError } from "../api";
import { useAuth } from "../auth";
import { Card, Empty, ErrorBanner, Field, Loading, PageHeader, SuccessBanner, useAsync } from "../components/ui";
import { auditLabel, formatDate } from "../format";

export function AccountPage() {
  const { user } = useAuth();
  const audit = useAsync(api.audit);
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  async function changePassword(event: FormEvent) {
    event.preventDefault();
    setSaved(false);
    if (next.length < 10) {
      setError("The new password must be at least 10 characters.");
      return;
    }
    try {
      await api.changePassword(current, next);
      setCurrent("");
      setNext("");
      setError(null);
      setSaved(true);
      await audit.reload();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Could not change the password.");
    }
  }

  return (
    <>
      <PageHeader title="Account" />
      <Card title="Profile">
        <dl className="details">
          <dt>Email</dt><dd>{user?.email}</dd>
          <dt>Name</dt><dd>{user?.displayName ?? "—"}</dd>
          <dt>Member since</dt><dd>{formatDate(user?.createdAt ?? null)}</dd>
        </dl>
      </Card>
      <Card title="Change password">
        <form onSubmit={changePassword} className="stack narrow">
          {saved && <SuccessBanner>Password changed. Your other sessions were signed out.</SuccessBanner>}
          {error && <ErrorBanner message={error} />}
          <Field label="Current password">
            <input type="password" autoComplete="current-password" required value={current} onChange={(e) => setCurrent(e.target.value)} />
          </Field>
          <Field label="New password" hint="At least 10 characters.">
            <input type="password" autoComplete="new-password" required minLength={10} maxLength={256} value={next} onChange={(e) => setNext(e.target.value)} />
          </Field>
          <button className="button button-primary">Change password</button>
        </form>
      </Card>
      <Card title="Security activity">
        {audit.error ? <ErrorBanner message={audit.error} onRetry={audit.reload} /> : !audit.data && audit.loading ? <Loading /> : audit.data!.length === 0 ? (
          <Empty title="No activity yet" />
        ) : (
          <div className="table-wrap">
            <table>
              <thead>
                <tr><th scope="col">Event</th><th scope="col">Detail</th><th scope="col">IP address</th><th scope="col">When</th></tr>
              </thead>
              <tbody>
                {audit.data!.map((e, i) => (
                  <tr key={i}>
                    <td>{auditLabel(e.type)}</td>
                    <td>{e.detail ?? ""}</td>
                    <td>{e.ipAddress ?? ""}</td>
                    <td>{formatDate(e.createdAt)}</td>
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
