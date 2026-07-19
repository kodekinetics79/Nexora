import React from 'react';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Box, Divider, Typography } from '@mui/material';
import { useAppTheme } from '../../../context/ThemeContext';
import dashboardService from '../../../api/services/dashboardService';
import GlassCard, { CardSkeleton, CardTitle } from './GlassCard';
import { FUNNEL_RAMP, formatCount, formatMoney } from './dashboardTheme';

/**
 * WP-B2: pipeline analytics — the four-stage funnel with counts AND money,
 * a weighted forecast of the open quotes, why deals were lost, and the
 * quoted-vs-cost margin proxy. Self-contained (owns its query) so wiring it
 * into DashboardPage is a single additive line. Fails silent: if the endpoint
 * errors the card simply doesn't render — the dashboard above is untouched.
 */
const PipelinePanel: React.FC = () => {
  const { mode } = useAppTheme();
  const ramp = FUNNEL_RAMP[mode];

  const analytics = useQuery({
    queryKey: ['dashboard', 'pipeline-analytics'],
    queryFn: dashboardService.getPipelineAnalytics,
    refetchInterval: 60_000,
    placeholderData: keepPreviousData,
    retry: 1,
  });

  if (analytics.isError) return null;

  const data = analytics.data;
  const funnel = data?.funnel ?? [];
  const maxCount = Math.max(...funnel.map((s) => s.count), 1);
  const lossReasons = (data?.lossReasons ?? []).slice(0, 5);
  const totalLost = lossReasons.reduce((sum, r) => sum + r.count, 0);

  return (
    <GlassCard label="Pipeline analytics">
      <CardTitle
        title="Pipeline"
        subtitle="From first request to won business — and what it is likely worth"
      />

      {analytics.isLoading && !data ? (
        <CardSkeleton rows={3} rowHeight={44} />
      ) : (
        <Box
          sx={{
            display: 'grid',
            gap: { xs: 2.5, md: 3 },
            gridTemplateColumns: { xs: '1fr', md: 'minmax(0, 7fr) minmax(0, 5fr)' },
            alignItems: 'start',
          }}
        >
          {/* ── Left: stage funnel, one labeled bar per stage ── */}
          <Box sx={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1.25 }}>
            {funnel.map((stage, i) => (
              <Box key={stage.key} sx={{ minWidth: 0 }}>
                <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', mb: 0.25 }}>
                  <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary' }}>
                    {stage.label}
                  </Typography>
                  <Typography variant="caption" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
                    <Box component="span" sx={{ fontWeight: 800, color: 'text.primary' }}>
                      {formatCount(stage.count)}
                    </Box>
                    {stage.value > 0 ? ` · ${formatMoney(stage.value)}` : ''}
                  </Typography>
                </Box>
                <Box
                  aria-hidden
                  sx={{
                    height: 12,
                    borderRadius: '6px',
                    bgcolor: ramp[i] ?? ramp[ramp.length - 1],
                    width: `${Math.max((stage.count / maxCount) * 100, 3)}%`,
                    transition: 'width 0.6s ease',
                  }}
                />
              </Box>
            ))}
          </Box>

          {/* ── Right: forecast, margin proxy, loss reasons ── */}
          <Box sx={{ minWidth: 0, display: 'flex', flexDirection: 'column', gap: 1.5 }}>
            <Box>
              <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', display: 'block' }}>
                Likely to close
              </Typography>
              <Typography variant="h5" sx={{ fontWeight: 900, lineHeight: 1.2 }}>
                {formatMoney(data?.weightedForecast ?? 0)}
              </Typography>
              <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                {formatCount(data?.awaitingResponseQuotes ?? 0)} quote(s) waiting on an answer,{' '}
                {formatCount(data?.respondedQuotes ?? 0)} in conversation
              </Typography>
            </Box>

            {data?.avgMarginPct !== null && data?.avgMarginPct !== undefined && (
              <Box>
                <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', display: 'block' }}>
                  Room above cost
                </Typography>
                <Typography variant="h6" sx={{ fontWeight: 800, lineHeight: 1.2 }}>
                  {data.avgMarginPct.toFixed(1)}%
                </Typography>
                <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                  Average across {formatCount(data.marginSampleLines)} priced line(s) with known cost
                </Typography>
              </Box>
            )}

            {lossReasons.length > 0 && (
              <>
                <Divider flexItem />
                <Box>
                  <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', display: 'block', mb: 0.5 }}>
                    Why quotes were lost
                  </Typography>
                  <Box sx={{ display: 'flex', flexDirection: 'column', gap: 0.5 }}>
                    {lossReasons.map((r) => (
                      <Box key={r.reason} sx={{ display: 'flex', justifyContent: 'space-between', gap: 1, minWidth: 0 }}>
                        <Typography
                          variant="caption"
                          sx={{ color: 'text.primary', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
                        >
                          {r.reason}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.secondary', whiteSpace: 'nowrap' }}>
                          {formatCount(r.count)}
                          {totalLost > 0 ? ` (${Math.round((r.count / totalLost) * 100)}%)` : ''}
                        </Typography>
                      </Box>
                    ))}
                  </Box>
                </Box>
              </>
            )}
          </Box>
        </Box>
      )}
    </GlassCard>
  );
};

export default PipelinePanel;
