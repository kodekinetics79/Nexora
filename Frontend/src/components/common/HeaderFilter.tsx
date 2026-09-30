import { useRef, useState, type FormEvent } from 'react';
import {
  Box, Button, IconButton, ListSubheader, MenuItem, MenuList, Popover, Stack, TextField, Tooltip, Typography,
} from '@mui/material';
import { FilterList as FilterIcon, Check as CheckIcon } from '@mui/icons-material';

/**
 * A filter that lives in its column's header (owner 2026-09-29: "filter should be given over here
 * ... keep it simple"). The title stays; a small filter button beside it opens a short popover, and
 * turns brass while the column is filtered so the reader can see why the list is shorter.
 *
 * The popover holds one of three things:
 * - one list of choices (`options`), optionally with a "Custom range…" pair of dates (`range`);
 * - several lists under their own headings (`groups`), for a column that shows two facts;
 * - one "Contains…" text box (`text`).
 */

export interface HeaderFilterOption<V> {
  value: V;
  label: string;
}

/** One list of choices. `value` null is the list's "any" row. */
export interface HeaderFilterList<V extends string | number = string> {
  /** Heading above the list when the popover carries more than one. */
  label?: string;
  /** What this list narrows by, for its find box and its empty line: "Customer". */
  noun?: string;
  options: HeaderFilterOption<V>[];
  value: V | null;
  onChange: (value: V | null) => void;
  /** The "no filter" row, e.g. "Any customer". */
  anyLabel: string;
  /** Put the "no filter" row last (date and size lists read best presets-first). */
  anyLast?: boolean;
  /** Long lists (customers) get a type-to-find box. */
  searchable?: boolean;
  loading?: boolean;
  error?: boolean;
}

/**
 * A custom From/To day range offered under the list's presets. Days are `yyyy-MM-dd`. The list's own
 * `onChange` must clear the range too (a preset or "Any" replaces it) — one write, not two.
 */
export interface HeaderFilterRange {
  from: string | null;
  to: string | null;
  onApply: (from: string | null, to: string | null) => void;
}

/** A single "contains" text filter. */
export interface HeaderFilterText {
  value: string | null;
  onChange: (value: string | null) => void;
}

type ListProps<V extends string | number> = Partial<Omit<HeaderFilterList<V>, 'label' | 'noun'>>;

interface HeaderFilterProps<V extends string | number> extends ListProps<V> {
  title: string;
  /** What the filter narrows by, for the button's name: "Customer" → "Filter by customer". */
  noun: string;
  groups?: HeaderFilterList<string>[];
  range?: HeaderFilterRange;
  text?: HeaderFilterText;
}

/** "Customer" → "customer", but an acronym ("RFQ/Bid number") keeps its capitals. */
const lowerNoun = (noun: string): string => (/^[A-Z]{2}/.test(noun) ? noun : noun.charAt(0).toLowerCase() + noun.slice(1));

const rangeWords = (from: string | null, to: string | null): string =>
  from && to ? `${from} to ${to}` : from ? `from ${from}` : to ? `until ${to}` : '';

