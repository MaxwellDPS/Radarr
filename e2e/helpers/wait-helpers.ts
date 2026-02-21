interface PollOptions {
  intervalMs?: number;
  timeoutMs?: number;
  description?: string;
}

export async function pollUntil<T>(
  fn: () => Promise<T>,
  predicate: (result: T) => boolean,
  options: PollOptions = {}
): Promise<T> {
  const {
    intervalMs = 5_000,
    timeoutMs = 120_000,
    description = 'condition',
  } = options;

  const start = Date.now();
  let lastResult: T | undefined;
  let lastError: Error | undefined;

  while (Date.now() - start < timeoutMs) {
    try {
      lastResult = await fn();

      if (predicate(lastResult)) {
        return lastResult;
      }
    } catch (err) {
      lastError = err instanceof Error ? err : new Error(String(err));
    }

    await new Promise((r) => setTimeout(r, intervalMs));
  }

  const elapsed = Math.round((Date.now() - start) / 1000);
  const details = lastError
    ? `Last error: ${lastError.message}`
    : `Last result: ${JSON.stringify(lastResult, null, 2)}`;

  throw new Error(
    `Timed out after ${elapsed}s waiting for: ${description}. ${details}`
  );
}
