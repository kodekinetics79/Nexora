import { Box, Button, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { Link as RouterLink } from 'react-router-dom';
import dayjs from 'dayjs';
import commercialIntelligenceService, {
  type IntelligenceMetric,
  type PerformanceDTO,
} from '../../../api/services/commercialIntelligenceService';
import dashboardService from '../../../api/services/dashboardService';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import BandShell from './BandShell';
import InsufficiencyPips from './InsufficiencyPips';
import Unavailable from './Unavailable';
import { seriesVar } from './tokens';
import { priorWindow, scopeWords, type GlanceScopeWords } from './scopeWords';

/**
 * Band 1 — did we win what we decided?
 *
 * The screen's second sentence, after "whose numbers": of the quotes that reached a decision in
 * this window, how many went our way. It opens with one line of plain English assembled only from
 * figures the server stated, and then draws those same figures as two opposed lengths on one count
 * axis — won to the right in brass, lost to the left in graphite.
 *
 * Directly beneath, at 40% height, the immediately-prior equal-length window is repeated on the
 * SAME axis and the SAME origin. That ghost row is the whole reason this band exists in this
 * shape. Period-over-period is a comparison of two lengths the reader makes with their eye; the
 * client computes no delta, prints no arrow and no percentage change. It structurally cannot
 * repeat the older dashboard's "+100% up" against a zero month, because there is no division and
 * no ratio anywhere in this file.
 *
 * Nothing here is computed from the server's figures. In particular /performance publishes no
 * top-level conversion rate — it exists per representative only — so the conversion slot shows how
 * far the sample is from the server's own minimum rather than dividing won by decided. Dividing
 * would be a fabricated figure with no scope and no freshness of its own.
 */
export interface VerdictBandProps {
  /** Inclusive first day of the selected window, YYYY-MM-DD. */
  from: string;
  /** Inclusive last day of the selected window, YYYY-MM-DD. */
  to: string;
  /** Position in the screen's entrance stagger. */
  index?: number;
}

/**
 * Whose numbers, in a form that can carry a verb.
 *
 * `scopeWords` gives the reader-facing noun phrase the seal prints; a sentence needs the same fact
 * as a subject and a possessive. An unrecognised scope gets neither: the sentence switches to a
 * subject-less template rather than inventing ownership for numbers we cannot place.
 */
const SENTENCE_SUBJECTS: Readonly<Record<GlanceScopeWords, { subject: string; possessive: string }>> = Object.freeze({
  'Company-wide': { subject: 'The company', possessive: 'our' },
  'Your managed scope': { subject: 'Your teams', possessive: 'your' },
  'Your assigned accounts': { subject: 'Your accounts', possessive: 'your' },
});

const numeral = {
  fontFamily: '"Cambay", "Source Sans 3", sans-serif',
  fontWeight: 700,
  fontVariantNumeric: 'tabular-nums',
} as const;

const COUNT_COLUMN = 64;
const MAIN_BAR_HEIGHT = 34;
/** 40% of the main bar, so the prior window reads as an echo rather than as a second series. */
const GHOST_BAR_HEIGHT = Math.round(MAIN_BAR_HEIGHT * 0.4);

const day = (value: string): string => {
  const d = dayjs(value);
  return d.isValid() ? d.format('D MMM') : value;
};

const plural = (n: number, one: string, many: string): string => (n === 1 ? one : many);

/**
 * A metric the server actually stated, or null.
 *
 * A missing row and a row carrying 0 are different facts and are kept apart all the way to the
 * pixels: 0 draws a zero-length bar with a 0 beside it, missing draws the axis with the server's
 * reason over it. Coercing one into the other is how a dashboard starts reporting zeros it was
 * never told.
 */
const metricValue = (metrics: IntelligenceMetric[] | undefined, key: string): number | null => {
  const row = metrics?.find((m) => m.key === key);
  return row && typeof row.value === 'number' && Number.isFinite(row.value) ? row.value : null;
};

const metricLabel = (metrics: IntelligenceMetric[] | undefined, key: string, fallback: string): string =>
  metrics?.find((m) => m.key === key)?.label?.trim() || fallback;

/**
 * The seam for the day /performance publishes a rate of its own.
 *
 * The contract has `conversionRate` per representative, as a percentage withheld below
 * `minimumConversionSample`. A top-level field of that name would be the same figure over the same
 * rows in the same unit, so reading it here is the swap the slot is waiting for — and until the
 * field exists this returns null and the pips stay. What must never appear in its place is
 * `won / decided` worked out in the browser.
 */
const publishedConversionRate = (performance: PerformanceDTO | undefined): number | null => {
  const rate = (performance as (PerformanceDTO & { conversionRate?: number | null }) | undefined)?.conversionRate;
  return typeof rate === 'number' && Number.isFinite(rate) ? rate : null;
};

interface Verdict {
  won: number | null;
  lost: number | null;
  decided: number | null;
  wonLabel: string;
  lostLabel: string;
}

const readVerdict = (performance: PerformanceDTO | undefined): Verdict => ({
  won: metricValue(performance?.metrics, 'won'),
  lost: metricValue(performance?.metrics, 'lost'),
  decided: metricValue(performance?.metrics, 'decided'),
  wonLabel: metricLabel(performance?.metrics, 'won', 'Won'),
  lostLabel: metricLabel(performance?.metrics, 'lost', 'Lost'),
});

/**
 * The lead sentence, chosen on what the server stated and never on whether the news is good.
 *
 * There is no cheerful variant for a strong window and no apologetic one for a weak window: a
 * reader who learns the shape of the sentence should be able to read the numbers out of it without
 * re-reading the adjectives. The only branches are which figures exist.
 */
const leadSentence = (
  verdict: Verdict,
  words: GlanceScopeWords | null,
  window: { from: string; to: string },
  leadsClause: string,
): string => {
  const voice = words ? SENTENCE_SUBJECTS[words] : null;
  const span = `between ${day(window.from)} and ${day(window.to)}`;
  const { won, lost, decided } = verdict;

  if (decided === null && won === null && lost === null) {
    return 'The server did not state any won or lost outcomes for this window.';
  }
  if (decided === 0) {
    return voice
      ? `${leadsClause}Nothing has been marked won or lost for ${voice.subject.toLowerCase()} ${span}.`
      : `${leadsClause}Nothing has been marked won or lost ${span}.`;
  }
  if (decided !== null) {
    const quotes = `${decided} ${plural(decided, 'quote', 'quotes')}`;
    if (won === null) {
      return voice
        ? `${leadsClause}${voice.subject} decided ${quotes} ${span}.`
        : `${leadsClause}${quotes} ${plural(decided, 'was', 'were')} decided ${span}.`;
    }
    return voice
      ? `${leadsClause}${voice.subject} decided ${quotes} ${span}, and ${won} went ${voice.possessive} way.`
      : `${leadsClause}${quotes} ${plural(decided, 'was', 'were')} decided ${span}, and ${won} ${plural(won, 'was', 'were')} won.`;
  }
  // Won and lost without a decided total. They are not added up here — two stated figures are
  // reported as two stated figures.
  const parts: string[] = [];
  if (won !== null) parts.push(`won ${won}`);
  if (lost !== null) parts.push(`lost ${lost}`);
  return voice
    ? `${leadsClause}${voice.subject} ${parts.join(' and ')} ${span}.`
    : `${leadsClause}${parts.join(' and ')} ${span}.`;
};

/**
 * Bar length as a share of the shared scale.
 *
 * The 3% floor keeps a count of one from vanishing into the centre rule. It slightly overstates the
 * smallest lengths, which is why the count is printed at the end of every bar that has one: the
 * length is for comparing, the numeral is for reading.
 */
const barWidth = (value: number | null, scale: number): string => {
  if (value === null || value <= 0) return '0%';
  return `${Math.max((value / scale) * 100, 3)}%`;
};

const wonFill = `linear-gradient(90deg, color-mix(in srgb, ${seriesVar('brassMark')} 45%, transparent) 0%, ${seriesVar('brassMark')} 100%)`;
const lostFill = `linear-gradient(270deg, color-mix(in srgb, ${seriesVar('graphite')} 45%, transparent) 0%, ${seriesVar('graphite')} 100%)`;

interface AxisRowProps {
  lost: number | null;
  won: number | null;
  scale: number;
  height: number;
  ghost?: boolean;
  countSize: number;
  lostLabel?: string;
  wonLabel?: string;
}

/**
 * One row of the opposed bar: a count column, the two-sided track, a count column.
 *
 * Both rows are this same component at two heights so the two lengths cannot drift apart — one
 * origin, one scale, one geometry. The count columns are equal width, which is what makes the
 * group's 50% mark and the track's centre the same line.
 */
const AxisRow = ({ lost, won, scale, height, ghost = false, countSize, lostLabel, wonLabel }: AxisRowProps) => {
  const endCap = (value: number | null, label: string | undefined, align: 'right' | 'left') => (
    <Box sx={{ width: COUNT_COLUMN, flexShrink: 0, textAlign: align }}>
      {value !== null && (
        <Typography
          component="p"
          sx={{
            ...numeral,
            fontSize: countSize,
            lineHeight: 1.1,
            color: ghost ? 'text.secondary' : 'text.primary',
          }}
        >
          {value.toLocaleString()}
        </Typography>
      )}
      {label && (
        <Typography
          variant="caption"
          sx={{ display: 'block', color: 'text.secondary', fontWeight: 700, letterSpacing: '0.04em', lineHeight: 1.3 }}
        >
          {label}
        </Typography>
      )}
    </Box>
  );

  const bar = (value: number | null, side: 'lost' | 'won') => (
    <Box
      data-testid={`${ghost ? 'ghost' : 'current'}-${side}-bar`}
      data-length={barWidth(value, scale)}
      sx={{
        width: barWidth(value, scale),
        height: '100%',
        borderRadius: side === 'lost' ? '4px 0 0 4px' : '0 4px 4px 0',
        background: side === 'lost' ? lostFill : wonFill,
        // Depth comes from the material — a lit top edge and a shadow the bar sits in — never from
        // turning the bar into a solid the reader has to read a value off the near face of.
        opacity: ghost ? 0.5 : 1,
        boxShadow: ghost
          ? 'none'
          : 'inset 0 1px 0 rgba(255,255,255,0.28), 0 6px 14px -8px rgba(15,18,24,0.55)',
        transition: 'width 420ms cubic-bezier(0.2, 0.7, 0.2, 1)',
        '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
      }}
    />
  );

  return (
    <Stack direction="row" sx={{ alignItems: 'center', gap: 1 }}>
      {endCap(lost, lostLabel, 'right')}
      <Box sx={{ flexGrow: 1, display: 'flex', height, minWidth: 0 }}>
        <Box sx={{ flex: 1, display: 'flex', justifyContent: 'flex-end', minWidth: 0 }}>{bar(lost, 'lost')}</Box>
        <Box sx={{ flex: 1, display: 'flex', minWidth: 0 }}>{bar(won, 'won')}</Box>
      </Box>
      {endCap(won, wonLabel, 'left')}
    </Stack>
  );
};

export default function VerdictBand({ from, to, index = 1 }: VerdictBandProps) {
  const prior = priorWindow(from, to);

  // Three independent reads. The prior window and the leads count are each allowed to fail on
  // their own: a ghost row we could not fetch costs the reader a comparison, not the band.
  const performance = useQuery({
    queryKey: ['glance', 'performance', from, to],
    queryFn: () => commercialIntelligenceService.getPerformance(from, to),
    retry: 1,
    meta: { silenceGlobalError: true, errorLabel: 'the win and loss counts' },
  });
  const priorPerformance = useQuery({
    queryKey: ['glance', 'performance', prior?.from, prior?.to],
    queryFn: () => commercialIntelligenceService.getPerformance(prior!.from, prior!.to),
    enabled: prior !== null,
    retry: 1,
    meta: { silenceGlobalError: true, errorLabel: 'the previous period' },
  });
  const release = useQuery({
    queryKey: ['glance', 'release-01', from, to],
    queryFn: () => dashboardService.getRelease01({ from, to }),
    retry: 1,
    meta: { silenceGlobalError: true, errorLabel: 'the requests received' },
  });

  const data = performance.data;
  const words = scopeWords(data?.scope);
  const current = readVerdict(data);
  const previous = readVerdict(priorPerformance.data);

  // The scale, not a figure: the longest bar on either row sets the axis so both rows are read
  // against the same ruler. Floored at 1 so an all-zero window still draws a real axis.
  const scale = Math.max(current.won ?? 0, current.lost ?? 0, previous.won ?? 0, previous.lost ?? 0, 1);

  /**
   * Release 01 scopes itself in three tiers and /performance in two, so the same reader can be
   * 'managed_scope' on one and something else on the other. The leads count only joins the
   * sentence when both endpoints name the same tier — a first clause counted over a wider set of
   * accounts than the clause after it is a wrong number wearing a right one's label.
   */
  const leadsKpi = release.data?.kpis.find((k) => k.key === 'leads_received');
  const leadsScope = scopeWords(release.data?.roleScope?.scope);
  const leadsStated =
    leadsKpi?.state === 'available' &&
    typeof leadsKpi.value === 'number' &&
    Number.isFinite(leadsKpi.value) &&
    leadsScope !== null &&
    leadsScope === words;
  const leadsClause = leadsStated
    ? `${(leadsKpi.value as number).toLocaleString()} ${(leadsKpi.label?.trim() || 'leads received').toLowerCase()}. `
    : '';

  const sentence = leadSentence(current, words, { from, to }, leadsClause);

  // The server stated neither side of the verdict. That is not an empty window and not a failure:
  // the axis stays, defocused, with the reason on top of it.
  const chartUnavailable = current.won === null && current.lost === null;

  const need = data?.minimumConversionSample;
  const needStated = typeof need === 'number' && need > 0;
  const publishedRate = publishedConversionRate(data);

  const ghostNote = (() => {
    if (prior === null) return 'The previous period could not be worked out from this window.';
    if (priorPerformance.isLoading) return 'Loading the previous period…';
    // No fallback sentence is passed: `toPresentableError` uses a caller fallback both when the
    // request never reached the server AND when the status forbids showing the body (5xx bodies
    // are exception dumps far more often than product copy), so a domestic sentence here would
    // shadow the boundary's better wording for an outage.
    if (priorPerformance.isError) return presentableErrorMessage(priorPerformance.error, undefined, 'list');
    const length = dayjs(prior.to).diff(dayjs(prior.from), 'day') + 1;
    return `The thinner bar is the ${length} ${plural(length, 'day', 'days')} before, ${day(prior.from)} – ${day(prior.to)}. Both bars use the same scale.`;
  })();

  const chartDescription = [
    `${current.wonLabel} ${current.won ?? 'not stated'}, ${current.lostLabel.toLowerCase()} ${current.lost ?? 'not stated'}, ${day(from)} to ${day(to)}.`,
    prior && !priorPerformance.isError && (previous.won !== null || previous.lost !== null)
      ? `The ${day(prior.from)} to ${day(prior.to)} window before it: ${current.wonLabel.toLowerCase()} ${previous.won ?? 'not stated'}, ${current.lostLabel.toLowerCase()} ${previous.lost ?? 'not stated'}.`
      : 'The window before it is not shown.',
  ].join(' ');

  const axis = (
    <Box
      data-testid="verdict-axis"
      role="img"
      aria-label={chartDescription}
      sx={{ position: 'relative', pb: 1, borderBottom: '1px solid', borderColor: 'divider' }}
    >
      {/* The origin both rows grow from. It is drawn in every state, empty included — the axis is
          the part of the band that must not move when the first outcome is recorded. */}
      <Box
        aria-hidden
        data-testid="verdict-centre-rule"
        sx={{ position: 'absolute', left: '50%', top: 0, bottom: 0, width: '1px', bgcolor: 'divider' }}
      />
      <AxisRow
        lost={current.lost}
        won={current.won}
        scale={scale}
        height={MAIN_BAR_HEIGHT}
        countSize={26}
        lostLabel={current.lostLabel}
        wonLabel={current.wonLabel}
      />
      <Box sx={{ mt: 1 }}>
        <AxisRow
          lost={previous.lost}
          won={previous.won}
          scale={scale}
          height={GHOST_BAR_HEIGHT}
          ghost
          countSize={14}
        />
      </Box>
    </Box>
  );

  const conversionSlot = publishedRate !== null ? (
    <Stack spacing={0.5} sx={{ minWidth: 0 }}>
      <Typography component="p" sx={{ ...numeral, fontSize: 30, lineHeight: 1.05 }}>
        {`${publishedRate.toLocaleString()}%`}
      </Typography>
      <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
        {current.decided !== null
          ? `Won, out of ${current.decided} decided ${plural(current.decided, 'quote', 'quotes')} in this window.`
          : 'Won, out of the quotes decided in this window.'}
      </Typography>
    </Stack>
  ) : current.decided !== null && needStated ? (
    <InsufficiencyPips have={current.decided} need={need} label="A win rate" />
  ) : (
    <Unavailable
      reason={
        current.decided === null
          ? 'The server did not state how many quotes were decided in this window, so a win rate cannot be placed against its minimum sample.'
          : 'The server did not state the minimum sample a win rate needs, so how far off it is cannot be shown.'
      }
    >
      {/* A five-pip frame purely so the slot keeps its height and nothing reflows when the figures
          arrive. It is blurred and hidden from assistive tech by Unavailable. */}
      <InsufficiencyPips have={0} need={5} label="A win rate" />
    </Unavailable>
  );

  return (
    <BandShell
      title="Did we win what we decided?"
      step="1"
      index={index}
      minHeight={320}
      loading={performance.isLoading}
      error={performance.isError ? presentableErrorMessage(performance.error, undefined, 'list') : null}
      onRetry={() => {
        void performance.refetch();
        if (prior) void priorPerformance.refetch();
        void release.refetch();
      }}
      seal={{
        scope: words,
        window: `${day(from)} – ${day(to)}`,
        generatedAt: data?.generatedAt ?? null,
        governed: true,
      }}
    >
      <Stack spacing={2} sx={{ flexGrow: 1 }}>
        <Typography
          component="p"
          data-testid="verdict-sentence"
          sx={{ fontSize: { xs: 18, md: 20 }, fontWeight: 600, lineHeight: 1.4, color: 'text.primary', maxWidth: 720 }}
        >
          {sentence}
        </Typography>

        <Stack
          direction={{ xs: 'column', md: 'row' }}
          spacing={{ xs: 2, md: 3 }}
          sx={{ alignItems: { md: 'flex-start' }, flexGrow: 1 }}
        >
          <Box sx={{ flexGrow: 1, minWidth: 0 }}>
            {chartUnavailable ? (
              <Unavailable
                reason={`The server did not state ${current.wonLabel.toLowerCase()} or ${current.lostLabel.toLowerCase()} counts for this window.`}
              >
                {axis}
              </Unavailable>
            ) : (
              axis
            )}
            <Typography
              variant="caption"
              data-testid="verdict-ghost-note"
              sx={{ display: 'block', mt: 1, color: 'text.secondary', lineHeight: 1.4 }}
            >
              {ghostNote}
            </Typography>
          </Box>

          <Stack spacing={1.5} sx={{ width: { xs: '100%', md: 260 }, flexShrink: 0 }}>
            {conversionSlot}
            {current.decided === 0 && (
              <Button
                component={RouterLink}
                to="/sales/quotes?state=sent"
                variant="outlined"
                size="small"
                sx={{
                  alignSelf: 'flex-start',
                  borderColor: seriesVar('brassBrand'),
                  color: 'var(--nx-glance-seal-ink)',
                  fontWeight: 700,
                  '&:hover': { borderColor: seriesVar('brassMark'), backgroundColor: 'var(--nx-glance-seal-ground)' },
                }}
              >
                Record an outcome
              </Button>
            )}
          </Stack>
        </Stack>
      </Stack>
    </BandShell>
  );
}
