import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Paper, Stack, Typography,
} from '@mui/material';
import { CloudOutlined, ExpandLess, ExpandMore, MemoryOutlined } from '@mui/icons-material';
import extractionReviewService, {
  type ProcessingEvidenceResource,
} from '../../api/services/extractionReviewService';

const readable = (value?: string | null) => value
  ? value.replace(/([a-z])([A-Z])/g, '$1 $2').replaceAll('_', ' ')
  : 'Not recorded';

const cost = (amount?: number | null, currency?: string | null) =>
  amount == null || !currency
    ? 'Unpriced'
    : new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(amount);

export default function CommercialProcessingEvidence({
  resource,
  id,
}: {
  resource: ProcessingEvidenceResource;
  id: number;
}) {
  const [expanded, setExpanded] = useState(false);
  const query = useQuery({
    queryKey: ['commercial-processing-evidence', resource, id],
    queryFn: () => extractionReviewService.getCommercialProcessingEvidence(resource, id),
    enabled: Number.isInteger(id) && id > 0,
    retry: 1,
  });

  if (query.isLoading) {
    return <Paper variant="outlined" sx={{ mb: 2, p: 2 }}><Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}><CircularProgress size={18} /><Typography variant="body2">Loading processing evidence...</Typography></Stack></Paper>;
  }
  if (query.isError) {
    return <Alert severity="warning" sx={{ mb: 2 }} action={<Button size="small" onClick={() => void query.refetch()}>Retry</Button>}>Processing evidence is temporarily unavailable.</Alert>;
  }

  const evidence = query.data;
  if (!evidence) {
    return <Paper variant="outlined" sx={{ mb: 2, p: 2 }}><Typography sx={{ fontWeight: 800 }}>Processing evidence</Typography><Typography variant="body2" color="text.secondary">No intake or extraction record is linked to this commercial case yet.</Typography></Paper>;
  }

  const latestRun = evidence.runs[evidence.runs.length - 1];
  const latestJob = evidence.jobs[evidence.jobs.length - 1];
  const usedExternal = evidence.externalRequestCount > 0;
  return <Paper variant="outlined" sx={{ mb: 2, p: 2 }}>
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', md: 'center' } }}>
      <Box>
        <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <Typography sx={{ fontWeight: 800 }}>Processing evidence</Typography>
          <Chip size="small" icon={usedExternal ? <CloudOutlined /> : <MemoryOutlined />} color={usedExternal ? 'warning' : 'success'} label={usedExternal ? 'External provider used' : 'Local-first'} />
          <Chip size="small" variant="outlined" label={readable(latestRun?.processingPath ?? latestJob?.status)} />
        </Stack>
        <Typography variant="body2" color="text.secondary">Nexora Serial {evidence.nexoraSerial || 'not recorded'} · {evidence.occurrences.length} occurrence{evidence.occurrences.length === 1 ? '' : 's'} · {evidence.runs.length} run{evidence.runs.length === 1 ? '' : 's'}</Typography>
      </Box>
      <Button variant="outlined" startIcon={expanded ? <ExpandLess /> : <ExpandMore />} onClick={() => setExpanded((value) => !value)}>{expanded ? 'Hide evidence' : 'Show evidence'}</Button>
    </Stack>
    {expanded && <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, minmax(0, 1fr))' }, gap: 2, mt: 2, pt: 2, borderTop: '1px solid', borderColor: 'divider' }}>
      <Box><Typography variant="caption" color="text.secondary">Processing path</Typography><Typography sx={{ fontWeight: 700 }}>{readable(latestRun?.processingPath)}</Typography></Box>
      <Box><Typography variant="caption" color="text.secondary">OCR outcome</Typography><Typography sx={{ fontWeight: 700 }}>{readable(latestRun?.ocrStatus)}</Typography></Box>
      <Box><Typography variant="caption" color="text.secondary">Provider use</Typography><Typography sx={{ fontWeight: 700 }}>{evidence.localRequestCount} local · {evidence.externalRequestCount} external</Typography></Box>
      <Box><Typography variant="caption" color="text.secondary">External cost</Typography><Typography sx={{ fontWeight: 700 }}>{cost(evidence.externalCostAmount, evidence.externalCostCurrency)}</Typography><Typography variant="caption" color="text.secondary">{readable(evidence.externalCostStatus)}</Typography></Box>
    </Box>}
  </Paper>;
}
