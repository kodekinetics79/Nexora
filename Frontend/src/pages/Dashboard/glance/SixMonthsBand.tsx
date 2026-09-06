import type { ReactNode } from 'react';
import { Box, Chip, Stack, Tooltip, Typography, useTheme } from '@mui/material';
import dayjs from 'dayjs';
import BandShell from './BandShell';
import Unavailable from './Unavailable';
import { useSeriesColors } from './tokens';
import type { SeriesToken } from './tokens';
import { formatMoney } from '../../../utils/currency';

/**
 * Band 6 — the last six months, as background rather than as a verdict.
 *
 * Every other band on this screen answers a question the reader is meant to act on today. This one
 * does not: `GET /api/Dashboard/{businessUnitId}` has no scoping of any kind, so a rep and a
 * director are looking at the identical company-wide series, and it covers a window the period
 * control cannot move. That combination makes it context, and it is labelled and sized as context —
 * demoted type, a stated "Background context" mark, an OUTLINED seal reading "Company-wide" even
 * for a reader whose other bands say "Your assigned accounts". A company-wide series sitting
 * silently under a personal heading is how a rep comes to believe a company number is theirs.
 *
 * <p><b>Two stacked panels, never a dual axis.</b> Counts and money have no common scale, so a
 * single plot carrying both would decide where the line sits relative to the bars by its two axis
 * ranges rather than by the business — "value is pulling ahead of volume" could be produced or
 * erased by a rendering choice nobody made. `TrendPanel` reached the same conclusion and this band
 * follows its geometry: identical left gutter, identical band widths, month labels carried once
 * beneath the pair, so the columns above and the line below are read as one picture with one
 * meaning per vertical position.</p>
 *
 * <p><b>What this fixes in TrendPanel.</b> Its empty test is
 * <code>count === 0 &amp;&amp; value === 0</code>, but the server sends <code>value: null</code>
 * — with a reason — whenever the business unit has no single base currency, which is the default
 * for a new tenant. Null is not 0, so that test failed and the panel drew a line chart of nulls: a
 * flat run along the baseline that reads as "we sold nothing", when the truth is "we cannot state
 * this in one currency". Here null counts as empty, and a series that is null while requests were
 * genuinely received renders the server's own ValueUnavailableReason over an intact frame instead
 * of a number.</p>
 */

/**
 * The wire row. `dashboardService.MonthlyTrendDTO` still types `value` as a plain number and omits
 * the two fields the backend has carried since the FX fix (see DashboardDTOs.MonthlyTrendDTO), so
 * this is stated locally at the true nullability; a `MonthlyTrendDTO[]` assigns to it unchanged.
 */
export interface SixMonthPoint {
  /** The server's month label, "Sep" or an ISO "2026-09" prefix. */
  month: string;
  /** RFQs created that month. */
  count: number;
  /** Order value in the base currency, or null when that month could not be converted. */
  value: number | null;
  valueCurrency?: string | null;
  valueUnavailableReason?: string | null;
}

export interface SixMonthsBandProps {
  points?: SixMonthPoint[] | null;
  /** The server's freshness. This endpoint states none today, so it is honestly absent. */
  generatedAt?: string | null;
  loading?: boolean;
  /** The server's reason for the failure; presence renders BandShell's error state. */
  error?: string | null;
  onRetry?: () => void;
  index?: number;
}

const MONTHS_SHOWN = 6;

// One geometry for both panels. The left gutter and the plot width have to be identical or the
// columns stop lining up with the points below them, and the reader is silently comparing October
// against November.
const VIEW_W = 720;
const AXIS_W = 52;
const PAD_R = 16;
const PAD_T = 12;
const PLOT_H = 132;
const LABELS_H = 24;
const PANEL_H = PAD_T + PLOT_H;
const PLOT_W = VIEW_W - AXIS_W - PAD_R;
const BASELINE = PAD_T + PLOT_H;

const srOnly = {
  position: 'absolute', width: 1, height: 1, p: 0, m: -1,
  overflow: 'hidden', clip: 'rect(0 0 0 0)', whiteSpace: 'nowrap', border: 0,
} as const;

const compact = (n: number) =>
  new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(n);

