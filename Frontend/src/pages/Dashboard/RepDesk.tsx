import { useMemo, useState } from 'react';
import { Box, ButtonBase, Chip, GlobalStyles, Stack, Typography } from '@mui/material';
import { ArrowForwardRounded as GoIcon } from '@mui/icons-material';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import dayjs from 'dayjs';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import TodayBand from './glance/TodayBand';
import ClosingBand from './glance/ClosingBand';
import { glanceCssVariables } from './glance/tokens';
import {
  NEU_SURFACE, NEU_TRANSITION, clayInkOverrides, neuCssVariables, neuFocus, neuInset, neuRaised,
} from './glance/neumorphic';

/**
 * A sales rep's dashboard: their own numbers, what needs them today, and their deadlines.
 *
 * Deliberately small. Six tiles a rep reads without being taught — each one a plain question with
 * one figure — and each opens the list it counted. The server scopes every figure to the signed-in
 * rep (performance returns only their own row), so nothing here filters by user.
 */
type PeriodKey = '30d' | '90d' | 'ytd';

const PERIODS: readonly { key: PeriodKey; label: string }[] = [
  { key: '30d', label: 'Last 30 days' },
  { key: '90d', label: 'Last 90 days' },
  { key: 'ytd', label: 'This year' },
];

const windowFor = (key: PeriodKey) => {
  const today = dayjs().startOf('day');
  const to = today.format('YYYY-MM-DD');
  if (key === 'ytd') return { from: today.startOf('year').format('YYYY-MM-DD'), to };
  return { from: today.subtract((key === '90d' ? 90 : 30) - 1, 'day').format('YYYY-MM-DD'), to };
};

interface TileProps {
  label: string;
  value: string;
  note?: string;
  /** Where the tile opens. Absent when the rep may not open that list. */
  to?: string;
  alert?: boolean;
}

function Tile({ label, value, note, to, alert = false }: TileProps) {
  const navigate = useNavigate();
  const body = (
    <>
      <Typography sx={{ fontSize: 13, fontWeight: 600, color: 'text.secondary', lineHeight: 1.3 }}>{label}</Typography>
      <Typography
        sx={{ fontSize: 30, fontWeight: 700, lineHeight: 1.15, fontVariantNumeric: 'tabular-nums', color: 'text.primary' }}
      >
        {value}
      </Typography>
      <Typography
        sx={{ fontSize: 12, lineHeight: 1.35, minHeight: '1.35em', color: alert ? 'var(--nx-series-oxide)' : 'text.secondary', fontWeight: alert ? 700 : 400 }}
      >
        {note ?? ''}
      </Typography>
    </>
  );
  const surface = (mode: 'light' | 'dark') => ({
    display: 'flex', flexDirection: 'column', alignItems: 'flex-start', textAlign: 'left',
    gap: 0.25, p: 2, borderRadius: '16px', minWidth: 0,
    boxShadow: neuRaised(mode, 4),
  });
  if (!to) {
    return <Box sx={(theme) => surface(theme.palette.mode)}>{body}</Box>;
  }
  return (
    <ButtonBase
      onClick={() => navigate(to)}
      aria-label={`${label}: ${value}${note ? `, ${note}` : ''}. Open the list`}
      sx={(theme) => ({
        ...surface(theme.palette.mode),
        ...NEU_TRANSITION,
        ...neuFocus,
        position: 'relative',
        '&:hover': { boxShadow: neuRaised(theme.palette.mode, 6) },
        '&:active': { boxShadow: neuInset(theme.palette.mode, 3) },
        '&:hover .nx-tile-go': { opacity: 1 },
      })}
    >
      {body}
      <GoIcon className="nx-tile-go" aria-hidden sx={{ position: 'absolute', top: 14, right: 14, fontSize: 18, color: 'text.secondary', opacity: 0.5 }} />
    </ButtonBase>
  );
}

