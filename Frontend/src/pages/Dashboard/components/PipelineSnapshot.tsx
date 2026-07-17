import React from 'react';
import { Box, Typography } from '@mui/material';
import { ChevronRight } from '@mui/icons-material';
import { useAppTheme } from '../../../context/ThemeContext';
import type {
  LeadStatsDTO,
  OrderStatsDTO,
  QuoteStatsDTO,
  RfqStatsDTO,
} from '../../../api/services/dashboardService';
import GlassCard, { CardSkeleton, CardTitle } from './GlassCard';
import { FUNNEL_RAMP, formatCount, formatMoney } from './dashboardTheme';

interface StageView {
  label: string;
  count: number | null;
  /** Money total where the backend actually has one; leads have counts only. */
  value: number | null;
}

interface PipelineSnapshotProps {
  leadStats?: LeadStatsDTO;
  rfqStats?: RfqStatsDTO;
  quoteStats?: QuoteStatsDTO;
  orderStats?: OrderStatsDTO;
  isLoading: boolean;
}

/**
 * "How is the money flowing?" — a horizontal Leads → RFQs → Quotes → Orders
 * stage strip with counts everywhere and real money totals where they exist
 * (quotes + orders). Any stage whose endpoint failed shows an em-dash.
 */
const PipelineSnapshot: React.FC<PipelineSnapshotProps> = ({
  leadStats,
  rfqStats,
  quoteStats,
  orderStats,
  isLoading,
}) => {
  const { mode } = useAppTheme();
  const ramp = FUNNEL_RAMP[mode];

  const stages: StageView[] = [
    { label: 'Leads', count: leadStats ? leadStats.totalActiveLeads : null, value: null },
    { label: 'RFQs', count: rfqStats ? rfqStats.totalRfqs : null, value: null },
    { label: 'Quotes', count: quoteStats ? quoteStats.totalQuotes : null, value: quoteStats ? quoteStats.totalQuotedAmount : null },
    { label: 'Orders', count: orderStats ? orderStats.totalOrders : null, value: orderStats ? orderStats.totalRevenue : null },
  ];

  const counts = stages.map((s) => s.count ?? 0);
  const max = Math.max(...counts, 1);
  const anyData = stages.some((s) => s.count !== null);

  return (
    <GlassCard label="Pipeline snapshot">
      <CardTitle title="Pipeline" subtitle="Where your work sits right now" />
      {isLoading && !anyData ? (
        <CardSkeleton rows={2} rowHeight={56} />
      ) : (
        <>
          <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(4, 1fr)', gap: 0.5 }}>
            {stages.map((stage, i) => (
              <Box key={stage.label} sx={{ minWidth: 0, position: 'relative', pr: 1 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.5, mb: 0.25 }}>
                  <Box aria-hidden sx={{ width: 8, height: 8, borderRadius: '50%', bgcolor: ramp[i], flexShrink: 0 }} />
                  <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary', whiteSpace: 'nowrap' }}>
                    {stage.label}
                  </Typography>
                </Box>
                <Typography variant="h6" sx={{ fontWeight: 800, lineHeight: 1.2, color: 'text.primary' }}>
                  {stage.count === null ? '—' : formatCount(stage.count)}
                </Typography>
                <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block', minHeight: '1.2em' }}>
                  {stage.value !== null && stage.value > 0 ? formatMoney(stage.value) : ' '}
                </Typography>
                {i < stages.length - 1 && (
                  <ChevronRight
                    aria-hidden
                    sx={{ position: 'absolute', right: -6, top: '38%', fontSize: 16, color: 'text.disabled' }}
                  />
                )}
              </Box>
            ))}
          </Box>

          {/* Proportional stage bar — decoration for the labeled counts above. */}
          <Box aria-hidden sx={{ display: 'flex', gap: '2px', mt: 1.5 }}>
            {stages.map((stage, i) => (
              <Box
                key={stage.label}
                sx={{
                  height: 10,
                  borderRadius: '4px',
                  bgcolor: ramp[i],
                  flexGrow: Math.max(stage.count ?? 0, max * 0.04),
                  flexBasis: 0,
                  transition: 'flex-grow 0.6s ease',
                }}
              />
            ))}
          </Box>
        </>
      )}
    </GlassCard>
  );
};

export default PipelineSnapshot;
