import { Box, Stack, Typography } from '@mui/material';
import {
  type PipelineAnalyticsDTO,
  type PipelineStageDTO,
} from '../../../api/services/dashboardService';
import { toPresentableError } from '../../../utils/apiErrors';
import { formatMoney } from '../../../utils/currency';
import BandShell from './BandShell';
import Unavailable from './Unavailable';
import HatchPattern, { HATCH_MEANING, hatchFill, useHatchPatternId } from './hatchPattern';
import { pipelineSeal, usePipelineAnalytics } from './pipelineAnalytics';
import { seriesVar, type SeriesToken } from './tokens';

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
 * <p><b>No ratio is printed between the stages.</b> "41% of requests were accepted" is arithmetic
 * this data does not support: the four stages count different populations reached over different
 * spans — a quote written this month can belong to a request received last year — so a percentage
 * between two of them divides one true figure by another true figure and produces a false one.
 * The bars share an axis, which is the honest comparison, and nothing on this band divides.</p>
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
const FUNNEL_STAGES: readonly { key: PipelineStageDTO['key']; label: string }[] = Object.freeze([
  { key: 'leads', label: 'Requests in' },
  { key: 'accepted', label: 'Accepted' },
  { key: 'quoted', label: 'Quotes written' },
  { key: 'won', label: 'Won' },
]);

/** The sent book's geometry. Fixed, so the bar is the same object at 4 quotes and at 400. */
const BOOK_W = 560;
const BOOK_BAR_Y = 10;
const BOOK_BAR_H = 46;
const BOOK_H = 132;
/**
 * The shortest a segment carrying quotes may be drawn, and the space one direct label needs.
 * A single quote beside two hundred is a sub-pixel sliver, and an invisible segment next to the
 * numeral "1" reads as a rendering fault — the numeral is the value, the length is the comparison.
 */
const MIN_SEGMENT = 5;
const LABEL_GAP = 128;

/** The funnel's geometry, lifted from the executive FunnelPanel this band replaces. */
const FUNNEL_W = 520;
const FUNNEL_ROW = 38;
const FUNNEL_GAP = 10;
const FUNNEL_LABEL_W = 118;
const FUNNEL_H = FUNNEL_STAGES.length * (FUNNEL_ROW + FUNNEL_GAP) - FUNNEL_GAP;
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

