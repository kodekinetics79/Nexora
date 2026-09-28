import { useMemo, useRef, useState, type PointerEvent } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { Box, ButtonBase, Stack, Typography } from '@mui/material';
import dashboardService, { type DeadlineBoardDTO, type DeadlineLeadDTO } from '../../../api/services/dashboardService';
import { toPresentableError } from '../../../utils/apiErrors';
import BandShell from './BandShell';
import { scopeWords } from './scopeWords';
import { seriesVar, type SeriesToken } from './tokens';
import { neuBar, neuFocus, neuInset, neuKey, neuRaised } from './neumorphic';
import { useEscapeWithin } from './useEscapeWithin';
import ChartMenu from './ChartMenu';
import { useChartChoice } from './chartPrefs';

/**
 * Band 4 — what is closing on us.
 *
 * Seven columns of open enquiries by how long is left to answer them, drawn in the server's own
 * bucket order and ALWAYS all seven, at zero as much as at two hundred. That is the point of the
 * band rather than a stylistic choice: a rep who has learned that "past deadline" is the leftmost
 * column and "no closing date" the rightmost can find her own worst news without reading a single
 * label, and a chart that drops its empty categories moves every remaining column every time the
 * data changes. So a zero column keeps its slot, its label and its figure, and renders as a
 * baseline tick — a mark that reads as "measured, and it is none", where a blank slot reads as
 * "we did not ask".
 *
 * Depth comes from the material: each bar is a lit slab — gradient down its face, a highlight
 * along its top edge, a shadow cast under it — and lifts when you press it. It is never tilted and
 * never extruded, because a perspective bar puts its front edge lower than its top and the reader
 * takes the front edge for the value.
 *
 * NOTHING HERE IS COMPUTED. Every count and every line total is a field of DeadlineBoardDTO; the
 * only arithmetic is the pixel height of a bar. The band states no percentage and no total of its
 * own, and where the server publishes a caveat — enquiries that arrived after their own deadline —
 * it is printed rather than folded away.
 *
 * The band answers its own follow-up question in place. Press a column (or drag across several)
 * and the enquiries it counted are listed under the chart, each one a door to its lead; "All
 * deadlines" or Escape puts the chart back. The measure switch on the chart swaps what the columns
 * count — enquiries or line items — and the reader's choice follows them to any browser.
 */

type BucketTone = Extract<SeriesToken, 'oxide' | 'brassMark' | 'graphite' | 'muted'>;

interface BucketColumn {
  key: string;
  /** Used only until the server's own label for this key arrives, and in the empty and error frames. */
  label: string;
  tone: BucketTone;
  /** Far-out work is settled volume, not something to act on today: same graphite, held back. */
  dimmed?: boolean;
}

/**
 * The fixed columns, in DashboardRepository.BucketOrder's order and with its keys verbatim.
 *
 * The colours carry the band's one idea. Oxide is late. Brass is the work that is still yours to
 * act on — today, and the two windows a quote can still realistically be built in. Graphite is
 * volume that has settled: real work, but not this week's, so it is held back rather than made
 * quiet by shrinking it. Muted is the data gap: an enquiry with no stated closing date cannot be
 * scheduled at all, and colouring it brass would promise a deadline the document never gave us.
 */
export const CLOSING_COLUMNS: readonly BucketColumn[] = Object.freeze([
  { key: 'overdue', label: 'Past deadline', tone: 'oxide' },
  { key: 'today', label: 'Closing today', tone: 'brassMark' },
  { key: 'days_1_3', label: '1–3 days', tone: 'brassMark' },
  { key: 'days_4_7', label: '4–7 days', tone: 'brassMark' },
  { key: 'days_8_30', label: '8–30 days', tone: 'graphite', dimmed: true },
  { key: 'later', label: 'More than 30 days', tone: 'graphite', dimmed: true },
  { key: 'unknown', label: 'No closing date', tone: 'muted' },
]);

/** Plot height in px. Held in every state, so the band cannot change size when data arrives. */
const PLOT_HEIGHT = 76;
/** A measured zero. Three pixels of the column's own colour, sitting on the baseline. */
const ZERO_TICK = 3;
/**
 * The shortest a bar with something in it may be drawn. One enquiry beside two hundred is a
 * sub-pixel bar, and an invisible mark next to the figure "1" reads as a rendering fault. The
 * figure above the bar is the value; the bar is only the comparison, so a floor costs nothing that
 * a reader could misread.
 */
