import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Stack, TextField,
  Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import commercialIntelligenceService, {
  type StockLedgerResultDTO,
} from '../../../api/services/commercialIntelligenceService';
import productService, { type ProductDTO } from '../../../api/services/productService';
import { serverMessage } from './StockActionsDialog';

/** A fresh idempotency key per attempt, exactly as the per-row dialog does. */
const newKey = () => (globalThis.crypto?.randomUUID?.() ?? `key-${Date.now()}-${Math.random()}`);

/** The product picker is a search box, not a full catalogue dump. This is the page it reads. */
const PAGE_SIZE = 20;

const productLabel = (product: ProductDTO) =>
  [product.partNo, product.productName].filter(Boolean).join(' — ') || `Product ${product.id}`;

/**
 * The bootstrap door: opening stock for a product that has never been stocked.
 *
 * <p><b>Why this exists.</b> Every other stock-write door in the application hangs off a row in an
 * inventory grid, and every one of those grids is an INNER JOIN from <code>Inventory</code> — a
 * table row that only comes into being once the product already has stock. A product created
 * through the product form is committed with no <code>Inventory</code> row at all, so it appears
 * on no inventory grid, so there is no row to click, so its opening stock could never be recorded.
 * A customer who already holds stock could not initialise the module at all. The write route was
 * never the gap: <code>POST stock/count</code> creates the inventory row on first use and its own
 * doc comment says it is "the route for opening stock and cycle counts". The gap was that nothing
 * in the UI could call it without a pre-existing row to hang off.</p>
 *
 * <p><b>Why a separate dialog rather than a nullable row on StockActionsDialog.</b> That component
 * dereferences <code>row</code> in three <code>useState</code> initialisers that run at first
 * render, plus its title and its transfer filter. Making the prop nullable without rewriting every
 * one of those is a crash on open, and it is the dialog the demo drives from a row. This composes
 * the same service call, the same idempotency key and the same cache invalidation, and leaves that
 * component untouched.</p>
 *
 * <p>Only a count is offered here, deliberately. Adjust, reclassify and transfer all reason about
 * a balance that does not exist yet; a count states what is physically on the shelf, which is the
 * only thing a person can know about a product on day one.</p>
 */
export default function OpeningStockDialog({ open, onClose }: {
  open: boolean;
  onClose: () => void;
}) {
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [search, setSearch] = useState('');
  const [product, setProduct] = useState<ProductDTO | null>(null);
  const [warehouseId, setWarehouseId] = useState('');
  const [counted, setCounted] = useState('');
  const [reason, setReason] = useState('');
  const [failure, setFailure] = useState<string | null>(null);
  const [result, setResult] = useState<StockLedgerResultDTO | null>(null);

  const products = useQuery({
    queryKey: ['inventory-intelligence', 'opening-stock', 'products', search],
    // Active products only: an archived part is not something anyone is opening stock for.
    queryFn: () => productService.getAll({
      search: search.trim() || undefined, pageNumber: 1, pageSize: PAGE_SIZE, isActive: true,
    }),
    enabled: open,
  });

  const warehouses = useQuery({
    queryKey: ['inventory-intelligence', 'warehouses'],
    queryFn: () => commercialIntelligenceService.getWarehouses(),
    enabled: open,
  });

  const found = products.data?.items ?? [];
  // A narrowed search must not silently drop the product already chosen — the selected value has
  // to stay in the option list or the picker would appear to clear itself.
  const options = product && !found.some(item => item.id === product.id) ? [product, ...found] : found;
  const warehouseRows = warehouses.data ?? [];

  const mutation = useMutation({
    mutationFn: async (): Promise<StockLedgerResultDTO> =>
      commercialIntelligenceService.recordStockCount(
        product!.id, Number(warehouseId), Number(counted), reason.trim() || undefined, newKey()),
    onSuccess: (value) => {
      setFailure(null);
      setResult(value);
      // The new Inventory row now exists, so every grid that INNER JOINs it must be refetched.
      void client.invalidateQueries({ queryKey: ['inventory-intelligence'] });
      enqueueSnackbar(
        `Opening stock recorded: ${value.countedQuantity ?? counted} unit(s) of ${productLabel(product!)}.`,
        { variant: 'success' });
      // Day one is many products into one warehouse, so the warehouse is kept and the rest cleared.
      setProduct(null);
      setCounted('');
      setReason('');
    },
    onError: (error) => {
      setResult(null);
      setFailure(serverMessage(error, 'The stock write was refused and no reason was returned.'));
    },
  });

  const valid = product != null && warehouseId !== ''
    && counted.trim() !== '' && Number.isFinite(Number(counted)) && Number(counted) >= 0;

  const close = () => {
    setFailure(null);
    setResult(null);
    onClose();
  };

  return (
    <Dialog open={open} onClose={close} fullWidth maxWidth="sm">
      <DialogTitle sx={{ pb: 0 }}>
        Record opening stock
        <Typography variant="body2" color="text.secondary">
          Enters what is physically on the shelf for a product that has never been stocked. This
          creates the inventory row and posts the balancing movement, so the item appears on every
          stock screen from here on.
        </Typography>
      </DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 2 }}>
          {failure && <Alert severity="error">{failure}</Alert>}
          {result && (
            <Alert severity="success">
              Recorded {result.countedQuantity ?? 0} unit(s). On hand is now {result.onHand}. Pick
              another product to continue, or close.
            </Alert>
          )}
          {products.isError && (
            <Alert severity="error">The product list could not be loaded, so nothing is offered here rather than an empty list that would read as "no products".</Alert>
          )}
          {warehouses.isError && (
            <Alert severity="error">Warehouses could not be loaded. Opening stock is held per warehouse and cannot be recorded without one.</Alert>
          )}
          {!warehouses.isLoading && !warehouses.isError && warehouseRows.length === 0 && (
            <Alert severity="warning">
              No warehouse exists yet. Stock is held per warehouse, so create one on the Warehouses
              screen before recording opening stock.
            </Alert>
          )}

          <TextField
            size="small"
            label="Search products"
            value={search}
            onChange={event => setSearch(event.target.value)}
            helperText={`Showing up to ${PAGE_SIZE} matches. Narrow the search if the product is not listed.`}
          />

          <TextField
            select
            size="small"
            label="Product"
            value={product ? String(product.id) : ''}
            onChange={event => {
              const picked = options.find(item => String(item.id) === event.target.value);
              setProduct(picked ?? null);
              setFailure(null);
              setResult(null);
            }}
          >
            {options.map(item => (
              <MenuItem key={item.id} value={String(item.id)}>{productLabel(item)}</MenuItem>
            ))}
          </TextField>

          <TextField
            select
            size="small"
            label="Warehouse"
            value={warehouseId}
            onChange={event => setWarehouseId(event.target.value)}
          >
            {warehouseRows.map(warehouse => (
              <MenuItem key={warehouse.warehouseId} value={String(warehouse.warehouseId)}>
                {warehouse.name}
              </MenuItem>
            ))}
          </TextField>

          <TextField
            label="Quantity on hand (units)"
            type="number"
            size="small"
            value={counted}
            onChange={event => setCounted(event.target.value)}
          />

          <TextField
            label="Reason (recorded on the movement)"
            size="small"
            value={reason}
            onChange={event => setReason(event.target.value)}
            helperText="Optional, but a movement that says 'opening balance, stocktake 21 Aug' is worth more later than a bare number."
          />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={close}>Close</Button>
        <Button variant="contained" disabled={!valid || mutation.isPending}
          onClick={() => mutation.mutate()}>
          {mutation.isPending ? 'Saving…' : 'Save'}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
