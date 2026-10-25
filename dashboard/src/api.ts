// Typed client for the gateway's control-plane API. Uses the same-origin session cookie and
// always sends the CSRF header the API requires on state-changing requests.

export interface User { id: string; email: string; displayName: string | null; createdAt: string }
export interface Token {
  id: string; name: string; prefix: string; createdAt: string;
  expiresAt: string | null; lastUsedAt: string | null; revokedAt: string | null; active: boolean;
}
export interface CreatedToken { token: Token; secret: string }
export interface Subdomain { id: string; subdomain: string; publicUrl: string; online: boolean }
export interface Project { id: string; name: string; createdAt: string; subdomains: Subdomain[] }
export interface Tunnel {
  tunnelId: string; subdomain: string; publicUrl: string; connectedAt: string;
  requests: number; inFlight: number; tokenId: string | null;
}
export interface Usage { day: string; subdomain: string; requests: number; errors: number; bytesIn: number; bytesOut: number }
export interface AuditEvent { type: string; createdAt: string; ipAddress: string | null; detail: string | null }
export interface DeviceRequest { userCode: string; clientName: string | null; createdAt: string; expiresAt: string }

export class ApiError extends Error {
  constructor(public readonly status: number, message: string, public readonly fieldErrors: Record<string, string[]> = {}) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    credentials: "same-origin",
    headers: {
      "X-MyTunnel-Csrf": "1",
      ...(body === undefined ? {} : { "Content-Type": "application/json" }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });

  if (response.status === 204) return undefined as T;
  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) throw toError(response.status, data);
  return data as T;
}

export function toError(status: number, data: unknown): ApiError {
  const problem = (data ?? {}) as { title?: string; errors?: Record<string, string[]> };
  const fieldErrors = problem.errors ?? {};
  const first = Object.values(fieldErrors)[0]?.[0];
  const fallback = status === 401 ? "Please sign in." : status === 429 ? "Too many requests. Try again shortly." : `Request failed (${status}).`;
  return new ApiError(status, first ?? problem.title ?? fallback, fieldErrors);
}

export const api = {
  me: () => request<User>("GET", "/me"),
  register: (email: string, password: string, displayName?: string) => request<User>("POST", "/auth/register", { email, password, displayName }),
  login: (email: string, password: string) => request<User>("POST", "/auth/login", { email, password }),
  logout: () => request<void>("POST", "/auth/logout"),
  changePassword: (currentPassword: string, newPassword: string) => request<void>("POST", "/account/password", { currentPassword, newPassword }),

  tokens: () => request<Token[]>("GET", "/tokens"),
  createToken: (name: string, expiresInDays: number | null) => request<CreatedToken>("POST", "/tokens", { name, expiresInDays }),
  revokeToken: (id: string) => request<void>("DELETE", `/tokens/${encodeURIComponent(id)}`),

  projects: () => request<Project[]>("GET", "/projects"),
  createProject: (name: string) => request<Project>("POST", "/projects", { name }),
  deleteProject: (id: string) => request<void>("DELETE", `/projects/${encodeURIComponent(id)}`),
  reserveSubdomain: (projectId: string, subdomain: string) => request<Subdomain>("POST", `/projects/${encodeURIComponent(projectId)}/subdomains`, { subdomain }),
  releaseSubdomain: (projectId: string, id: string) => request<void>("DELETE", `/projects/${encodeURIComponent(projectId)}/subdomains/${encodeURIComponent(id)}`),

  tunnels: () => request<Tunnel[]>("GET", "/tunnels"),
  stopTunnel: (tunnelId: string) => request<void>("DELETE", `/tunnels/${encodeURIComponent(tunnelId)}`),
  usage: (days: number) => request<Usage[]>("GET", `/usage?days=${days}`),
  audit: () => request<AuditEvent[]>("GET", "/audit"),

  device: (code: string) => request<DeviceRequest>("GET", `/device/${encodeURIComponent(code)}`),
  approveDevice: (code: string) => request<void>("POST", `/device/${encodeURIComponent(code)}/approve`),
  denyDevice: (code: string) => request<void>("POST", `/device/${encodeURIComponent(code)}/deny`),
};
