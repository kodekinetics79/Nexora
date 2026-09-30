import { useCallback, useMemo } from 'react';
import { useSearchParams } from 'react-router-dom';

/**
 * The column-header filters a list page carries, held on the URL.
 *
 * The URL is the only store: a filtered list can be bookmarked, sent to a colleague and walked back
 * with the browser's Back button, and nothing is remembered anywhere else. Defaults are never
 * written, so an unfiltered list keeps a clean address, and a value the page cannot read (a hand-
 * edited or stale link) falls back to the default instead of emptying the list.
 *
 * Generic on purpose: Leads uses it first, RFQs and Quotes are meant to reuse it unchanged. Each key
 * has one KIND that says what a readable value looks like; a page uses the keys it has columns for.
 */

export const DUE_WINDOWS = ['overdue', '7d', '14d'] as const;
export type DueWindow = (typeof DUE_WINDOWS)[number];

/** Short words for each window, shared by the header filter and the filtered-empty message. */
export const DUE_WINDOW_LABELS: Record<DueWindow, string> = {
  overdue: 'Overdue',
  '7d': 'Next 7 days',
  '14d': 'Next 14 days',
};

/** Looking back from today: when a record arrived. */
export const RECEIVED_WINDOWS = ['today', '7d', '30d'] as const;
export type ReceivedWindow = (typeof RECEIVED_WINDOWS)[number];
export const RECEIVED_WINDOW_LABELS: Record<ReceivedWindow, string> = {
  today: 'Today',
  '7d': 'Last 7 days',
  '30d': 'Last 30 days',
};

/** Looking ahead from today: when the goods are wanted. */
export const AHEAD_WINDOWS = ['30d'] as const;
export type AheadWindow = (typeof AHEAD_WINDOWS)[number];
export const AHEAD_WINDOW_LABELS: Record<AheadWindow, string> = { '30d': 'Next 30 days' };

export const OWNER_CHOICES = ['unassigned', 'mine'] as const;
export type OwnerChoice = (typeof OWNER_CHOICES)[number];

/** A customer or status id, or `none` for records with nothing linked yet. */
export const NO_CUSTOMER = 'none';
export const NOT_OPENED = 'none';

type Kind =
  | { kind: 'enum'; values: readonly string[] }
  /** A positive id, or `none`. Kept as text: it goes straight back to the server. */
  | { kind: 'idOrNone' }
  /** A positive whole number. */
  | { kind: 'id' }
  /** A whole number of zero or more. */
  | { kind: 'count' }
  /** A calendar day, `yyyy-MM-dd`. */
  | { kind: 'day' }
  /** Free text the server matches with "contains". */
  | { kind: 'text' }
  /** One short word (a source such as Email). */
  | { kind: 'word' };

const SCHEMA = {
  customer: { kind: 'idOrNone' },
  source: { kind: 'word' },
  status: { kind: 'idOrNone' },
  owner: { kind: 'enum', values: OWNER_CHOICES },
  rep: { kind: 'id' },
  due: { kind: 'enum', values: DUE_WINDOWS },
  dueFrom: { kind: 'day' },
  dueTo: { kind: 'day' },
  received: { kind: 'enum', values: RECEIVED_WINDOWS },
  receivedFrom: { kind: 'day' },
  receivedTo: { kind: 'day' },
  ingested: { kind: 'enum', values: RECEIVED_WINDOWS },
  ingestedFrom: { kind: 'day' },
  ingestedTo: { kind: 'day' },
  required: { kind: 'enum', values: AHEAD_WINDOWS },
  requiredFrom: { kind: 'day' },
  requiredTo: { kind: 'day' },
  itemsMin: { kind: 'count' },
  itemsMax: { kind: 'count' },
  serial: { kind: 'text' },
  rfq: { kind: 'text' },
  buyer: { kind: 'text' },
  agreement: { kind: 'text' },
} as const satisfies Record<string, Kind>;

export type ListFilterKey = keyof typeof SCHEMA;
export const LIST_FILTER_KEYS = Object.keys(SCHEMA) as ListFilterKey[];

export interface ListFilterValues {
  customer: string | null;
  source: string | null;
  status: string | null;
  owner: OwnerChoice | null;
  rep: number | null;
  due: DueWindow | null;
  dueFrom: string | null;
  dueTo: string | null;
  received: ReceivedWindow | null;
  receivedFrom: string | null;
  receivedTo: string | null;
  ingested: ReceivedWindow | null;
  ingestedFrom: string | null;
  ingestedTo: string | null;
  required: AheadWindow | null;
  requiredFrom: string | null;
  requiredTo: string | null;
  itemsMin: number | null;
  itemsMax: number | null;
  serial: string | null;
  rfq: string | null;
  buyer: string | null;
  agreement: string | null;
}

export const EMPTY_LIST_FILTERS: ListFilterValues = Object.freeze(
  Object.fromEntries(LIST_FILTER_KEYS.map((key) => [key, null])) as unknown as ListFilterValues,
);

const POSITIVE_INTEGER = /^[1-9]\d*$/;
const COUNT = /^(0|[1-9]\d{0,8})$/;
const WORD = /^[A-Za-z][A-Za-z _-]{0,39}$/;
const TEXT_LIMIT = 200;

/** True for a real calendar day written `yyyy-MM-dd` (2026-02-30 is not one). */
export const isCalendarDay = (value: string): boolean => {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!match) return false;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(year, month - 1, day);
  return date.getFullYear() === year && date.getMonth() === month - 1 && date.getDate() === day;
};

