import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Box, Button, Paper, Stack, Typography } from '@mui/material';
import { ExpandLess as HideIcon, ExpandMore as ShowIcon } from '@mui/icons-material';
import commercialIntelligenceService, { type IntelligenceMetric } from '../../api/services/commercialIntelligenceService';
import { formatMoney } from '../../utils/currency';

/**
 * Today's numbers for the person working the queue — a slim strip above the Inbox, never a
 * dashboard. Three or four figures scoped to the reader (the server decides the scope: their own
 * accounts for a rep, their team for a supervisor), each with its unit spelled out, and a way to
 * fold the strip away that is remembered on this browser.
 *
 * It fails quietly on purpose: when the figures cannot be read the strip is simply absent. The
 * Inbox is the work; this is a glance at how the work is going, and a glance that errors is
 * worse than none.
 */
const STORAGE_KEY = 'nexora.inbox.glance';

const readFolded = (): boolean => {
  try { return localStorage.getItem(STORAGE_KEY) === 'folded'; } catch { return false; }
};
const writeFolded = (folded: boolean) => {
  try { localStorage.setItem(STORAGE_KEY, folded ? 'folded' : 'open'); } catch { /* not available */ }
};

export const formatMetric = (m: IntelligenceMetric): string => {
  if (m.unit === 'currency') return formatMoney(m.value, m.currencyCode);
  if (m.unit === 'percentage') return `${m.value.toLocaleString('en-US', { maximumFractionDigits: 1 })}%`;
  if (m.unit === 'hours') return `${m.value.toLocaleString('en-US', { maximumFractionDigits: 1 })} h`;
  return m.value.toLocaleString('en-US', { maximumFractionDigits: 0 });
};

export default function GlanceStrip() {
  const [folded, setFolded] = useState(readFolded);
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

  const toggle = () => { setFolded((f) => { writeFolded(!f); return !f; }); };

  return (
    <Box component="section" aria-label="Today at a glance" sx={{ mb: 2 }}>
      {folded ? (
        <Button size="small" startIcon={<ShowIcon />} onClick={toggle} sx={{ fontWeight: 700, px: 1 }}>
          Show today's numbers
        </Button>
      ) : (
        <Paper variant="outlined" className="nx-glass" sx={{ p: { xs: 1.25, sm: 1.5 }, borderRadius: 3 }}>
          <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between', gap: 1, flexWrap: 'wrap' }}>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: 'repeat(2, minmax(0, 1fr))', sm: `repeat(${metrics.length}, minmax(0, 1fr))` }, gap: { xs: 1.5, sm: 3 }, flexGrow: 1, minWidth: 0 }}>
              {metrics.map((m) => (
                <Box key={m.key} sx={{ minWidth: 0 }}>
                  <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase', fontSize: 10.5, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {m.label}
                  </Typography>
                  <Typography sx={{ fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700, fontSize: { xs: 20, sm: 24 }, lineHeight: 1.1, fontVariantNumeric: 'tabular-nums' }}>
                    {formatMetric(m)}
                  </Typography>
                </Box>
              ))}
            </Box>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexShrink: 0 }}>
              <Typography variant="caption" color="text.secondary">{scope}</Typography>
              <Button size="small" startIcon={<HideIcon />} onClick={toggle} sx={{ fontWeight: 700, px: 1 }}>Hide</Button>
            </Stack>
          </Stack>
        </Paper>
      )}
    </Box>
  );
}
