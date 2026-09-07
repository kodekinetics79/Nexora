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
const HALF_H = 124;
/** The tallest a column may be drawn, leaving its count numeral room above or below it. */
const MAX_BAR = 84;
/** A reason with losses against it is never a sub-pixel sliver next to its own numeral. */
const MIN_BAR = 6;
/** The gutter carrying the two group headings, one on each side of the horizon. */
const GUTTER = 176;

const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

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
}

/**
 * One reason, as a column that grows away from the horizon.
 *
 * The label sits on the OPPOSITE side of the line from the bar, hard against it. That is what lets
 * both halves carry labels without either of them colliding with a bar, and it keeps every label
 * next to the horizon so the line itself stays the thing the eye lands on.
 */
const Column = ({ row, above, scale }: ColumnProps) => {
  const height = row.count === 0 ? 0 : Math.max(MIN_BAR, Math.round((row.count / scale) * MAX_BAR));
  const numeral = (
    <Typography
      component="span"
      data-testid={`loss-count-${row.code}`}
      sx={{
        fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
        fontSize: 16, lineHeight: 1.2, fontVariantNumeric: 'tabular-nums', color: 'text.primary',
      }}
    >
      {row.count.toLocaleString()}
    </Typography>
  );
  const bar = (
    <Box
      data-testid={`loss-bar-${row.code}`}
      data-side={above ? 'above' : 'below'}
      aria-hidden
      sx={{
        width: '100%',
        maxWidth: 46,
        height: `${height}px`,
        backgroundColor: seriesVar(above ? 'graphite' : 'oxide'),
        // Lit from above in both halves, so a column that hangs below the horizon still reads as
        // the same material rather than as a different kind of mark.
        backgroundImage: height === 0
          ? 'none'
          : 'linear-gradient(180deg, rgba(255,255,255,0.28) 0%, rgba(255,255,255,0.05) 45%, rgba(0,0,0,0.14) 100%)',
        borderRadius: above ? '4px 4px 0 0' : '0 0 4px 4px',
      }}
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

  return (
    <Tooltip title={valueSentence(row)} placement="top">
      <Box
        data-testid={`loss-column-${row.code}`}
        sx={{ flex: '1 1 0', minWidth: 62, display: 'flex', flexDirection: 'column' }}
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

export default function LossesBand({ from, to, index = 3 }: LossesBandProps) {
  const analytics = usePipelineAnalytics(from, to, 'the loss reasons');

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
  const scale = Math.max(1, ...rows.map((row) => row.count));
  const isEmpty = stated.length === 0 && never.length === 0;

  const plotDescription = isEmpty
    ? 'No lost quote has a reason against it in this window.'
    : `Above the line, reasons the customer gave: ${stated.map((r) => `${r.reason} ${r.count}`).join(', ') || 'none'}. `
      + `Below the line, losses we never established a reason for: ${never.map((r) => `${r.reason} ${r.count}`).join(', ') || 'none'}.`;

  return (
    <BandShell
      title="Why we lost"
      step="3"
      index={index}
      minHeight={400}
      loading={analytics.isLoading}
      error={presented && !forbidden ? presented.message : null}
      forbidden={forbidden}
      onRetry={() => void analytics.refetch()}
      seal={pipelineSeal(data)}
    >
      <Stack spacing={1.5} sx={{ flexGrow: 1, minWidth: 0 }}>
        <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.45 }}>
          Reasons the customer gave rise above the line. Losses we never found a reason for hang
          below it, on the same scale.
        </Typography>

        <Box sx={{ overflowX: 'auto' }}>
          <Box sx={{ position: 'relative', display: 'flex', minWidth: 520 }}>
            {/* The gutter's two headings sit on the same sides of the horizon as the columns they
                total, so neither needs a swatch to say which half it belongs to. */}
            <Box sx={{ width: GUTTER, flexShrink: 0, pr: 2 }}>
              <Box sx={{ height: HALF_H, display: 'flex', flexDirection: 'column', justifyContent: 'flex-end', pb: 0.5 }}>
                <Typography
                  component="p"
                  data-testid="losses-stated-total"
                  sx={{
                    fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
                    fontSize: 24, lineHeight: 1.1, fontVariantNumeric: 'tabular-nums',
                  }}
                >
                  {statedTotal.toLocaleString()}
                </Typography>
                <Typography sx={{ fontSize: 12, fontWeight: 700, lineHeight: 1.3, color: 'text.secondary' }}>
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
                    fontSize: 36, lineHeight: 1.05, fontVariantNumeric: 'tabular-nums',
                  }}
                >
                  {neverTotal.toLocaleString()}
                </Typography>
                <Typography sx={{ fontSize: 12, fontWeight: 700, lineHeight: 1.3, color: 'text.secondary' }}>
                  We never found out
                </Typography>
              </Box>
            </Box>

            <Box
              role="img"
              aria-label={plotDescription}
              sx={{ flexGrow: 1, display: 'flex', gap: { xs: 0.5, sm: 1 }, minWidth: 0 }}
            >
              {stated.map((row) => <Column key={row.code} row={row} above scale={scale} />)}
              {never.map((row) => <Column key={row.code} row={row} above={false} scale={scale} />)}
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

        {isEmpty ? (
          <Typography variant="body2" data-testid="losses-empty" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
            No quote has been marked lost or expired in this window, so there is nothing to explain
            yet. The line stays clean until one is — and a column below it will mean a loss nobody
            recorded a reason for.
          </Typography>
        ) : neverTotal === 0 ? (
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
            Nothing hangs below the line: every loss in this window has a reason the customer gave.
          </Typography>
        ) : (
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
            {plural(neverTotal, 'loss', 'losses')} below the line{' '}
            {neverTotal === 1 ? 'has' : 'have'} no reason from the customer behind{' '}
            {neverTotal === 1 ? 'it' : 'them'} — an expiry, a silence, or a reason nobody wrote down.
          </Typography>
        )}

        {ungrouped.length > 0 && (
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
            The server also returned {plural(ungrouped.length, 'reason', 'reasons')} this screen cannot
            place on either side of the line. {plural(ungrouped.reduce((sum, r) => sum + r.count, 0), 'loss is', 'losses are')}{' '}
            not in the figures above.
          </Typography>
        )}
      </Stack>
    </BandShell>
  );
}
