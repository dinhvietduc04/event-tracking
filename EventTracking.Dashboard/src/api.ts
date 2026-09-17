export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}
export const csrfFetch: typeof fetch = async (input, init = {}) => {
  const tokenResponse = await fetch("/dashboard-api/auth/token", {
    credentials: "same-origin",
    signal: init.signal,
  });
  if (!tokenResponse.ok) return tokenResponse;
  const { token } = await tokenResponse.json();
  const headers = new Headers(init.headers);
  headers.set("X-CSRF-Token", token);
  return fetch(input, { ...init, credentials: "same-origin", headers });
};
export async function api<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const response = await (options.method === "POST" ? csrfFetch : fetch)(
    "/dashboard-api" + path,
    {
      ...options,
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", ...options.headers },
    },
  );
  if (!response.ok) {
    const error = await response.json().catch(() => ({}));
    const details = error.errors
      ? Object.values(error.errors).flat().join(" ")
      : "";
    throw new ApiError(
      response.status,
      details || error.title || `Request failed (${response.status}).`,
    );
  }
  return response.status === 204 ? (undefined as T) : response.json();
}
export type Project = { id: string; name: string; canDemo: boolean };
export type Session = { username: string; projects: Project[] };
export type Event = {
  eventId: string;
  eventType: string;
  occurredAt: string;
  receivedAt: string;
  userId: string | null;
  anonymousId: string | null;
  sessionId: string | null;
  properties: Record<string, unknown>;
  schemaVersion: number;
};
export type Page = { events: Event[]; nextCursor: string | null };
export type Overview = {
  summary: { eventType: string; count: number }[];
  series: { bucket: string; eventType: string; count: number }[];
  users: { identifiedUsers: number; anonymousUsers: number };
  profile: string;
  interval: string;
  refreshedAt: string;
  status: {
    pending: number;
    oldestPendingSeconds: number | null;
    processingP95Seconds: number | null;
    lastReceivedAt: string | null;
  };
};