const MIN_BAR = 7;

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

type Measure = 'enquiries' | 'lines';
const MEASURES = ['enquiries', 'lines'] as const;
const MEASURE_OPTIONS: readonly { value: Measure; label: string }[] = [
  { value: 'enquiries', label: 'Enquiries' },
  { value: 'lines', label: 'Line items' },
];

/**
 * The most rows the drill asks for — the server's own ceiling (DashboardRepository clamps at 1000).
 * The server returns rows most urgent first, so a cut falls on the far-out buckets; the drill says
 * so whenever it lists fewer rows than the bucket counted.
 */
const DRILL_MAX_LEADS = 1000;

/** How long after a drag ends a stray click is ignored, so a drag never also drills one column. */
const DRAG_CLICK_GUARD_MS = 350;

/** "3 days late", "Due today", "4 days left", or the data gap said plainly. */
const timeLeft = (lead: DeadlineLeadDTO): string => {
  if (lead.daysLeft === null || lead.daysLeft === undefined) return 'No closing date';
  if (lead.daysLeft < 0) return `${plural(-lead.daysLeft, 'day', 'days')} late`;
  if (lead.daysLeft === 0) return 'Due today';
  return `${plural(lead.daysLeft, 'day', 'days')} left`;
};

export interface ClosingBandProps {
  /** Where "details →" opens; the page passes it only when the reader may open that page. */
  detailsTo?: string;
  /** A manager's rep filter: only this rep's own work. Absent = everyone the reader can see. */
  ownerUserId?: number;
  /** The band's numeral in the screen's sentence. */
  step?: string;
  index?: number;
  /**
   * Where "see all" goes when the drill list is shorter than the bucket. Defaults to the deadline
   * board carrying the bucket key, which is the screen that lists exactly these rows.
   */
  onOpenBucket?: (bucketKey: string) => void;
}

