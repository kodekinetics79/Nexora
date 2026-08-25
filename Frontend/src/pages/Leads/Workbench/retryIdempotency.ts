export interface RetryOperation {
  fingerprint: string;
  key: string;
}

const canonicalize = (value: unknown): unknown => {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value && typeof value === 'object') {
    return Object.keys(value as Record<string, unknown>)
      .sort()
      .reduce<Record<string, unknown>>((result, key) => {
        const child = (value as Record<string, unknown>)[key];
        if (child !== undefined) result[key] = canonicalize(child);
        return result;
      }, {});
  }
  return value;
};

export const retryOperation = (
  current: RetryOperation | null,
  scope: string,
  leadId: number,
  payload: unknown,
  createId: () => string = () => crypto.randomUUID(),
): RetryOperation => {
  const fingerprint = JSON.stringify(canonicalize(payload));
  if (current?.fingerprint === fingerprint) return current;
  return { fingerprint, key: `${scope}:${leadId}:${createId()}` };
};
