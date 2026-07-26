import type { ReactNode } from 'react';
import { Alert, Box, Button, Chip, CircularProgress, Paper, Stack, Typography } from '@mui/material';
import { Refresh as RefreshIcon } from '@mui/icons-material';
import type { CurrencyAmountGroupDTO, CurrencyPipelineGroupDTO, IntelligenceMetric } from '../../api/services/commercialIntelligenceService';

export const formatMetric = (metric: IntelligenceMetric) => {
  if (metric.unit === 'percentage') return `${metric.value.toLocaleString(undefined, { maximumFractionDigits: 1 })}%`;
  if (metric.unit === 'hours') return `${metric.value.toLocaleString(undefined, { maximumFractionDigits: 1 })} h`;
  const value = metric.value.toLocaleString(undefined, { maximumFractionDigits: metric.unit === 'currency' ? 2 : 0 });
  return metric.unit === 'currency' ? `${metric.currencyCode ?? ''} ${value}`.trim() : value;
};

export const formatDateTime = (value?: string | null) => value
  ? new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })
  : 'Not scheduled';

export function PipelineGroups({ groups, weighted = true }: { groups: CurrencyPipelineGroupDTO[]; weighted?: boolean }) {
  if (!groups.length) return <Typography component="span" color="text.secondary">No active value</Typography>;
  return (
    <Stack spacing={0.25} sx={{ alignItems: 'flex-end' }}>
      {groups.map(group => (
        <Typography component="span" variant="body2" key={`${group.currencyId ?? 'unassigned'}:${group.currencyCode ?? ''}`}>
          {group.currencyCode || (group.currencyId == null ? 'Currency unassigned' : `Currency ${group.currencyId}`)}{' '}
          {(weighted ? group.weightedPipeline : group.pipelineValue).toLocaleString(undefined, { maximumFractionDigits: 2 })}
        </Typography>
      ))}
    </Stack>
  );
}

export function CurrencyAmounts({ groups }: { groups: CurrencyAmountGroupDTO[] }) {
  if (!groups.length) return <Typography component="span" color="text.secondary">No recognized value</Typography>;
  return <Stack spacing={0.25}>{groups.map(group => <Typography key={group.currencyCode}>{group.currencyCode} {group.value.toLocaleString(undefined, { maximumFractionDigits: 2 })}</Typography>)}</Stack>;
}

export function PageShell({ title, subtitle, actions, children }: { title: string; subtitle: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <Box sx={{ maxWidth: 1440, mx: 'auto', p: { xs: 1, sm: 2, md: 3 }, minWidth: 0 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'center' }, mb: 2.5 }}>
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="h5" sx={{ fontWeight: 900 }}>{title}</Typography>
          <Typography variant="body2" color="text.secondary">{subtitle}</Typography>
        </Box>
        {actions}
      </Stack>
      {children}
    </Box>
  );
}

export function MetricGrid({ metrics }: { metrics: IntelligenceMetric[] }) {
  if (!metrics.length) return null;
  return (
    <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, minmax(0, 1fr))' }, gap: 1.5, mb: 2.5 }}>
      {metrics.map((metric) => (
        <Paper key={metric.key} variant="outlined" sx={{ p: 2, minWidth: 0 }}>
          <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>{metric.label}</Typography>
          <Typography variant="h6" sx={{ fontWeight: 900, overflowWrap: 'anywhere' }}>{formatMetric(metric)}</Typography>
        </Paper>
      ))}
    </Box>
  );
}

export function QueryState({ loading, error, empty, onRetry, emptyText, children }: { loading: boolean; error: boolean; empty: boolean; onRetry: () => void; emptyText: string; children: ReactNode }) {
  if (loading) return <Box sx={{ minHeight: 240, display: 'grid', placeItems: 'center' }}><CircularProgress aria-label="Loading" /></Box>;
  if (error) return <Alert severity="error" action={<Button color="inherit" startIcon={<RefreshIcon />} onClick={onRetry}>Retry</Button>}>This persisted view could not be loaded. No empty result has been assumed.</Alert>;
  if (empty) return <Paper variant="outlined" sx={{ p: 4, textAlign: 'center' }}><Typography color="text.secondary">{emptyText}</Typography></Paper>;
  return <>{children}</>;
}

export function ResponsiveTable({ label, children }: { label: string; children: ReactNode }) {
  return <Paper variant="outlined" role="region" aria-label={label} tabIndex={0} sx={{ overflowX: 'auto', maxWidth: '100%' }}>{children}</Paper>;
}

export function StatusChip({ value }: { value: string }) {
  const normalized = value.toLowerCase();
  const color = normalized.includes('overdue') || normalized.includes('short') || normalized.includes('blocked')
    ? 'error'
    : normalized.includes('pending') || normalized.includes('due') || normalized.includes('review')
      ? 'warning'
      : normalized.includes('active') || normalized.includes('available') || normalized.includes('complete')
        ? 'success'
        : 'default';
  return <Chip size="small" label={value} color={color} variant="outlined" sx={{ fontWeight: 700 }} />;
}
