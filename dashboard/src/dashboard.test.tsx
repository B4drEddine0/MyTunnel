import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { api, ApiError, toError } from "./api";
import { dailyTotals, formatBytes, formatRelative } from "./format";
import { safeNext } from "./pages/AuthPages";
import { summarize } from "./pages/UsagePage";
import { RequestsChart } from "./components/RequestsChart";

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("format", () => {
  it("formats bytes", () => {
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1536)).toBe("1.5 KB");
    expect(formatBytes(20 * 1024 * 1024)).toBe("20 MB");
  });

  it("formats relative times", () => {
    const now = new Date("2026-10-28T12:00:00Z");
    expect(formatRelative(null, now)).toBe("never");
    expect(formatRelative("2026-10-28T11:59:30Z", now)).toBe("just now");
    expect(formatRelative("2026-10-28T11:00:00Z", now)).toBe("1 h ago");
    expect(formatRelative("2026-10-28T11:45:00Z", now)).toBe("15 min ago");
  });

  it("fills missing days with zero and sums subdomains", () => {
    const totals = dailyTotals(
      [
        { day: "2026-10-27", requests: 2 },
        { day: "2026-10-27", requests: 3 },
        { day: "2026-10-25", requests: 1 },
      ],
      4,
      new Date("2026-10-28T09:00:00Z"),
    );
    expect(totals).toEqual([
      { day: "2026-10-25", requests: 1 },
      { day: "2026-10-26", requests: 0 },
      { day: "2026-10-27", requests: 5 },
      { day: "2026-10-28", requests: 0 },
    ]);
  });
});

describe("safeNext", () => {
  it("only allows same-site relative paths", () => {
    expect(safeNext("/tokens")).toBe("/tokens");
    expect(safeNext("/activate?code=ABCD-EFGH")).toBe("/activate?code=ABCD-EFGH");
    expect(safeNext("//evil.example")).toBe("/");
    expect(safeNext("/\\evil.example")).toBe("/");
    expect(safeNext("https://evil.example")).toBe("/");
    expect(safeNext(null)).toBe("/");
  });
});

describe("api", () => {
  it("sends the CSRF header and JSON body", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ id: "1" }), { status: 201 }));
    await api.createProject("Site");
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/api/projects");
    expect(init?.method).toBe("POST");
    expect((init?.headers as Record<string, string>)["X-MyTunnel-Csrf"]).toBe("1");
    expect(init?.credentials).toBe("same-origin");
    expect(init?.body).toBe(JSON.stringify({ name: "Site" }));
  });

  it("surfaces the first validation message", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ title: "One or more validation errors occurred.", errors: { email: ["An account with this email already exists."] } }), { status: 400 }),
    );
    const error = await api.register("a@example.com", "long enough!").catch((e) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(error.message).toBe("An account with this email already exists.");
    expect(error.fieldErrors.email).toHaveLength(1);
  });

  it("maps status codes to friendly messages", () => {
    expect(toError(401, undefined).message).toBe("Please sign in.");
    expect(toError(429, {}).message).toMatch(/Too many requests/);
  });
});

describe("usage", () => {
  it("summarizes per subdomain, busiest first", () => {
    const rows = summarize([
      { day: "2026-10-27", subdomain: "a", requests: 1, errors: 0, bytesIn: 1, bytesOut: 2 },
      { day: "2026-10-28", subdomain: "b", requests: 5, errors: 1, bytesIn: 1, bytesOut: 2 },
      { day: "2026-10-28", subdomain: "a", requests: 2, errors: 1, bytesIn: 1, bytesOut: 2 },
    ]);
    expect(rows.map((r) => [r.subdomain, r.requests, r.errors])).toEqual([["b", 5, 1], ["a", 3, 1]]);
  });

  it("renders one accessible bar per day", () => {
    render(<RequestsChart points={[{ day: "2026-10-27", requests: 3 }, { day: "2026-10-28", requests: 0 }]} />);
    expect(screen.getByRole("button", { name: "2026-10-27: 3 requests" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "2026-10-28: 0 requests" })).toBeTruthy();
  });
});