export default function ClosingBand({ step = '4', index = 0, onOpenBucket, detailsTo, ownerUserId }: ClosingBandProps) {
  const navigate = useNavigate();
  const [measure, setMeasure] = useChartChoice('closing', MEASURES, 'enquiries');

  // The picked buckets, as a [from, to] run of column indexes. Null is the whole chart.
  const [picked, setPicked] = useState<readonly [number, number] | null>(null);
  // A drag in progress: where it started and where the pointer is now.
  const [drag, setDrag] = useState<{ from: number; to: number } | null>(null);
  const dragEndedAt = useRef(0);

  // Its own query, its own key, its own failure. A band that cannot load must not take a neighbour
  // down with it, so there is no shared fetch and no composite endpoint anywhere on this screen.
  // maxLeads is the smallest the server accepts: the bucket counts are computed over every open
  // enquiry in scope regardless, and the chart draws no rows, so asking for two hundred lead
  // records to render seven numbers would be a payload nobody reads.
  const board = useQuery({
    queryKey: ['glance', 'closing-band', ownerUserId ?? 'all'],
    queryFn: () => dashboardService.getDeadlineBoard({ maxLeads: 1, ownerUserId }),
    staleTime: 60_000,
    meta: { silenceGlobalError: true, errorLabel: 'the deadline board' },
  });

  // The rows are fetched only once a reader drills in, and then once for every bucket.
  const rows = useQuery({
    queryKey: ['glance', 'closing-band', 'rows', ownerUserId ?? 'all'],
    queryFn: () => dashboardService.getDeadlineBoard({ maxLeads: DRILL_MAX_LEADS, ownerUserId }),
    enabled: picked !== null,
    staleTime: 60_000,
    meta: { silenceGlobalError: true, errorLabel: 'the enquiries in this group' },
  });

  // Failure copy comes from the product's error-presentation boundary, which renders the server's
  // own sentence where the status permits one and substitutes governed wording where it does not —
  // notably a 403, whose message must stay the permission wording rather than this band's. No
  // fallback is passed for that reason: a band-specific sentence would override the refusal.
  const failure = board.isError ? toPresentableError(board.error, { context: 'list' }) : null;

  const model = useMemo(() => {
    const data: DeadlineBoardDTO | undefined = board.data;
    const byKey = new Map((data?.buckets ?? []).map((bucket) => [bucket.key, bucket]));
    const columns = CLOSING_COLUMNS.map((column) => {
      const served = byKey.get(column.key);
      return {
        ...column,
        // The server's own wording wins wherever it sent one; ours exists so the empty frame and
        // the error frame still carry seven labelled columns.
        label: served?.label?.trim() || column.label,
        // A bucket the server did not send was not counted. It is drawn as "—", never as a 0,
        // because a 0 would say "measured, and it is none".
        counted: !!served,
        leads: served?.leads ?? 0,
        lineItems: served?.lineItems ?? 0,
      };
    });
    const valueOf = (column: (typeof columns)[number]) => (measure === 'lines' ? column.lineItems : column.leads);
    const tallest = columns.reduce((max, column) => Math.max(max, valueOf(column)), 0);
    return {
      columns,
      tallest,
      valueOf,
      // Buckets this build has never heard of. Silently dropping one would hide open work behind a
      // deploy-order mismatch, so the count is disclosed rather than absorbed.
      unknownBuckets: (data?.buckets ?? []).filter((bucket) => !CLOSING_COLUMNS.some((c) => c.key === bucket.key)).length,
    };
  }, [board.data, measure]);

  // What the drill lists: the picked buckets' keys, their own counts, and the rows the server sent.
  const drill = useMemo(() => {
    if (!picked) return null;
    const [lo, hi] = picked;
    const columns = model.columns.slice(lo, hi + 1);
    const keys = new Set(columns.map((column) => column.key));
    const counted = columns.filter((column) => column.counted);
    const listed = (rows.data?.leads ?? []).filter((lead) => keys.has(lead.bucket));
    return {
      columns,
      enquiries: counted.reduce((sum, column) => sum + column.leads, 0),
      lineItems: counted.reduce((sum, column) => sum + column.lineItems, 0),
      uncounted: columns.length - counted.length,
      listed,
    };
  }, [picked, model.columns, rows.data]);

  const openBucket = (bucketKey?: string) => {
    if (bucketKey && onOpenBucket) { onOpenBucket(bucketKey); return; }
    navigate(bucketKey ? `/analytics/deadlines?bucket=${encodeURIComponent(bucketKey)}` : '/analytics/deadlines');
  };

  const isPicked = (columnIndex: number) => {
    const run = drag ? [Math.min(drag.from, drag.to), Math.max(drag.from, drag.to)] : picked;
    return !!run && columnIndex >= run[0] && columnIndex <= run[1];
  };

  // Pointer down starts a run; moving across columns stretches it; letting go picks it. A run of
  // one column is left to the click that follows, so a plain press and Enter behave the same.
  const columnAt = (event: PointerEvent<HTMLElement>): number | null => {
    // Under pointer capture the event's target is the captor, so the column under the finger is
    // found by position where the browser can say, and by target where it cannot (jsdom).
    const hit = document.elementFromPoint?.(event.clientX, event.clientY) ?? (event.target as Element | null);
    const found = hit?.closest?.('[data-bucket-index]')?.getAttribute('data-bucket-index');
    return found === undefined || found === null ? null : Number(found);
  };
  const onPointerDown = (columnIndex: number) => (event: PointerEvent<HTMLElement>) => {
    if (event.button !== 0) return;
    event.currentTarget.setPointerCapture?.(event.pointerId);
    setDrag({ from: columnIndex, to: columnIndex });
  };
  const onPointerMove = (event: PointerEvent<HTMLElement>) => {
    if (!drag) return;
    const at = columnAt(event);
    if (at !== null && at !== drag.to) setDrag({ ...drag, to: at });
  };
  const onPointerUp = () => {
    if (!drag) return;
    if (drag.from !== drag.to) {
      setPicked([Math.min(drag.from, drag.to), Math.max(drag.from, drag.to)]);
      dragEndedAt.current = Date.now();
    }
    setDrag(null);
  };
  const onColumnClick = (columnIndex: number) => {
    if (Date.now() - dragEndedAt.current < DRAG_CLICK_GUARD_MS) return;
    setPicked([columnIndex, columnIndex]);
  };
  const escapeRef = useEscapeWithin<HTMLDivElement>(!!picked, () => setPicked(null));

  // The server's own count of open work decides this, not the height of the tallest column: a
  // tenant whose only enquiries landed in a bucket this build cannot draw has work, and telling
  // it there is nothing scheduled would be wrong.
  const isEmpty = !!board.data && board.data.openLeads === 0;
  const pickedLabel = drill
    ? drill.columns.length === 1 ? drill.columns[0].label : `${drill.columns.length} buckets`
    : '';

  return (
    <BandShell
      detailsTo={detailsTo}
      title="What's closing on us"
      step={step}
      index={index}
      minHeight={240}
      hint="Open enquiries by how long is left to answer them. Press a column to list the ones it counted, or drag across several columns to list them together."
      loading={board.isLoading}
      error={failure && failure.status !== 403 ? failure.message : null}
      forbidden={failure && failure.status === 403 ? failure.message : null}
      onRetry={() => void board.refetch()}
      seal={{
        // The endpoint resolves the caller's account-team scope server-side but publishes no scope
        // word on the payload, so this band cannot state whose numbers these are and says so.
        // Borrowing a neighbouring band's scope would be a guess printed as a fact.
        scope: scopeWords(undefined),
        window: 'Every open enquiry',
        generatedAt: board.data?.generatedAt ?? null,
        // The deadline board takes no from/to at all: it looks forward from today over everything
        // still open. An outlined seal is the screen's way of saying the period control above does
        // not reach this band.
        governed: false,
      }}
    >
      <Stack ref={escapeRef} spacing={1} sx={{ minWidth: 0 }}>

        {/* The measure switch sits where the axis title would. */}
        <Stack direction="row" spacing={0.75} sx={{ alignItems: 'center' }}>
          <Typography component="span" sx={{ fontSize: 11, color: 'text.secondary' }}>Columns show</Typography>
          <ChartMenu label="Columns show" value={measure} options={MEASURE_OPTIONS} onChange={setMeasure} />
        </Stack>

        <Box
          role="group"
          aria-label="Open enquiries by time left"
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={() => setDrag(null)}
          sx={{
            display: 'grid',
            gridTemplateColumns: 'repeat(7, minmax(0, 1fr))',
            // Five shared rows — count, plot, baseline, label, lines — that every column subgrids
            // into. A label that wraps to three lines then grows its row for all seven columns,
            // so every bar keeps the same baseline and the heights stay comparable.
            gridTemplateRows: 'repeat(5, auto)',
            columnGap: { xs: 0.5, sm: 0.75 },
            rowGap: 0,
          }}
        >
          {model.columns.map((column, columnIndex) => {
            const value = model.valueOf(column);
            const zero = !column.counted || value === 0;
            const height = zero
              ? ZERO_TICK
              : Math.max(MIN_BAR, Math.round((value / model.tallest) * PLOT_HEIGHT));
            const pressed = isPicked(columnIndex);
            return (
              <ButtonBase
                key={column.key}
                data-bucket-index={columnIndex}
                aria-pressed={pressed}
                disabled={!column.counted}
                onClick={() => onColumnClick(columnIndex)}
                onPointerDown={onPointerDown(columnIndex)}
                aria-label={column.counted
                  ? `${column.label}: ${plural(column.leads, 'open enquiry', 'open enquiries')}, ${plural(column.lineItems, 'line', 'lines')}. Lists the enquiries this counted.`
                  : `${column.label}: not counted by the server.`}
                sx={(theme) => ({
                  // Each column is a soft key on the clay: it stands proud, rises on hover and
                  // presses in when opened — the column is the button that opens its enquiries.
                  ...neuFocus,
                  display: 'grid',
                  gridRow: 'span 5',
                  gridTemplateRows: 'subgrid',
                  rowGap: 0,
                  alignItems: 'start',
                  borderRadius: '12px',
                  px: 0.5,
                  pt: 0.75,
                  pb: 0.75,
                  textAlign: 'center',
                  // Horizontal drags are ours (they pick a run of columns); vertical ones still scroll.
                  touchAction: 'pan-y',
                  userSelect: 'none',
                  // A picked column stays pressed in, so the reader can see what the list below is of.
                  boxShadow: pressed ? neuInset(theme.palette.mode, 3) : neuRaised(theme.palette.mode, 3),
                  transform: pressed ? 'translateY(1px)' : 'none',
                  transition: 'transform 180ms cubic-bezier(0.2, 0.7, 0.2, 1), box-shadow 180ms ease-out',
                  '&:hover': pressed ? {} : { transform: 'translateY(-2px)', boxShadow: neuRaised(theme.palette.mode, 6) },
                  '&:active': { transform: 'translateY(1px)', boxShadow: neuInset(theme.palette.mode, 3) },
                  '@media (prefers-reduced-motion: reduce)': {
                    transition: 'none',
                    '&:hover, &:active': { transform: 'none' },
                  },
                })}
              >
                <Typography
                  component="span"
                  data-testid={`closing-value-${column.key}`}
                  sx={{
                    fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
                    fontSize: 20, lineHeight: 1.1, fontVariantNumeric: 'tabular-nums',
                    color: 'text.primary',
                  }}
                >
                  {column.counted ? value.toLocaleString() : '—'}
                </Typography>
                {/* The plot cell keeps its full height whatever the bar does, which is what stops
                    the band from resizing between a zero tenant and a busy one. */}
                <Box sx={{ height: PLOT_HEIGHT, display: 'flex', alignItems: 'flex-end', mt: 0.5, opacity: column.dimmed ? 0.62 : 1 }}>
                  <Box
                    data-testid={`closing-bar-${column.key}`}
                    data-zero={zero ? 'true' : 'false'}
                    aria-hidden
                    className="nx-enter"
                    data-decorative-motion="true"
                    style={{ animationDelay: `${columnIndex * 45}ms` }}
                    sx={(theme) => ({
                      width: '100%',
                      height: `${height}px`,
                      // The bar grows or shrinks to its new value when the measure changes.
                      transition: 'height 320ms cubic-bezier(0.2, 0.7, 0.2, 1)',
                      '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
                      borderRadius: zero ? 0.5 : '10px 10px 4px 4px',
                      // Lit slab, not a flat rectangle: the face falls off downwards, a highlight
                      // sits on the top edge and the shadow is cast beneath. All of the depth is
                      // in the lighting, none of it in the geometry, so the top of the bar is the
                      // only edge the eye can read a value from.
                      backgroundColor: seriesVar(column.tone),
                      backgroundImage: zero
                        ? 'none'
                        : 'linear-gradient(180deg, rgba(255,255,255,0.30) 0%, rgba(255,255,255,0.06) 42%, rgba(0,0,0,0.14) 100%)',
                      boxShadow: zero ? 'none' : neuBar(theme.palette.mode),
                    })}
                  />
                </Box>
                {/* The baseline. One rule under every column, drawn even where the bar is a tick,
                    so "zero" always has something to sit on. */}
                <Box aria-hidden sx={{ height: '1px', backgroundColor: 'divider', mt: '2px', mb: 0.75 }} />
                <Typography
                  component="span"
                  sx={{ fontSize: 10.5, fontWeight: 700, lineHeight: 1.2, color: 'text.primary' }}
                >
                  {column.label}
                </Typography>
                <Typography
                  component="span"
                  sx={{ fontSize: 10.5, lineHeight: 1.25, color: 'text.secondary', fontVariantNumeric: 'tabular-nums' }}
                >
                  {/* Under the bar, whichever measure the bar is not showing. */}
                  {!column.counted ? '—' : measure === 'lines'
                    ? plural(column.leads, 'enquiry', 'enquiries')
                    : plural(column.lineItems, 'line', 'lines')}
                </Typography>
              </ButtonBase>
            );
          })}
        </Box>

        {drill ? (
          <Stack spacing={0.75} aria-label="Enquiries in the picked columns" role="region">
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between' }}>
              <Typography sx={{ fontSize: 12.5, fontWeight: 700, color: 'text.primary' }}>
                {pickedLabel} · {plural(drill.enquiries, 'enquiry', 'enquiries')} · {plural(drill.lineItems, 'line', 'lines')}
              </Typography>
              <ButtonBase
                onClick={() => setPicked(null)}
                sx={(theme) => ({
                  ...neuKey(theme.palette.mode), ...neuFocus,
                  px: 1, py: 0.25, borderRadius: '10px', fontSize: 12, fontWeight: 700, color: 'text.primary', minHeight: 26,
                })}
              >
                ← All deadlines
              </ButtonBase>
            </Stack>
            {drill.uncounted > 0 && (
              <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
                {plural(drill.uncounted, 'column was', 'columns were')} not counted by the server and {drill.uncounted === 1 ? 'is' : 'are'} not in these figures.
              </Typography>
            )}
            {rows.isLoading ? (
              <Typography variant="caption" sx={{ color: 'text.secondary' }}>Loading the enquiries…</Typography>
            ) : rows.isError ? (
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                  {toPresentableError(rows.error, { context: 'list' }).message}
                </Typography>
                <ButtonBase onClick={() => void rows.refetch()} sx={{ ...neuFocus, fontSize: 12, fontWeight: 700, textDecoration: 'underline' }}>
                  Retry
                </ButtonBase>
              </Stack>
            ) : drill.enquiries === 0 && drill.listed.length === 0 ? (
              <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                No open enquiries {drill.columns.length === 1 ? 'in this column' : 'in these columns'}.
              </Typography>
            ) : (
              <>
                <Box
                  component="ul"
                  sx={{ listStyle: 'none', m: 0, p: 0, maxHeight: 220, overflowY: 'auto', display: 'grid', gap: 0.5 }}
                >
                  {drill.listed.map((lead) => (
                    <Box component="li" key={lead.leadId}>
                      <ButtonBase
                        onClick={() => navigate(`/procurement/leads/${lead.leadId}/workbench`)}
                        aria-label={`Open ${lead.rfqno || `enquiry ${lead.leadId}`}, ${lead.buyersName || 'customer not stated'}, ${timeLeft(lead)}, ${plural(lead.lineItems, 'line', 'lines')}`}
                        sx={(theme) => ({
                          ...neuFocus,
                          width: '100%', display: 'grid', gridTemplateColumns: 'minmax(0, 1.1fr) minmax(0, 1.6fr) auto auto',
                          columnGap: 1, alignItems: 'center', textAlign: 'left',
                          px: 1, py: 0.5, borderRadius: '10px', fontSize: 12,
                          boxShadow: neuRaised(theme.palette.mode, 2),
                          '&:hover': { boxShadow: neuRaised(theme.palette.mode, 4) },
                        })}
                      >
                        <Box component="span" sx={{ fontWeight: 700, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {lead.rfqno || `#${lead.leadId}`}
                        </Box>
                        <Box component="span" sx={{ color: 'text.secondary', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {lead.buyersName || 'Customer not stated'}
                        </Box>
                        <Box component="span" sx={{ color: lead.daysLeft !== null && lead.daysLeft < 0 ? seriesVar('oxide') : 'text.primary', whiteSpace: 'nowrap' }}>
                          {timeLeft(lead)}
                        </Box>
                        <Box component="span" sx={{ color: 'text.secondary', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
                          {plural(lead.lineItems, 'line', 'lines')}
                        </Box>
                      </ButtonBase>
                    </Box>
                  ))}
                </Box>
                {/* The server lists the most urgent enquiries first and stops at its ceiling, so a
                    list shorter than its own count says so instead of passing for the whole group. */}
                {drill.listed.length < drill.enquiries && (
                  <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
                    Showing {drill.listed.length.toLocaleString()} of {drill.enquiries.toLocaleString()} — the list stops at
                    the {DRILL_MAX_LEADS.toLocaleString()} most urgent open enquiries.{' '}
                    <ButtonBase
                      onClick={() => openBucket(drill.columns.length === 1 ? drill.columns[0].key : undefined)}
                      sx={{ ...neuFocus, fontSize: 'inherit', fontWeight: 700, textDecoration: 'underline', verticalAlign: 'baseline' }}
                    >
                      See all on the deadline board
                    </ButtonBase>
                  </Typography>
                )}
              </>
            )}
          </Stack>
        ) : isEmpty ? (
          /* Not "all clear". On a tenant with nothing in it an empty urgency board means no open
             enquiry has a deadline against it — that is an empty diary, not a quiet week, and
             telling a rep everything is under control would be the screen's first lie. */
          <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            Nothing is scheduled yet. No open enquiry is waiting on an answer, so there is no
            deadline to count down to — the columns fill in as enquiries arrive.
          </Typography>
        ) : (
          <Stack spacing={0.25}>
            <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
              {plural(board.data?.openLeads ?? 0, 'open enquiry', 'open enquiries')} carrying{' '}
              {plural(board.data?.openLineItems ?? 0, 'line', 'lines')}.
            </Typography>
            {(board.data?.lateIngestedExcludedLeads ?? 0) > 0 && (
              <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
                {board.data!.lateIngestedExcludedLeads.toLocaleString()} of them reached Nexora after their
                own deadline had already passed. They sit under “Past deadline” because they are, but nobody
                here answered late.
              </Typography>
            )}
            {model.unknownBuckets > 0 && (
              <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
                The server also returned {plural(model.unknownBuckets, 'group', 'groups')} of enquiries this
                screen does not yet know how to show. They are not in the columns above.
              </Typography>
            )}
          </Stack>
        )}
      </Stack>
    </BandShell>
  );
}
