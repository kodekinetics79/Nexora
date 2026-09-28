import { useState, type KeyboardEvent } from 'react';
import { Box, Stack, Tooltip, Typography } from '@mui/material';
import {
  type PipelineAnalyticsDTO,
  type PipelineLossReasonDTO,
} from '../../../api/services/dashboardService';
import { toPresentableError } from '../../../utils/apiErrors';
import { formatMoney } from '../../../utils/currency';
import BandShell from './BandShell';
import { pipelineSeal, usePipelineAnalytics } from './pipelineAnalytics';
import { seriesVar } from './tokens';
import { neuBar, neuFocus, neuWell } from './neumorphic';
import { useEscapeWithin } from './useEscapeWithin';
import ChartMenu from './ChartMenu';
import { useChartChoice } from './chartPrefs';

/**
 * Band 3 — why we lost.
 *
 * One horizon, and everything is read against it. A reason the customer actually gave us rises
 * ABOVE the line in graphite; a loss we never found the reason for hangs BELOW the same line, on
 * the same linear scale, in oxide. So the horizon is itself the answer at a glance: a clean line
 * with nothing beneath it means the team is learning from its losses, and a heavy row of columns
 * hanging under it means the losses are going unexamined — before a single label has been read.
 *
 * <p><b>The two groups are never ranked together.</b> The server publishes its reasons ordered by
 * count across both, and a single top-to-bottom list of that ordering is actively misleading: an
 * auto-expiry at the top reads as the market rejecting us, when what it means is that nobody
 * followed up. So the band keys on `group` — 'customer_stated' and 'never_established' are wire
 * constants, not display words — and each group carries its own subtotal.</p>
 *
 * <p><b>The larger of the two figures is "we never found out".</b> Not because it is the bigger
 * number, but because it is the one a manager can act on this week: a price loss is the market's
 * answer, and an unrecorded loss is our own process, and only the second is inside our control.
 * The type sizes say which is which.</p>
 */
export interface LossesBandProps {
  /** Inclusive first day of the selected window, YYYY-MM-DD. */
  from: string;
  /** Inclusive last day of the selected window, YYYY-MM-DD. */
  to: string;
  index?: number;
}

/** The wire constants. Nothing here keys on `reason`, which is the tenant's own wording. */
const STATED = 'customer_stated';
const NEVER = 'never_established';

/** Each half of the plot. Held in every state, empty included, so the horizon never moves. */
const HALF_H = 84;
/** The tallest a column may be drawn, leaving its count numeral room above or below it. */
const MAX_BAR = 54;
/** A reason with losses against it is never a sub-pixel sliver next to its own numeral. */
const MIN_BAR = 6;
/** The gutter carrying the two group headings, one on each side of the horizon. */
const GUTTER = 118;

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

/** What the columns measure. Count is the default: every loss has one, not every loss has a value. */
const MEASURES = ['count', 'value'] as const;
type Measure = (typeof MEASURES)[number];
const MEASURE_OPTIONS = [
  { value: 'count' as const, label: 'Number lost' },
  { value: 'value' as const, label: 'Value lost' },
];

/** The height an unpriced column is drawn at under Value: an empty outline, never a zero. */
const UNPRICED_BAR = 22;

/** Heights move when the measure changes; readers who asked for less motion get the jump. */
const BAR_MOTION = {
  transition: 'height 260ms ease, transform 180ms ease, box-shadow 180ms ease, filter 180ms ease',
  '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
} as const;

/** "SAR 210K" in a 48px column; the full figure is in the tooltip and the drill sentence. */
const compactMoney = (value: number, currency: string | null | undefined): string => {
  const code = currency?.trim();
  try {
    return new Intl.NumberFormat('en-US', code
      ? { style: 'currency', currency: code, currencyDisplay: 'code', notation: 'compact', maximumFractionDigits: 1 }
      : { notation: 'compact', maximumFractionDigits: 1 }).format(value).replace(/\u00a0/g, ' ');
  } catch {
    return formatMoney(value, currency);
  }
};

/** The measured figure for a row; null only under Value, when the server would not state one. */
const measureOf = (row: PipelineLossReasonDTO, measure: Measure): number | null =>
  measure === 'count' ? row.count : row.value;

const sideWords = (above: boolean) => (above ? 'a reason the customer gave' : 'we never found out');

/**
 * A group's subtotal under the chosen measure. Money is summed only when every row is priced in
 * one currency; otherwise the figure is withheld rather than printed short.
 */
const subtotal = (group: PipelineLossReasonDTO[], measure: Measure): string => {
  if (measure === 'count') return group.reduce((sum, row) => sum + row.count, 0).toLocaleString();
  if (group.length === 0) return '0';
  const currencies = new Set(group.map((row) => row.valueCurrency));
  if (group.some((row) => row.value === null) || currencies.size > 1) return 'Not stated';
  return compactMoney(group.reduce((sum, row) => sum + (row.value ?? 0), 0), group[0].valueCurrency);
};

