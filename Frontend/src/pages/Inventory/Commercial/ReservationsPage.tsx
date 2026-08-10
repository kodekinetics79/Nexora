import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography } from '@mui/material';
import { useSnackbar } from 'notistack';
import commercialIntelligenceService, { type ReservationDTO } from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, StatusChip, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';

export default function ReservationsPage() {
  const canRelease = useAuth().hasPermission('Products', 'edit');
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const query = useQuery({ queryKey: ['inventory-intelligence', 'reservations'], queryFn: () => commercialIntelligenceService.getReservations({ status: 'active' }) });
  const mutation = useMutation({ mutationFn: (row: ReservationDTO) => commercialIntelligenceService.releaseReservation(row.id, row.version, crypto.randomUUID()), onSuccess: () => { enqueueSnackbar('Reservation released', { variant: 'success' }); void client.invalidateQueries({ queryKey: ['inventory-intelligence', 'reservations'] }); }, onError: () => enqueueSnackbar('Reservation could not be released', { variant: 'error' }) });
  const rows = query.data ?? [];
  const untraceable = rows.filter(row => !row.materialLotId).length;
  return <PageShell title="Reservations" subtitle="Inventory committed to RFQs, quotes, and orders."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No active inventory reservations exist.">
    {/* FR-INV-01. A hold that names no lot is stock a recall cannot reach. Stated as a count
        above the grid rather than left to be inferred from a column of dashes, because it is the
        number somebody has to act on — the fix is a stock take that puts the balance behind a
        receipt, and nobody starts one they cannot see the need for. */}
    {untraceable > 0 && <Typography variant="body2" color="warning.main" sx={{ mb: 1.5, fontWeight: 600 }}>
      {untraceable} of {rows.length} active holds name no material lot. That stock entered by count, adjustment or transfer, so it has no goods receipt behind it and a recall cannot reach it.
    </Typography>}
    <ResponsiveTable label="Inventory reservations"><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Warehouse</TableCell><TableCell align="right">Quantity (units)</TableCell><TableCell>Material lot</TableCell><TableCell>Demand</TableCell><TableCell>Nexora Serial</TableCell><TableCell>Required</TableCell><TableCell>Status</TableCell>{canRelease && <TableCell>Action</TableCell>}</TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{row.partNumber} - {row.productName}</TableCell><TableCell>{row.warehouseName}</TableCell><TableCell align="right">{row.quantity}</TableCell><TableCell>{row.lotNumber
      ? row.lotNumber
      : <Tooltip title="This hold names no lot, so the physical units it reserves cannot be identified. Stock that entered by opening count, adjustment or transfer has no goods receipt behind it."><Typography component="span" variant="body2" color="warning.main" sx={{ fontWeight: 600 }}>No lot — not traceable</Typography></Tooltip>}</TableCell><TableCell>{row.demandType} {row.demandReference}</TableCell><TableCell>{row.nexoraSerial || 'Not linked'}</TableCell><TableCell>{formatDateTime(row.requiredAt)}</TableCell><TableCell><StatusChip value={row.status} /></TableCell>{canRelease && <TableCell><Button color="warning" size="small" disabled={mutation.isPending} onClick={() => mutation.mutate(row)}>Release</Button></TableCell>}</TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