export default function OutstandingBand({ from, to, index = 2 }: OutstandingBandProps) {
  const hatchId = useHatchPatternId();

  const analytics = usePipelineAnalytics(from, to, 'the sent book and the funnel');

  const data: PipelineAnalyticsDTO | undefined = analytics.data;
  const presented = analytics.isError ? toPresentableError(analytics.error, { context: 'list' }) : null;
  const forbidden = presented?.status === 403 ? presented.message : null;

  const stages = FUNNEL_STAGES.map((stage) => {
    const served = data?.funnel.find((row) => row.key === stage.key);
    return {
      key: stage.key,
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
    { key: 'won', words: 'Won', count: wonStage?.count ?? 0, tone: 'brassMark' },
    { key: 'lost', words: 'Lost or expired', count: lostOrExpired, tone: 'oxide' },
    { key: 'responded', words: 'Supplier responded', count: data?.respondedQuotes ?? 0, tone: 'graphite' },
    { key: 'awaiting', words: 'Awaiting the customer', count: data?.awaitingResponseQuotes ?? 0, tone: 'brassBrand', open: true },
  ];
  const bookTotal = segments.reduce((sum, s) => sum + s.count, 0);
  // The funnel carries no 'won' row at all, so the first segment of the sent book has no figure
  // behind it. The bar is not drawn short by one state: the frame is kept and the reason is put
  // over it, because a stacked length that silently omits a state is a wrong total.
  const bookUnavailable = data !== undefined && wonStage?.stated === false
    ? 'The server did not state a won stage for this window, so the sent book cannot be split into its four states.'
    : null;

  const widths = segmentWidths(segments.map((s) => s.count), BOOK_W);
  const starts = widths.reduce<number[]>((acc, _, i) => [...acc, i === 0 ? 0 : acc[i - 1] + widths[i - 1]], []);
  const anchors = bookTotal > 0
    ? segments.map((_, i) => starts[i] + widths[i] / 2)
    // Nothing has been sent. The four labels space themselves evenly under an empty rail, so the
    // reader still learns the four states the band will fill in.
    : segments.map((_, i) => ((i + 0.5) / segments.length) * BOOK_W);
  const labelX = placeLabels(anchors, LABEL_GAP, BOOK_W);

  const bookDescription = `The sent book, ${plural(bookTotal, 'quote', 'quotes')} in total: `
    + segments.map((s) => `${s.words.toLowerCase()} ${s.count.toLocaleString()}`).join(', ')
    + '. The awaiting segment is hatched because it has not been decided.';

  const bookChart = (
    <Box sx={{ overflowX: 'auto' }}>
      <svg
        viewBox={`0 0 ${BOOK_W} ${BOOK_H}`}
        width="100%"
        height={BOOK_H}
        role="img"
        aria-label={bookDescription}
        style={{ display: 'block', minWidth: 340, overflow: 'visible' }}
      >
        <HatchPattern id={hatchId} />
        {/* The rail. It is drawn in every state, empty included: it is the part of the band that
            must not move when the first quote is sent. */}
        <rect
          x={0} y={BOOK_BAR_Y} width={BOOK_W} height={BOOK_BAR_H} rx={6}
          fill="none" stroke="currentColor" strokeOpacity={0.18}
        />
        {segments.map((segment, i) => {
          if (widths[i] <= 0) return null;
          return (
            <g key={segment.key} data-testid={`book-segment-${segment.key}`} data-width={widths[i].toFixed(2)}>
              <rect
                x={starts[i]} y={BOOK_BAR_Y} width={widths[i]} height={BOOK_BAR_H}
                fill={segment.open ? hatchFill(hatchId) : seriesVar(segment.tone)}
                stroke={segment.open ? seriesVar('brassBrand') : 'none'}
                strokeWidth={segment.open ? 1.5 : 0}
              />
              {/* Depth from lighting only: a lit top edge on the solid states. The hollow one gets
                  none, because a highlight on an outline would start to read as a fill. */}
              {!segment.open && (
                <rect x={starts[i]} y={BOOK_BAR_Y} width={widths[i]} height={1.5} fill="rgba(255,255,255,0.35)" />
              )}
            </g>
          );
        })}
        {segments.map((segment, i) => (
          <g key={segment.key}>
            <line
              x1={anchors[i]} y1={BOOK_BAR_Y + BOOK_BAR_H} x2={labelX[i]} y2={BOOK_BAR_Y + BOOK_BAR_H + 12}
              stroke="currentColor" strokeOpacity={0.28} strokeWidth={1}
            />
            <text
              x={labelX[i]} y={BOOK_BAR_Y + BOOK_BAR_H + 32} textAnchor="middle"
              fontSize={17} fontWeight={700} fill="currentColor"
              style={{ fontVariantNumeric: 'tabular-nums' }}
            >
              {segment.count.toLocaleString()}
            </text>
            <text
              x={labelX[i]} y={BOOK_BAR_Y + BOOK_BAR_H + 47} textAnchor="middle"
              fontSize={11} fill="currentColor" fillOpacity={0.72}
            >
              {segment.words}
            </text>
          </g>
        ))}
      </svg>
    </Box>
  );

  const funnelDescription = `The funnel on one count axis: ${stages.map((s) => `${s.label} ${s.count}`).join(', ')}.`;

  const funnelChart = (
    <Box sx={{ overflowX: 'auto' }}>
      <svg
        viewBox={`0 0 ${FUNNEL_W} ${FUNNEL_H}`}
        width="100%"
        height={FUNNEL_H}
        role="img"
        aria-label={funnelDescription}
        style={{ display: 'block', minWidth: 320, overflow: 'visible' }}
      >
        {stages.map((stage, i) => {
          const y = i * (FUNNEL_ROW + FUNNEL_GAP);
          const track = FUNNEL_W - FUNNEL_LABEL_W - 64;
          const width = stage.count === 0 ? ZERO_TICK : Math.max(8, (track * stage.count) / funnelTop);
          const won = stage.key === 'won';
          return (
            <g key={stage.key} transform={`translate(0 ${y})`}>
              <text x={0} y={FUNNEL_ROW / 2 + 4} fontSize={12} fontWeight={700} fill="currentColor">
                {stage.label}
              </text>
              <rect
                data-testid={`funnel-bar-${stage.key}`}
                data-zero={stage.count === 0 ? 'true' : 'false'}
                x={FUNNEL_LABEL_W} y={4} width={width} height={FUNNEL_ROW - 8} rx={stage.count === 0 ? 1 : 5}
                fill={seriesVar(won ? 'brassMark' : 'graphite')}
              />
              {stage.count > 0 && (
                <rect x={FUNNEL_LABEL_W} y={4} width={width} height={1.5} rx={1} fill="rgba(255,255,255,0.35)" />
              )}
              <text
                data-testid={`funnel-count-${stage.key}`}
                x={FUNNEL_LABEL_W + width + 8} y={FUNNEL_ROW / 2 + 5} fontSize={14} fontWeight={700}
                fill="currentColor" style={{ fontVariantNumeric: 'tabular-nums' }}
              >
                {stage.count.toLocaleString()}
              </text>
            </g>
          );
        })}
        {/* The shared axis the four bars are read against. One origin, one scale — the comparison
            the tapering shape this replaces could only suggest. */}
        <line
          x1={FUNNEL_LABEL_W} y1={0} x2={FUNNEL_LABEL_W} y2={FUNNEL_H}
          stroke="currentColor" strokeOpacity={0.3} strokeWidth={1}
        />
      </svg>
    </Box>
  );

  // Money is reported per stage, in words, under the chart. A stage whose value the server could
  // not state in one currency prints ITS OWN reason in full — truncating a reason to fit beside a
  // bar leaves the reader with a figure they cannot place and half a sentence about why.
  const valueLines = stages
    .filter((stage) => stage.stated)
    .map((stage) => ({
      key: stage.key,
      text: stage.value !== null
        ? `${stage.label}: ${formatMoney(stage.value, stage.valueCurrency)}`
        : `${stage.label}: value not available — ${stage.valueUnavailableReason ?? 'the server stated no reason.'}`,
    }));

  return (
    <BandShell
      title="What's out with customers, and where it stops"
      step="2"
      index={index}
      minHeight={400}
      loading={analytics.isLoading}
      error={presented && !forbidden ? presented.message : null}
      forbidden={forbidden}
      onRetry={() => void analytics.refetch()}
      seal={pipelineSeal(data)}
    >
      <Stack
        direction={{ xs: 'column', lg: 'row' }}
        spacing={{ xs: 2.5, lg: 3 }}
        sx={{ flexGrow: 1, minWidth: 0 }}
      >
        <Stack spacing={1} sx={{ flex: { lg: 7 }, minWidth: 0 }}>
          <Typography component="h3" sx={{ fontWeight: 800, fontSize: 13 }}>
            The sent book
          </Typography>
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.45 }}>
            Every quote that has left the building, in counts. {HATCH_MEANING} — that money is still in the air.
          </Typography>
          {bookUnavailable ? <Unavailable reason={bookUnavailable}>{bookChart}</Unavailable> : bookChart}
          {bookTotal === 0 && !bookUnavailable && (
            <Typography variant="body2" data-testid="book-empty" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
              No quote has been sent to a customer in this window, so the book is empty rather than
              balanced. The four states fill in from the left as quotes go out.
            </Typography>
          )}
        </Stack>

        <Stack spacing={1} sx={{ flex: { lg: 5 }, minWidth: 0 }}>
          <Typography component="h3" sx={{ fontWeight: 800, fontSize: 13 }}>
            Where it stops
          </Typography>
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.45 }}>
            Four counts on one axis. The stages count different populations, so no share of one is
            stated against another.
          </Typography>
          {funnelChart}
          {valueLines.length > 0 && (
            <Stack spacing={0.25}>
              {valueLines.map((line) => (
                <Typography
                  key={line.key}
                  variant="caption"
                  data-testid={`funnel-value-${line.key}`}
                  sx={{ color: 'text.secondary', lineHeight: 1.45 }}
                >
                  {line.text}
                </Typography>
              ))}
            </Stack>
          )}
          {(data?.unownedQuotesExcluded ?? 0) > 0 && (
            <Typography variant="body2" data-testid="unowned-excluded" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
              {plural(data!.unownedQuotesExcluded, 'quote is', 'quotes are')} not in any figure on this band.{' '}
              {data!.unownedQuotesExcludedReason}
            </Typography>
          )}
          {unknownStages > 0 && (
            <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
              The server also returned {plural(unknownStages, 'stage', 'stages')} this screen does not yet
              know how to draw. They are not in the bars above.
            </Typography>
          )}
        </Stack>
      </Stack>
    </BandShell>
  );
}
