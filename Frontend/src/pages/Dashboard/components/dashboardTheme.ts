import dayjs, { Dayjs } from 'dayjs';

// ─── Chart palette (validated with the dataviz palette validator against the
//     app's real card surfaces: #ffffff light / #1e293b dark) ────────────────

/** Ordinal single-hue ramp for the 4 pipeline stages, light→dark = early→late. */
export const FUNNEL_RAMP: Record<'light' | 'dark', [string, string, string, string]> = {
  light: ['#86b6ef', '#5598e7', '#2a78d6', '#1c5cab'],
  dark: ['#9ec5f4', '#6da7ec', '#3987e5', '#256abf'],
};

/** Single accent series hue for sparklines (categorical slot 1). */
export const SERIES_HUE: Record<'light' | 'dark', string> = {
  light: '#2a78d6',
  dark: '#3987e5',
};

/** Glass-card fill + border tokens, per theme mode. */
export const glassTokens = (mode: 'light' | 'dark') =>
  mode === 'dark'
    ? {
        // Solid fallback first; the @supports block upgrades to true glass.
        solidBg: 'rgba(30, 41, 59, 0.96)',
        glassBg: 'rgba(30, 41, 59, 0.55)',
        border: 'rgba(255, 255, 255, 0.08)',
        shadow: '0 8px 30px rgba(0, 0, 0, 0.30)',
        tooltipBg: '#0f172a',
        tooltipBorder: '#334155',
      }
    : {
        solidBg: 'rgba(255, 255, 255, 0.97)',
        glassBg: 'rgba(255, 255, 255, 0.62)',
        border: 'rgba(15, 23, 42, 0.07)',
        shadow: '0 8px 30px rgba(15, 23, 42, 0.06)',
        tooltipBg: '#ffffff',
        tooltipBorder: '#e2e8f0',
      };

// ─── Formatting (zero-training rules: no raw decimals, no jargon) ───────────

const compact = new Intl.NumberFormat('en', { notation: 'compact', maximumFractionDigits: 1 });
const whole = new Intl.NumberFormat('en');

export const formatCount = (n: number): string => (n >= 10000 ? compact.format(n) : whole.format(n));

export const formatMoney = (n: number): string => {
  if (n >= 1000) return `$${compact.format(n)}`;
  return `$${whole.format(Math.round(n))}`;
};

/** Sentinel dates (< year 2000) and unparsable strings are never rendered. */
export const asRealDate = (iso: string | null | undefined): Dayjs | null => {
  if (!iso) return null;
  const d = dayjs(iso);
  return d.isValid() && d.year() >= 2000 ? d : null;
};

/** Plain-language countdown to a bid close: "closes in 5h" / "closed 2d ago". */
export const humanizeDeadline = (deadline: Dayjs, now: Dayjs = dayjs()): string => {
  const minutes = deadline.diff(now, 'minute');
  const abs = Math.abs(minutes);
  const span =
    abs >= 60 * 48
      ? `${Math.round(abs / (60 * 24))} days`
      : abs >= 60
        ? `${Math.round(abs / 60)}h`
        : `${Math.max(abs, 1)}m`;
  return minutes >= 0 ? `Closes in ${span}` : `Closed ${span} ago`;
};
