import { useQuery } from '@tanstack/react-query';
import { Box, Stack, Typography } from '@mui/material';
import commercialIntelligenceService, { type IntelligenceMetric } from '../../api/services/commercialIntelligenceService';
import { formatMoney } from '../../utils/currency';

/**
 * Today's numbers for the person working the queue — three or four quiet figures in the Inbox
 * header, never a dashboard. The server decides the scope (their own accounts for a rep, their
 * team for a supervisor, the company for an administrator) and the scope is named beside them.
 *
 * It fails quietly on purpose: when the figures cannot be read they are simply absent. The Inbox
 * is the work; this is a glance at how the work is going, and a glance that errors is worse than
 * none.
 */
export const formatMetric = (m: IntelligenceMetric): string => {
  if (m.unit === 'currency') return formatMoney(m.value, m.currencyCode);
  if (m.unit === 'percentage') return `${m.value.toLocaleString('en-US', { maximumFractionDigits: 1 })}%`;
  if (m.unit === 'hours') return `${m.value.toLocaleString('en-US', { maximumFractionDigits: 1 })} h`;
  return m.value.toLocaleString('en-US', { maximumFractionDigits: 0 });
};

export default function GlanceStrip() {
  const query = useQuery({
    queryKey: ['commercial-intelligence', 'sales-today'],
    queryFn: commercialIntelligenceService.getSalesToday,
    retry: false,
    staleTime: 60_000,
    meta: { silenceGlobalError: true },
  });
  const metrics = query.data?.metrics?.slice(0, 4) ?? [];
  if (query.isError || query.isLoading || metrics.length === 0) return null;
  const scope = query.data?.scope === 'assigned_to_me' ? 'Your accounts' : query.data?.scope === 'managed_scope' ? 'Your team' : 'Company-wide';

  return (
    <Stack
      component="section"
      aria-label="Today at a glance"
      direction="row"
      sx={{ alignItems: 'flex-end', gap: { xs: 2, sm: 3.5 }, flexWrap: 'wrap', rowGap: 1 }}
    >
      {metrics.map((m) => (
        <Box key={m.key} sx={{ minWidth: 0 }}>
          <Typography sx={{ fontWeight: 700, fontSize: 20, lineHeight: 1.1, fontVariantNumeric: 'tabular-nums' }}>
            {formatMetric(m)}
          </Typography>
          <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', whiteSpace: 'nowrap' }}>
            {m.label}
          </Typography>
        </Box>
      ))}
      <Typography
        variant="caption"
        sx={{ color: 'text.secondary', border: '1px solid', borderColor: 'divider', borderRadius: 99, px: 1, py: 0.25, mb: 0.25, whiteSpace: 'nowrap' }}
      >
        {scope}
      </Typography>
    </Stack>
  );
}
