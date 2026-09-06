import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { Box, ButtonBase, Stack, Typography } from '@mui/material';
import dashboardService, { type DeadlineBoardDTO } from '../../../api/services/dashboardService';
import { toPresentableError } from '../../../utils/apiErrors';
import BandShell from './BandShell';
import { scopeWords } from './scopeWords';
import { seriesVar, type SeriesToken } from './tokens';

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
const PLOT_HEIGHT = 148;
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

export interface ClosingBandProps {
  /** The band's numeral in the screen's sentence. */
  step?: string;
  index?: number;
  /**
   * Where a pressed column goes. Defaults to the deadline board carrying the bucket key, which is
   * the screen that lists exactly these rows.
   */
  onOpenBucket?: (bucketKey: string) => void;
}

export default function ClosingBand({ step = '4', index = 0, onOpenBucket }: ClosingBandProps) {
  const navigate = useNavigate();

  // Its own query, its own key, its own failure. A band that cannot load must not take a neighbour
  // down with it, so there is no shared fetch and no composite endpoint anywhere on this screen.
  // maxLeads is the smallest the server accepts: the bucket counts are computed over every open
  // enquiry in scope regardless, and this band draws no rows, so asking for two hundred lead
  // records to render seven numbers would be a payload nobody reads.
  const board = useQuery({
    queryKey: ['glance', 'closing-band'],
    queryFn: () => dashboardService.getDeadlineBoard({ maxLeads: 1 }),
    staleTime: 60_000,
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
        leads: served?.leads ?? 0,
        lineItems: served?.lineItems ?? 0,
      };
    });
    const tallest = columns.reduce((max, column) => Math.max(max, column.leads), 0);
    return {
      columns,
      tallest,
      // Buckets this build has never heard of. Silently dropping one would hide open work behind a
      // deploy-order mismatch, so the count is disclosed rather than absorbed.
      unknownBuckets: (data?.buckets ?? []).filter((bucket) => !CLOSING_COLUMNS.some((c) => c.key === bucket.key)).length,
    };
  }, [board.data]);

  const openBucket = (bucketKey: string) => {
    if (onOpenBucket) { onOpenBucket(bucketKey); return; }
    navigate(`/analytics/deadlines?bucket=${encodeURIComponent(bucketKey)}`);
  };

  // The server's own count of open work decides this, not the height of the tallest column: a
  // tenant whose only enquiries landed in a bucket this build cannot draw has work, and telling
  // it there is nothing scheduled would be wrong.
  const isEmpty = !!board.data && board.data.openLeads === 0;

  return (
    <BandShell
      title="What's closing on us"
      step={step}
      index={index}
      minHeight={340}
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
      <Stack spacing={1.5} sx={{ minWidth: 0 }}>
        <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.45 }}>
          Open enquiries by how long is left to answer them. Press a column to open the ones it counted.
        </Typography>

        <Box
          role="group"
          aria-label="Open enquiries by time left"
          sx={{
            display: 'grid',
            gridTemplateColumns: 'repeat(7, minmax(0, 1fr))',
            gap: { xs: 0.5, sm: 1 },
            alignItems: 'end',
          }}
        >
          {model.columns.map((column, columnIndex) => {
            const height = column.leads === 0
              ? ZERO_TICK
              : Math.max(MIN_BAR, Math.round((column.leads / model.tallest) * PLOT_HEIGHT));
            return (
              <ButtonBase
                key={column.key}
                onClick={() => openBucket(column.key)}
                aria-label={`${column.label}: ${plural(column.leads, 'open enquiry', 'open enquiries')}, ${plural(column.lineItems, 'line', 'lines')}. Opens the enquiries this counted.`}
                sx={{
                  flexDirection: 'column',
                  alignItems: 'stretch',
                  justifyContent: 'flex-end',
                  borderRadius: 2,
                  px: 0.25,
                  pt: 0.5,
                  pb: 0.75,
                  textAlign: 'center',
                  transition: 'transform 180ms cubic-bezier(0.2, 0.7, 0.2, 1), background-color 180ms ease-out',
                  '&:hover': { transform: 'translateY(-3px)', backgroundColor: 'action.hover' },
                  '&:active': { transform: 'translateY(1px)' },
                  '@media (prefers-reduced-motion: reduce)': {
                    transition: 'none',
                    '&:hover, &:active': { transform: 'none' },
                  },
                }}
              >
                <Typography
                  component="span"
                  data-testid={`closing-value-${column.key}`}
                  sx={{
                    fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
                    fontSize: { xs: 16, md: 20 }, lineHeight: 1.1, fontVariantNumeric: 'tabular-nums',
                    color: 'text.primary',
                  }}
                >
                  {column.leads.toLocaleString()}
                </Typography>
                {/* The plot cell keeps its full height whatever the bar does, which is what stops
                    the band from resizing between a zero tenant and a busy one. */}
                <Box sx={{ height: PLOT_HEIGHT, display: 'flex', alignItems: 'flex-end', mt: 0.5, opacity: column.dimmed ? 0.62 : 1 }}>
                  <Box
                    data-testid={`closing-bar-${column.key}`}
                    data-zero={column.leads === 0 ? 'true' : 'false'}
                    aria-hidden
                    className="nx-enter"
                    data-decorative-motion="true"
                    style={{ animationDelay: `${columnIndex * 45}ms` }}
                    sx={{
                      width: '100%',
                      height: `${height}px`,
                      borderRadius: column.leads === 0 ? 0.5 : '5px 5px 2px 2px',
                      // Lit slab, not a flat rectangle: the face falls off downwards, a highlight
                      // sits on the top edge and the shadow is cast beneath. All of the depth is
                      // in the lighting, none of it in the geometry, so the top of the bar is the
                      // only edge the eye can read a value from.
                      backgroundColor: seriesVar(column.tone),
                      backgroundImage: column.leads === 0
                        ? 'none'
                        : 'linear-gradient(180deg, rgba(255,255,255,0.30) 0%, rgba(255,255,255,0.06) 42%, rgba(0,0,0,0.14) 100%)',
                      boxShadow: column.leads === 0
                        ? 'none'
                        : 'inset 0 1px 0 rgba(255,255,255,0.45), 0 6px 14px -8px rgba(15,18,24,0.55)',
                    }}
                  />
                </Box>
                {/* The baseline. One rule under every column, drawn even where the bar is a tick,
                    so "zero" always has something to sit on. */}
                <Box aria-hidden sx={{ height: '1px', backgroundColor: 'divider', mt: '2px', mb: 0.75 }} />
                <Typography
                  component="span"
                  sx={{ fontSize: { xs: 10, sm: 11 }, fontWeight: 700, lineHeight: 1.25, color: 'text.primary' }}
                >
                  {column.label}
                </Typography>
                <Typography
                  component="span"
                  sx={{ fontSize: { xs: 10, sm: 11 }, lineHeight: 1.3, color: 'text.secondary', fontVariantNumeric: 'tabular-nums' }}
                >
                  {plural(column.lineItems, 'line', 'lines')}
                </Typography>
              </ButtonBase>
            );
          })}
        </Box>

        {isEmpty ? (
          /* Not "all clear". On a tenant with nothing in it an empty urgency board means no open
             enquiry has a deadline against it — that is an empty diary, not a quiet week, and
             telling a rep everything is under control would be the screen's first lie. */
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
            Nothing is scheduled yet. No open enquiry is waiting on an answer, so there is no
            deadline to count down to — the columns fill in as enquiries arrive.
          </Typography>
        ) : (
          <Stack spacing={0.5}>
            <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
              {plural(board.data?.openLeads ?? 0, 'open enquiry', 'open enquiries')} carrying{' '}
              {plural(board.data?.openLineItems ?? 0, 'line', 'lines')}.
            </Typography>
            {(board.data?.lateIngestedExcludedLeads ?? 0) > 0 && (
              <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
                {board.data!.lateIngestedExcludedLeads.toLocaleString()} of them reached Nexora after their
                own deadline had already passed. They sit under “Past deadline” because they are, but nobody
                here answered late.
              </Typography>
            )}
            {model.unknownBuckets > 0 && (
              <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>
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
