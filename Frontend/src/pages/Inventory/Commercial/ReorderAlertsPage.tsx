import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Paper, Stack,
  Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import commercialIntelligenceService, {
  type ReorderAlertDTO,
} from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';
import { serverMessage } from './StockActionsDialog';

const KIND_LABEL: Record<ReorderAlertDTO['kind'], string> = {
  OUT_OF_STOCK: 'Out of stock',
  BELOW_MINIMUM: 'Below minimum',
  REORDER_POINT: 'Reorder point reached',
  OVERSTOCK: 'Above maximum',
};

const KIND_COLOUR: Record<ReorderAlertDTO['kind'], 'error.main' | 'warning.main'> = {
  OUT_OF_STOCK: 'error.main',
  BELOW_MINIMUM: 'error.main',
  REORDER_POINT: 'warning.main',
  OVERSTOCK: 'warning.main',
};

/**
 * FR-INV-04. The reorder alert ledger.
 *
 * <p>Open and acknowledged alerts by default. A resolved alert is history, and burying today's four
 * alerts under a year of resolved ones is how an exception screen stops being read — which is the
 * same failure, one level up, as an alert that fires on everything.</p>
 *
 * <p>Every row states the arithmetic that raised it: available to promise, what is already on order,
 * and the level that was breached. An alert that only said "low stock" would make the reader open
 * three other screens to decide whether it is worth acting on, and after the third time they stop
 * opening them.</p>
 */
