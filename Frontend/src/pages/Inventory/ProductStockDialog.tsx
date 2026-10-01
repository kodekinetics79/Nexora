import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  Table, TableBody, TableCell, TableHead, TableRow, Typography,
} from '@mui/material';
import commercialIntelligenceService, { type AvailabilityDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { QueryState, ResponsiveTable } from '../SalesManagement/CommercialPagePrimitives';
import StockActionsDialog from './Commercial/StockActionsDialog';

interface ProductStockDialogProps {
  productId: number | undefined;
  productName?: string;
  open: boolean;
  onClose: () => void;
}

const ROW_CAP = 500;
const level = (value: number | null | undefined) => value == null ? 'Not set' : value.toLocaleString();

/** A product-scoped view of the existing stock ledger, opened from the Products sheet. */
export default function ProductStockDialog({ productId, productName, open, onClose }: ProductStockDialogProps) {
  const canEdit = useAuth().hasPermission('Products', 'edit');
  const [acting, setActing] = useState<AvailabilityDTO | null>(null);
  const query = useQuery({
    queryKey: ['inventory-intelligence', 'availability', 'product', productId],
    queryFn: () => commercialIntelligenceService.getAvailability({ productId }),
    enabled: open && productId != null,
  });
  const rows = query.data ?? [];
  const close = () => { setActing(null); onClose(); };

  return <>
    <Dialog open={open} onClose={close} fullWidth maxWidth="md" aria-labelledby="product-stock-title">
      <DialogTitle id="product-stock-title">
        Stock by warehouse
        {productName && <Typography component="p" variant="body2" color="text.secondary">{productName}</Typography>}
      </DialogTitle>
      <DialogContent>
        <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length}
          hasData={query.data !== undefined} updatedAt={query.dataUpdatedAt}
          onRetry={() => void query.refetch()}
          emptyText={canEdit
            ? 'No stock recorded for this product. Use Record stock on the Products list to enter an opening balance.'
            : 'No stock recorded for this product.'}>
          {rows.length >= ROW_CAP && <Alert severity="info" sx={{ mb: 2 }}>Showing the first {ROW_CAP} warehouse records.</Alert>}
          <ResponsiveTable label="Product stock by warehouse">
            <Table size="small" sx={{ minWidth: 600 }}>
              <TableHead><TableRow>
                <TableCell>Warehouse</TableCell>
                <TableCell align="right">In stock</TableCell>
                <TableCell align="right">Available</TableCell>
                <TableCell align="right">Incoming</TableCell>
                {canEdit && <TableCell>Action</TableCell>}
              </TableRow></TableHead>
              <TableBody>{rows.map(row => <TableRow key={row.inventoryId}>
                <TableCell>
                  {row.warehouseName}
                  <Typography component="p" variant="caption" color="text.secondary">
                    Min {level(row.minimumLevel)} · Max {level(row.maximumLevel)} · Safety {level(row.safetyStock)}
                  </Typography>
                </TableCell>
                <TableCell align="right">{row.onHand.toLocaleString()}</TableCell>
                <TableCell align="right">{row.available.toLocaleString()}</TableCell>
                <TableCell align="right">{row.incoming.toLocaleString()}</TableCell>
                {canEdit && <TableCell><Button size="small" onClick={() => setActing(row)} aria-label={`Stock actions for ${row.warehouseName}`}>Stock actions</Button></TableCell>}
              </TableRow>)}</TableBody>
            </Table>
          </ResponsiveTable>
        </QueryState>
      </DialogContent>
      <DialogActions><Button onClick={close}>Close</Button></DialogActions>
    </Dialog>
    {open && canEdit && acting && acting.productId === productId && <StockActionsDialog key={acting.inventoryId} row={acting} open onClose={() => setActing(null)} />}
  </>;
}
