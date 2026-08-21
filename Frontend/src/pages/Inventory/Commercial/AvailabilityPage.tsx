import { useState } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Box, Button, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import commercialIntelligenceService, { type AvailabilityDTO } from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, StatusChip } from '../../SalesManagement/CommercialPagePrimitives';
import StockActionsDialog from './StockActionsDialog';
import OpeningStockDialog from './OpeningStockDialog';

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
  // The bootstrap door. Every per-row action needs a row, and a row only exists once the product
  // already has stock, so a never-stocked product has no way in without this.
  const [opening, setOpening] = useState(false);
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: ['inventory-intelligence', 'availability', search],
    queryFn: () => commercialIntelligenceService.getAvailability({ search: search || undefined }),
  });
  const rows = query.data ?? [];
  // The server caps this list. Say so, rather than letting a rep read an unfiltered page as the
  // whole stock position — a silently truncated availability table is worse than no table.
  const truncated = rows.length >= ROW_CAP;
  const searching = search.trim() !== '';

  /**
   * Which emptiness this is — with the nothing-is-stocked case asked FIRST, ahead of the search
   * filter, for the same reason the stock-levels ladder asks it first. "No availability records
   * match this search" is a true sentence and a useless one on a tenant that has never stocked
   * anything: the next step is not a different search term, it is the opening-stock door.
   *
   * <p>This endpoint returns a bare array with no unfiltered total to read, so the unfiltered
   * result is taken from the query cache rather than from a second request — this component always
   * mounts with an empty search, so that query has already run by the time a search can be typed.
   * Undefined (never resolved, or evicted) falls back to the search wording rather than guessing.</p>
   */
  const unfiltered = queryClient.getQueryData<AvailabilityDTO[]>(['inventory-intelligence', 'availability', '']);
  const nothingStocked = !searching || unfiltered?.length === 0;
  const emptyText = nothingStocked
    ? `No stock has been recorded yet. No product in this business unit has an opening balance in any warehouse.${canEdit
      ? ' Use "Record opening stock" above to enter what is on the shelf.'
      : ' Someone who can edit products needs to record the opening stock before this screen has anything to show.'}`
    : 'No availability records match this search.';

  return (
    <PageShell
      title="Availability"
      subtitle="On-hand, reserved, available and incoming quantities by warehouse, against the levels set for each row."
      actions={
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', flexWrap: 'wrap' }}>
          <TextField size="small" label="Search part or product" value={search} onChange={event => setSearch(event.target.value)} />
          {canEdit && (
            <Button variant="outlined" onClick={() => setOpening(true)}>Record opening stock</Button>
          )}
        </Box>
      }
    >
      <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText={emptyText}>
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
      {opening && <OpeningStockDialog open onClose={() => setOpening(false)} />}
    </PageShell>
  );
}