export default function RepDesk() {
  const { userData, hasPermission } = useAuth();
  const navigate = useNavigate();
  const [period, setPeriod] = useState<PeriodKey>('30d');
  const range = useMemo(() => windowFor(period), [period]);

  const performance = useQuery({
    queryKey: ['rep-desk', 'performance', range.from, range.to],
    queryFn: () => commercialIntelligenceService.getPerformance(range.from, range.to),
    retry: 1,
    meta: { silenceGlobalError: true, errorLabel: 'your numbers' },
  });
  const rows = performance.data?.representatives ?? [];
  const me = rows.find((r) => r.userId === userData.id) ?? (rows.length === 1 ? rows[0] : undefined);
  const minimum = performance.data?.minimumConversionSample ?? 5;

  const figure = (n: number | undefined) => {
    if (performance.isLoading) return '…';
    if (n === undefined) return '0';
    return n.toLocaleString();
  };

  const winRatio = (() => {
    if (performance.isLoading) return { value: '…', note: undefined };
    if (!me) return { value: '0', note: 'No decided quotes yet' };
    if (me.conversionEligible && typeof me.conversionRate === 'number') {
      return { value: `${Math.round(me.conversionRate)}%`, note: `${me.wonQuotes} of ${me.decidedQuotes} decided quotes` };
    }
    return { value: 'Not yet', note: `Shows after ${minimum} decided quotes · you have ${me.decidedQuotes}` };
  })();

  const withPeriod = (path: string) => `${path}${path.includes('?') ? '&' : '?'}from=${range.from}&to=${range.to}`;

  return (
    <Box
      sx={(theme) => ({
        ...glanceCssVariables(theme.palette.mode),
        ...neuCssVariables(theme.palette.mode),
        ...clayInkOverrides(theme.palette),
        maxWidth: 1560, mx: 'auto', p: { xs: 1.5, md: 2 },
        backgroundColor: NEU_SURFACE[theme.palette.mode],
      })}
    >
      <GlobalStyles
        styles={(theme) => ({
          body: { backgroundColor: `${NEU_SURFACE[theme.palette.mode]} !important`, backgroundImage: 'none !important' },
        })}
      />

      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 1.5, mb: 2 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 700, letterSpacing: '-0.02em' }}>
          My desk
        </Typography>
        <Stack direction="row" role="group" aria-label="Period" sx={{ flexWrap: 'wrap', gap: 0.75 }}>
          {PERIODS.map((choice) => {
            const chosen = period === choice.key;
            return (
              <Chip
                key={choice.key}
                label={choice.label}
                size="small"
                clickable
                aria-pressed={chosen}
                variant="outlined"
                onClick={() => setPeriod(choice.key)}
                sx={(theme) => ({
                  ...NEU_TRANSITION,
                  ...neuFocus,
                  height: 28, px: 0.5, fontWeight: 700,
                  backgroundColor: NEU_SURFACE[theme.palette.mode],
                  border: '1px solid',
                  borderColor: chosen ? 'var(--nx-glance-seal-rim)' : 'transparent',
                  color: chosen ? 'var(--nx-glance-seal-ink)' : 'text.primary',
                  boxShadow: chosen ? neuInset(theme.palette.mode, 2) : neuRaised(theme.palette.mode, 2),
                  '&&:hover': { backgroundColor: NEU_SURFACE[theme.palette.mode] },
                })}
              />
            );
          })}
        </Stack>
      </Stack>

      {performance.isError && (
        <Typography role="alert" sx={{ mb: 1.5, color: 'error.main', fontSize: 14 }}>
          Your numbers could not be loaded. Refresh the page to try again.
        </Typography>
      )}

      {/* The six questions a rep asks about their own work, left to right: what am I on, what
          have I sent, how did it go, what do I owe. */}
      <Box
        component="section"
        aria-label="My numbers"
        sx={{ display: 'grid', gap: 2, mb: 2.5, gridTemplateColumns: 'repeat(auto-fit, minmax(170px, 1fr))' }}
      >
        <Tile
          label="Enquiries I'm working"
          value={figure(me?.activeLeads)}
          note={me && me.overdueLeads > 0 ? `${me.overdueLeads} past deadline` : undefined}
          alert={!!me && me.overdueLeads > 0}
          to={hasPermission('Leads') ? '/procurement/leads/all' : undefined}
        />
        <Tile
          label="My open RFQs"
          value={figure(me?.openRfqs)}
          to={hasPermission('RFQ Management') ? '/procurement/rfqs/all' : undefined}
        />
        <Tile
          label="Quotes I sent"
          value={figure(me?.quoteSent)}
          note={me && me.draftQuotes > 0 ? `${me.draftQuotes} still in draft` : undefined}
          to={hasPermission('Quotations') ? '/sales/quotes?state=sent' : undefined}
        />
        <Tile
          label="Won"
          value={figure(me?.wonQuotes)}
          note={me ? `${me.lostQuotes} lost` : undefined}
          to={hasPermission('Quotations') ? '/sales/quotes?state=outcomes' : undefined}
        />
        <Tile
          label="My win ratio"
          value={winRatio.value}
          note={winRatio.note}
          to={withPeriod('/sales/me')}
        />
        <Tile
          label="Follow-ups"
          value={figure(me?.openFollowUps)}
          note={me && me.overdueFollowUps > 0 ? `${me.overdueFollowUps} late` : undefined}
          alert={!!me && me.overdueFollowUps > 0}
          to={hasPermission('Quotations') ? '/sales/follow-ups' : undefined}
        />
      </Box>

      <Box sx={{ display: 'grid', gap: 2.5, gridTemplateColumns: { xs: '1fr', lg: '3fr 2fr' }, alignItems: 'stretch' }}>
        <TodayBand index={1} step="" />
        <ClosingBand step="" index={2} />
      </Box>

      <Stack direction="row" sx={{ justifyContent: 'flex-end', mt: 2 }}>
        <ButtonBase
          onClick={() => navigate(withPeriod('/sales/me'))}
          sx={{ ...neuFocus, gap: 0.5, px: 1, py: 0.5, borderRadius: '8px', fontWeight: 700, fontSize: 14, color: 'var(--nx-glance-seal-ink)' }}
        >
          See my full performance <GoIcon sx={{ fontSize: 18 }} />
        </ButtonBase>
      </Stack>
    </Box>
  );
}