function HeaderFilter<V extends string | number>({
  title, noun, groups, range, text, options, value = null, onChange, anyLabel, anyLast = false,
  searchable = false, loading = false, error = false,
}: HeaderFilterProps<V>) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const [terms, setTerms] = useState<Record<number, string>>({});
  const [draftText, setDraftText] = useState('');
  const [rangeOpen, setRangeOpen] = useState(false);
  const [draftFrom, setDraftFrom] = useState('');
  const [draftTo, setDraftTo] = useState('');
  const findRef = useRef<HTMLInputElement | null>(null);
  const textRef = useRef<HTMLInputElement | null>(null);

  // One shape for the single list and the grouped lists.
  const lists: HeaderFilterList<string | number>[] = groups
    ? (groups as HeaderFilterList<string | number>[])
    : options && onChange
      ? [{
        noun, options, value, anyLabel: anyLabel ?? 'Any', anyLast, searchable, loading, error,
        onChange: onChange as (next: string | number | null) => void,
      }]
      : [];

  const rangeActive = Boolean(range && (range.from || range.to));
  const textActive = Boolean(text?.value);
  const active = lists.some((list) => list.value != null) || rangeActive || textActive;

  const summary = [
    ...lists.flatMap((list) => {
      const chosen = list.options.find((option) => option.value === list.value);
      return chosen ? [chosen.label] : list.value != null ? ['chosen'] : [];
    }),
    ...(rangeActive ? [rangeWords(range!.from, range!.to)] : []),
    ...(textActive ? [`contains "${text!.value}"`] : []),
  ].join(' · ');

  const close = () => {
    setAnchor(null);
    setTerms({});
  };
  const open = (target: HTMLElement) => {
    setDraftText(text?.value ?? '');
    setRangeOpen(rangeActive);
    setDraftFrom(range?.from ?? '');
    setDraftTo(range?.to ?? '');
    setAnchor(target);
  };
  const applyText = () => {
    text?.onChange(draftText.trim() || null);
    close();
  };
  const applyRange = () => {
    if (!range) return;
    let from = draftFrom || null;
    let to = draftTo || null;
    // A range typed back to front still means the days between the two.
    if (from && to && from > to) [from, to] = [to, from];
    range.onApply(from, to);
    close();
  };
  const firstSearchable = lists.findIndex((list) => list.searchable);

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.25, minWidth: 0 }}>
      <Typography component="span" sx={{ fontWeight: 700, fontSize: 'inherit', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
        {title}
      </Typography>
      <Tooltip title={active ? `${noun}: ${summary}` : `Filter by ${lowerNoun(noun)}`}>
        <IconButton
          size="small"
          aria-label={`Filter by ${lowerNoun(noun)}`}
          aria-pressed={active}
          // The header also sorts and drags on click; the filter button must do only its own job.
          onClick={(event) => { event.stopPropagation(); open(event.currentTarget); }}
          onMouseDown={(event) => event.stopPropagation()}
          sx={{
            p: 0.5,
            flexShrink: 0,
            color: active ? 'primary.contrastText' : 'text.secondary',
            bgcolor: active ? 'primary.main' : 'transparent',
            '&:hover': { bgcolor: active ? 'primary.dark' : 'action.hover' },
          }}
        >
          <FilterIcon sx={{ fontSize: 16 }} />
        </IconButton>
      </Tooltip>
      <Popover
        open={Boolean(anchor)}
        anchorEl={anchor}
        onClose={close}
        // The popover is portalled, but React still bubbles its events to the column header, which
        // sorts on click, drags on mouse-down and moves between columns on arrow keys.
        onClick={(event) => event.stopPropagation()}
        onMouseDown={(event) => event.stopPropagation()}
        onKeyDown={(event) => event.stopPropagation()}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
        slotProps={{
          paper: { sx: { mt: 0.5, minWidth: 220, maxWidth: 320 } },
          // Straight to typing for a text box or a long list, once the popover is on screen.
          transition: { onEntered: () => (text ? textRef.current?.focus() : findRef.current?.focus()) },
        }}
      >
        {text && (
          <Box
            component="form"
            onSubmit={(event: FormEvent) => { event.preventDefault(); applyText(); }}
            sx={{ p: 1.25, display: 'flex', flexDirection: 'column', gap: 1 }}
          >
            <TextField
              size="small"
              fullWidth
              inputRef={textRef}
              placeholder="Contains…"
              value={draftText}
              onChange={(event) => setDraftText(event.target.value)}
              slotProps={{ htmlInput: { 'aria-label': `${noun} contains` } }}
            />
            <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
              <Button size="small" color="inherit" onClick={() => { text.onChange(null); close(); }} sx={{ fontWeight: 700 }}>
                Clear
              </Button>
              <Button size="small" variant="contained" type="submit" disableElevation sx={{ fontWeight: 700 }}>
                Apply
              </Button>
            </Stack>
          </Box>
        )}

        {lists.map((list, index) => {
          const term = terms[index] ?? '';
          const needle = term.trim().toLowerCase();
          const shown = needle ? list.options.filter((option) => option.label.toLowerCase().includes(needle)) : list.options;
          const listNoun = list.noun ?? noun;
          const withRange = range && lists.length === 1;
          const anySelected = list.value == null && !(withRange && rangeActive);
          const anyRow = (
            <MenuItem key="any" selected={anySelected} onClick={() => { list.onChange(null); close(); }} sx={{ gap: 1 }}>
              <CheckIcon sx={{ fontSize: 16, visibility: anySelected ? 'visible' : 'hidden' }} />
              {list.anyLabel}
            </MenuItem>
          );
          return (
            <Box key={list.label ?? index} sx={{ borderTop: index > 0 ? '1px solid' : 0, borderColor: 'divider' }}>
              {list.label && (
                <ListSubheader component="div" disableSticky sx={{ lineHeight: 2.25, fontWeight: 800, fontSize: '0.72rem', textTransform: 'uppercase', letterSpacing: '0.04em' }}>
                  {list.label}
                </ListSubheader>
              )}
              {list.searchable && (
                <Box sx={{ px: 1, pt: list.label ? 0 : 1 }}>
                  <TextField
                    size="small"
                    fullWidth
                    inputRef={index === firstSearchable ? findRef : undefined}
                    placeholder="Type a name"
                    value={term}
                    onChange={(event) => setTerms((current) => ({ ...current, [index]: event.target.value }))}
                    onKeyDown={(event) => event.stopPropagation()}
                    slotProps={{ htmlInput: { 'aria-label': `Find a ${lowerNoun(listNoun)}` } }}
                  />
                </Box>
              )}
              <MenuList
                dense
                autoFocusItem={index === 0 && !list.searchable && !text}
                aria-label={list.label ?? listNoun}
                sx={{ maxHeight: 320, overflowY: 'auto' }}
              >
                {!list.anyLast && anyRow}
                {shown.map((option) => {
                  const selected = option.value === list.value;
                  return (
                    <MenuItem key={String(option.value)} selected={selected} onClick={() => { list.onChange(option.value); close(); }} sx={{ gap: 1 }}>
                      <CheckIcon sx={{ fontSize: 16, visibility: selected ? 'visible' : 'hidden' }} />
                      {option.label}
                    </MenuItem>
                  );
                })}
                {shown.length === 0 && (
                  <MenuItem disabled>
                    {list.loading ? 'Loading…' : list.error ? `${listNoun}s could not be loaded` : `No ${lowerNoun(listNoun)} matches`}
                  </MenuItem>
                )}
                {withRange && (
                  <MenuItem key="range" selected={rangeActive} onClick={() => setRangeOpen((current) => !current)} aria-expanded={rangeOpen} sx={{ gap: 1 }}>
                    <CheckIcon sx={{ fontSize: 16, visibility: rangeActive ? 'visible' : 'hidden' }} />
                    Custom range…
                  </MenuItem>
                )}
                {list.anyLast && anyRow}
              </MenuList>
            </Box>
          );
        })}

        {range && rangeOpen && (
          <Box
            component="form"
            aria-label="Custom range"
            onSubmit={(event: FormEvent) => { event.preventDefault(); applyRange(); }}
            sx={{ p: 1.25, pt: 1, display: 'flex', flexDirection: 'column', gap: 1, borderTop: '1px solid', borderColor: 'divider' }}
          >
            <Stack direction="row" spacing={1}>
              <TextField
                type="date"
                size="small"
                label="From"
                // The header's arrow keys must not act while a date is being typed.
                onKeyDown={(event) => event.stopPropagation()}
                value={draftFrom}
                onChange={(event) => setDraftFrom(event.target.value)}
                slotProps={{ inputLabel: { shrink: true } }}
              />
              <TextField
                type="date"
                size="small"
                label="To"
                // The header's arrow keys must not act while a date is being typed.
                onKeyDown={(event) => event.stopPropagation()}
                value={draftTo}
                onChange={(event) => setDraftTo(event.target.value)}
                slotProps={{ inputLabel: { shrink: true } }}
              />
            </Stack>
            <Stack direction="row" sx={{ justifyContent: 'flex-end' }}>
              <Button size="small" variant="contained" type="submit" disableElevation disabled={!draftFrom && !draftTo} sx={{ fontWeight: 700 }}>
                Apply
              </Button>
            </Stack>
          </Box>
        )}
      </Popover>
    </Box>
  );
}

export default HeaderFilter;
