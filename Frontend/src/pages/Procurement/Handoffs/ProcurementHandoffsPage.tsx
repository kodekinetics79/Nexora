import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControl, InputLabel, MenuItem, Paper, Select, Stack, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, TextField, Typography, useMediaQuery, useTheme,
} from '@mui/material';
import { Add, EditNote, OpenInNew, Refresh, Sync } from '@mui/icons-material';
import procurementHandoffService, { type ProcurementHandoff } from '../../../api/services/procurementHandoffService';
import { useAuth } from '../../../context/AuthContext';
import { statusLabel } from '../../../utils/statusLabels';
import { formatMoney } from '../../../utils/currency';

const todayPlus = (days: number) => new Date(Date.now() + days * 86_400_000).toISOString().slice(0, 10);
const statusOptions: Record<string, string[]> = {
  CREATED: ['EXTERNAL_PO_CREATED', 'CANCELLED'],
  EXTERNAL_PO_CREATED: ['EXTERNAL_PO_CREATED', 'SUPPLIER_CONFIRMED', 'CANCELLED'],
  SUPPLIER_CONFIRMED: ['SUPPLIER_CONFIRMED', 'DISPATCHED', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CANCELLED'],
  DISPATCHED: ['DISPATCHED', 'DELIVERED', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CANCELLED'],
  DELIVERED: ['DELIVERED', 'PARTIALLY_RECEIVED', 'RECEIVED', 'CANCELLED'],
  PARTIALLY_RECEIVED: ['PARTIALLY_RECEIVED', 'RECEIVED', 'CANCELLED'],
  RECEIVED: ['RECEIVED'], CANCELLED: ['CANCELLED'],
};
const statusColor = (value: string): 'default' | 'warning' | 'success' | 'error' | 'info' => {
  if (value === 'CANCELLED') return 'error';
  if (value === 'CREATED') return 'warning';
  if (value === 'RECEIVED') return 'success';
  return 'info';
};

export default function ProcurementHandoffsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const theme = useTheme();
  const mobile = useMediaQuery(theme.breakpoints.down('md'));
  const canEdit = hasPermission('Orders', 'edit') && hasPermission('Supplier History');
  const [search, setSearch] = useState('');
  const [selected, setSelected] = useState<ProcurementHandoff | null>(null);
  const [createOpen, setCreateOpen] = useState(false);
  const [candidateLineId, setCandidateLineId] = useState<number | ''>('');
  const [deliveryLocation, setDeliveryLocation] = useState('');
  const [requiredOn, setRequiredOn] = useState(todayPlus(14));
  const [poNumber, setPoNumber] = useState('');
  const [poLine, setPoLine] = useState('');
  const [expectedOn, setExpectedOn] = useState(todayPlus(14));
  const [status, setStatus] = useState('EXTERNAL_PO_CREATED');
  const query = useQuery({
    queryKey: ['procurement-handoffs', search],
    queryFn: () => procurementHandoffService.search(search),
  });
  const integration = useQuery({
    queryKey: ['procurement-integration-status'],
    queryFn: procurementHandoffService.integrationStatus,
  });
  const candidates = useQuery({
    queryKey: ['procurement-handoff-candidates'],
    queryFn: procurementHandoffService.candidates,
    enabled: canEdit,
  });
  const createHandoff = useMutation({
    mutationFn: () => procurementHandoffService.create({
      customerOrderLineId: Number(candidateLineId),
      destinationType: 'DROP_SHIP', deliveryLocation: deliveryLocation.trim(), requiredOn,
    }, `handoff-create:${candidateLineId}:${crypto.randomUUID()}`),
    onSuccess: async () => {
      setCreateOpen(false); setCandidateLineId(''); setDeliveryLocation('');
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['procurement-handoffs'] }),
        queryClient.invalidateQueries({ queryKey: ['procurement-handoff-candidates'] }),
      ]);
    },
  });
  const synchronize = useMutation({
    mutationFn: () => procurementHandoffService.synchronize(selected!.id, {
      expectedVersion: selected!.version,
      externalSupplierPoNumber: poNumber.trim(),
      externalSupplierPoLineNumber: poLine.trim(),
      orderedQuantity: selected!.requiredQuantity,
      approvedUnitCost: selected!.selectedUnitCost,
      expectedOn,
      status,
      synchronizedOn: new Date().toISOString(),
    }, `handoff-sync:${selected!.id}:${crypto.randomUUID()}`),
    onSuccess: async () => {
      setSelected(null);
      await queryClient.invalidateQueries({ queryKey: ['procurement-handoffs'] });
    },
  });

  const open = (handoff: ProcurementHandoff) => {
    setSelected(handoff);
    setPoNumber(handoff.externalSupplierPoNumber || '');
    setPoLine(handoff.externalSupplierPoLineNumber || '');
    setExpectedOn(handoff.externalExpectedOn || todayPlus(14));
    setStatus(handoff.externalStatus || 'EXTERNAL_PO_CREATED');
  };

  return <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1600, mx: 'auto' }}>
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', mb: 3 }}>
      <Box><Typography variant="h4" component="h1" sx={{ fontWeight: 800 }}>Procurement Handoffs</Typography>
        <Typography color="text.secondary">Sourced Customer Order lines awaiting or synchronized with an external procurement system.</Typography></Box>
      <Stack direction="row" spacing={1}>{canEdit && <Button variant="contained" startIcon={<Add />} onClick={() => setCreateOpen(true)}>Create handoff</Button>}
        <Button startIcon={<Refresh />} onClick={() => { void query.refetch(); void integration.refetch(); }}>Refresh</Button></Stack>
    </Stack>
    {integration.isError && <Alert severity="error" sx={{ mb: 2 }}>Integration status could not be loaded.</Alert>}
    {integration.data && <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
      <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ justifyContent: 'space-between' }}>
        <Box><Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>Operational synchronization</Typography>
          <Chip size="small" color={integration.data.isConfigured ? 'info' : 'default'}
            label={statusLabel(integration.data.connectorStatus)} />
        </Stack><Typography variant="body2" color="text.secondary">
          {integration.data.sourceSystem} · {integration.data.lastSuccessfulSync
            ? `Last verified ${new Date(integration.data.lastSuccessfulSync).toLocaleString()}`
            : 'No authoritative callback received'}
        </Typography></Box>
        <Stack direction="row" spacing={2} sx={{ flexWrap: 'wrap', rowGap: 1 }}>
          <Box><Typography variant="caption" color="text.secondary">Awaiting sync</Typography><Typography sx={{ fontWeight: 800 }}>{integration.data.awaitingSynchronization}</Typography></Box>
          <Box><Typography variant="caption" color="text.secondary">Dispatch backlog</Typography><Typography sx={{ fontWeight: 800 }}>{integration.data.pendingDispatch}</Typography></Box>
          <Box><Typography variant="caption" color="text.secondary">Retries</Typography><Typography sx={{ fontWeight: 800 }}>{integration.data.retryingDispatch}</Typography></Box>
          <Box><Typography variant="caption" color="text.secondary">Uncertain / failed</Typography><Typography sx={{ fontWeight: 800 }}>{integration.data.failedDispatch}</Typography></Box>
          <Box><Typography variant="caption" color="text.secondary">Dead letters</Typography><Typography sx={{ fontWeight: 800 }}>{integration.data.deadLetteredDispatch}</Typography></Box>
          <Box><Typography variant="caption" color="text.secondary">Stale</Typography><Typography sx={{ fontWeight: 800 }}>{integration.data.staleHandoffs}</Typography></Box>
          <Box><Typography variant="caption" color="text.secondary">Differences</Typography><Typography sx={{ fontWeight: 800 }}>{integration.data.reconciliationDifferences}</Typography></Box>
        </Stack>
      </Stack>
    </Paper>}
    <Paper variant="outlined" sx={{ p: 2, mb: 2 }}><TextField fullWidth size="small"
      label="Search Nexora Serial, Customer Order, Supplier, or external PO"
      value={search} onChange={(event) => setSearch(event.target.value)} /></Paper>
    {query.isError && <Alert severity="error">Procurement handoffs could not be loaded.</Alert>}
    {query.data?.length === 0 && <Alert severity="info">{search.trim() ? 'No handoffs match this search.' : 'No sourced Customer Order lines have been handed off.'}</Alert>}
    {query.data && query.data.length > 0 && mobile && <Stack spacing={1.5}>{query.data.map((item) => <Paper key={item.id} variant="outlined" sx={{ p: 2 }}>
      <Stack spacing={1}><Stack direction="row" sx={{ justifyContent: 'space-between', gap: 1 }}><Button onClick={() => navigate(`/sales/orders/${item.customerOrderId}`)}>{item.customerOrderNumber}</Button>
        <Chip size="small" label={statusLabel(item.externalStatus || item.status)} color={statusColor(item.externalStatus || item.status)} /></Stack>
        <Typography sx={{ fontFamily: 'monospace', fontWeight: 700 }}>{item.nexoraSerial}</Typography>
        <Typography variant="body2">{item.supplierName} · {item.requiredQuantity} · {formatMoney(item.selectedUnitCost, item.currencyCode)}</Typography>
        <Typography variant="caption">Demand {item.commercialDemandLineId} · Award {item.sourcingAwardId}</Typography>
        <Typography variant="caption">{statusLabel(item.destinationType)}{item.deliveryLocation ? ` · ${item.deliveryLocation}` : ''}</Typography>
        <Typography variant="caption">{item.externalSupplierPoNumber
          ? `Supplier PO ${item.externalSupplierPoNumber} · line ${item.externalSupplierPoLineNumber}`
          : `Awaiting ${item.externalSystemTarget}`}</Typography>
        {item.externalSalesOrderNumber && <Typography variant="caption">Sales Order {item.externalSalesOrderNumber}</Typography>}
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <Chip size="small" variant="outlined" color={item.isAuthoritative ? 'success' : 'default'} label={item.isAuthoritative ? 'Authoritative' : 'Not authoritative'} />
          <Typography variant="caption">{item.sourceOfTruth || 'Not synchronized'}{item.lastSynchronizedOn ? ` · ${new Date(item.lastSynchronizedOn).toLocaleString()}` : ''}</Typography>
        </Stack>
        {canEdit && !item.isAuthoritative && <Button startIcon={item.externalSupplierPoNumber ? <Sync /> : <EditNote />} onClick={() => open(item)}>
          {item.externalSupplierPoNumber ? 'Update status' : 'Add external reference'}</Button>}
      </Stack></Paper>)}</Stack>}
    {query.data && query.data.length > 0 && !mobile && <TableContainer component={Paper} variant="outlined">
      <Table size="small" sx={{ minWidth: 1100 }}><TableHead><TableRow>
        <TableCell>Customer Order</TableCell><TableCell>Commercial lineage</TableCell><TableCell>Supplier</TableCell>
        <TableCell align="right">Quantity / cost</TableCell><TableCell>Destination</TableCell>
        <TableCell>External status</TableCell><TableCell>Authority</TableCell><TableCell align="right">Action</TableCell>
      </TableRow></TableHead><TableBody>{query.data.map((item) => <TableRow key={item.id} hover>
        <TableCell><Button onClick={() => navigate(`/sales/orders/${item.customerOrderId}`)}>{item.customerOrderNumber}</Button></TableCell>
        <TableCell><Typography sx={{ fontFamily: 'monospace', fontWeight: 700 }}>{item.nexoraSerial}</Typography>
          <Typography variant="caption">Demand {item.commercialDemandLineId} · Award {item.sourcingAwardId} · Quote line {item.supplierQuotedItemId}</Typography></TableCell>
        <TableCell>{item.supplierName}</TableCell>
        <TableCell align="right">{item.requiredQuantity} / {formatMoney(item.selectedUnitCost, item.currencyCode)}</TableCell>
        <TableCell>{statusLabel(item.destinationType)}{item.deliveryLocation && <Typography variant="caption" sx={{ display: 'block' }}>{item.deliveryLocation}</Typography>}</TableCell>
        <TableCell><Chip size="small" label={statusLabel(item.externalStatus || item.status)} color={statusColor(item.externalStatus || item.status)} />
          <Typography variant="caption" sx={{ display: 'block' }}>{item.externalSupplierPoNumber
            ? `Supplier PO ${item.externalSupplierPoNumber} · line ${item.externalSupplierPoLineNumber}`
            : `Awaiting ${item.externalSystemTarget}`}</Typography>
          {item.externalSalesOrderNumber && <Typography variant="caption" sx={{ display: 'block' }}>Sales Order {item.externalSalesOrderNumber}</Typography>}</TableCell>
        <TableCell><Chip size="small" variant="outlined" color={item.isAuthoritative ? 'success' : 'default'} label={item.isAuthoritative ? 'Authoritative' : 'Not authoritative'} />
          <Typography variant="caption" sx={{ display: 'block' }}>{item.sourceOfTruth || 'Not synchronized'}{item.lastSynchronizedOn ? ` · ${new Date(item.lastSynchronizedOn).toLocaleString()}` : ''}</Typography></TableCell>
        <TableCell align="right">{canEdit && !item.isAuthoritative && <Button startIcon={item.externalSupplierPoNumber ? <Sync /> : <EditNote />} onClick={() => open(item)}>
          {item.externalSupplierPoNumber ? 'Update status' : 'Add external reference'}</Button>}</TableCell>
      </TableRow>)}</TableBody></Table>
    </TableContainer>}

    <Dialog open={Boolean(selected)} onClose={() => !synchronize.isPending && setSelected(null)} fullWidth maxWidth="sm">
      <DialogTitle>External Supplier PO reference</DialogTitle><DialogContent>
        <Alert severity={selected?.externalSystemTarget === 'MANUAL' ? 'info' : 'warning'} sx={{ mb: 2 }}>
          This controlled user entry remains non-authoritative. Authoritative callbacks require an authenticated integration.
        </Alert>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField required label="External Supplier PO number" value={poNumber} onChange={(event) => setPoNumber(event.target.value)} />
          <TextField required label="External Supplier PO line" value={poLine} onChange={(event) => setPoLine(event.target.value)} />
          <TextField required type="date" label="Expected date" value={expectedOn} onChange={(event) => setExpectedOn(event.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
          <FormControl><InputLabel id="handoff-status">Status</InputLabel><Select labelId="handoff-status" label="Status" value={status} onChange={(event) => setStatus(event.target.value)}>
            {(statusOptions[selected?.externalStatus || selected?.status || 'CREATED'] || []).map((value) => <MenuItem value={value} key={value}>{statusLabel(value)}</MenuItem>)}
          </Select></FormControl>
          {synchronize.isError && <Alert severity="error">The external reference could not be saved. Reload before retrying.</Alert>}
        </Stack>
      </DialogContent><DialogActions><Button onClick={() => setSelected(null)} disabled={synchronize.isPending}>Cancel</Button>
        <Button variant="contained" startIcon={<OpenInNew />} onClick={() => synchronize.mutate()}
          disabled={synchronize.isPending || !poNumber.trim() || !poLine.trim() || !expectedOn}>Save reference</Button></DialogActions>
    </Dialog>

    <Dialog open={createOpen} onClose={() => !createHandoff.isPending && setCreateOpen(false)} fullWidth maxWidth="sm">
      <DialogTitle>Create procurement handoff</DialogTitle><DialogContent><Stack spacing={2} sx={{ mt: 1 }}>
        <Alert severity="info">Supplier, quantity, cost, and Nexora Serial come from the approved sourcing decision.</Alert>
        <FormControl required><InputLabel id="handoff-candidate">Sourced Customer Order line</InputLabel>
          <Select labelId="handoff-candidate" label="Sourced Customer Order line" value={candidateLineId}
            onChange={(event) => setCandidateLineId(Number(event.target.value))}>
            {(candidates.data || []).map((candidate) => <MenuItem key={candidate.customerOrderLineId} value={candidate.customerOrderLineId}>
              {candidate.customerOrderNumber} · {candidate.supplierName} · {candidate.nexoraSerial}
            </MenuItem>)}
          </Select></FormControl>
        {candidates.data?.length === 0 && <Alert severity="info">No fully sourced Customer Order lines are awaiting handoff.</Alert>}
        <TextField required label="Delivery location" value={deliveryLocation} onChange={(event) => setDeliveryLocation(event.target.value)} slotProps={{ htmlInput: { maxLength: 500 } }} />
        <TextField required type="date" label="Required date" value={requiredOn} onChange={(event) => setRequiredOn(event.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
        {createHandoff.isError && <Alert severity="error">The handoff could not be created. Reload the eligible lines and retry.</Alert>}
      </Stack></DialogContent><DialogActions><Button onClick={() => setCreateOpen(false)} disabled={createHandoff.isPending}>Cancel</Button>
        <Button variant="contained" onClick={() => createHandoff.mutate()} disabled={createHandoff.isPending || !candidateLineId || !deliveryLocation.trim() || !requiredOn}>Create handoff</Button></DialogActions>
    </Dialog>
  </Box>;
}
