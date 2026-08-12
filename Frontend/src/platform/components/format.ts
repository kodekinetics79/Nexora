// Formatting helpers shared across the platform console.

export const fmtNumber = (n: number): string => new Intl.NumberFormat('en-US').format(n);

export const fmtCompact = (n: number): string =>
  new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(n);

export const fmtCurrency = (n: number, compact = false): string =>
  new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    notation: compact ? 'compact' : 'standard',
    maximumFractionDigits: compact ? 1 : 0,
  }).format(n);

/**
 * A ratio with no denominator is NOT zero.
 *
 * The overview reported "Extraction Success 0.0%" on a fleet that had never run a job,
 * because the server divided by nothing and sent 0. Both ends now say "no data" instead:
 * the server sends null, and null renders as an em dash rather than as total failure.
 */
export const fmtPercent = (fraction: number | null | undefined, digits = 1): string =>
  fraction == null ? '—' : `${(fraction * 100).toFixed(digits)}%`;

export const fmtDateTime = (iso: string): string =>
  new Date(iso).toLocaleString('en-US', {
    year: 'numeric',
    month: 'short',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  });

export const fmtDate = (iso: string): string =>
  new Date(iso).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: '2-digit' });

export const fmtRelative = (iso: string | null): string => {
  if (!iso) return 'never';
  const diffMs = Date.now() - new Date(iso).getTime();
  const mins = Math.round(diffMs / 60_000);
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  return `${days}d ago`;
};

/**
 * A browser-trust window, in the words the operator agreed to.
 *
 * The permitted range now spans 8 hours to 30 days, and "720 hours" is not a duration anybody
 * reasons about — the operator ticking the box on the sign-in screen and the Owner setting the
 * policy have to be reading the same sentence, or they are not agreeing to the same thing. Whole
 * days are rendered as days; anything else stays in hours rather than being rounded into a lie.
 */
export const fmtTrustWindow = (hours: number): string => {
  if (!Number.isFinite(hours) || hours <= 0) return 'no window';
  if (hours % 24 === 0) {
    const days = hours / 24;
    return days === 1 ? '1 day' : `${days} days`;
  }
  return hours === 1 ? '1 hour' : `${hours} hours`;
};

export const fmtLatency = (ms: number | null): string => {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(1)}s`;
};
