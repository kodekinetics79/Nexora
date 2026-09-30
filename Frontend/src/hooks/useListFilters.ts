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
 * Generic on purpose: each list defines its own keys once with `defineListFilters` and reads them
 * with `useListFilterState`; the default export is the Leads list's. Each key has one KIND that says
 * what a readable value looks like.
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

/** What a readable value looks like for one key. */
export type ListFilterKind =
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
type Kind = ListFilterKind;

type KindValue<K extends Kind> =
  K extends { kind: 'enum'; values: readonly (infer V)[] } ? V
    : K extends { kind: 'id' } | { kind: 'count' } ? number
      : string;

/** The values a schema's keys read as: each one null while it does not narrow the list. */
export type ListFilterValuesOf<S extends Record<string, Kind>> = { [K in keyof S]: KindValue<S[K]> | null };

/**
 * One list's filter keys and the rules between them, built once per list page. Every list keeps its
 * own keys on the URL; the reading, writing and clearing are the same for all of them.
 */
export interface ListFilterDefinition<S extends Record<string, Kind>> {
  schema: S;
  keys: (keyof S & string)[];
  empty: ListFilterValuesOf<S>;
  parse: (params: URLSearchParams) => ListFilterValuesOf<S>;
  write: (params: URLSearchParams, values: Partial<ListFilterValuesOf<S>>) => URLSearchParams;
  clearKeys: (params: URLSearchParams) => URLSearchParams;
  any: (values: ListFilterValuesOf<S>) => boolean;
}

export interface ListFilterRules<S extends Record<string, Kind>> {
  /** `[preset, from, to]`: a preset and a custom range for the same date cannot both hold; the preset wins. */
  presets?: readonly (readonly [keyof S & string, keyof S & string, keyof S & string])[];
  /** `[winner, loser]`: while the first holds, the second is dropped. */
  supersedes?: readonly (readonly [keyof S & string, keyof S & string])[];
}

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

/** Builds one list's filter keys: how each is read from the URL, written back and cleared. */
export const defineListFilters = <S extends Record<string, Kind>>(
  schema: S,
  rules: ListFilterRules<S> = {},
): ListFilterDefinition<S> => {
  const keys = Object.keys(schema) as (keyof S & string)[];
  const empty = Object.freeze(Object.fromEntries(keys.map((key) => [key, null]))) as unknown as ListFilterValuesOf<S>;

  const parse = (params: URLSearchParams): ListFilterValuesOf<S> => {
    const values = { ...empty } as Record<string, string | number | null>;
    keys.forEach((key) => {
      values[key] = readValue(schema[key], params.get(key)?.trim() ?? '');
    });
    (rules.presets ?? []).forEach(([preset, from, to]) => {
      if (values[preset] != null) { values[from] = null; values[to] = null; }
    });
    (rules.supersedes ?? []).forEach(([winner, loser]) => {
      if (values[winner] != null) values[loser] = null;
    });
    return values as unknown as ListFilterValuesOf<S>;
  };

  const write = (params: URLSearchParams, values: Partial<ListFilterValuesOf<S>>): URLSearchParams => {
    const next = new URLSearchParams(params);
    (Object.keys(values) as (keyof S & string)[]).forEach((key) => {
      if (!(key in schema)) return;
      const value = values[key];
      const text = value == null ? '' : String(value).trim();
      if (text) next.set(key, text);
      else next.delete(key);
    });
    return next;
  };

  const clearKeys = (params: URLSearchParams): URLSearchParams => {
    const next = new URLSearchParams(params);
    keys.forEach((key) => next.delete(key));
    return next;
  };

  const any = (values: ListFilterValuesOf<S>): boolean => keys.some((key) => values[key] != null);

  return { schema, keys, empty, parse, write, clearKeys, any };
};

/** The Leads list's keys. */
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

const LEAD_LIST_FILTERS = defineListFilters(SCHEMA, {
  presets: [['due', 'dueFrom', 'dueTo'], ['received', 'receivedFrom', 'receivedTo'], ['ingested', 'ingestedFrom', 'ingestedTo'], ['required', 'requiredFrom', 'requiredTo']],
  // One rep's list is a slice of everyone's: "Unassigned" or "Mine" plus a rep is not a question.
  supersedes: [['owner', 'rep']],
});

export type ListFilterKey = keyof typeof SCHEMA;
export const LIST_FILTER_KEYS: ListFilterKey[] = LEAD_LIST_FILTERS.keys;
export type ListFilterValues = ListFilterValuesOf<typeof SCHEMA>;
export const EMPTY_LIST_FILTERS: ListFilterValues = LEAD_LIST_FILTERS.empty;

export const parseListFilters = LEAD_LIST_FILTERS.parse;

/** True while any key narrows the list. */
export const anyListFilter = LEAD_LIST_FILTERS.any;

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
export const writeListFilters = LEAD_LIST_FILTERS.write;

/** Removes only the filter keys from a copy of `params`. */
export const clearListFilterKeys = LEAD_LIST_FILTERS.clearKeys;

export interface ListFilterState<S extends Record<string, Kind>> {
  /** The values as one object that keeps its identity until the URL changes (a memo dependency). */
  values: ListFilterValuesOf<S>;
  /** True while any key narrows the list. */
  active: boolean;
  set: (values: Partial<ListFilterValuesOf<S>>) => void;
  clear: () => void;
}

/** One list's filters, held on the URL. */
export const useListFilterState = <S extends Record<string, Kind>>(definition: ListFilterDefinition<S>): ListFilterState<S> => {
  const [searchParams, setSearchParams] = useSearchParams();
  const values = useMemo(() => definition.parse(searchParams), [definition, searchParams]);

  const set = useCallback((next: Partial<ListFilterValuesOf<S>>) => {
    setSearchParams((current) => definition.write(current, next), { replace: true });
  }, [definition, setSearchParams]);

  const clear = useCallback(() => {
    setSearchParams((current) => definition.clearKeys(current), { replace: true });
  }, [definition, setSearchParams]);

  return { values, active: definition.any(values), set, clear };
};

export interface UseListFilters extends ListFilterValues, ListFilterState<typeof SCHEMA> {
  dueParams: () => { due?: DueWindow; today?: string };
}

/** The Leads list's filters. */
const useListFilters = (): UseListFilters => {
  const state = useListFilterState(LEAD_LIST_FILTERS);
  const due = state.values.due;
  const dueParamsForReader = useCallback(() => dueParams(due), [due]);

  return {
    ...state.values,
    ...state,
    dueParams: dueParamsForReader,
  };
};

export default useListFilters;