export default function ReorderAlertsPage() {
  const canAcknowledge = useAuth().hasPermission('Products', 'edit');
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [status, setStatus] = useState('');
  const [kind, setKind] = useState('');
  const [taking, setTaking] = useState<ReorderAlertDTO | null>(null);
  const [reason, setReason] = useState('');
  const [failure, setFailure] = useState<string | null>(null);

  const query = useQuery({
    queryKey: ['inventory-intelligence', 'reorder-alerts', status, kind],
    queryFn: () => commercialIntelligenceService.getReorderAlerts({
      status: status || undefined,
      kind: kind || undefined,
    }),
  });

  const acknowledge = useMutation({
    mutationFn: (alert: ReorderAlertDTO) =>
      commercialIntelligenceService.acknowledgeReorderAlert(alert.id, alert.version, reason),
    onSuccess: () => {
      enqueueSnackbar('Alert acknowledged', { variant: 'success' });
      setTaking(null);
      setReason('');
      setFailure(null);
      void client.invalidateQueries({ queryKey: ['inventory-intelligence', 'reorder-alerts'] });
    },
    onError: (error) => setFailure(serverMessage(error, 'The acknowledgement was refused and no reason was returned.')),
  });

  const rows = query.data?.rows ?? [];

  return (
    <PageShell
      title="Reorder alerts"
      subtitle="Stock that has breached the minimum, maximum or reorder level set for it."
      actions={
        <Stack direction="row" spacing={1}>
          <TextField select size="small" label="Status" value={status} sx={{ minWidth: 160 }}
            onChange={e => setStatus(e.target.value)}>
            <MenuItem value="">Open and acknowledged</MenuItem>
            <MenuItem value="OPEN">Open only</MenuItem>
            <MenuItem value="ACKNOWLEDGED">Acknowledged only</MenuItem>
            <MenuItem value="RESOLVED">Resolved</MenuItem>
          </TextField>
          <TextField select size="small" label="Kind" value={kind} sx={{ minWidth: 180 }}
            onChange={e => setKind(e.target.value)}>
            <MenuItem value="">All kinds</MenuItem>
            {(Object.keys(KIND_LABEL) as ReorderAlertDTO['kind'][]).map(key =>
              <MenuItem key={key} value={key}>{KIND_LABEL[key]}</MenuItem>)}
          </TextField>
        </Stack>
      }
    >
      <QueryState
        loading={query.isLoading}
        error={query.isError}
        empty={!rows.length}
        onRetry={() => void query.refetch()}
        emptyText={
          // Not "all healthy". A tenant that has configured no levels raises no alerts by design,
          // and telling them everything is fine would be a claim the data cannot support.
          status === 'RESOLVED'
            ? 'No alerts have been resolved yet.'
            : 'No stock is currently breaching a configured level. Items with no minimum or maximum set are not being watched at all — the stock levels screen shows how many.'
        }
      >
        <ResponsiveTable label="Reorder alerts">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Raised</TableCell>
                <TableCell>Part</TableCell>
                <TableCell>Warehouse</TableCell>
                <TableCell>Condition</TableCell>
                <TableCell align="right">Available (units)</TableCell>
                <TableCell align="right">On order (units)</TableCell>
                <TableCell align="right">Level (units)</TableCell>
                <TableCell align="right">Gap (units)</TableCell>
                <TableCell>Notified</TableCell>
                <TableCell>State</TableCell>
                {canAcknowledge && <TableCell>Action</TableCell>}
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map(row => (
                <TableRow hover key={row.id}>
                  <TableCell>{formatDateTime(row.raisedOn)}</TableCell>
                  <TableCell>{row.partNumber}{row.productName ? ` — ${row.productName}` : ''}</TableCell>
                  <TableCell>{row.warehouseName}</TableCell>
                  <TableCell>
                    <Typography component="span" variant="body2"
                      sx={{ fontWeight: 700, color: KIND_COLOUR[row.kind] }}>
                      {KIND_LABEL[row.kind]}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">{row.availableQuantity}</TableCell>
                  <TableCell align="right">{row.incomingQuantity}</TableCell>
                  <TableCell align="right">{row.thresholdQuantity}</TableCell>
                  <TableCell align="right">{row.shortfallQuantity}</TableCell>
                  <TableCell>
                    {row.notifiedCount > 0
                      ? `${row.notifiedCount} sent`
                      : (
                        // A zero here is a real and actionable state — the alert is on the ledger
                        // and nobody has been mailed. It must not read as a blank.
                        <Tooltip title="No email copy was accepted by the provider. The alert is still live here; check the mail configuration.">
                          <Typography component="span" variant="body2" color="warning.main" sx={{ fontWeight: 600 }}>
                            Not emailed
                          </Typography>
                        </Tooltip>
                      )}
                  </TableCell>
                  <TableCell>
                    {row.status === 'ACKNOWLEDGED'
                      ? <Tooltip title={`${row.acknowledgedBy ?? 'Unknown'}: ${row.acknowledgementReason ?? ''}`}>
                          <Typography component="span" variant="body2">Taken by {row.acknowledgedBy}</Typography>
                        </Tooltip>
                      : row.status === 'RESOLVED'
                        ? <Typography component="span" variant="body2" color="success.main">
                            {row.resolutionReason ?? 'Resolved'}
                          </Typography>
                        : <Typography component="span" variant="body2">Open</Typography>}
                  </TableCell>
                  {canAcknowledge && (
                    <TableCell>
                      {row.status === 'OPEN' && (
                        <Button size="small" onClick={() => { setTaking(row); setReason(''); setFailure(null); }}>
                          Acknowledge
                        </Button>
                      )}
                    </TableCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>

      <Dialog open={taking !== null} onClose={() => setTaking(null)} fullWidth maxWidth="sm">
        <DialogTitle>Acknowledge {taking ? KIND_LABEL[taking.kind] : ''}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            {failure && <Alert severity="error">{failure}</Alert>}
            <Typography variant="body2" color="text.secondary">
              Acknowledging records that you own this alert. It does not silence the condition — the
              alert stays on the ledger and resolves itself when the stock recovers.
            </Typography>
            <TextField
              label="What are you doing about it?"
              size="small"
              multiline
              minRows={2}
              value={reason}
              onChange={e => setReason(e.target.value)}
              helperText="Required. An acknowledgement with no reason is a mute button with nobody's name on it."
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setTaking(null)}>Cancel</Button>
          <Button variant="contained" disabled={!reason.trim() || acknowledge.isPending}
            onClick={() => taking && acknowledge.mutate(taking)}>
            Acknowledge
          </Button>
        </DialogActions>
      </Dialog>

      {query.data && (
        <Box sx={{ mt: 2 }}>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="body2" color="text.secondary">
              Alerts are raised against available-to-promise plus stock already on order, not against
              on-hand: a warehouse whose entire stock is reserved can supply nobody, and a row whose
              gap is already covered by a purchase order needs no chasing. Only the worst condition a
              row qualifies for is raised.
            </Typography>
          </Paper>
        </Box>
      )}
    </PageShell>
  );
}
