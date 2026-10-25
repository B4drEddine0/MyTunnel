import { useState, type FormEvent } from "react";
import { Link, Navigate, useNavigate, useSearchParams } from "react-router-dom";
import { api, ApiError } from "../api";
import { useAuth } from "../auth";
import { ErrorBanner, Field } from "../components/ui";

/** Only same-site relative paths are allowed as a post-login destination (no open redirect). */
export function safeNext(next: string | null): string {
  return next && next.startsWith("/") && !next.startsWith("//") && !next.startsWith("/\\") ? next : "/";
}

function AuthFrame({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="auth-page">
      <div className="auth-card card">
        <div className="brand brand-large">MyTunnel</div>
        <h1>{title}</h1>
        {children}
      </div>
    </div>
  );
}

export function LoginPage() {
  const { user, setUser } = useAuth();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const next = safeNext(params.get("next"));

  if (user) return <Navigate to={next} replace />;

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      setUser(await api.login(email, password));
      navigate(next, { replace: true });
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Could not reach the server.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthFrame title="Sign in">
      <form onSubmit={submit} className="stack">
        {error && <ErrorBanner message={error} />}
        <Field label="Email">
          <input type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label="Password">
          <input type="password" autoComplete="current-password" required value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <button className="button button-primary" disabled={busy}>{busy ? "Signing in…" : "Sign in"}</button>
      </form>
      <p className="muted">No account? <Link to={`/register?next=${encodeURIComponent(next)}`}>Create one</Link></p>
    </AuthFrame>
  );
}

export function RegisterPage() {
  const { user, setUser } = useAuth();
  const [params] = useSearchParams();
  const navigate = useNavigate();
  const [email, setEmail] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState("");
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const next = safeNext(params.get("next"));

  if (user) return <Navigate to={next} replace />;

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (password.length < 10) {
      setErrors({ password: ["Password must be at least 10 characters."] });
      return;
    }
    setBusy(true);
    setErrors({});
    setError(null);
    try {
      setUser(await api.register(email, password, displayName || undefined));
      navigate(next, { replace: true });
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.fieldErrors).length > 0) setErrors(e.fieldErrors);
      else setError(e instanceof Error ? e.message : "Registration failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthFrame title="Create your account">
      <form onSubmit={submit} className="stack">
        {error && <ErrorBanner message={error} />}
        <Field label="Email" error={errors.email?.[0]}>
          <input type="email" autoComplete="email" required maxLength={254} value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label="Name (optional)">
          <input autoComplete="name" maxLength={100} value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </Field>
        <Field label="Password" error={errors.password?.[0]} hint="At least 10 characters. A passphrase works well.">
          <input type="password" autoComplete="new-password" required minLength={10} maxLength={256} value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <button className="button button-primary" disabled={busy}>{busy ? "Creating…" : "Create account"}</button>
      </form>
      <p className="muted">Already registered? <Link to={`/login?next=${encodeURIComponent(next)}`}>Sign in</Link></p>
    </AuthFrame>
  );
}