const readValue = (kind: Kind, raw: string): string | number | null => {
  if (!raw) return null;
  switch (kind.kind) {
    case 'enum': return kind.values.includes(raw) ? raw : null;
    case 'idOrNone': return raw === 'none' || POSITIVE_INTEGER.test(raw) ? raw : null;
    case 'id': return POSITIVE_INTEGER.test(raw) ? Number(raw) : null;
    case 'count': return COUNT.test(raw) ? Number(raw) : null;
    case 'day': return isCalendarDay(raw) ? raw : null;
    case 'text': return raw.length <= TEXT_LIMIT ? raw : null;
    case 'word': return WORD.test(raw) ? raw : null;
    default: return null;
  }
};

export const parseListFilters = (params: URLSearchParams): ListFilterValues => {
  const values = { ...EMPTY_LIST_FILTERS } as Record<ListFilterKey, string | number | null>;
  LIST_FILTER_KEYS.forEach((key) => {
    values[key] = readValue(SCHEMA[key], params.get(key)?.trim() ?? '');
  });
  const parsed = values as unknown as ListFilterValues;
  // A preset and a custom range for the same date cannot both hold; the preset wins.
  if (parsed.due) { parsed.dueFrom = null; parsed.dueTo = null; }
  if (parsed.received) { parsed.receivedFrom = null; parsed.receivedTo = null; }
  if (parsed.ingested) { parsed.ingestedFrom = null; parsed.ingestedTo = null; }
  if (parsed.required) { parsed.requiredFrom = null; parsed.requiredTo = null; }
  // One rep's list is a slice of everyone's: "Unassigned" or "Mine" plus a rep is not a question.
  if (parsed.owner) parsed.rep = null;
  return parsed;
};

/** True while any key narrows the list. */
export const anyListFilter = (values: ListFilterValues): boolean =>
  LIST_FILTER_KEYS.some((key) => values[key] != null);

/**
 * The reader's today as `yyyy-MM-dd`, on the local calendar — the same day `calendarDaysUntil`
 * (utils/dates.ts) counts from, so "Overdue" here and the red Deadline cell never disagree.
 */
export const localToday = (now: Date = new Date()): string => localDay(now);

const pad = (n: number) => String(n).padStart(2, '0');
const localDay = (date: Date): string => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;

/** The local calendar day `days` after (or, negative, before) `now`. */
export const localDayOffset = (days: number, now: Date = new Date()): string =>
  localDay(new Date(now.getFullYear(), now.getMonth(), now.getDate() + days));

/** The server's due-date parameters: nothing at all for "Any", and always `today` with a window. */
export const dueParams = (due: DueWindow | null, now: Date = new Date()): { due?: DueWindow; today?: string } =>
  due ? { due, today: localToday(now) } : {};

/** A looking-back window as a day range ending today, inclusive: "Last 7 days" is today and the 6 before. */
export const receivedRange = (window: ReceivedWindow, now: Date = new Date()): { from: string; to: string } => {
  const back = window === 'today' ? 0 : window === '7d' ? 6 : 29;
  return { from: localDayOffset(-back, now), to: localToday(now) };
};

/**
 * A local day range as the two instants the server compares Ingested with: the reader's midnight
 * starting `from`, and the midnight after `to`. Ingested is a moment shown on the reader's clock,
 * so its filter is counted on that clock too.
 */
export const localDayInstants = (from: string | null, to: string | null): { from?: string; before?: string } => {
  const midnight = (day: string, plusDays: number) => {
    const [year, month, date] = day.split('-').map(Number);
    return new Date(year, month - 1, date + plusDays).toISOString();
  };
  return {
    ...(from ? { from: midnight(from, 0) } : {}),
    ...(to ? { before: midnight(to, 1) } : {}),
  };
};

/** A looking-ahead window as a day range starting today, inclusive. */
export const aheadRange = (window: AheadWindow, now: Date = new Date()): { from: string; to: string } => {
  const ahead = window === '30d' ? 30 : 0;
  return { from: localToday(now), to: localDayOffset(ahead, now) };
};

/** Writes the given values onto a copy of `params`: a default removes its key, other keys are kept. */
export const writeListFilters = (params: URLSearchParams, values: Partial<ListFilterValues>): URLSearchParams => {
  const next = new URLSearchParams(params);
  (Object.keys(values) as ListFilterKey[]).forEach((key) => {
    if (!(key in SCHEMA)) return;
    const value = values[key];
    const text = value == null ? '' : String(value).trim();
    if (text) next.set(key, text);
    else next.delete(key);
  });
  return next;
};

/** Removes only the filter keys from a copy of `params`. */
export const clearListFilterKeys = (params: URLSearchParams): URLSearchParams => {
  const next = new URLSearchParams(params);
  LIST_FILTER_KEYS.forEach((key) => next.delete(key));
  return next;
};

export interface UseListFilters extends ListFilterValues {
  /** The same values as one object that keeps its identity until the URL changes (a memo dependency). */
  values: ListFilterValues;
  /** True while any key narrows the list. */
  active: boolean;
  set: (values: Partial<ListFilterValues>) => void;
  clear: () => void;
  dueParams: () => { due?: DueWindow; today?: string };
}

const useListFilters = (): UseListFilters => {
  const [searchParams, setSearchParams] = useSearchParams();
  const values = useMemo(() => parseListFilters(searchParams), [searchParams]);

  const set = useCallback((next: Partial<ListFilterValues>) => {
    setSearchParams((current) => writeListFilters(current, next), { replace: true });
  }, [setSearchParams]);

  const clear = useCallback(() => {
    setSearchParams((current) => clearListFilterKeys(current), { replace: true });
  }, [setSearchParams]);

  const due = values.due;
  const dueParamsForReader = useCallback(() => dueParams(due), [due]);

  return {
    ...values,
    values,
    active: anyListFilter(values),
    set,
    clear,
    dueParams: dueParamsForReader,
  };
};

export default useListFilters;
