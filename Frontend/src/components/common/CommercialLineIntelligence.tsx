import React from 'react';
import { Alert, Box, Button, Chip, CircularProgress, MenuItem, Paper, Stack, TextField, Typography } from '@mui/material';
import { Inventory2Outlined as InventoryIcon, Refresh as RefreshIcon } from '@mui/icons-material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import commercialIntelligenceService, { type CommercialLineResolutionDTO } from '../../api/services/commercialIntelligenceService';

type Props = { stage: 'lead' | 'rfq' | 'quote'; recordId: number };
const labels: Record<CommercialLineResolutionDTO['classification'], { text: string; color: 'success' | 'info' | 'warning' | 'error' }> = {
  KnownInStock: { text: 'Known, in stock', color: 'success' },
  KnownIncoming: { text: 'Known, incoming', color: 'info' },
  KnownShortage: { text: 'Known, shortage', color: 'warning' },
  UnknownProduct: { text: 'Unknown product', color: 'error' },
  PossibleMatchReview: { text: 'Possible match review', color: 'warning' },
};

const CommercialLineIntelligence: React.FC<Props> = ({ stage, recordId }) => {
  const client = useQueryClient();
  const [limit, setLimit] = React.useState<10 | 20 | 50>(10);
  const key = ['commercial-line-resolutions', stage, recordId];
  const query = useQuery({
    queryKey: key,
    queryFn: () => stage === 'lead'
      ? commercialIntelligenceService.getLeadLineResolutions(recordId)
      : stage === 'rfq'
        ? commercialIntelligenceService.getRfqLineResolutions(recordId)
        : commercialIntelligenceService.getQuoteLineResolutions(recordId),
    enabled: Number.isFinite(recordId) && recordId > 0,
  });
  const resolve = useMutation({
    mutationFn: () => commercialIntelligenceService.resolveLeadLines(recordId, limit),
    onSuccess: (rows) => client.setQueryData(key, rows),
  });
  const rows = resolve.data ?? query.data ?? [];

  return (
    <Paper sx={{ p: 2, border: '1px solid', borderColor: 'divider', borderRadius: 2 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' }, mb: rows.length ? 2 : 0 }}>
        <Box>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <InventoryIcon color="primary" fontSize="small" />
            <Typography sx={{ fontWeight: 900 }}>Commercial line intelligence</Typography>
          </Stack>
          <Typography variant="caption" color="text.secondary">Inventory and tenant-local sourcing evidence captured for this lead revision.</Typography>
        </Box>
        {stage === 'lead' && (
          <Stack direction="row" spacing={1}>
            <TextField select size="small" label="Supplier options" value={limit} onChange={(event) => setLimit(Number(event.target.value) as 10 | 20 | 50)} sx={{ minWidth: 150 }}>
              {[10, 20, 50].map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}
            </TextField>
            <Button variant="outlined" startIcon={resolve.isPending ? <CircularProgress size={16} /> : <RefreshIcon />} onClick={() => resolve.mutate()} disabled={resolve.isPending}>
              Check
            </Button>
          </Stack>
        )}
      </Stack>
      {(query.isError || resolve.isError) && <Alert severity="error">Commercial evidence could not be loaded. No product or supplier was selected automatically.</Alert>}
      {query.isLoading && <CircularProgress size={22} />}
      {!query.isLoading && rows.length === 0 && <Typography variant="body2" color="text.secondary">No persisted line resolution is available yet.</Typography>}
      <Stack spacing={1.25}>
        {rows.map((row) => {
          const label = labels[row.classification];
          return <Box key={row.id} sx={{ p: 1.5, border: '1px solid', borderColor: 'divider', borderRadius: 1 }}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ justifyContent: 'space-between' }}>
              <Box><Typography sx={{ fontWeight: 850 }}>{row.requestedPartNumber}</Typography><Typography variant="caption" color="text.secondary">Requested {row.requestedQuantity} · ATP {row.availableToPromise} · Incoming {row.incomingAvailable}</Typography></Box>
              <Chip size="small" color={label.color} label={label.text} sx={{ fontWeight: 800, alignSelf: 'flex-start' }} />
            </Stack>
            <Typography variant="caption" color="text.secondary">Evidence {row.evidenceReference || 'not recorded'} · inventory as of {new Date(row.inventoryAsOfUtc).toLocaleString()}</Typography>
            {row.relatedResources.length > 0 && <Typography variant="body2" sx={{ mt: .75 }}>{row.relatedResources.length} tenant-local supplier or product references available. External discovery was not used.</Typography>}
          </Box>;
        })}
      </Stack>
    </Paper>
  );
};

export default CommercialLineIntelligence;
