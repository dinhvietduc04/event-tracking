import test from "node:test";
import assert from "node:assert/strict";
import { Tracker, sendWithRetry } from "../src/tracker.mjs";

test("lost acknowledgement retries identical bytes and IDs without another stored event", async () => {
  const stored = new Set();
  const bodies = [];
  let calls = 0;
  const tracker = new Tracker("/demo", {
    intervalMs: 0,
    sleep: async () => {},
    fetcher: async (_, request) => {
      bodies.push(request.body);
      for (const event of JSON.parse(request.body).events)
        stored.add(event.eventId);
      if (++calls === 1) throw new TypeError("Connection lost after commit");
      return new Response(JSON.stringify({ events: [] }), { status: 202 });
    },
  });
  try {
    tracker.track("page_view");
    tracker.track("login");
    await tracker.flush();
    assert.equal(stored.size, 2);
    assert.equal(bodies[0], bodies[1]);
    assert.equal(tracker.queue.length, 0);
  } finally {
    tracker.dispose();
  }
});
test("batches stay bounded and concurrent flushes share one request sequence", async () => {
  const batches = [];
  const tracker = new Tracker("/demo", {
    intervalMs: 0,
    fetcher: async (_, request) => {
      batches.push(JSON.parse(request.body).events);
      return new Response("{}", { status: 200 });
    },
  });
  try {
    for (let i = 0; i < 45; i++) tracker.track("page_view");
    await Promise.all([tracker.flush(), tracker.flush()]);
    assert.deepEqual(
      batches.map((b) => b.length),
      [20, 20, 5],
    );
    assert.equal(new Set(batches.flat().map((e) => e.eventId)).size, 45);
  } finally {
    tracker.dispose();
  }
});
test("retry exhaustion pauses automatic delivery and a manual retry preserves identity", async () => {
  let failing = true;
  let calls = 0;
  const bodies = [];
  const tracker = new Tracker("/demo", {
    intervalMs: 0,
    sleep: async () => {},
    fetcher: async (_, request) => {
      calls++;
      bodies.push(request.body);
      return new Response("{}", { status: failing ? 503 : 200 });
    },
  });
  try {
    tracker.track("login");
    await assert.rejects(tracker.flush());
    assert.equal(calls, 4);
    assert.equal(tracker.queue.length, 1);
    await tracker.flush();
    assert.equal(calls, 4);
    failing = false;
    await tracker.flush(true);
    assert.equal(calls, 5);
    assert.equal(bodies[0], bodies[4]);
  } finally {
    tracker.dispose();
  }
});
test("permanent rejection is not retried; long Retry-After pauses within the time budget", async () => {
  for (const status of [400, 401, 403, 409, 413, 429]) {
    let calls = 0;
    await assert.rejects(
      sendWithRetry(
        "/demo",
        {},
        {
          fetcher: async () => {
            calls++;
            return new Response("{}", {
              status,
              headers: { "Retry-After": "60" },
            });
          },
          sleep: async () => {},
        },
      ),
    );
    assert.equal(calls, 1);
  }
});
test("backoff uses jitter, honors Retry-After, and respects cancellation", async () => {
  const waits = [];
  let calls = 0;
  await sendWithRetry(
    "/demo",
    {},
    {
      random: () => 0,
      sleep: async (ms) => {
        waits.push(ms);
      },
      fetcher: async () =>
        ++calls < 3
          ? new Response("{}", { status: 503, headers: { "Retry-After": "1" } })
          : new Response("{}"),
    },
  );
  assert.deepEqual(waits, [1000, 1000]);
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(
    sendWithRetry(
      "/demo",
      {},
      {
        signal: controller.signal,
        fetcher: async () => {
          assert.fail("Cancelled request was sent");
        },
      },
    ),
  );
});
test("queue rejects overflow and snapshots caller properties", () => {
  const tracker = new Tracker("/demo", { intervalMs: 0 });
  try {
    const properties = { nested: { value: 1 } };
    tracker.track("page_view", properties);
    properties.nested.value = 2;
    for (let i = 1; i < 200; i++) tracker.track("page_view");
    assert.throws(() => tracker.track("page_view"), /full/);
    assert.equal(tracker.queue[0].properties.nested.value, 1);
  } finally {
    tracker.dispose();
  }
});
