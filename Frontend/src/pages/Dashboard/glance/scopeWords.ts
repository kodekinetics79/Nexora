import dayjs from 'dayjs';

/**
 * Whose numbers these are, in the reader's words.
 *
 * Two server contracts disagree about the vocabulary and both reach this screen. Release 01 sends
 * `AccountTeamScope.ScopeName` — 'tenant' | 'managed_scope' | 'assigned_accounts' — while
 * sales-today and performance send `CommercialIntelligenceController.ScopeWireName`, which spells
 * the narrowest tier 'assigned_to_me'. They mean the same three tiers, so one normaliser handles
 * both and the bands never have to know which endpoint they came from.
 *
 * An unrecognised value returns null rather than being printed raw. A wire word leaking into the
 * header would tell the reader their numbers are scoped some way they cannot name, which is worse
 * than saying we could not work it out — so the caller says SCOPE_UNRESOLVED instead.
 */
export type GlanceScopeWire =
  | 'tenant'
  | 'managed_scope'
  | 'assigned_accounts'
  | 'assigned_to_me';

export type GlanceScopeWords = 'Company-wide' | 'Your managed scope' | 'Your assigned accounts';

const SCOPE_WORDS: Readonly<Record<GlanceScopeWire, GlanceScopeWords>> = Object.freeze({
  tenant: 'Company-wide',
  managed_scope: 'Your managed scope',
  assigned_accounts: 'Your assigned accounts',
  assigned_to_me: 'Your assigned accounts',
});

/** What every band prints when the server's scope word is missing or unrecognised. */
export const SCOPE_UNRESOLVED = 'Scope not stated';

export const scopeWords = (wire: string | null | undefined): GlanceScopeWords | null => {
  if (typeof wire !== 'string') return null;
  const key = wire.trim().toLowerCase();
  return Object.prototype.hasOwnProperty.call(SCOPE_WORDS, key)
    ? SCOPE_WORDS[key as GlanceScopeWire]
    : null;
};

export interface GlanceWindow {
  /** Inclusive first day, YYYY-MM-DD. */
  from: string;
  /** Inclusive last day, YYYY-MM-DD. */
  to: string;
}

/**
 * The equal-length window that ends the day before this one starts — the verdict band's ghost row
 * compares against it, so "the previous 30 days" has to mean exactly 30 days and has to abut the
 * current window with no shared day and no gap.
 *
 * Windows are inclusive of both ends everywhere on this screen, matching how the endpoints read
 * from&to, so a 1 Jan – 30 Jan window is 30 days and its predecessor is 2 Dec – 31 Dec. Anything
 * unparseable or inverted returns null: an invented comparison period is a wrong number.
 */
export const priorWindow = (from: string | null | undefined, to: string | null | undefined): GlanceWindow | null => {
  if (!from || !to) return null;
  const start = dayjs(from);
  const end = dayjs(to);
  if (!start.isValid() || !end.isValid()) return null;
  const days = end.startOf('day').diff(start.startOf('day'), 'day') + 1;
  if (days < 1) return null;
  const priorEnd = start.startOf('day').subtract(1, 'day');
  return {
    from: priorEnd.subtract(days - 1, 'day').format('YYYY-MM-DD'),
    to: priorEnd.format('YYYY-MM-DD'),
  };
};
