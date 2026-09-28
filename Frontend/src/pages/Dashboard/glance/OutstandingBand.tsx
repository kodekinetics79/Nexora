import { useState, type KeyboardEvent } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Box, Link, Stack, Typography, type Theme } from '@mui/material';
import {
  type PipelineAnalyticsDTO,
  type PipelineStageDTO,
} from '../../../api/services/dashboardService';
import { toPresentableError } from '../../../utils/apiErrors';
import { formatMoney } from '../../../utils/currency';
import BandShell from './BandShell';
import Unavailable from './Unavailable';
import { useMeasuredWidth } from './useMeasuredWidth';
import HatchPattern, { HATCH_MEANING, hatchFill, useHatchPatternId } from './hatchPattern';
import { pipelineSeal, usePipelineAnalytics } from './pipelineAnalytics';
import { seriesVar, type SeriesToken } from './tokens';
import { useEscapeWithin } from './useEscapeWithin';
import ChartMenu from './ChartMenu';
import { useChartChoice } from './chartPrefs';
import { neuInset } from './neumorphic';

/**
 * Band 2 — what's out with customers, and where it stops.
 *
 * Two panels reading left to right as one thought. The left is the SENT BOOK: every quote that has
 * left the building, as one length in counts, cut into the four states such a quote can be in.
 * Solid means a decision exists; the awaiting segment is hollow — brass outline, the kit's hatch —
 * because that money is still in the air, and hatch means the same thing on every band of this
 * screen. The right is the FUNNEL, and it is four bars on one shared axis rather than a tapering
 * shape: a trapezoid encodes drop-off as an AREA, and a reader cannot compare areas, so the
 * narrowing does the arguing instead of the numbers.
 *
 * <p><b>No ratio is printed, ever.</b> The four stages count populations reached over different
 * spans — a quote written this month can belong to a request received last year — so a share
 * between stages would be a conversion nobody measured. Clicking a stage states its count beside
 * the stage before it, and nothing more.</p>
 *
 * <p>Both charts drill in place: click a stage or a segment and it is pressed in (pressed means
 * "this is what is in force", as on the rest of the screen) and one sentence appears under the
 * chart. Click again, or Escape, closes it.</p>
 *
 * The weighted forecast on this same payload is deliberately NOT drawn, here or anywhere on the
 * screen. It is an unmeasured 0.3/0.5 probability heuristic presented as an instruction, and it
 * was taken off the dashboard rather than relabelled; a band that reintroduced it would put a
 * fabricated number back on the one screen that exists to carry stated ones.
 */
export interface OutstandingBandProps {
  /** Inclusive first day of the selected window, YYYY-MM-DD. */
  from: string;
  /** Inclusive last day of the selected window, YYYY-MM-DD. */
  to: string;
  index?: number;
}

/**
 * The stages, in the order the endpoint publishes them and with its keys verbatim.
 *
 * The labels are only a floor. Where the server sends its own wording it wins; ours exists so the
 * empty frame and the error frame still carry four labelled bars, which is the whole point of a
 * band whose primary state is a tenant that has never quoted anything.
 */
interface FunnelStageDef {
  key: PipelineStageDTO['key'];
  label: string;
  /** What this stage's records are called, and how the drill sentence says one reached it. */
  noun: [string, string];
  reached: string;
  /** An existing list of these records. Unfiltered by the window; absent where no list exists. */
  list?: { to: string; words: string };
}

const FUNNEL_STAGES: readonly FunnelStageDef[] = Object.freeze([
  { key: 'leads', label: 'Requests in', noun: ['request', 'requests'], reached: 'came in', list: { to: '/procurement/leads/all', words: 'Open requests' } },
  { key: 'accepted', label: 'Accepted', noun: ['request', 'requests'], reached: 'accepted' },
  { key: 'quoted', label: 'Quotes written', noun: ['accepted request', 'accepted requests'], reached: 'quoted', list: { to: '/sales/quotes', words: 'Open quotes' } },
  { key: 'won', label: 'Won', noun: ['quote written', 'quotes written'], reached: 'won', list: { to: '/sales/quotes?state=outcomes', words: 'Open won, lost and expired quotes' } },
]);