const monthLabel = (raw: string): string => {
  // The server sends "MMM" today, but an ISO prefix has appeared on sibling endpoints and printing
  // "2026-09" under a column would read as a figure rather than a month.
  if (/^\d{4}-\d{2}/.test(raw)) {
    const parsed = dayjs(raw.slice(0, 7));
    if (parsed.isValid()) return parsed.format('MMM');
  }
  return raw.length > 4 ? raw.slice(0, 3) : raw;
};

/**
 * Axis furniture for a band with no rows at all. These are month names, not measurements — the
 * empty state has to keep a real, labelled axis so nothing moves when the first record arrives, and
 * an axis labelled "1…6" would be a worse invention than the calendar.
 */
const fallbackMonths = (): string[] =>
  Array.from({ length: MONTHS_SHOWN }, (_, i) => dayjs().subtract(MONTHS_SHOWN - 1 - i, 'month').format('MMM'));

/** A round number at or above `v`, so the top tick is readable rather than exact. */
const niceCeil = (v: number): number => {
  if (!Number.isFinite(v) || v <= 1) return 1;
  const magnitude = 10 ** Math.floor(Math.log10(v));
  const n = v / magnitude;
  const step = n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10;
  return step * magnitude;
};

const axisTicks = (max: number): number[] => (max <= 2 ? [0, max] : [0, max / 2, max]);

/** A column with rounded data-end, anchored square to the baseline. */
const columnPath = (x: number, w: number, top: number, r = 4): string => {
  const h = BASELINE - top;
  const radius = Math.max(0, Math.min(r, h, w / 2));
  return [
    `M${x} ${BASELINE}`,
    `V${top + radius}`,
    `Q${x} ${top} ${x + radius} ${top}`,
    `H${x + w - radius}`,
    `Q${x + w} ${top} ${x + w} ${top + radius}`,
    `V${BASELINE}`,
    'Z',
  ].join(' ');
};