/**
 * What the reader is told about a reason's money.
 *
 * A null value is not zero and is never drawn as one: it carries the server's own sentence about
 * why it could not be stated, which is the only thing that tells a reader whether the figure is
 * missing or the losses are worthless.
 */
const valueSentence = (row: PipelineLossReasonDTO): string =>
  row.value !== null
    ? formatMoney(row.value, row.valueCurrency)
    : `Value not available — ${row.valueUnavailableReason ?? 'the server stated no reason.'}`;

interface ColumnProps {
  row: PipelineLossReasonDTO;
  /** Above the horizon for a stated reason, below it for one we never established. */
  above: boolean;
  scale: number;
  measure: Measure;
  pressed: boolean;
  /** Another column is open, so this one steps back. */
  dimmed: boolean;
  onToggle: () => void;
}

/**
 * One reason, as a column that grows away from the horizon.
 *
 * The label sits on the OPPOSITE side of the line from the bar, hard against it. That is what lets
 * both halves carry labels without either of them colliding with a bar, and it keeps every label
 * next to the horizon so the line itself stays the thing the eye lands on.
 *
 * <p>The whole column is the button: pressing it opens the reason's share beneath the chart.</p>
 */
const Column = ({ row, above, scale, measure, pressed, dimmed, onToggle }: ColumnProps) => {
  const figure = measureOf(row, measure);
  // Under Value an unpriced reason is an empty outline at a fixed height — present, not zero.
  const unpriced = figure === null;
  const height = unpriced
    ? UNPRICED_BAR
    : figure === 0 ? 0 : Math.max(MIN_BAR, Math.round((figure / scale) * MAX_BAR));
  const numeral = (
    <Typography
      component="span"
      data-testid={`loss-count-${row.code}`}
      sx={unpriced ? { fontSize: 10, fontWeight: 700, lineHeight: 1.2, color: 'text.secondary' } : {
        fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
        fontSize: measure === 'count' ? 16 : 12, lineHeight: 1.2, fontVariantNumeric: 'tabular-nums',
        color: 'text.primary', whiteSpace: 'nowrap',
      }}
    >
      {unpriced ? 'No value' : measure === 'count' ? row.count.toLocaleString() : compactMoney(figure, row.valueCurrency)}
    </Typography>
  );
  const bar = (
    <Box
      data-testid={`loss-bar-${row.code}`}
      data-side={above ? 'above' : 'below'}
      data-unpriced={unpriced ? 'true' : undefined}
      data-bar=""
      aria-hidden
      sx={(theme) => ({
        ...BAR_MOTION,
        width: '100%',
        maxWidth: 46,
        height: `${height}px`,
        boxSizing: 'border-box',
        borderRadius: above ? '10px 10px 3px 3px' : '3px 3px 10px 10px',
        ...(unpriced ? {
          backgroundColor: 'transparent',
          backgroundImage: 'none',
          border: '1.5px dashed',
          borderColor: 'text.secondary',
          boxShadow: 'none',
        } : {
          backgroundColor: seriesVar(above ? 'graphite' : 'oxide'),
          // Lit from above in both halves, so a column that hangs below the horizon still reads as
          // the same material rather than as a different kind of mark.
          backgroundImage: height === 0
            ? 'none'
            : 'linear-gradient(180deg, rgba(255,255,255,0.28) 0%, rgba(255,255,255,0.05) 45%, rgba(0,0,0,0.14) 100%)',
          boxShadow: height === 0 ? 'none' : neuBar(theme.palette.mode),
        }),
      })}
    />
  );
  const label = (
    <Typography
      component="span"
      sx={{ fontSize: 11, fontWeight: 700, lineHeight: 1.25, color: 'text.primary', textAlign: 'center', px: 0.25 }}
    >
      {row.reason}
    </Typography>
  );

  const onKeyDown = (event: KeyboardEvent) => {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      onToggle();
    }
  };

  return (
    <Tooltip title={valueSentence(row)} placement="top" describeChild>
      <Box
        data-testid={`loss-column-${row.code}`}
        role="button"
        tabIndex={0}
        aria-pressed={pressed}
        aria-label={`${row.reason}, ${sideWords(above)}: ${plural(row.count, 'loss', 'losses')}. ${valueSentence(row)}`}
        onClick={onToggle}
        onKeyDown={onKeyDown}
        sx={(theme) => ({
          ...neuFocus,
          ...BAR_MOTION,
          flex: '1 1 0', minWidth: 48, display: 'flex', flexDirection: 'column',
          cursor: 'pointer', borderRadius: '12px', outline: 'none',
          opacity: dimmed ? 0.55 : 1,
          // Pressed into the clay while open: the same "in force" language as the period keys.
          ...(pressed ? neuWell(theme.palette.mode, 3) : { border: '1px solid transparent' }),
          // Hover lifts the bar off the horizon, away from the line on either side.
          '&:hover [data-bar]': {
            transform: `translateY(${above ? -2 : 2}px)`,
            filter: 'brightness(1.08)',
          },
          '@media (prefers-reduced-motion: reduce)': {
            transition: 'none',
            '&:hover [data-bar]': { transform: 'none', filter: 'brightness(1.08)' },
          },
        })}
      >
        <Box sx={{
          height: HALF_H, display: 'flex', flexDirection: 'column', alignItems: 'center',
          justifyContent: 'flex-end', gap: 0.5, pb: above ? 0 : 0.5,
        }}>
          {above ? <>{numeral}{bar}</> : label}
        </Box>
        <Box sx={{
          height: HALF_H, display: 'flex', flexDirection: 'column', alignItems: 'center',
          justifyContent: 'flex-start', gap: 0.5, pt: above ? 0.5 : 0,
        }}>
          {above ? label : <>{bar}{numeral}</>}
        </Box>
      </Box>
    </Tooltip>
  );
};

