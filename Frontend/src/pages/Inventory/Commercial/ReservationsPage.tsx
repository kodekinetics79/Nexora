import { useQuery } from '@tanstack/react-query';
import { Alert, Table, TableBody, TableCell, TableHead, TableRow, Tooltip, Typography } from '@mui/material';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { PageShell, QueryState, ResponsiveTable, StatusChip, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';

export default function ReservationsPage() {
  const query = useQuery({ queryKey: ['inventory-intelligence', 'reservations', 'history'], queryFn: () => commercialIntelligenceService.getReservations({}) });
  const rows = query.data ?? [];
  const untraceable = rows.filter(row => !row.materialLotId).length;
  return <PageShell title="Reservation history" subtitle="Previously recorded inventory reservations."><Alert severity="info" sx={{ mb: 2 }}>Reservations are disabled for this release. Historical records do not withhold stock.</Alert><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No historical inventory reservations exist.">
    {/* FR-INV-01. A hold that names no lot is stock a recall cannot reach. Stated as a count
        above the grid rather than left to be inferred from a column of dashes, because it is the
        number somebody has to act on — the fix is a stock take that puts the balance behind a
        receipt, and nobody starts one they cannot see the need for. */}
    {untraceable > 0 && <Typography variant="body2" color="warning.main" sx={{ mb: 1.5, fontWeight: 600 }}>
      {untraceable} of {rows.length} historical records name no material lot.
    </Typography>}
    <ResponsiveTable label="Inventory reservation history"><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Warehouse</TableCell><TableCell align="right">Quantity (units)</TableCell><TableCell>Material lot</TableCell><TableCell>Demand</TableCell><TableCell>Nexora Serial</TableCell><TableCell>Required</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{row.partNumber} - {row.productName}</TableCell><TableCell>{row.warehouseName}</TableCell><TableCell align="right">{row.quantity}</TableCell><TableCell>{row.lotNumber
      ? row.lotNumber
      : <Tooltip title="This historical record names no material lot."><Typography component="span" variant="body2" color="warning.main" sx={{ fontWeight: 600 }}>No lot — not traceable</Typography></Tooltip>}</TableCell><TableCell>{row.demandType} {row.demandReference}</TableCell><TableCell>{row.nexoraSerial || 'Not linked'}</TableCell><TableCell>{formatDateTime(row.requiredAt)}</TableCell><TableCell><StatusChip value={row.status} /></TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