/** Existing quote lists a sent-book segment can open. Replied has no list of its own, so none. */
const SEGMENT_LISTS: Readonly<Record<string, { to: string; words: string }>> = Object.freeze({
  won: { to: '/sales/quotes?state=outcomes', words: 'Open won, lost and expired quotes' },
  lost: { to: '/sales/quotes?state=outcomes', words: 'Open won, lost and expired quotes' },
  awaiting: { to: '/sales/quotes?state=sent', words: 'Open sent quotes' },
});

type Measure = 'count' | 'value';
const MEASURES = ['count', 'value'] as const;
const MEASURE_OPTIONS: readonly { value: Measure; label: string }[] = [
  { value: 'count', label: 'Where it stops · by count' },
  { value: 'value', label: 'Where it stops · by value' },
];

/** Bar widths move and bars lift; readers who asked for less motion get the end state at once. */
const BAR_MOTION = {
  transition: 'width 320ms cubic-bezier(0.2, 0, 0, 1), transform 180ms ease, filter 180ms ease',
  '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
} as const;

/** Enter and Space press a role=button that is not a native button. */
const pressOnKey = (toggle: () => void) => (e: KeyboardEvent) => {
  if (e.key === 'Enter' || e.key === ' ') {
    e.preventDefault();
    toggle();
  }
};

/** The sent book's geometry. Fixed, so the bar is the same object at 4 quotes and at 400. */
/** The design width, used until the band has measured its own. */
const BOOK_W = 440;
const BOOK_BAR_Y = 8;
const BOOK_BAR_H = 28;
const BOOK_H = 76;
/**
 * The shortest a segment carrying quotes may be drawn, and the space one direct label needs.
 * A single quote beside two hundred is a sub-pixel sliver, and an invisible segment next to the
 * numeral "1" reads as a rendering fault — the numeral is the value, the length is the comparison.
 */
const MIN_SEGMENT = 5;
const LABEL_GAP = 108;

/** The funnel's geometry, lifted from the executive FunnelPanel this band replaces. */
const FUNNEL_ROW = 22;
const FUNNEL_GAP = 4;
const FUNNEL_LABEL_W = 118;
/** The right-hand column each stage's money is printed in, clear of the longest bar and its count. */
const FUNNEL_MONEY_W = 80;
/** A measured zero: a short tick of the bar's own colour sitting on the axis, never a blank row. */
const ZERO_TICK = 3;

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

interface Segment {
  key: string;
  words: string;
  count: number;
  tone: SeriesToken;
  /** Hollow: outline and hatch instead of a fill, because no decision exists yet. */
  open?: boolean;
  /** The segment's money where the server states it; `currencyStated` false means it sent none. */
  value?: number | null;
  currency?: string | null;
  currencyStated?: boolean;
}

/**
 * Where each direct label sits, given where its segment actually is.
 *
 * Labels are placed under their own segment and then pushed apart to a legible spacing, first
 * left to right and then back right to left so a run that overflowed the right edge is pulled
 * inside rather than clipped. The leader line keeps the label attached to the segment it names,
 * which is what lets this bar carry no legend at all.
 */
const placeLabels = (anchors: number[], gap: number, width: number): number[] => {
  const out = anchors.map((a) => Math.min(Math.max(a, gap / 2), width - gap / 2));
  for (let i = 1; i < out.length; i += 1) out[i] = Math.max(out[i], out[i - 1] + gap);
  for (let i = out.length - 2; i >= 0; i -= 1) out[i] = Math.min(out[i], out[i + 1] - gap);
  return out;
};

/**
 * Segment lengths in pixels.
 *
 * Segments carrying quotes are floored at MIN_SEGMENT and the floor is paid for out of the
 * segments that can spare it, so the four lengths still sum to the full width and the bar remains
 * one continuous object.
 */
const segmentWidths = (counts: number[], width: number): number[] => {
  const total = counts.reduce((sum, n) => sum + n, 0);
  if (total <= 0) return counts.map(() => 0);
  const raw = counts.map((n) => (n / total) * width);
  const floored = raw.map((w, i) => (counts[i] > 0 ? Math.max(w, MIN_SEGMENT) : 0));
  const overflow = floored.reduce((sum, w) => sum + w, 0) - width;
  if (overflow <= 0) return floored;
  // Only the segments that are above the floor can give the space back, and they give it in
  // proportion to how far above it they are.
  const slack = floored.map((w) => Math.max(0, w - MIN_SEGMENT));
  const slackTotal = slack.reduce((sum, w) => sum + w, 0);
  if (slackTotal <= 0) return floored;
  return floored.map((w, i) => w - (slack[i] / slackTotal) * overflow);
};

