/** Retry a stable serialized request. No privileged keys are needed by this same-origin client. */
export async function sendWithRetry(
  url,
  payload,
  {
    fetcher = fetch,
    sleep = delay,
    random = Math.random,
    signal,
    attempts = 4,
    timeoutMs = 8000,
  } = {},
) {
  const body = JSON.stringify(payload);
  for (let attempt = 0; attempt < attempts; attempt++) {
    signal?.throwIfAborted();
    let response;
    try {
      const timeout = AbortSignal.timeout(timeoutMs);
      response = await fetcher(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body,
        credentials: "same-origin",
        signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
      });
    } catch (error) {
      signal?.throwIfAborted();
      if (attempt === attempts - 1) throw error;
    }
    if (response && (response.status === 200 || response.status === 202))
      return response.json();
    if (
      response &&
      response.status !== 408 &&
      response.status !== 429 &&
      response.status < 500
    ) {
      const error = await response.json().catch(() => ({}));
      throw new Error(error.title || `Event rejected (${response.status}).`);
    }
    if (attempt === attempts - 1)
      throw new Error("Delivery paused. Retry when the service is available.");
    let wait = Math.min(5000, 250 * 2 ** attempt) * (0.5 + random() * 0.5);
    const header = response?.headers.get("Retry-After");
    if (header) {
      const requested = /^\d+$/.test(header)
        ? Number(header) * 1000
        : Date.parse(header) - Date.now();
      if (requested > 5000)
        throw new Error("Delivery rate limit reached. Wait before retrying.");
      if (Number.isFinite(requested)) wait = Math.max(wait, requested);
    }
    await sleep(wait, signal);
  }
  throw new Error("No delivery attempts configured.");
}

function delay(ms, signal) {
  return new Promise((resolve, reject) => {
    signal?.throwIfAborted();
    const abort = () => {
      clearTimeout(timer);
      reject(signal.reason);
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", abort);
      resolve();
    }, ms);
    signal?.addEventListener("abort", abort, { once: true });
  });
}

/** Bounded in-memory telemetry queue; reloads may lose unsent browser events. */
export class Tracker {
  constructor(
    url,
    {
      sessionId = crypto.randomUUID(),
      userId = "demo-user-001",
      fetcher = fetch,
      onStatus = () => {},
      intervalMs = 2000,
      ...retry
    } = {},
  ) {
    this.url = url;
    this.sessionId = sessionId;
    this.userId = userId;
    this.fetcher = fetcher;
    this.onStatus = onStatus;
    this.retry = retry;
    this.queue = [];
    this.controller = new AbortController();
    this.inflight = null;
    this.timer =
      intervalMs > 0
        ? setInterval(() => this.flush().catch(() => {}), intervalMs)
        : null;
    this.paused = false;
  }
  track(eventType, properties = {}) {
    this.controller.signal.throwIfAborted();
    if (this.queue.length >= 200)
      throw new Error(
        "Event queue is full (200). Retry delivery before adding more.",
      );
    const event = {
      eventId: crypto.randomUUID(),
      eventType,
      schemaVersion: 1,
      occurredAt: new Date().toISOString(),
      userId: this.userId,
      sessionId: this.sessionId,
      properties: { ...properties, source: "browser" },
    };
    // Snapshot now, so callers cannot mutate a pending event between retries.
    this.queue.push(JSON.parse(JSON.stringify(event)));
    this.onStatus({ pending: this.queue.length, state: "queued" });
    return event.eventId;
  }
  flush(manual = false) {
    if (manual) this.paused = false;
    if (this.inflight) return this.inflight;
    if (this.paused || this.queue.length === 0) return Promise.resolve();
    this.inflight = this.drain().finally(() => {
      this.inflight = null;
    });
    return this.inflight;
  }
  async drain() {
    try {
      while (this.queue.length > 0) {
        const batch = this.queue.slice(0, 20);
        await sendWithRetry(
          this.url,
          { events: batch },
          {
            ...this.retry,
            fetcher: this.fetcher,
            signal: this.controller.signal,
          },
        );
        this.queue.splice(0, batch.length);
        this.onStatus({ pending: this.queue.length, state: "delivered" });
      }
    } catch (error) {
      this.paused = true;
      this.onStatus({
        pending: this.queue.length,
        state: "paused",
        error: error.message,
      });
      throw error;
    }
  }
  dispose() {
    if (this.timer) clearInterval(this.timer);
    this.controller.abort();
    this.queue.length = 0;
  }
}
