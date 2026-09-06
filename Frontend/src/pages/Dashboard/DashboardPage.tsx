import { useMemo, useState } from 'react';
import { Alert, Box, Button, Chip, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import dayjs, { type Dayjs } from 'dayjs';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import dashboardService from '../../api/services/dashboardService';
import { useAuth } from '../../context/AuthContext';
import { presentableErrorMessage } from '../../utils/apiErrors';
import VerdictBand from './glance/VerdictBand';
import ClosingBand from './glance/ClosingBand';
import TodayBand from './glance/TodayBand';
import SixMonthsBand, { type SixMonthPoint } from './glance/SixMonthsBand';
import { SCOPE_UNRESOLVED, scopeWords, type GlanceScopeWords, type GlanceWindow } from './glance/scopeWords';

/**
 * The dashboard, read top to bottom as one sentence.
 *
 *   0  whose numbers          the scope strip below the title
 *   1  did we win             VerdictBand
 *   2  what's out there       NOT BUILT — see the seam below
 *   3  why we lost            NOT BUILT — see the seam below
 *   4  what's closing on us   ClosingBand
 *   5  what needs you today   TodayBand
 *   6  the last six months    SixMonthsBand
 *
 * One screen for three audiences. A representative, their manager and a director all get the same
 * bands in the same order with the same marks; what differs is the DATA, which every endpoint
 * scopes server-side, and each band prints the scope it was given on its own seal. That is why the
 * rail row is no longer manager-only (navCatalog.tsx): there is nothing here to keep from a rep,
 * and the route was admitting them by URL anyway.
 *
 * Every band fetches on its own query and fails on its own. There is no composite endpoint and no
 * shared "load the dashboard" call, so a band that cannot load shows its own Alert and leaves its
 * neighbours' figures on the screen — asserted in DashboardPage.test.tsx rather than left as an
 * intention. Nothing is computed here: this file passes a window and renders bands, and the only
 * arithmetic in it is turning a chosen preset into two dates.
 */
type PeriodKey = '30d' | '90d' | 'ytd' | 'custom';

const PERIOD_CHOICES: readonly { key: PeriodKey; label: string }[] = Object.freeze([
  { key: '30d', label: 'Last 30 days' },
  { key: '90d', label: 'Last 90 days' },
  { key: 'ytd', label: 'This year' },
  { key: 'custom', label: 'Custom…' },
]);

/**
 * Both ends inclusive, matching how the endpoints read `from`/`to` and how the kit's `priorWindow`
 * derives the comparison period — so "last 30 days" is today and the twenty-nine before it, and
 * the ghost row under band 1 is exactly the thirty days before that with no shared day.
 */
const presetRange = (key: Exclude<PeriodKey, 'custom'>, today: Dayjs): GlanceWindow => {
  const to = today.format('YYYY-MM-DD');
  if (key === 'ytd') return { from: today.startOf('year').format('YYYY-MM-DD'), to };
  return { from: today.subtract((key === '90d' ? 90 : 30) - 1, 'day').format('YYYY-MM-DD'), to };
};

const isValidWindow = (from: string, to: string): boolean => {
  const start = dayjs(from);
  const end = dayjs(to);
  return start.isValid() && end.isValid() && !start.startOf('day').isAfter(end.startOf('day'));
};

/**
 * What each scope word means, in one clause a salesperson does not have to be taught. The word
 * itself is the server's; the clause after it only says what that word covers, so the strip never
 * asserts anything the server did not.
 */
const SCOPE_GLOSS: Readonly<Record<GlanceScopeWords, string>> = Object.freeze({
  'Company-wide': 'every account in this workspace',
  'Your managed scope': 'the accounts your teams own',
  'Your assigned accounts': 'only the accounts assigned to you',
});

const day = (value: string): string => {
  const parsed = dayjs(value);
  return parsed.isValid() ? parsed.format('D MMM YYYY') : value;
};

export default function DashboardPage() {
  const { hasPermission, userData } = useAuth();
  const navigate = useNavigate();
  const today = useMemo(() => dayjs().startOf('day'), []);

  const [period, setPeriod] = useState<PeriodKey>('30d');
  const [applied, setApplied] = useState<GlanceWindow>(() => presetRange('30d', today));
  const [customFrom, setCustomFrom] = useState(applied.from);
  const [customTo, setCustomTo] = useState(applied.to);
  const customValid = isValidWindow(customFrom, customTo);

  const choosePeriod = (key: PeriodKey) => {
    setPeriod(key);
    if (key === 'custom') return;
    const range = presetRange(key, today);
    setApplied(range);
    // Custom opens on whatever is currently on screen rather than on a stale pair of dates, so the
    // first thing a reader sees in the fields is the window they are already looking at.
    setCustomFrom(range.from);
    setCustomTo(range.to);
  };

  // A half-typed date is not a window. The bands keep the last window that was actually valid, and
  // the strip says so, rather than being sent to refetch on every keystroke of "2026-0…".
  const changeCustom = (next: GlanceWindow) => {
    setCustomFrom(next.from);
    setCustomTo(next.to);
    if (isValidWindow(next.from, next.to)) setApplied(next);
  };

  /**
   * Whose numbers, from the server that states it.
   *
   * Deliberately the same query key and function band 1 uses, so react-query serves both from one
   * cache entry and one request. That is not a shared fetch in the sense rule 5 forbids: nothing
   * on this screen waits on it, and when it fails the strip says the scope is not stated while
   * band 1 shows its own Alert — no other band notices.
   */
  const scope = useQuery({
    queryKey: ['glance', 'performance', applied.from, applied.to],
    queryFn: () => commercialIntelligenceService.getPerformance(applied.from, applied.to),
    retry: 1,
    meta: { silenceGlobalError: true, errorLabel: 'whose numbers these are' },
  });
  const words = scopeWords(scope.data?.scope);

  /**
   * Band 6's series. It is fetched here because `SixMonthsBand` is presentational — it draws the
   * points it is handed — but it is still its own query with its own key and its own failure, and
   * no other band reads it.
   *
   * `/api/Dashboard/{businessUnitId}` addresses a business unit rather than the caller, so with no
   * unit on the sign-in there is no request to make. That is stated as an inability to load, with
   * the reason, and no Retry: retrying would ask the same unanswerable question.
   */
  const businessUnitId = userData.businessUnitId;
  const canRequestSeries = typeof businessUnitId === 'number' && businessUnitId > 0;
  const series = useQuery({
    queryKey: ['glance', 'six-months', businessUnitId],
    queryFn: () => dashboardService.getDashboard(businessUnitId as number),
    enabled: canRequestSeries,
    staleTime: 5 * 60_000,
    retry: 1,
    meta: { silenceGlobalError: true, errorLabel: 'the last six months' },
  });
  const points: SixMonthPoint[] | null = series.data ? series.data.volumeTrend ?? [] : null;
  const seriesError = !canRequestSeries
    ? 'This sign-in carries no business unit, so the six-month history cannot be requested for it.'
    : series.isError
      ? presentableErrorMessage(series.error, undefined, 'list')
      : null;

  const scopeSentence = (() => {
    if (words) return { words, gloss: SCOPE_GLOSS[words] };
    if (scope.isLoading) return { words: 'Working out whose numbers these are…', gloss: null };
    if (scope.isError) return { words: SCOPE_UNRESOLVED, gloss: presentableErrorMessage(scope.error, undefined, 'list') };
    return { words: SCOPE_UNRESOLVED, gloss: 'The server did not name a scope for these figures.' };
  })();

  return (
    <Box sx={{ maxWidth: 1280, mx: 'auto', p: { xs: 1, sm: 2, md: 3 } }}>
      <Typography
        variant="h4"
        component="h1"
        sx={{ fontWeight: 900, fontFamily: '"Cambay", "Source Sans 3", sans-serif', letterSpacing: '-0.02em' }}
      >
        Dashboard
      </Typography>

      {/*
        0 · Whose numbers, and over what period.

        A strip, not a card: no glass, no rim, no elevation. Everything below it is a band, and if
        this looked like one it would read as a figure. The scope on the left is the server's own
        word in plain text — never a control, because the reader cannot choose their scope and a
        thing that looks pressable says they can. The period on the right is chips rather than two
        date inputs: picking "Last 90 days" is one press, and typing two ISO dates to see a quarter
        was a day-one defect on the screen this replaces.
      */}
      <Box
        component="section"
        aria-label="Whose numbers, and over what period"
        sx={{
          minHeight: 56,
          display: 'flex',
          flexWrap: 'wrap',
          alignItems: 'center',
          justifyContent: 'space-between',
          gap: 1.5,
          py: 1,
        }}
      >
        <Box sx={{ minWidth: 0 }}>
          <Typography data-testid="scope-sentence" variant="body2" sx={{ fontWeight: 700 }}>
            {scopeSentence.words}
            {scopeSentence.gloss && (
              <Box component="span" sx={{ fontWeight: 400, color: 'text.secondary' }}>
                {` — ${scopeSentence.gloss}`}
              </Box>
            )}
          </Typography>
          {/*
            This word comes from ONE aggregate (/performance) and is not a fact about the whole
            screen: the six-month history is company-wide for every reader, and the deadline board
            publishes no scope word at all. Saying so here is the difference between a heading and
            a claim, and it points at the seal that carries the truth band by band.
          */}
          <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
            Each band states its own scope and freshness on its seal.
          </Typography>
        </Box>

        <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
          {/*
            Which bands these dates actually move. A period control that silently governs one band
            out of four is the same lie as one that claims to govern all of them, so the strip names
            the band by the title the reader can see, and the seals repeat it band by band.
          */}
          <Typography
            variant="caption"
            title="Steps 4, 5 and 6 have their own fixed windows, set by the server."
            sx={{
              textTransform: 'uppercase',
              letterSpacing: '0.08em',
              fontSize: 11,
              fontWeight: 700,
              color: 'text.secondary',
            }}
          >
            Dates govern · Did we win
          </Typography>
          <Stack direction="row" spacing={0.75} role="group" aria-label="Period" sx={{ flexWrap: 'wrap', gap: 0.75 }}>
            {PERIOD_CHOICES.map((choice) => (
              <Chip
                key={choice.key}
                label={choice.label}
                size="small"
                clickable
                aria-pressed={period === choice.key}
                variant={period === choice.key ? 'filled' : 'outlined'}
                color={period === choice.key ? 'primary' : 'default'}
                onClick={() => choosePeriod(choice.key)}
                sx={{ fontWeight: 700 }}
              />
            ))}
          </Stack>
        </Stack>
      </Box>

      {period === 'custom' && (
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mb: 1.5, alignItems: { sm: 'center' } }}>
          <TextField
            type="date"
            label="From"
            size="small"
            value={customFrom}
            onChange={(event) => changeCustom({ from: event.target.value, to: customTo })}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <TextField
            type="date"
            label="To"
            size="small"
            value={customTo}
            error={!customValid}
            onChange={(event) => changeCustom({ from: customFrom, to: event.target.value })}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {`Showing ${day(applied.from)} – ${day(applied.to)}`}
          </Typography>
        </Stack>
      )}
      {period === 'custom' && !customValid && (
        <Alert severity="warning" sx={{ mb: 1.5 }}>
          {`These dates are not being used: the start date must be on or before the end date. The bands still show ${day(applied.from)} – ${day(applied.to)}.`}
        </Alert>
      )}

      <Stack spacing={2}>
        <VerdictBand from={applied.from} to={applied.to} index={1} />

        {/*
          SEAM — bands 2 and 3 belong HERE, in this order, and are not in this pass.

            2 · "What's out there, and where it stops" — the funnel. It needs a server aggregate
                that states each stage's count with its own scope and freshness; the existing
                /pipeline-analytics carries `weightedForecast`, which is removed from this screen
                (an unmeasured 0.3/0.5 heuristic presented as instruction), and no stage-level
                figure on it is scoped per reader.
            3 · "Why we lost" — loss reasons. There is no aggregate at all today: /performance
                publishes won/lost/decided and nothing about cause.

          Nothing is rendered in their place ON PURPOSE. A greyed card labelled "coming soon" reads
          as a band that failed to load, which is exactly the state rule 3 spends its effort making
          distinguishable. When the aggregates exist, the bands drop in at this point and the
          numerals on steps 4, 5 and 6 already leave room for them.
        */}

        <ClosingBand step="4" index={2} />

        <TodayBand index={3} />

        <SixMonthsBand
          points={points}
          loading={canRequestSeries && series.isLoading}
          error={seriesError}
          onRetry={canRequestSeries ? () => void series.refetch() : undefined}
          index={4}
        />
      </Stack>

      {/*
        The screens behind the bands, each shown only when the reader's own module grant would let
        the route open — a link that lands on "Access denied" is worse than no link.
      */}
      <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1, mt: 2.5 }}>
        {hasPermission('Leads') && (
          <Button size="small" onClick={() => navigate('/analytics/deadlines')} sx={{ fontWeight: 700 }}>
            Every deadline in full
          </Button>
        )}
        {hasPermission('Dashboard') && (
          <Button size="small" onClick={() => navigate('/sales/performance')} sx={{ fontWeight: 700 }}>
            Performance by rep
          </Button>
        )}
        {hasPermission('Leads') && (
          <Button size="small" onClick={() => navigate('/procurement/extraction/review')} sx={{ fontWeight: 700 }}>
            Documents to check
          </Button>
        )}
      </Stack>
    </Box>
  );
}
