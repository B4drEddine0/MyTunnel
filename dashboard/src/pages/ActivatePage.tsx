import { useState, type FormEvent } from "react";
import { useSearchParams } from "react-router-dom";
import { api, ApiError, type DeviceRequest } from "../api";
import { Card, ErrorBanner, Field, PageHeader, SuccessBanner } from "../components/ui";
import { formatDate } from "../format";

/** Approves a `mytunnel login` request after the user compares the code shown in their terminal. */
export function ActivatePage() {
  const [params] = useSearchParams();
  const [code, setCode] = useState(params.get("code") ?? "");
  const [request, setRequest] = useState<DeviceRequest | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<"approved" | "denied" | null>(null);

  async function lookup(event?: FormEvent) {
    event?.preventDefault();
    setError(null);
    try {
      setRequest(await api.device(code.trim()));
    } catch (e) {
      setRequest(null);
      setError(e instanceof ApiError && e.status === 404 ? "That code is unknown, expired or already used. Run 'mytunnel login' again." : "Could not look up the code.");
    }
  }

  async function decide(approve: boolean) {
    if (!request) return;
    try {
      await (approve ? api.approveDevice(request.userCode) : api.denyDevice(request.userCode));
      setDone(approve ? "approved" : "denied");
    } catch {
      setError("The request expired. Run 'mytunnel login' again.");
    }
  }

  return (
    <>
      <PageHeader title="Authorize the CLI" description="Connect a terminal to your account." />
      <Card>
        {done === "approved" && <SuccessBanner>Approved. Return to your terminal; the CLI is now signed in.</SuccessBanner>}
        {done === "denied" && <SuccessBanner>Denied. The CLI was not given access.</SuccessBanner>}
        {!done && (
          <div className="stack narrow">
            {error && <ErrorBanner message={error} />}
            {!request ? (
              <form onSubmit={lookup} className="stack">
                <Field label="Code shown in your terminal">
                  <input value={code} autoComplete="off" placeholder="ABCD-EFGH" maxLength={16} onChange={(e) => setCode(e.target.value.toUpperCase())} />
                </Field>
                <button className="button button-primary" disabled={!code.trim()}>Continue</button>
              </form>
            ) : (
              <>
                <p>Check that your terminal shows this code. Approve only if you just ran <code>mytunnel login</code> yourself.</p>
                <p className="user-code">{request.userCode}</p>
                <dl className="details">
                  <dt>Device</dt><dd>{request.clientName ?? "unknown"}</dd>
                  <dt>Requested</dt><dd>{formatDate(request.createdAt)}</dd>
                </dl>
                <div className="row">
                  <button className="button button-primary" onClick={() => decide(true)}>Approve</button>
                  <button className="button" onClick={() => decide(false)}>Deny</button>
                </div>
              </>
            )}
          </div>
        )}
      </Card>
    </>
  );
}
