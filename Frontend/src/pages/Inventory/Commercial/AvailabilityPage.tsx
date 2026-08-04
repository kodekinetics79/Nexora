import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Alert, Table, TableBody, TableCell, TableHead, TableRow, TextField } from '@mui/material';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { PageShell, QueryState, ResponsiveTable, StatusChip } from '../../SalesManagement/CommercialPagePrimitives';

/** Matches the server-side cap in InventoryIntelligenceController.MaxRows. */
const ROW_CAP = 500;

export default function AvailabilityPage() {
  const [search, setSearch] = useState('');
  const query = useQuery({ queryKey: ['inventory-intelligence', 'availability', search], queryFn: () => commercialIntelligenceService.getAvailability({ search: search || undefined }) });
  const rows = query.data ?? [];
  // The server caps this list. Say so, rather than letting a rep read an unfiltered page as the
  // whole stock position — a silently truncated availability table is worse than no table.
  const truncated = rows.length >= ROW_CAP;
  return <PageShell title="Availability" subtitle="On-hand, reserved, available, and incoming quantities by warehouse." actions={<TextField size="small" label="Search part or product" value={search} onChange={event => setSearch(event.target.value)} />}><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No availability records match this search.">{truncated && <Alert severity="info" sx={{ mb: 2 }}>Showing the first {ROW_CAP} rows. Search by part or product to narrow this list.</Alert>}<ResponsiveTable label="Product availability"><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Product</TableCell><TableCell>Warehouse</TableCell><TableCell align="right">On hand</TableCell><TableCell align="right">Reserved</TableCell><TableCell align="right">Available</TableCell><TableCell align="right">Incoming</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={`${row.productId}-${row.warehouseId}`}><TableCell>{row.partNumber}</TableCell><TableCell>{row.productName}</TableCell><TableCell>{row.warehouseName}</TableCell><TableCell align="right">{row.onHand}</TableCell><TableCell align="right">{row.reserved}</TableCell><TableCell align="right">{row.available}</TableCell><TableCell align="right">{row.incoming}</TableCell><TableCell><StatusChip value={row.available <= 0 ? 'Unavailable' : row.reorderPoint != null && row.available <= row.reorderPoint ? 'Low availability' : 'Available'} /></TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