export default function SixMonthsBand({
  points, generatedAt = null, loading = false, error = null, onRetry, index = 6,
}: SixMonthsBandProps) {
  const theme = useTheme();
  // Literals rather than the CSS custom properties: these values are interpolated into gradient
  // stops and shadow colours, which cannot be derived from a var() the browser has not resolved.
  const series = useSeriesColors();
  const gridInk = theme.palette.mode === 'dark' ? 'rgba(163,169,181,0.16)' : 'rgba(42,47,58,0.12)';
  const axisInk = theme.palette.text.secondary;
  const outlineInk = theme.palette.divider;

  const rows = (points ?? []).slice(-MONTHS_SHOWN);
  const labels = rows.length ? rows.map((r) => monthLabel(r.month)) : fallbackMonths();
  const slots = labels.length;

  // Null is emptiness, not zero. This is the line TrendPanel gets wrong: a new tenant with no
  // single base currency gets `value: null` on every month, and testing `value === 0` sent it down
  // the "we have data" path to draw a flat line of nulls along the baseline.
  const empty = rows.length === 0
    || rows.every((r) => r.count === 0 && (r.value === null || r.value === undefined || r.value === 0));

  const statedValues = rows.filter((r) => typeof r.value === 'number');
  // Requests came in but not one month's money could be converted: the value panel alone has an
  // answer, and it is the server's sentence rather than a chart.
  const valueUnavailableReason = !empty && statedValues.length === 0
    ? (rows.find((r) => r.valueUnavailableReason)?.valueUnavailableReason ?? 'The server did not state order value for these months.')
    : null;
  // A partial gap is different again: some months converted and some did not, so the line is drawn
  // and broken, and the months it skips are named in words underneath.
  const gapMonths = !empty && statedValues.length > 0
    ? rows.map((r, i) => (typeof r.value === 'number' ? null : labels[i])).filter((m): m is string => m !== null)
    : [];
  const gapReason = gapMonths.length
    ? (rows.find((r) => r.value === null && r.valueUnavailableReason)?.valueUnavailableReason ?? null)
    : null;

  const currency = rows.find((r) => r.valueCurrency)?.valueCurrency ?? null;
  const countMax = niceCeil(Math.max(...rows.map((r) => r.count), 0));
  const valueMax = niceCeil(Math.max(...statedValues.map((r) => r.value as number), 0));

  const band = PLOT_W / slots;
  const barW = Math.min(40, band * 0.5);
  const centre = (i: number) => AXIS_W + band * (i + 0.5);
  const countY = (v: number) => BASELINE - (v / countMax) * PLOT_H;
  const valueY = (v: number) => BASELINE - (v / valueMax) * PLOT_H;

  const linePoints = rows.map((r, i) => (typeof r.value === 'number' ? { x: centre(i), y: valueY(r.value), i } : null));
  // Contiguous runs only. Joining across a month whose value the server refused to state would
  // draw a slope nobody measured.
  const segments: { x: number; y: number }[][] = [];
  for (const p of linePoints) {
    if (!p) { segments.push([]); continue; }
    if (!segments.length) segments.push([]);
    segments[segments.length - 1].push(p);
  }
  const drawnSegments = segments.filter((s) => s.length > 0);
  const lastPoint = linePoints.filter((p) => p !== null).slice(-1)[0] ?? null;
  const lastRow = lastPoint ? rows[lastPoint.i] : null;

  const countSummary = rows.length
    ? rows.map((r, i) => `${labels[i]} ${r.count}`).join(', ')
    : `no requests recorded, ${labels.join(', ')}`;
  const valueSummary = rows.length
    ? rows.map((r, i) => `${labels[i]} ${typeof r.value === 'number' ? formatMoney(r.value, currency) : 'not stated'}`).join(', ')
    : `no order value recorded, ${labels.join(', ')}`;

  const gridLines = (max: number, y: (v: number) => number) =>
    axisTicks(max).map((t) => (
      <line key={t} x1={AXIS_W} x2={VIEW_W - PAD_R} y1={y(t)} y2={y(t)} stroke={gridInk} strokeWidth={1} />
    ));
  const tickLabels = (max: number, y: (v: number) => number, format: (v: number) => string) =>
    axisTicks(max).map((t) => (
      <text
        key={t} x={AXIS_W - 8} y={y(t) + 4} textAnchor="end"
        fill={axisInk} fontSize={11} fontFamily='"Cambay", "Source Sans 3", sans-serif'
        style={{ fontVariantNumeric: 'tabular-nums' }}
      >
        {format(t)}
      </text>
    ));

  return (
    <BandShell
      title="The last six months"
      step="6"
      index={index}
      minHeight={400}
      loading={loading}
      error={error}
      onRetry={onRetry}
      seal={{
        // Hard-coded rather than read from a scope field, because this endpoint has none: it is
        // company-wide for every reader, and the seal is outlined because its window is the
        // server's six months, not the period the reader picked.
        scope: 'Company-wide',
        window: 'Last 6 months',
        generatedAt,
        governed: false,
      }}
    >
      <Stack spacing={0.75} sx={{ mb: 1.5 }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 0.75 }}>
          <Chip
            label="Background context"
            size="small"
            variant="outlined"
            sx={{ height: 20, fontSize: 11, fontWeight: 700, color: 'text.secondary', borderColor: 'divider' }}
          />
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            Everyone sees the same company-wide history here, whatever the bands above are scoped to.
          </Typography>
        </Stack>
      </Stack>

      {empty ? (
        <Typography variant="body2" sx={{ color: 'text.secondary', mb: 1 }}>
          No requests or orders were recorded in the last six months.
        </Typography>
      ) : null}

      {/* Requests received — graphite, because a received request is volume that has already
          settled into the past, not something the reader can still act on. */}
      <Typography variant="caption" sx={{ display: 'block', fontWeight: 800, color: 'text.secondary' }}>
        Requests received{empty ? '' : ' · count'}
      </Typography>
      <Box
        component="svg"
        data-testid="six-months-requests"
        viewBox={`0 0 ${VIEW_W} ${PANEL_H}`}
        role="img"
        aria-label={`Requests received, ${countSummary}`}
        sx={{ display: 'block', width: '100%', height: 'auto', overflow: 'visible' }}
      >
        <defs>
          <linearGradient id="nx-six-months-column" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0" stopColor={series.graphite} stopOpacity={0.95} />
            <stop offset="1" stopColor={series.graphite} stopOpacity={0.62} />
          </linearGradient>
        </defs>
        {gridLines(countMax, countY)}
        {tickLabels(countMax, countY, (t) => t.toLocaleString('en-US'))}
        <line x1={AXIS_W} x2={VIEW_W - PAD_R} y1={BASELINE} y2={BASELINE} stroke={axisInk} strokeWidth={1} opacity={0.5} />
        {labels.map((label, i) => {
          const row = rows[i];
          const x = centre(i) - barW / 2;
          if (!row || row.count === 0) {
            // The empty column: a calm outline sitting on the baseline, holding the slot open at
            // its real width so nothing shifts sideways when the first request lands in it.
            return (
              <rect
                key={label + i} data-testid="six-months-empty-column"
                x={x} y={BASELINE - 3} width={barW} height={3} rx={1.5}
                fill="none" stroke={outlineInk} strokeWidth={1.25}
              />
            );
          }
          return (
            <Tooltip key={label + i} title={`${label}: ${row.count.toLocaleString('en-US')} requests received`}>
              <path d={columnPath(x, barW, countY(row.count))} fill="url(#nx-six-months-column)" />
            </Tooltip>
          );
        })}
      </Box>

      {/* Order value — brass, and brass here means the newly won, not merely the large. */}
      <Typography variant="caption" sx={{ display: 'block', mt: 1, fontWeight: 800, color: 'text.secondary' }}>
        Order value{currency ? ` · ${currency}` : empty || valueUnavailableReason ? '' : ' · currency not stated'}
      </Typography>
      {valueUnavailableReason ? (
        <Unavailable reason={valueUnavailableReason}>
          <ValueFrame
            labels={labels} gridLines={gridLines} tickLabels={tickLabels} valueMax={valueMax}
            valueY={valueY} axisInk={axisInk} outlineInk={outlineInk} centre={centre} barW={barW}
            segments={[]} lastPoint={null} lastRow={null} currency={currency} series={series}
            summary={valueSummary} gapIndexes={[]}
          />
        </Unavailable>
      ) : (
        <ValueFrame
          labels={labels} gridLines={gridLines} tickLabels={tickLabels} valueMax={valueMax}
          valueY={valueY} axisInk={axisInk} outlineInk={outlineInk} centre={centre} barW={barW}
          segments={drawnSegments} lastPoint={lastPoint} lastRow={lastRow} currency={currency}
          series={series} summary={valueSummary}
          gapIndexes={linePoints.map((p, i) => (p === null ? i : -1)).filter((i) => i >= 0)}
        />
      )}

      {gapMonths.length > 0 && (
        <Typography variant="caption" sx={{ display: 'block', mt: 0.5, color: 'text.secondary' }}>
          {`The line skips ${gapMonths.join(', ')}: no order value was stated for ${gapMonths.length === 1 ? 'that month' : 'those months'}.`}
          {gapReason ? ` ${gapReason}` : ''}
        </Typography>
      )}

      <Box component="table" sx={srOnly}>
        <caption>The last six months, company-wide</caption>
        <thead>
          <tr><th scope="col">Month</th><th scope="col">Requests received</th><th scope="col">Order value</th></tr>
        </thead>
        <tbody>
          {labels.map((label, i) => (
            <tr key={label + i}>
              <th scope="row">{label}</th>
              <td>{rows[i] ? rows[i].count.toLocaleString('en-US') : '0'}</td>
              <td>{rows[i] && typeof rows[i].value === 'number' ? formatMoney(rows[i].value, currency) : 'not stated'}</td>
            </tr>
          ))}
        </tbody>
      </Box>
    </BandShell>
  );
}