/**
 * The sentence an open column puts under the chart: its share of the losses, its money, and which
 * side of the line it sits on.
 */
const drillSentence = (row: PipelineLossReasonDTO, above: boolean, total: number): string => {
  const pct = total > 0 ? Math.round((row.count / total) * 100) : 0;
  const money = row.value !== null
    ? formatMoney(row.value, row.valueCurrency)
    : `value not available — ${row.valueUnavailableReason ?? 'the server stated no reason.'}`;
  return `${row.reason}: ${row.count.toLocaleString()} of ${plural(total, 'loss', 'losses')} (${pct}%), ${money}`
    .replace(/\.$/, '')
    + `. ${above ? 'A reason the customer gave.' : 'We never found out why.'}`;
};

export default function LossesBand({ from, to, index = 3 }: LossesBandProps) {
  const analytics = usePipelineAnalytics(from, to, 'the loss reasons');
  const [measure, setMeasure] = useChartChoice('losses', MEASURES, 'count');
  const [open, setOpen] = useState<string | null>(null);

  const data: PipelineAnalyticsDTO | undefined = analytics.data;
  const presented = analytics.isError ? toPresentableError(analytics.error, { context: 'list' }) : null;
  const forbidden = presented?.status === 403 ? presented.message : null;

  const rows = data?.lossReasons ?? [];
  const stated = rows.filter((row) => row.group === STATED);
  const never = rows.filter((row) => row.group === NEVER);
  // A group this build has never heard of. Folding it into either half would put a loss on the
  // wrong side of the horizon, which is the one thing this band must never do, so it is left out
  // of both and the count is disclosed.
  const ungrouped = rows.filter((row) => row.group !== STATED && row.group !== NEVER);

  const statedTotal = stated.reduce((sum, row) => sum + row.count, 0);
  const neverTotal = never.reduce((sum, row) => sum + row.count, 0);
  // One ruler for both halves. Floored at 1 so an empty window still draws a real horizon rather
  // than dividing by nothing.
  // Under Value the ruler is the largest stated value; an unpriced reason never sets it.
  const scale = Math.max(1, ...[...stated, ...never].map((row) => measureOf(row, measure) ?? 0));
  const isEmpty = stated.length === 0 && never.length === 0;

  const toggle = (code: string) => setOpen((current) => (current === code ? null : code));
  const openRow = [...stated, ...never].find((row) => row.code === open) ?? null;
  const escapeRef = useEscapeWithin<HTMLDivElement>(open !== null, () => setOpen(null));

  const plotDescription = isEmpty
    ? 'No lost quote has a reason against it in this window.'
    // Each reason's money is spoken too: on screen it is a hover tooltip, which a keyboard or
    // screen-reader user never reaches.
    : `Above the line, reasons the customer gave: ${stated.map((r) => `${r.reason} ${r.count} (${valueSentence(r)})`).join(', ') || 'none'}. `
      + `Below the line, losses we never established a reason for: ${never.map((r) => `${r.reason} ${r.count} (${valueSentence(r)})`).join(', ') || 'none'}.`;

  return (
    <BandShell
      title="Why we lost"
      step="3"
      index={index}
      minHeight={240}
      hint="Reasons the customer gave rise above the line. Losses we never found a reason for hang below it, on the same scale."
      loading={analytics.isLoading}
      error={presented && !forbidden ? presented.message : null}
      forbidden={forbidden}
      onRetry={() => void analytics.refetch()}
      seal={pipelineSeal(data)}
    >
      <Stack ref={escapeRef} spacing={1} sx={{ flexGrow: 1, minWidth: 0 }}>

        <Box sx={{ display: 'flex', justifyContent: 'flex-end' }}>
          <ChartMenu label="Columns show" value={measure} options={MEASURE_OPTIONS} onChange={setMeasure} />
        </Box>

        <Box sx={{ overflowX: 'auto' }}>
          <Box sx={{ position: 'relative', display: 'flex', minWidth: 300 }}>
            {/* The gutter's two headings sit on the same sides of the horizon as the columns they
                total, so neither needs a swatch to say which half it belongs to. */}
            <Box sx={{ width: GUTTER, flexShrink: 0, pr: 1.5 }}>
              <Box sx={{ height: HALF_H, display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', pb: 0.5 }}>
                <Typography
                  component="p"
                  data-testid="losses-stated-total"
                  sx={{
                    fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
                    fontSize: measure === 'count' ? 20 : 15, whiteSpace: 'nowrap', lineHeight: 1.1, fontVariantNumeric: 'tabular-nums',
                  }}
                >
                  {subtotal(stated, measure)}
                </Typography>
                <Typography sx={{ fontSize: 11, fontWeight: 700, lineHeight: 1.25, color: 'text.secondary' }}>
                  Reasons the customer gave
                </Typography>
              </Box>
              <Box sx={{ height: HALF_H, display: 'flex', flexDirection: 'column', justifyContent: 'flex-start', pt: 0.5 }}>
                {/* The larger figure of the two, deliberately: it is the one inside our control. */}
                <Typography
                  component="p"
                  data-testid="losses-never-total"
                  sx={{
                    fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
                    fontSize: measure === 'count' ? 28 : 17, whiteSpace: 'nowrap', lineHeight: 1.05, fontVariantNumeric: 'tabular-nums',
                  }}
                >
                  {subtotal(never, measure)}
                </Typography>
                <Typography sx={{ fontSize: 11, fontWeight: 700, lineHeight: 1.25, color: 'text.secondary' }}>
                  We never found out
                </Typography>
              </Box>
            </Box>

            {/* A group, not an image: its columns are buttons, and an image's children are not
                reachable. The description still speaks both halves in one pass. */}
            <Box
              role="group"
              aria-label={plotDescription}
              sx={{ flexGrow: 1, display: 'flex', gap: { xs: 0.5, sm: 1 }, minWidth: 0 }}
            >
              {[...stated, ...never].map((row) => (
                <Column
                  key={row.code}
                  row={row}
                  above={row.group === STATED}
                  scale={scale}
                  measure={measure}
                  pressed={open === row.code}
                  dimmed={open !== null && open !== row.code}
                  onToggle={() => toggle(row.code)}
                />
              ))}
            </Box>

            {/* The horizon. One rule, all the way across, drawn in every state including the empty
                one — it is the band's answer, not its decoration. */}
            <Box
              aria-hidden
              data-testid="losses-horizon"
              sx={{ position: 'absolute', left: 0, right: 0, top: HALF_H, height: '2px', backgroundColor: 'text.primary', opacity: 0.75 }}
            />
          </Box>
        </Box>

        {openRow && (
          <Typography
            variant="body2"
            data-testid="losses-drill"
            aria-live="polite"
            sx={{ fontWeight: 600, lineHeight: 1.4 }}
          >
            {drillSentence(openRow, openRow.group === STATED, statedTotal + neverTotal)}
          </Typography>
        )}

        {measure === 'value' && [...stated, ...never].some((row) => row.value === null) && (
          <Typography variant="caption" data-testid="losses-unpriced-note" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            Dashed columns have no value the server could state. They are not zero.
          </Typography>
        )}

        {isEmpty ? (
          <Typography variant="caption" data-testid="losses-empty" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            No quote has been marked lost or expired in this window, so there is nothing to explain
            yet. The line stays clean until one is — and a column below it will mean a loss nobody
            recorded a reason for.
          </Typography>
        ) : neverTotal === 0 ? (
          <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            Nothing hangs below the line: every loss in this window has a reason the customer gave.
          </Typography>
        ) : (
          <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            {plural(neverTotal, 'loss', 'losses')} below the line{' '}
            {neverTotal === 1 ? 'has' : 'have'} no reason from the customer behind{' '}
            {neverTotal === 1 ? 'it' : 'them'} — an expiry, a silence, or a reason nobody wrote down.
          </Typography>
        )}

        {ungrouped.length > 0 && (
          <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            The server also returned {plural(ungrouped.length, 'reason', 'reasons')} this screen cannot
            place on either side of the line. {plural(ungrouped.reduce((sum, r) => sum + r.count, 0), 'loss is', 'losses are')}{' '}
            not in the figures above.
          </Typography>
        )}
      </Stack>
    </BandShell>
  );
}
