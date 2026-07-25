import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
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
  return <PageShell title="Reservations" subtitle="Inventory committed to RFQs, quotes, and orders."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No active inventory reservations exist."><ResponsiveTable label="Inventory reservations"><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Warehouse</TableCell><TableCell align="right">Quantity</TableCell><TableCell>Demand</TableCell><TableCell>Nexora Serial</TableCell><TableCell>Required</TableCell><TableCell>Status</TableCell>{canRelease && <TableCell>Action</TableCell>}</TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{row.partNumber} - {row.productName}</TableCell><TableCell>{row.warehouseName}</TableCell><TableCell align="right">{row.quantity}</TableCell><TableCell>{row.demandType} {row.demandReference}</TableCell><TableCell>{row.nexoraSerial || 'Not linked'}</TableCell><TableCell>{formatDateTime(row.requiredAt)}</TableCell><TableCell><StatusChip value={row.status} /></TableCell>{canRelease && <TableCell><Button color="warning" size="small" disabled={mutation.isPending} onClick={() => mutation.mutate(row)}>Release</Button></TableCell>}</TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