interface ValueFrameProps {
  labels: string[];
  gridLines: (max: number, y: (v: number) => number) => ReactNode;
  tickLabels: (max: number, y: (v: number) => number, format: (v: number) => string) => ReactNode;
  valueMax: number;
  valueY: (v: number) => number;
  axisInk: string;
  outlineInk: string;
  centre: (i: number) => number;
  barW: number;
  segments: { x: number; y: number }[][];
  lastPoint: { x: number; y: number; i: number } | null;
  lastRow: SixMonthPoint | null;
  currency: string | null;
  series: Record<SeriesToken, string>;
  summary: string;
  gapIndexes: number[];
}

/**
 * The value panel and the shared month axis, drawn as one piece so that the frame is identical in
 * every state. `Unavailable` blurs exactly this element and lays the server's sentence over it,
 * which only reads as "the chart is still there, we just will not state it" if the axis, the ticks
 * and the month labels are all present underneath.
 */
function ValueFrame({
  labels, gridLines, tickLabels, valueMax, valueY, axisInk, outlineInk,
  centre, barW, segments, lastPoint, lastRow, currency, series, summary, gapIndexes,
}: ValueFrameProps) {
  return (
    <Box
      component="svg"
      data-testid="six-months-value"
      viewBox={`0 0 ${VIEW_W} ${PANEL_H + LABELS_H}`}
      role="img"
      aria-label={`Order value, ${summary}`}
      sx={{ display: 'block', width: '100%', height: 'auto', overflow: 'visible' }}
    >
      <defs>
        <linearGradient id="nx-six-months-value-fill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor={series.brassMark} stopOpacity={0.28} />
          <stop offset="1" stopColor={series.brassMark} stopOpacity={0} />
        </linearGradient>
      </defs>
      {gridLines(valueMax, valueY)}
      {tickLabels(valueMax, valueY, compact)}
      <line x1={AXIS_W} x2={VIEW_W - PAD_R} y1={BASELINE} y2={BASELINE} stroke={axisInk} strokeWidth={1} opacity={0.5} />

      {segments.length === 0 && labels.map((label, i) => (
        // No stated value anywhere: the same calm outline the column panel uses, so "nothing yet"
        // looks like one decision across the band rather than two different treatments.
        <rect
          key={label + i} data-testid="six-months-empty-point"
          x={centre(i) - barW / 2} y={BASELINE - 3} width={barW} height={3} rx={1.5}
          fill="none" stroke={outlineInk} strokeWidth={1.25}
        />
      ))}

      {gapIndexes.map((i) => (
        <line
          key={`gap-${i}`} data-testid="six-months-value-gap"
          x1={centre(i)} x2={centre(i)} y1={PAD_T} y2={BASELINE}
          stroke={series.oxide} strokeWidth={1.25} strokeDasharray="2 4" opacity={0.7}
        />
      ))}

      {segments.map((segment, s) => {
        const line = segment.map((p, i) => `${i === 0 ? 'M' : 'L'}${p.x.toFixed(1)} ${p.y.toFixed(1)}`).join(' ');
        const area = segment.length > 1
          ? `${line} L${segment[segment.length - 1].x.toFixed(1)} ${BASELINE} L${segment[0].x.toFixed(1)} ${BASELINE} Z`
          : null;
        return (
          <g key={`seg-${s}`}>
            {area && <path d={area} fill="url(#nx-six-months-value-fill)" />}
            <path
              d={line} fill="none" stroke={series.brassMark} strokeWidth={2}
              strokeLinejoin="round" strokeLinecap="round"
            />
            {segment.map((p) => (
              <circle key={`${s}-${p.x}`} cx={p.x} cy={p.y} r={4} fill={series.brassMark} />
            ))}
          </g>
        );
      })}

      {lastPoint && lastRow && (
        // The endpoint carries the emphasis because "where we are now" is the only part of a
        // history a reader acts on. A halo and a direct label, so the current figure never has to
        // be read off an axis.
        <g data-testid="six-months-endpoint">
          <circle cx={lastPoint.x} cy={lastPoint.y} r={9} fill={series.brassBrand} opacity={0.18} />
          <circle cx={lastPoint.x} cy={lastPoint.y} r={5} fill={series.brassMark} stroke="#fff" strokeWidth={2} />
          <text
            x={Math.min(lastPoint.x, VIEW_W - PAD_R)} y={Math.max(lastPoint.y - 14, 12)}
            textAnchor="end" fill={axisInk} fontSize={12} fontWeight={700}
            fontFamily='"Cambay", "Source Sans 3", sans-serif' style={{ fontVariantNumeric: 'tabular-nums' }}
          >
            {formatMoney(lastRow.value, currency)}
          </text>
        </g>
      )}

      {labels.map((label, i) => (
        <text
          key={label + i} x={centre(i)} y={BASELINE + 17} textAnchor="middle"
          fill={axisInk} fontSize={11} fontFamily='"Cambay", "Source Sans 3", sans-serif'
        >
          {label}
        </text>
      ))}
    </Box>
  );
}
