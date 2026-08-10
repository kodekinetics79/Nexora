import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import commercialIntelligenceService, { type AvailabilityDTO } from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, StatusChip } from '../../SalesManagement/CommercialPagePrimitives';
import StockActionsDialog from './StockActionsDialog';

/** Matches the server-side cap in InventoryIntelligenceController.MaxRows. */
const ROW_CAP = 500;

/** Null is "not configured", and a numeric column showing an empty cell reads as zero. */
function Level({ value }: { value?: number | null }) {
  if (value == null) return <Typography component="span" variant="body2" color="text.secondary">Not set</Typography>;
  return <>{value}</>;
}

export default function AvailabilityPage() {
  // FR-INV-04 and the stock-mutation surface. Until this gate not one of count, adjust,
  // reclassify, transfer or safety-stock had a caller anywhere in the application, so opening
  // stock could not be entered and a miscount could not be corrected without a REST client.
  const canEdit = useAuth().hasPermission('Products', 'edit');
  const [search, setSearch] = useState('');
  const [acting, setActing] = useState<AvailabilityDTO | null>(null);
  const query = useQuery({
    queryKey: ['inventory-intelligence', 'availability', search],
    queryFn: () => commercialIntelligenceService.getAvailability({ search: search || undefined }),
  });
  const rows = query.data ?? [];
  // The server caps this list. Say so, rather than letting a rep read an unfiltered page as the
  // whole stock position — a silently truncated availability table is worse than no table.
  const truncated = rows.length >= ROW_CAP;

  return (
    <PageShell
      title="Availability"
      subtitle="On-hand, reserved, available and incoming quantities by warehouse, against the levels set for each row."
      actions={<TextField size="small" label="Search part or product" value={search} onChange={event => setSearch(event.target.value)} />}
    >
      <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No availability records match this search.">
        {truncated && <Alert severity="info" sx={{ mb: 2 }}>Showing the first {ROW_CAP} rows. Search by part or product to narrow this list.</Alert>}
        <ResponsiveTable label="Product availability">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Part</TableCell>
                <TableCell>Product</TableCell>
                <TableCell>Warehouse</TableCell>
                <TableCell align="right">On hand</TableCell>
                <TableCell align="right">Reserved</TableCell>
                <TableCell align="right">Available</TableCell>
                <TableCell align="right">Incoming</TableCell>
                <TableCell align="right">Minimum</TableCell>
                <TableCell align="right">Maximum</TableCell>
                <TableCell>Status</TableCell>
                {canEdit && <TableCell>Action</TableCell>}
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map(row => (
                <TableRow hover key={`${row.productId}-${row.warehouseId}`}>
                  <TableCell>{row.partNumber}</TableCell>
                  <TableCell>{row.productName}</TableCell>
                  <TableCell>{row.warehouseName}</TableCell>
                  <TableCell align="right">{row.onHand}</TableCell>
                  <TableCell align="right">{row.reserved}</TableCell>
                  <TableCell align="right">{row.available}</TableCell>
                  <TableCell align="right">{row.incoming}</TableCell>
                  <TableCell align="right"><Level value={row.minimumLevel} /></TableCell>
                  <TableCell align="right"><Level value={row.maximumLevel} /></TableCell>
                  <TableCell>
                    <StatusChip value={
                      row.available <= 0
                        ? 'Unavailable'
                        : row.minimumLevel != null && row.available + row.incoming < row.minimumLevel
                          ? 'Below minimum'
                          : row.maximumLevel != null && row.onHand > row.maximumLevel
                            ? 'Above maximum'
                            : row.reorderPoint != null && row.reorderPoint > 0 && row.available <= row.reorderPoint
                              ? 'Low availability'
                              : 'Available'
                    } />
                  </TableCell>
                  {canEdit && (
                    <TableCell>
                      <Button size="small" onClick={() => setActing(row)}>Stock actions</Button>
                    </TableCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
      {acting && <StockActionsDialog row={acting} open onClose={() => setActing(null)} />}
    </PageShell>
  );
}
