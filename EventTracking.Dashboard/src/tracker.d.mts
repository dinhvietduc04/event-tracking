export type RetryOptions = {
  fetcher?: typeof fetch;
  sleep?: (ms: number, signal?: AbortSignal) => Promise<void>;
  random?: () => number;
  signal?: AbortSignal;
  attempts?: number;
  timeoutMs?: number;
};
export function sendWithRetry(
  url: string,
  payload: unknown,
  options?: RetryOptions,
): Promise<any>;
export class Tracker {
  constructor(
    url: string,
    options?: RetryOptions & {
      sessionId?: string;
      userId?: string;
      intervalMs?: number;
      onStatus?: (value: {
        pending: number;
        state: string;
        error?: string;
      }) => void;
    },
  );
  sessionId: string;
  track(eventType: string, properties?: Record<string, unknown>): string;
  flush(manual?: boolean): Promise<void>;
  dispose(): void;
}
