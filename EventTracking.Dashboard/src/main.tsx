import React, { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  api,
  ApiError,
  csrfFetch,
  type Event,
  type Overview,
  type Page,
  type Project,
  type Session,
} from "./api";
import { Tracker, sendWithRetry } from "./tracker.mjs";
import "@fontsource/dm-sans/latin-400.css";
import "@fontsource/dm-sans/latin-500.css";
import "@fontsource/dm-sans/latin-600.css";
import "@fontsource/manrope/latin-500.css";
import "@fontsource/manrope/latin-600.css";
import "@fontsource/manrope/latin-700.css";
import "./style.css";

const number = (value: number) => new Intl.NumberFormat("en-US").format(value);
const date = (value: string) =>
  new Date(value).toLocaleString("en-US", {
    timeZone: "UTC",
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
const day = (value: Date) => value.toISOString().slice(0, 10);
function range(days: number) {
  const end = new Date();
  const start = new Date(end);
  start.setUTCDate(start.getUTCDate() - days + 1);
  return { from: day(start), to: day(end) };
}
type Filters = {
  from: string;
  to: string;
  eventType: string;
  userId: string;
  propertyName: string;
  propertyValue: string;
};
const initial = (): Filters => ({
  ...range(7),
  eventType: "",
  userId: "",
  propertyName: "",
  propertyValue: "",
});
type View = "overview" | "events" | "people" | "demo";

function Mark() {
  return (
    <span className="mark" aria-hidden="true">
      <i />
      <i />
      <i />
    </span>
  );
}
function Icon({ name }: { name: string }) {
  const paths: Record<string, React.ReactNode> = {
    overview: (
      <>
        <rect x="3" y="3" width="7" height="7" rx="1" />
        <rect x="14" y="3" width="7" height="7" rx="1" />
        <rect x="3" y="14" width="7" height="7" rx="1" />
        <rect x="14" y="14" width="7" height="7" rx="1" />
      </>
    ),
    events: (
      <>
        <path d="m13 2-8 12h7l-1 8 8-12h-7z" />
      </>
    ),
    people: (
      <>
        <circle cx="9" cy="8" r="3" />
        <path d="M3 21v-3a6 6 0 0 1 12 0v3M17 5a3 3 0 0 1 0 6M18 15a5 5 0 0 1 3 5" />
      </>
    ),
    demo: (
      <>
        <path d="M3 8h18l-2-5H5L3 8Zm1 0v13h16V8M9 21v-7h6v7" />
        <path d="M3 8a3 3 0 0 0 6 0 3 3 0 0 0 6 0 3 3 0 0 0 6 0" />
      </>
    ),
    refresh: (
      <>
        <path d="M20 7v5h-5M4 17v-5h5M6 6a8 8 0 0 1 14 6M4 12a8 8 0 0 0 14 6" />
      </>
    ),
    arrow: (
      <>
        <path d="M5 12h14m-5-5 5 5-5 5" />
      </>
    ),
  };
  return (
    <svg
      width="19"
      height="19"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      {paths[name] || paths.events}
    </svg>
  );
}

function Login({ onLogin }: { onLogin: (session: Session) => void }) {
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy(true);
    setError("");
    const form = new FormData(event.currentTarget);
    try {
      await api("/auth/login", {
        method: "POST",
        body: JSON.stringify({
          username: form.get("username"),
          password: form.get("password"),
        }),
      });
      onLogin(await api<Session>("/session"));
    } catch (error) {
      setError((error as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <main className="login">
      <section className="login-story">
        <a className="brand" href="/dashboard/">
          <Mark />
          signal<span>ANALYTICS</span>
        </a>
        <div>
          <p className="eyebrow">EVERY EVENT TELLS A STORY</p>
          <h1>
            Small signals.
            <br />
            Clearer decisions.
          </h1>
          <p>
            Understand what happens in your product,
            <br />
            one event at a time.
          </p>
          <div className="story-chart" aria-hidden="true">
            {[22, 38, 29, 51, 42, 69, 55, 86, 73, 100].map((height, i) => (
              <i key={i} style={{ height: height + "%" }} />
            ))}
          </div>
        </div>
        <small>YOUR PRODUCT, IN PERSPECTIVE.</small>
      </section>
      <section className="login-form">
        <div>
          <span className="tag">YOUR WORKSPACE</span>
          <h2>Welcome back.</h2>
          <p className="muted">Sign in to explore your projects.</p>
          <form onSubmit={submit}>
            <label>
              Username
              <input
                name="username"
                autoComplete="username"
                required
                maxLength={60}
              />
            </label>
            <label>
              Password
              <input
                type="password"
                name="password"
                autoComplete="current-password"
                required
                maxLength={256}
              />
            </label>
            {error && (
              <p role="alert" className="error">
                {error}
              </p>
            )}
            <button className="primary" disabled={busy}>
              {busy ? "Signing in…" : "Sign in"}
              <Icon name="arrow" />
            </button>
          </form>
          <p className="login-note">
            Accounts are provided by your workspace administrator.
          </p>
        </div>
      </section>
    </main>
  );
}

function Chart({ data, filters }: { data: Overview; filters: Filters }) {
  const container = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(890);
  useEffect(() => {
    const observer = new ResizeObserver((entries) =>
      setWidth(Math.max(300, entries[0].contentRect.width)),
    );
    if (container.current) observer.observe(container.current);
    return () => observer.disconnect();
  }, []);
  const step = data.interval === "hour" ? 3600000 : 86400000;
  const start = Date.parse(filters.from + "T00:00:00Z");
  const end = Date.parse(filters.to + "T00:00:00Z") + 86400000;
  const sums = new Map<number, number>();
  data.series.forEach((p) =>
    sums.set(
      Date.parse(p.bucket),
      (sums.get(Date.parse(p.bucket)) || 0) + p.count,
    ),
  );
  const values = Array.from(
    { length: Math.max(1, Math.round((end - start) / step)) },
    (_, i) => ({
      time: start + i * step,
      count: sums.get(start + i * step) || 0,
    }),
  );
  const max = Math.max(4, ...values.map((v) => v.count));
  const points = values.map((v, i) => ({
    ...v,
    x: 48 + (i / Math.max(1, values.length - 1)) * (width - 80),
    y: 187 - (v.count / max) * 145,
  }));
  const line = points.map((p) => `${p.x},${p.y}`).join(" ");
  return (
    <div className="chart" ref={container}>
      <svg
        viewBox={`0 0 ${width} 230`}
        role="img"
        aria-label={`Event volume by ${data.interval}, UTC. ${number(values.reduce((sum, v) => sum + v.count, 0))} total events.`}
      >
        <defs>
          <linearGradient id="area" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="#65a88a" stopOpacity=".24" />
            <stop offset="100%" stopColor="#65a88a" stopOpacity=".01" />
          </linearGradient>
        </defs>
        {[0, 1, 2, 3, 4].map((t) => (
          <g key={t}>
            <line
              x1="48"
              x2={width - 32}
              y1={187 - t * 36.25}
              y2={187 - t * 36.25}
              stroke="#e8ebe6"
              strokeDasharray="3 5"
            />
            <text x="30" y={191 - t * 36.25} textAnchor="end">
              {Math.round((max * t) / 4)}
            </text>
          </g>
        ))}
        <polygon
          points={`48,187 ${line} ${width - 32},187`}
          fill="url(#area)"
        />
        <polyline
          points={line}
          fill="none"
          stroke="#32765c"
          strokeWidth="2.5"
          strokeLinejoin="round"
        />
        {points
          .filter(
            (_, i) =>
              i % Math.ceil(values.length / (width < 500 ? 3 : 7)) === 0 ||
              i === values.length - 1,
          )
          .map((p, i) => (
            <g key={p.time}>
              <circle
                cx={p.x}
                cy={p.y}
                r="4"
                fill="#32765c"
                stroke="#fff"
                strokeWidth="2"
              >
                <title>
                  {new Date(p.time).toISOString()}: {p.count} events
                </title>
              </circle>
              <text
                x={p.x}
                y="217"
                textAnchor={
                  i === 0 ? "start" : p === points.at(-1) ? "end" : "middle"
                }
              >
                {new Date(p.time).toLocaleString(
                  "en-US",
                  data.interval === "hour"
                    ? { timeZone: "UTC", hour: "2-digit" }
                    : { timeZone: "UTC", month: "short", day: "numeric" },
                )}
              </text>
            </g>
          ))}
      </svg>
    </div>
  );
}

function Demo({
  project,
  onExplore,
}: {
  project: Project;
  onExplore: () => void;
}) {
  const [status, setStatus] = useState({
    pending: 0,
    state: "ready",
    error: "",
  });
  const [receipt, setReceipt] = useState("");
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [product, setProduct] = useState("mug");
  const [quantity, setQuantity] = useState(1);
  const [loggedIn, setLoggedIn] = useState(false);
  const tracker = useRef<Tracker | null>(null);
  const pendingOrder = useRef<object | null>(null);
  const abort = useRef(new AbortController());
  useEffect(() => {
    const controller = new AbortController();
    abort.current = controller;
    const client = new Tracker(
      `/dashboard-api/projects/${encodeURIComponent(project.id)}/demo/events`,
      {
        fetcher: csrfFetch,
        onStatus: (value: { pending: number; state: string; error?: string }) =>
          setStatus({ ...value, error: value.error || "" }),
      },
    );
    tracker.current = client;
    if (project.canDemo)
      client.track("page_view", { demo: true, page: "shop" });
    return () => {
      controller.abort();
      client.dispose();
    };
  }, [project.id, project.canDemo]);
  async function login() {
    try {
      tracker.current?.track("login", { demo: true });
      await tracker.current?.flush(true);
      setLoggedIn(true);
    } catch (error) {
      setError((error as Error).message);
    }
  }
  async function purchase() {
    setBusy(true);
    setError("");
    try {
      if (!pendingOrder.current) {
        tracker.current?.track("checkout_started", {
          demo: true,
          productId: product,
        });
        pendingOrder.current = {
          eventId: crypto.randomUUID(),
          occurredAt: new Date().toISOString(),
          sessionId: tracker.current?.sessionId,
          productId: product,
          quantity,
        };
      }
      await tracker.current?.flush(true);
      const result = await sendWithRetry(
        `/dashboard-api/projects/${encodeURIComponent(project.id)}/demo/purchase`,
        pendingOrder.current,
        { fetcher: csrfFetch, signal: abort.current.signal },
      );
      setReceipt(
        `Order confirmed · ${new Intl.NumberFormat("en-US", { style: "currency", currency: result.currency }).format(result.amountMinor / 100)} · ${result.receipt.status}`,
      );
      pendingOrder.current = null;
    } catch (error) {
      if (!abort.current.signal.aborted) setError((error as Error).message);
    } finally {
      if (!abort.current.signal.aborted) setBusy(false);
    }
  }
  const products = [
    { id: "mug", name: "Everyday mug", price: 24, shape: "mug" },
    { id: "tote", name: "Market tote", price: 32, shape: "tote" },
    { id: "notebook", name: "Pocket notebook", price: 18, shape: "book" },
  ];
  return (
    <>
      <div className="page-heading">
        <div>
          <p className="eyebrow">A LITTLE EXPERIMENT</p>
          <h1>Meet your next event.</h1>
          <p className="muted">
            Browse, sign in, and place a mock order. Watch the story appear in
            your analytics.
          </p>
        </div>
        <span className="tag">DEMO SHOP</span>
      </div>
      {!project.canDemo ? (
        <div className="notice">
          Your membership allows analytics viewing. Ask your administrator for
          demo access.
        </div>
      ) : (
        <>
          <div className="demo-banner">
            <div>
              <strong>Little things, made for every day.</strong>
              <p>Just a make-believe shop. No payments or personal details.</p>
            </div>
            <button
              className="secondary"
              onClick={login}
              disabled={busy || loggedIn}
            >
              {loggedIn
                ? "Signed in as demo-user-001"
                : "Simulate customer login"}
            </button>
          </div>
          <div className="products">
            {products.map((p) => (
              <button
                className={`product ${product === p.id ? "chosen" : ""}`}
                key={p.id}
                disabled={busy || !!pendingOrder.current}
                onClick={() => setProduct(p.id)}
                aria-pressed={product === p.id}
              >
                <div className={`product-art ${p.shape}`}>
                  <span />
                </div>
                <div className="product-title">
                  <strong>{p.name}</strong>
                  <span>${p.price}</span>
                </div>
                <small>
                  {product === p.id
                    ? "Selected for your order"
                    : "Choose this item"}
                </small>
              </button>
            ))}
          </div>
          <section className="panel checkout">
            <div>
              <span className="eyebrow">YOUR MOCK ORDER</span>
              <h2>{products.find((p) => p.id === product)?.name}</h2>
            </div>
            <label>
              Quantity
              <select
                value={quantity}
                onChange={(e) => setQuantity(Number(e.target.value))}
                disabled={busy || !!pendingOrder.current}
              >
                {Array.from({ length: 10 }, (_, i) => (
                  <option key={i}>{i + 1}</option>
                ))}
              </select>
            </label>
            <button className="primary" onClick={purchase} disabled={busy}>
              {busy
                ? "Confirming…"
                : pendingOrder.current
                  ? "Retry same order"
                  : "Place mock order"}
              <Icon name="arrow" />
            </button>
          </section>
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
          {receipt && (
            <div className="notice success" role="status">
              {receipt}
              <button className="text-button" onClick={onExplore}>
                Explore events →
              </button>
            </div>
          )}
          <section className="panel delivery">
            <span
              className={`status-dot ${status.state === "paused" ? "amber" : ""}`}
            />
            <div>
              <strong>Browser delivery: {status.state}</strong>
              <p>
                {status.pending} pending · Stable event IDs are preserved across
                retries.{status.error && " " + status.error}
              </p>
            </div>
            <button
              className="secondary"
              onClick={() =>
                tracker.current
                  ?.flush(true)
                  .catch((e: Error) => setError(e.message))
              }
            >
              Flush events
            </button>
          </section>
          <p className="footnote">
            Browser activity records page views and login actions. The backend
            validates catalog prices and quantities before confirming a
            purchase. Unsent browser events may be lost when you leave this
            page.
          </p>
        </>
      )}
    </>
  );
}

function Workspace({
  session,
  onSession,
  onLogout,
}: {
  session: Session;
  onSession: (s: Session) => void;
  onLogout: () => void;
}) {
  const [projectId, setProjectId] = useState(session.projects[0]?.id || "");
  const [view, setView] = useState<View>("overview");
  const [draft, setDraft] = useState(initial);
  const [filters, setFilters] = useState(initial);
  const [refresh, setRefresh] = useState(0);
  const [overview, setOverview] = useState<Overview | null>(null);
  const [page, setPage] = useState<Page | null>(null);
  const [busy, setBusy] = useState(false);
  const [paging, setPaging] = useState(false);
  const [error, setError] = useState("");
  const [expanded, setExpanded] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [projectError, setProjectError] = useState("");
  const request = useRef<AbortController | null>(null);
  const requestKey = useRef("");
  const selected = session.projects.find((p) => p.id === projectId);
  const params = new URLSearchParams({
    from: filters.from + "T00:00:00Z",
    to: new Date(
      Date.parse(filters.to + "T00:00:00Z") + 86400000,
    ).toISOString(),
  });
  for (const field of [
    "eventType",
    "userId",
    "propertyName",
    "propertyValue",
  ] as const)
    if (filters[field]) params.set(field, filters[field]);
  const query = params.toString();
  const key = projectId + "?" + query;
  requestKey.current = key;
  useEffect(() => {
    const controller = new AbortController();
    request.current = controller;
    setOverview(null);
    setPage(null);
    setError("");
    setExpanded(null);
    setPaging(false);
    if (!projectId || view === "demo") {
      setBusy(false);
      return () => controller.abort();
    }
    setBusy(true);
    const base = `/projects/${encodeURIComponent(projectId)}`;
    Promise.all([
      api<Overview>(`${base}/overview?${query}`, { signal: controller.signal }),
      api<Page>(`${base}/events?${query}`, { signal: controller.signal }),
    ])
      .then(([overview, page]) => {
        if (!controller.signal.aborted) {
          setOverview(overview);
          setPage(page);
        }
      })
      .catch((error) => {
        if (!controller.signal.aborted) {
          if (error instanceof ApiError && error.status === 401) onLogout();
          else
            setError(
              error instanceof ApiError && error.status === 403
                ? "You no longer have access to this project. Select another project or contact your administrator."
                : error.message,
            );
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) setBusy(false);
      });
    return () => controller.abort();
  }, [projectId, query, refresh, view === "demo"]);
  useEffect(() => {
    if (!overview?.status.pending || busy || view === "demo") return;
    const timer = setTimeout(() => setRefresh((value) => value + 1), 2000);
    return () => clearTimeout(timer);
  }, [overview, busy, view]);
  async function loadMore() {
    if (!page?.nextCursor) return;
    const captured = key;
    setPaging(true);
    try {
      const next = await api<Page>(
        `/projects/${encodeURIComponent(projectId)}/events?${query}&cursor=${encodeURIComponent(page.nextCursor)}`,
        { signal: request.current?.signal },
      );
      if (requestKey.current === captured)
        setPage((old) =>
          old
            ? {
                events: [...old.events, ...next.events],
                nextCursor: next.nextCursor,
              }
            : next,
        );
    } catch (error) {
      if (!(error instanceof DOMException && error.name === "AbortError"))
        setError((error as Error).message);
    } finally {
      if (requestKey.current === captured) setPaging(false);
    }
  }
  async function createProject(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setProjectError("");
    const name = new FormData(event.currentTarget).get("name");
    try {
      const project = await api<Project>("/projects", {
        method: "POST",
        body: JSON.stringify({ name }),
      });
      onSession({ ...session, projects: [...session.projects, project] });
      setProjectId(project.id);
      setCreating(false);
    } catch (error) {
      setProjectError((error as Error).message);
    }
  }
  function preset(days: number) {
    const next = { ...draft, ...range(days) };
    setDraft(next);
    setFilters(next);
  }
  function timeline(userId: string) {
    const next = { ...filters, userId };
    setFilters(next);
    setDraft(next);
    setView("people");
  }
  const total =
    overview?.summary.reduce((sum, event) => sum + event.count, 0) || 0;
  async function signOut() {
    try {
      await api("/auth/logout", { method: "POST" });
      onLogout();
    } catch (error) {
      setError((error as Error).message);
    }
  }
  return (
    <div className="shell">
      <aside className="sidebar">
        <a href="/dashboard/" className="brand">
          <Mark />
          signal<span>ANALYTICS</span>
        </a>
        <p className="nav-label">WORKSPACE</p>
        <nav>
          {(
            [
              ["overview", "Overview"],
              ["events", "Event explorer"],
              ["people", "User timeline"],
              ["demo", "Demo shop"],
            ] as [View, string][]
          ).map(([id, name]) => (
            <button
              key={id}
              className={view === id ? "active" : ""}
              onClick={() => setView(id)}
            >
              <Icon name={id} />
              {name}
              {id === "demo" && <span className="nav-badge">TRY IT</span>}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="workspace-note">
            <span className="status-dot" />A clearer view of your product.
            <p>Explore the signals that matter.</p>
          </div>
          <div className="account">
            <span className="avatar">
              {session.username.slice(0, 1).toUpperCase()}
            </span>
            <div>
              <strong>{session.username}</strong>
              <small>Your workspace</small>
            </div>
            <button
              title="Sign out"
              aria-label="Sign out"
              onClick={async () => {
                try {
                  await api("/auth/logout", { method: "POST" });
                  onLogout();
                } catch (error) {
                  setError((error as Error).message);
                }
              }}
            >
              ↗
            </button>
          </div>
        </div>
      </aside>
      <div className="workspace">
        <header className="topbar">
          <div className="project-control">
            <span className="project-icon">S</span>
            <label className="sr-only" htmlFor="project">
              Project
            </label>
            <select
              id="project"
              value={projectId}
              onChange={(e) => {
                setProjectId(e.target.value);
                setExpanded(null);
              }}
            >
              {!session.projects.length && (
                <option value="">No projects yet</option>
              )}
              {session.projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
            <span className="top-divider" />
            <span className="muted">
              {view === "demo" ? "Demo shop" : "Analytics"}
            </span>
          </div>
          <div className="top-actions">
            <button
              className="text-button"
              onClick={() => setCreating(!creating)}
            >
              ＋ New project
            </button>
            <button
              className="mobile-signout text-button"
              aria-label="Sign out"
              onClick={signOut}
            >
              ↗
            </button>
          </div>
        </header>
        <main className="main">
          {creating && (
            <form className="panel new-project" onSubmit={createProject}>
              <label>
                Project name
                <input
                  name="name"
                  placeholder="e.g. My storefront"
                  maxLength={100}
                  required
                />
              </label>
              <button className="primary">Create project</button>
              <button
                type="button"
                className="secondary"
                onClick={() => setCreating(false)}
              >
                Cancel
              </button>
              {projectError && (
                <p role="alert" className="error">
                  {projectError}
                </p>
              )}
            </form>
          )}
          {!selected ? (
            <div className="empty panel">
              <Icon name="overview" />
              <h2>Your next idea starts here.</h2>
              <p>Create a project to collect your first events.</p>
              <button className="primary" onClick={() => setCreating(true)}>
                Create a project
              </button>
            </div>
          ) : view === "demo" ? (
            <Demo
              key={projectId}
              project={selected}
              onExplore={() => {
                setDraft(initial());
                setFilters(initial());
                setView("events");
                setRefresh((v) => v + 1);
              }}
            />
          ) : (
            <>
              <div className="page-heading">
                <div>
                  <p className="eyebrow">
                    {selected.name.toUpperCase()} / ANALYTICS
                  </p>
                  <h1>
                    {view === "overview"
                      ? "The bigger picture."
                      : view === "events"
                        ? "Every event, in focus."
                        : "Follow the journey."}
                  </h1>
                  <p className="muted">
                    {view === "overview"
                      ? "A little less guessing. A little more understanding."
                      : view === "events"
                        ? "Explore the actions behind the numbers."
                        : "See a customer’s activity in chronological order."}
                  </p>
                </div>
                <button
                  className="secondary"
                  onClick={() => setRefresh((v) => v + 1)}
                  disabled={busy}
                >
                  <Icon name="refresh" />
                  {busy ? "Refreshing…" : "Refresh"}
                </button>
              </div>
              <div className="range-row">
                <div className="range-buttons">
                  <button onClick={() => preset(1)}>Today</button>
                  <button onClick={() => preset(7)}>7 days</button>
                  <button onClick={() => preset(30)}>30 days</button>
                </div>
                <span className="muted">
                  {filters.from} — {filters.to}
                  <span className="utc">UTC</span>
                </span>
              </div>
              <form
                className="filters"
                onSubmit={(e) => {
                  e.preventDefault();
                  setFilters({ ...draft });
                }}
              >
                <label>
                  From
                  <input
                    type="date"
                    value={draft.from}
                    onChange={(e) =>
                      setDraft({ ...draft, from: e.target.value })
                    }
                    required
                  />
                </label>
                <label>
                  Through
                  <input
                    type="date"
                    value={draft.to}
                    onChange={(e) => setDraft({ ...draft, to: e.target.value })}
                    required
                  />
                </label>
                <label>
                  Event
                  <input
                    placeholder="All events"
                    value={draft.eventType}
                    onChange={(e) =>
                      setDraft({ ...draft, eventType: e.target.value })
                    }
                    maxLength={100}
                  />
                </label>
                <label>
                  User
                  <input
                    placeholder="All users"
                    value={draft.userId}
                    onChange={(e) =>
                      setDraft({ ...draft, userId: e.target.value })
                    }
                    maxLength={200}
                  />
                </label>
                <label>
                  Property
                  <input
                    placeholder="e.g. source"
                    value={draft.propertyName}
                    onChange={(e) =>
                      setDraft({ ...draft, propertyName: e.target.value })
                    }
                    maxLength={100}
                  />
                </label>
                <label>
                  Value (JSON)
                  <input
                    placeholder={'e.g. "browser"'}
                    value={draft.propertyValue}
                    onChange={(e) =>
                      setDraft({ ...draft, propertyValue: e.target.value })
                    }
                    maxLength={4096}
                  />
                </label>
                <button className="filter-button">Apply</button>
              </form>
              {error && (
                <div role="alert" className="error">
                  {error}
                  <button
                    className="text-button"
                    onClick={() => setRefresh((v) => v + 1)}
                  >
                    Retry
                  </button>
                </div>
              )}
              {busy && (
                <div className="loading panel" role="status">
                  <span className="spinner" />
                  Gathering your latest signals…
                </div>
              )}
              {overview && (
                <>
                  <div className="metrics">
                    <Metric
                      title="Total events"
                      value={number(total)}
                      subtitle="Actions in the selected period"
                      icon="events"
                    />
                    <Metric
                      title="Active users"
                      value={number(overview.users.identifiedUsers)}
                      subtitle={`${number(overview.users.anonymousUsers)} anonymous identities separately`}
                      icon="people"
                    />
                    <Metric
                      title="Event types"
                      value={number(overview.summary.length)}
                      subtitle="Distinct actions recorded"
                      icon="overview"
                    />
                    <Metric
                      title="Pending events"
                      value={number(overview.status.pending)}
                      subtitle={
                        overview.status.oldestPendingSeconds == null
                          ? "No events waiting to be processed"
                          : `Oldest waiting ${overview.status.oldestPendingSeconds.toFixed(1)}s`
                      }
                      icon="refresh"
                    />
                  </div>
                  <div className="freshness">
                    <span>
                      <span className="status-dot" />
                      {overview.profile === "Hosted"
                        ? "Persisted during ingestion"
                        : "Asynchronous processing"}
                      <span className="freshness-separator">·</span>
                      {overview.profile === "Hosted"
                        ? "No background queue"
                        : `Processing p95 (last 5m): ${overview.status.processingP95Seconds == null ? "no recent samples" : overview.status.processingP95Seconds.toFixed(2) + "s"}`}
                    </span>
                    <span>Refreshed {date(overview.refreshedAt)} UTC</span>
                  </div>
                  {view === "overview" && (
                    <>
                      <section className="panel volume">
                        <div className="panel-heading">
                          <div>
                            <h2>Event volume</h2>
                            <p>
                              A pulse on your product,{" "}
                              {overview.interval === "day"
                                ? "day by day"
                                : "hour by hour"}
                              .
                            </p>
                          </div>
                          <span className="legend">
                            <i />
                            All matching events
                          </span>
                        </div>
                        <Chart data={overview} filters={filters} />
                        {total === 0 && (
                          <p className="chart-empty">
                            No events in this range. Try a different filter or
                            generate some in the demo shop.
                          </p>
                        )}
                      </section>
                      <section className="panel breakdown">
                        <div className="panel-heading">
                          <div>
                            <h2>What’s happening</h2>
                            <p>Your most frequent actions.</p>
                          </div>
                          <button
                            className="text-button"
                            onClick={() => setView("events")}
                          >
                            View all events →
                          </button>
                        </div>
                        {overview.summary.length ? (
                          overview.summary.map((event, i) => (
                            <div
                              className="breakdown-row"
                              key={event.eventType}
                            >
                              <span className="event-name">
                                <i
                                  style={{
                                    background: [
                                      "#4c826a",
                                      "#c39158",
                                      "#8097a7",
                                      "#a68fa2",
                                    ][i % 4],
                                  }}
                                />
                                {event.eventType}
                              </span>
                              <div className="bar-track">
                                <span
                                  style={{
                                    width: `${(event.count / Math.max(total, 1)) * 100}%`,
                                  }}
                                />
                              </div>
                              <strong>{number(event.count)}</strong>
                              <span className="muted">
                                {Math.round(
                                  (event.count / Math.max(total, 1)) * 100,
                                )}
                                %
                              </span>
                            </div>
                          ))
                        ) : (
                          <p className="empty-text">
                            Your first event will appear here.
                          </p>
                        )}
                      </section>
                    </>
                  )}
                  {(view === "events" || view === "people") && (
                    <section className="panel explorer">
                      <div className="panel-heading">
                        <div>
                          <h2>
                            {view === "people"
                              ? filters.userId
                                ? `Activity for ${filters.userId}`
                                : "Choose a user to explore"
                              : "Event explorer"}
                          </h2>
                          <p>
                            Chronological order ·{" "}
                            {number(page?.events.length || 0)} events loaded ·
                            UTC
                          </p>
                        </div>
                        <span className="tag">LIVE DATA</span>
                      </div>
                      {view === "people" && !filters.userId && (
                        <div className="notice">
                          Enter a user ID in the filters, or select a user from
                          an event below.
                        </div>
                      )}
                      {!page?.events.length ? (
                        <div className="empty">
                          <Icon name="events" />
                          <h2>No signals here yet.</h2>
                          <p>
                            Try a wider date range, clear your filters, or visit
                            the demo shop.
                          </p>
                          <button
                            className="secondary"
                            onClick={() => setView("demo")}
                          >
                            Open demo shop
                          </button>
                        </div>
                      ) : (
                        <div className="table-scroll">
                          <table>
                            <thead>
                              <tr>
                                <th>Event</th>
                                <th>User / identity</th>
                                <th>Occurred at</th>
                                <th>Source</th>
                                <th>
                                  <span className="sr-only">Details</span>
                                </th>
                              </tr>
                            </thead>
                            <tbody>
                              {page.events.map((event) => (
                                <React.Fragment key={event.eventId}>
                                  <tr>
                                    <td>
                                      <span className="event-pill">
                                        {event.eventType}
                                      </span>
                                    </td>
                                    <td>
                                      {event.userId ? (
                                        <button
                                          className="text-button"
                                          onClick={() =>
                                            timeline(event.userId!)
                                          }
                                        >
                                          {event.userId}
                                        </button>
                                      ) : (
                                        <span className="muted">
                                          {event.anonymousId || "Unidentified"}
                                        </span>
                                      )}
                                    </td>
                                    <td className="mono">
                                      {date(event.occurredAt)}
                                    </td>
                                    <td>
                                      <span
                                        className={`source ${event.properties.source === "server" ? "server" : ""}`}
                                      >
                                        {String(event.properties.source || "—")}
                                      </span>
                                    </td>
                                    <td>
                                      <button
                                        className="text-button"
                                        aria-expanded={
                                          expanded === event.eventId
                                        }
                                        aria-label={`Details for ${event.eventType} ${event.eventId}`}
                                        onClick={() =>
                                          setExpanded(
                                            expanded === event.eventId
                                              ? null
                                              : event.eventId,
                                          )
                                        }
                                      >
                                        {expanded === event.eventId
                                          ? "Close"
                                          : "Details"}
                                      </button>
                                    </td>
                                  </tr>
                                  {expanded === event.eventId && (
                                    <tr className="detail-row">
                                      <td colSpan={5}>
                                        <EventDetails event={event} />
                                      </td>
                                    </tr>
                                  )}
                                </React.Fragment>
                              ))}
                            </tbody>
                          </table>
                        </div>
                      )}
                      {page?.nextCursor && (
                        <div className="load-more">
                          <button
                            className="secondary"
                            disabled={paging}
                            onClick={loadMore}
                          >
                            {paging ? "Loading…" : "Load more events"}
                          </button>
                        </div>
                      )}
                    </section>
                  )}
                  <footer className="page-footer">
                    <span>Made of small moments.</span>
                    <span>
                      Project-wide ingestion status · Filters apply to analytics
                    </span>
                  </footer>
                </>
              )}
            </>
          )}
        </main>
      </div>
    </div>
  );
}
function Metric({
  title,
  value,
  subtitle,
  icon,
}: {
  title: string;
  value: string;
  subtitle: string;
  icon: string;
}) {
  return (
    <section className="panel metric">
      <div>
        <span>{title}</span>
        <Icon name={icon} />
      </div>
      <strong>{value}</strong>
      <p>{subtitle}</p>
    </section>
  );
}
function EventDetails({ event }: { event: Event }) {
  return (
    <div className="event-details">
      <dl>
        <dt>Event ID</dt>
        <dd>{event.eventId}</dd>
        <dt>Received at</dt>
        <dd>{date(event.receivedAt)} UTC</dd>
        <dt>Session</dt>
        <dd>{event.sessionId || "Not provided"}</dd>
        <dt>Schema</dt>
        <dd>{event.schemaVersion}</dd>
      </dl>
      <pre>{JSON.stringify(event.properties, null, 2)}</pre>
    </div>
  );
}
function App() {
  const [session, setSession] = useState<Session | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  async function init() {
    setError("");
    setLoading(true);
    try {
      setSession(await api<Session>("/session"));
    } catch (error) {
      if (!(error instanceof ApiError && error.status === 401))
        setError((error as Error).message);
    } finally {
      setLoading(false);
    }
  }
  useEffect(() => {
    void init();
  }, []);
  if (loading)
    return (
      <div className="boot" role="status">
        <Mark />
        Opening your workspace…
      </div>
    );
  if (error)
    return (
      <div className="boot">
        <h1>We couldn’t reach your workspace.</h1>
        <p role="alert">{error}</p>
        <button className="primary" onClick={init}>
          Try again
        </button>
      </div>
    );
  return session ? (
    <Workspace
      session={session}
      onSession={setSession}
      onLogout={() => setSession(null)}
    />
  ) : (
    <Login onLogin={setSession} />
  );
}
createRoot(document.getElementById("root")!).render(<App />);