/**
 * "SAR 2.14M" rather than "SAR 2,140,000.00": the figure sits in an 80px column beside its bar.
 * Falls back to the full format for a currency code Intl does not know, as formatMoney does.
 */
const compactMoney = (value: number, currency: string | null | undefined): string => {
  const code = currency?.trim();
  try {
    return new Intl.NumberFormat('en-US', code
      ? { style: 'currency', currency: code, currencyDisplay: 'code', notation: 'compact', maximumFractionDigits: 2 }
      : { notation: 'compact', maximumFractionDigits: 2 }).format(value).replace(/\u00a0/g, ' ');
  } catch {
    return formatMoney(value, currency);
  }
};

export default function OutstandingBand({ from, to, index = 2 }: OutstandingBandProps) {
  const hatchId = useHatchPatternId();
  // Both charts draw at the band's own width, so their labels render at their stated size.
  const [measureRef, bookW] = useMeasuredWidth<HTMLDivElement>(BOOK_W);

  const analytics = usePipelineAnalytics(from, to, 'the sent book and the funnel');
  const [measure, setMeasure] = useChartChoice('outstanding', MEASURES, 'count');
  // One drill open at a time across both charts, so the band never carries two sentences.
  const [picked, setPicked] = useState<{ chart: 'stage' | 'segment'; key: string } | null>(null);
  const escapeRef = useEscapeWithin<HTMLDivElement>(!!picked, () => setPicked(null));
  const toggle = (chart: 'stage' | 'segment', key: string) =>
    setPicked((now) => (now?.chart === chart && now.key === key ? null : { chart, key }));

  const data: PipelineAnalyticsDTO | undefined = analytics.data;
  const presented = analytics.isError ? toPresentableError(analytics.error, { context: 'list' }) : null;
  const forbidden = presented?.status === 403 ? presented.message : null;

  const stages = FUNNEL_STAGES.map((stage) => {
    const served = data?.funnel.find((row) => row.key === stage.key);
    return {
      ...stage,
      label: served?.label?.trim() || stage.label,
      count: served?.count ?? 0,
      stated: served !== undefined,
      value: served?.value ?? null,
      valueCurrency: served?.valueCurrency ?? null,
      valueUnavailableReason: served?.valueUnavailableReason ?? null,
    };
  });
  // Stages this build cannot draw. Dropping one silently would hide a whole population behind a
  // deploy-order mismatch, so the count is disclosed the way the deadline board discloses its own.
  const unknownStages = (data?.funnel ?? []).filter((row) => !FUNNEL_STAGES.some((s) => s.key === row.key)).length;
  const funnelTop = Math.max(1, ...stages.map((s) => s.count));

  const wonStage = stages.find((s) => s.key === 'won');
  // Every lost or expired quote reaches the loss list under some code — 'UNRECORDED' where nobody
  // wrote a reason down — so the list's total IS the decided-against side of the sent book. It is
  // a subtotal of one server aggregate over one scope, not a figure assembled from two endpoints.
  const lostOrExpired = (data?.lossReasons ?? []).reduce((sum, row) => sum + row.count, 0);

  const segments: Segment[] = [
    { key: 'won', words: 'Won', count: wonStage?.count ?? 0, tone: 'brassMark', value: wonStage?.value, currency: wonStage?.valueCurrency, currencyStated: true },
    // No single server value for lost: summing the loss rows would be a figure we made, so none.
    { key: 'lost', words: 'Lost or expired', count: lostOrExpired, tone: 'oxide' },
    { key: 'responded', words: 'Customer replied', count: data?.respondedQuotes ?? 0, tone: 'graphite', value: data?.respondedValue, currencyStated: false },
    { key: 'awaiting', words: 'Awaiting the customer', count: data?.awaitingResponseQuotes ?? 0, tone: 'brassBrand', open: true, value: data?.awaitingResponseValue, currencyStated: false },
  ];
  const bookTotal = segments.reduce((sum, s) => sum + s.count, 0);
  // The funnel carries no 'won' row at all, so the first segment of the sent book has no figure
  // behind it. The bar is not drawn short by one state: the frame is kept and the reason is put
  // over it, because a stacked length that silently omits a state is a wrong total.
  const bookUnavailable = data !== undefined && wonStage?.stated === false
    ? 'The server did not state a won stage for this window, so the sent book cannot be split into its four states.'
    : null;

  const widths = segmentWidths(segments.map((s) => s.count), bookW);
  const starts = widths.reduce<number[]>((acc, _, i) => [...acc, i === 0 ? 0 : acc[i - 1] + widths[i - 1]], []);
  const anchors = bookTotal > 0
    ? segments.map((_, i) => starts[i] + widths[i] / 2)
    // Nothing has been sent. The four labels space themselves evenly under an empty rail, so the
    // reader still learns the four states the band will fill in.
    : segments.map((_, i) => ((i + 0.5) / segments.length) * bookW);
  const labelX = placeLabels(anchors, Math.min(LABEL_GAP, bookW / segments.length), bookW);

  const bookDescription = `The sent book, ${plural(bookTotal, 'quote', 'quotes')} in total: `
    + segments.map((s) => `${s.words.toLowerCase()} ${s.count.toLocaleString()}`).join(', ')
    + '. The awaiting segment is hatched because it has not been decided.';

  const pickedSegment = picked?.chart === 'segment' ? picked.key : null;

  const bookChart = (
    <Box
      sx={{
        overflowX: 'auto',
        // Alive, not dead: a segment lifts and brightens under the pointer, and a focused one
        // carries the brass ring every key on this screen uses.
        '& [data-segment]': {
          cursor: 'pointer', outline: 'none',
          transition: 'transform 180ms ease, opacity 180ms ease',
          '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
        },
        '& [data-segment]:hover': { transform: 'translateY(-1.5px)', opacity: 1 },
        '& [data-segment]:hover > rect:first-of-type': { filter: 'brightness(1.12)' },
        '& [data-segment]:focus-visible > rect:first-of-type': {
          stroke: seriesVar('brassMark'), strokeWidth: 2,
        },
      }}
    >
      <svg
        viewBox={`0 0 ${bookW} ${BOOK_H}`}
        width="100%"
        role="group"
        aria-label={bookDescription}
        style={{ display: 'block', height: 'auto', overflow: 'visible' }}
      >
        <HatchPattern id={hatchId} />
        {/* The rail. It is drawn in every state, empty included: it is the part of the band that
            must not move when the first quote is sent. */}
        <rect
          x={0} y={BOOK_BAR_Y} width={bookW} height={BOOK_BAR_H} rx={6}
          fill="none" stroke="currentColor" strokeOpacity={0.18}
        />
        {segments.map((segment, i) => {
          if (widths[i] <= 0) return null;
          const on = pickedSegment === segment.key;
          return (
            <g
              key={segment.key} data-testid={`book-segment-${segment.key}`} data-width={widths[i].toFixed(2)}
              data-segment=""
              role="button"
              tabIndex={0}
              aria-pressed={on}
              aria-label={`${segment.words}: ${plural(segment.count, 'quote', 'quotes')}`}
              onClick={() => toggle('segment', segment.key)}
              onKeyDown={pressOnKey(() => toggle('segment', segment.key))}
              style={{
                ...(segment.open ? {} : { filter: 'var(--nx-neu-drop)' }),
                // The picked segment stays full strength; the rest step back.
                opacity: pickedSegment && !on ? 0.4 : 1,
              }}
            >
              <rect
                x={starts[i]} y={BOOK_BAR_Y} width={widths[i]} height={BOOK_BAR_H}
                fill={segment.open ? hatchFill(hatchId) : seriesVar(segment.tone)}
                stroke={segment.open ? seriesVar('brassMark') : 'none'}
                strokeWidth={segment.open ? 1.5 : 0}
              />
              {/* Depth from lighting only: a lit top edge on the solid states. The hollow one gets
                  none, because a highlight on an outline would start to read as a fill. */}
              {!segment.open && (
                <rect x={starts[i]} y={BOOK_BAR_Y} width={widths[i]} height={1.5} fill="rgba(255,255,255,0.35)" />
              )}
              {on && (
                <rect
                  x={starts[i] - 1} y={BOOK_BAR_Y - 3} width={widths[i] + 2} height={BOOK_BAR_H + 6} rx={4}
                  fill="none" stroke="currentColor" strokeWidth={1.5} pointerEvents="none"
                />
              )}
            </g>
          );
        })}
        {segments.map((segment, i) => (
          <g key={segment.key}>
            <line
              x1={anchors[i]} y1={BOOK_BAR_Y + BOOK_BAR_H} x2={labelX[i]} y2={BOOK_BAR_Y + BOOK_BAR_H + 10}
              stroke="currentColor" strokeOpacity={0.28} strokeWidth={1}
            />
            <text
              x={labelX[i]} y={BOOK_BAR_Y + BOOK_BAR_H + 25} textAnchor="middle"
              fontSize={14} fontWeight={700} fill="currentColor"
              style={{ fontVariantNumeric: 'tabular-nums' }}
            >
              {segment.count.toLocaleString()}
            </text>
            <text
              x={labelX[i]} y={BOOK_BAR_Y + BOOK_BAR_H + 38} textAnchor="middle"
              fontSize={10} fill="currentColor" fillOpacity={0.72}
            >
              {segment.words}
            </text>
          </g>
        ))}
      </svg>
    </Box>
  );

  // Under Value the bars share one money axis, which only exists when every stated value is in
  // one currency. Otherwise no stage gets a bar: SAR against USD on one axis is a false length.
  const valued = stages.filter((s) => s.stated && s.value !== null);
  const currencies = new Set(valued.map((s) => s.valueCurrency?.trim() || ''));
  const mixedCurrency = currencies.size > 1;
  const valueTop = Math.max(1, ...valued.map((s) => s.value as number));

  /** The bar's length as a share of its lane, or the words drawn in its place. */
  const barOf = (stage: (typeof stages)[number]): { frac: number | null; words?: string } => {
    if (measure === 'count') return { frac: stage.count / funnelTop };
    if (!stage.stated) return { frac: null, words: 'Not stated by the server.' };
    if (stage.value === null) return { frac: null, words: stage.valueUnavailableReason ?? 'The server stated no reason.' };
    if (mixedCurrency) return { frac: null, words: 'In a different currency from another stage, so not drawn on one axis.' };
    return { frac: stage.value / valueTop };
  };

  const funnelDescription = `The funnel on one ${measure} axis: ${stages.map((s) => {
    if (!s.stated || s.value === null) return `${s.label} ${s.count}`;
    return `${s.label} ${s.count}, ${formatMoney(s.value, s.valueCurrency)}`;
  }).join(', ')}.`;

  const pickedStage = picked?.chart === 'stage' ? picked.key : null;

  const funnelChart = (
    <Box role="group" aria-label={funnelDescription} sx={{ minWidth: 0 }}>
      {stages.map((stage) => {
        const bar = barOf(stage);
        const zero = bar.frac === 0;
        const won = stage.key === 'won';
        const on = pickedStage === stage.key;
        return (
          <Box
            key={stage.key}
            data-testid={`funnel-stage-${stage.key}`}
            role="button"
            tabIndex={0}
            aria-pressed={on}
            aria-label={`${stage.label}: ${stage.count.toLocaleString()}`}
            onClick={() => toggle('stage', stage.key)}
            onKeyDown={pressOnKey(() => toggle('stage', stage.key))}
            sx={(theme: Theme) => ({
              display: 'grid',
              gridTemplateColumns: `${FUNNEL_LABEL_W}px minmax(0, 1fr) ${FUNNEL_MONEY_W}px`,
              alignItems: 'center',
              minHeight: FUNNEL_ROW,
              mb: `${FUNNEL_GAP}px`,
              px: 0.5,
              mx: -0.5,
              borderRadius: '8px',
              cursor: 'pointer',
              outline: 'none',
              opacity: pickedStage && !on ? 0.5 : 1,
              // Pressed in = the stage being read, as a pressed key is the one in force.
              boxShadow: on ? neuInset(theme.palette.mode, 2) : 'none',
              transition: 'opacity 180ms ease, box-shadow 180ms ease',
              '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
              '&:hover [data-bar]': { transform: 'translateY(-1px)', filter: 'brightness(1.12)' },
              '&:focus-visible': { boxShadow: `0 0 0 2px ${seriesVar('brassMark')}` },
            })}
          >
            <Typography component="span" sx={{ fontSize: 11, fontWeight: 700, lineHeight: 1.2, pr: 1 }}>
              {stage.label}
            </Typography>
            {/* The lane. Its left edge is the one shared axis all four bars start from. */}
            <Box sx={{ display: 'flex', alignItems: 'center', minWidth: 0, borderLeft: '1px solid', borderColor: 'divider', minHeight: FUNNEL_ROW }}>
              {bar.frac === null ? (
                <Typography
                  component="span" data-testid={`funnel-reason-${stage.key}`}
                  sx={{ fontSize: 10.5, lineHeight: 1.3, color: 'text.secondary', pl: 1, py: 0.25 }}
                >
                  {bar.words}
                </Typography>
              ) : (
                <>
                  <Box
                    data-bar=""
                    data-testid={`funnel-bar-${stage.key}`}
                    data-zero={zero ? 'true' : 'false'}
                    data-frac={bar.frac.toFixed(4)}
                    sx={{
                      flex: 'none',
                      height: FUNNEL_ROW - 8,
                      // A measured zero is a short tick on the axis, never a blank row. The 48px
                      // held back is room for the figure after the bar.
                      width: zero ? `${ZERO_TICK}px` : `max(8px, calc((100% - 48px) * ${bar.frac.toFixed(4)}))`,
                      borderRadius: zero ? '1px' : '5px',
                      backgroundColor: seriesVar(won ? 'brassMark' : 'graphite'),
                      boxShadow: zero ? 'none' : 'var(--nx-neu-raised-sm), inset 0 1.5px 0 rgba(255,255,255,0.35)',
                      ...BAR_MOTION,
                    }}
                  />
                  <Typography
                    component="span" data-testid={`funnel-count-${stage.key}`}
                    sx={{ fontSize: 13, fontWeight: 700, ml: 1, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}
                  >
                    {stage.count.toLocaleString()}
                  </Typography>
                </>
              )}
            </Box>
            {/* The stage's money, beside its own bar, compact; the exact figure is in the
                description. A value the server would not state is never drawn as a number. */}
            {stage.stated ? (
              <Typography
                component="span" data-testid={`funnel-money-${stage.key}`}
                sx={{
                  fontSize: 11, textAlign: 'right', fontWeight: stage.value !== null ? 700 : 400,
                  opacity: 0.78, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap',
                }}
              >
                {stage.value !== null ? compactMoney(stage.value, stage.valueCurrency) : 'value not stated'}
              </Typography>
            ) : <span />}
          </Box>
        );
      })}
    </Box>
  );

  /** The step in plain words. Counts only; the value follows when the server states one. */
  const stageSentence = (key: string): string => {
    const i = stages.findIndex((s) => s.key === key);
    const stage = stages[i];
    const n = stage.count;
    let words: string;
    if (i === 0) {
      words = `${plural(n, stage.noun[0], stage.noun[1])} ${stage.reached}.`;
    } else {
      // Beside the stage before, never as a share of it: each stage counts its own records over its
      // own span, so "61 of 148 (41%) … 87 stopped here" would be a conversion nobody measured.
      const prev = stages[i - 1];
      words = `${n.toLocaleString()} ${n === 1 ? 'was' : 'were'} ${stage.reached} in this window (${prev.label}: ${prev.count.toLocaleString()}).`;
    }
    if (stage.value !== null) words += ` Worth ${formatMoney(stage.value, stage.valueCurrency)}.`;
    return words;
  };

  const segmentSentence = (key: string): string => {
    const segment = segments.find((s) => s.key === key)!;
    let words = `${segment.count.toLocaleString()} of ${plural(bookTotal, 'sent quote', 'sent quotes')}: ${segment.words.toLowerCase()}.`;
    if (segment.value != null) {
      words += segment.currencyStated
        ? ` Worth ${formatMoney(segment.value, segment.currency)}.`
        : ` Worth ${formatMoney(segment.value, null)}; the server states no currency for it.`;
    }
    return words;
  };

  /** The drill line under a chart: one sentence, and the existing list where there is one. */
  const drillLine = (testId: string, sentence: string, list?: { to: string; words: string }) => (
    <Box
      data-testid={testId} role="status"
      sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'baseline', columnGap: 1.5, rowGap: 0.25 }}
    >
      <Typography variant="caption" sx={{ fontWeight: 700, lineHeight: 1.45 }}>{sentence}</Typography>
      {list && (
        <Link component={RouterLink} to={list.to} variant="caption" sx={{ fontWeight: 700 }}>
          {list.words}
        </Link>
      )}
    </Box>
  );

  // Money is reported per stage, in words, under the chart. A stage whose value the server could
  // not state in one currency prints ITS OWN reason in full — truncating a reason to fit beside a
  // bar leaves the reader with a figure they cannot place and half a sentence about why.
  const valueLines = stages
    .filter((stage) => stage.stated && stage.value === null)
    .map((stage) => ({
      key: stage.key,
      text: `${stage.label}: value not available — ${stage.valueUnavailableReason ?? 'the server stated no reason.'}`,
    }));

  return (
    <BandShell
      title="What's out with customers, and where it stops"
      step="2"
      index={index}
      minHeight={240}
      hint={`The sent book: every quote that has left the building, in counts. ${HATCH_MEANING} — that money is still in the air. Where it stops: four bars on one axis, by count or by value. Click a stage or a segment to read it in words.`}
      loading={analytics.isLoading}
      error={presented && !forbidden ? presented.message : null}
      forbidden={forbidden}
      onRetry={() => void analytics.refetch()}
      seal={pipelineSeal(data)}
    >
      <Stack
        ref={(el: HTMLDivElement | null) => { measureRef.current = el; escapeRef.current = el; }}
        spacing={1} sx={{ flexGrow: 1, minWidth: 0 }}
      >
        <Stack spacing={0.5} sx={{ minWidth: 0 }}>
          <Typography component="h3" sx={{ fontWeight: 800, fontSize: 11, lineHeight: 1.2 }}>
            The sent book
          </Typography>
          {bookUnavailable ? <Unavailable reason={bookUnavailable}>{bookChart}</Unavailable> : bookChart}
          {bookTotal === 0 && !bookUnavailable && (
            <Typography variant="caption" data-testid="book-empty" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
              No quote has been sent to a customer in this window, so the book is empty rather than
              balanced. The four states fill in from the left as quotes go out.
            </Typography>
          )}
          {pickedSegment && drillLine('book-drill', segmentSentence(pickedSegment), SEGMENT_LISTS[pickedSegment])}
        </Stack>

        <Stack spacing={0.5} sx={{ minWidth: 0 }}>
          {/* The chart's title is its measure switch: the reader changes what the bars count
              where they are already looking. */}
          <Box component="h3" sx={{ m: 0, display: 'flex' }}>
            <ChartMenu label="Bars show" value={measure} options={MEASURE_OPTIONS} onChange={setMeasure} />
          </Box>
          {funnelChart}
          {pickedStage && drillLine(
            'funnel-drill', stageSentence(pickedStage), stages.find((s) => s.key === pickedStage)?.list,
          )}
          {/* Under Count, a stage that could not be valued gets a line here with its own reason.
              Under Value the reason already stands where its bar would be. */}
          {measure === 'count' && valueLines.length > 0 && (
            <Typography variant="caption" component="p" sx={{ color: 'text.secondary', lineHeight: 1.45 }}>
              {valueLines.map((line, i) => (
                <span key={line.key} data-testid={`funnel-value-${line.key}`}>
                  {i > 0 ? ' · ' : ''}{line.text}
                </span>
              ))}
            </Typography>
          )}
          {(data?.unownedQuotesExcluded ?? 0) > 0 && (
            <Typography variant="caption" data-testid="unowned-excluded" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
              {plural(data!.unownedQuotesExcluded, 'quote is', 'quotes are')} not in any figure on this band.{' '}
              {data!.unownedQuotesExcludedReason}
            </Typography>
          )}
          {unknownStages > 0 && (
            <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
              The server also returned {plural(unknownStages, 'stage', 'stages')} this screen does not yet
              know how to draw. They are not in the bars above.
            </Typography>
          )}
        </Stack>
      </Stack>
    </BandShell>
  );
}
