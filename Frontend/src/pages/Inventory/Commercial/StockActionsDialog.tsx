import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Stack, Tab, Tabs,
  TextField, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import commercialIntelligenceService, {
  type AvailabilityDTO, type StockLedgerResultDTO,
} from '../../../api/services/commercialIntelligenceService';

/**
 * The server's own message, verbatim. Every refusal from the stock ledger names the arithmetic that
 * caused it ("the change would leave 12 committed or non-sellable units against only 10 on hand"),
 * and that sentence is the only thing that tells an operator what to do next. Replacing it with
 * "Could not save" throws the answer away.
 */
export function serverMessage(error: unknown, fallback: string): string {
  const data = (error as { response?: { data?: { error?: string; detail?: string; title?: string } } })?.response?.data;
  return data?.error || data?.detail || data?.title || fallback;
}

/** A fresh idempotency key per attempt. Every stock write requires one. */
const newKey = () => (globalThis.crypto?.randomUUID?.() ?? `key-${Date.now()}-${Math.random()}`);

type Mode = 'count' | 'adjust' | 'reclassify' | 'transfer' | 'levels';

const MODES: Array<{ value: Mode; label: string }> = [
  { value: 'count', label: 'Count' },
  { value: 'adjust', label: 'Adjust' },
  { value: 'reclassify', label: 'Reclassify' },
  { value: 'transfer', label: 'Transfer' },
  { value: 'levels', label: 'Levels' },
];

/**
 * Every stock mutation the inventory controller exposes, reachable from the row it applies to.
 *
 * <p><b>Why this exists.</b> Until now not one of <code>stock/count</code>,
 * <code>stock/adjust</code>, <code>stock/reclassify</code>, <code>stock/transfer</code>,
 * <code>stock/safety-stock</code> or <code>stock/levels</code> had a caller anywhere in the
 * application. The entire write half of the module was reachable only from a REST client — which
 * means a client's opening stock could not be entered, a miscount could not be corrected, damaged
 * goods could not be written down and stock could not be moved between the warehouses the product
 * lets you create. A backend capability with no user is the same defect as a column with no
 * writer.</p>
 *
 * <p>Each tab states the unit of the number it asks for, and each renders the server's refusal
 * verbatim rather than inventing copy that might contradict it.</p>
 */
export default function StockActionsDialog({ row, open, onClose }: {
  row: AvailabilityDTO;
  open: boolean;
  onClose: () => void;
}) {
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [mode, setMode] = useState<Mode>('count');
  const [failure, setFailure] = useState<string | null>(null);
  const [result, setResult] = useState<StockLedgerResultDTO | null>(null);

  const [counted, setCounted] = useState('');
  const [delta, setDelta] = useState('');
  const [bucket, setBucket] = useState<'Quarantine' | 'Damaged' | 'Expired'>('Quarantine');
  const [bucketQuantity, setBucketQuantity] = useState('');
  const [destination, setDestination] = useState('');
  const [transferQuantity, setTransferQuantity] = useState('');
  const [minimum, setMinimum] = useState(row.minimumLevel == null ? '' : String(row.minimumLevel));
  const [maximum, setMaximum] = useState(row.maximumLevel == null ? '' : String(row.maximumLevel));
  const [safety, setSafety] = useState(String(row.safetyStock ?? 0));
  const [reason, setReason] = useState('');

  const warehouses = useQuery({
    queryKey: ['inventory-intelligence', 'warehouses'],
    queryFn: () => commercialIntelligenceService.getWarehouses(),
    enabled: open && mode === 'transfer',
  });

  const settle = () => {
    void client.invalidateQueries({ queryKey: ['inventory-intelligence'] });
  };

  const mutation = useMutation({
    mutationFn: async (): Promise<StockLedgerResultDTO> => {
      const key = newKey();
      const { productId, warehouseId } = row;
      switch (mode) {
        case 'count':
          return commercialIntelligenceService.recordStockCount(
            productId, warehouseId, Number(counted), reason || undefined, key);
        case 'adjust':
          return commercialIntelligenceService.adjustStock(
            productId, warehouseId, Number(delta), reason || undefined, key);
        case 'reclassify':
          return commercialIntelligenceService.reclassifyStock(
            productId, warehouseId, bucket, Number(bucketQuantity), reason || undefined, key);
        case 'transfer':
          return (await commercialIntelligenceService.transferStock(
            productId, warehouseId, Number(destination), Number(transferQuantity),
            reason || undefined, key)).from;
        default: {
          // Safety stock and the two levels are separate routes because they are separate
          // decisions: safety stock is withheld from availability, a minimum is a buying trigger.
          if (Number(safety) !== (row.safetyStock ?? 0)) {
            await commercialIntelligenceService.setSafetyStock(productId, warehouseId, Number(safety), key);
          }
          // Blank means "not configured" and is sent as null, which CLEARS the level. It is not
          // the same as zero, and the server stores the difference.
          return commercialIntelligenceService.setStockLevels(
            productId, warehouseId,
            minimum.trim() === '' ? null : Number(minimum),
            maximum.trim() === '' ? null : Number(maximum),
            newKey());
        }
      }
    },
    onSuccess: (value) => {
      setFailure(null);
      setResult(value);
      settle();
      enqueueSnackbar(
        mode === 'count' && value.variance != null && value.variance !== 0
          ? `Count recorded. Variance ${value.variance > 0 ? '+' : ''}${value.variance} unit(s) against a book value of ${value.bookQuantity}.`
          : 'Stock updated',
        { variant: 'success' });
    },
    onError: (error) => {
      setResult(null);
      setFailure(serverMessage(error, 'The stock write was refused and no reason was returned.'));
    },
  });

  const valid = mode === 'count' ? counted.trim() !== '' && Number(counted) >= 0
    : mode === 'adjust' ? delta.trim() !== '' && Number(delta) !== 0
      : mode === 'reclassify' ? bucketQuantity.trim() !== '' && Number(bucketQuantity) !== 0
        : mode === 'transfer' ? destination !== '' && Number(transferQuantity) > 0
          : true;

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle sx={{ pb: 0 }}>
        {row.partNumber} — {row.productName}
        <Typography variant="body2" color="text.secondary">
          {row.warehouseName} · {row.onHand} on hand · {row.available} available to promise
        </Typography>
      </DialogTitle>
      <Tabs
        value={mode}
        onChange={(_, next: Mode) => { setMode(next); setFailure(null); setResult(null); }}
        variant="scrollable"
        sx={{ px: 3, borderBottom: 1, borderColor: 'divider' }}
      >
        {MODES.map(item => <Tab key={item.value} value={item.value} label={item.label} />)}
      </Tabs>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {failure && <Alert severity="error">{failure}</Alert>}
          {result && mode === 'count' && result.variance != null && (
            <Alert severity={result.variance === 0 ? 'success' : 'warning'}>
              {result.variance === 0
                ? `Counted ${result.countedQuantity} — the count agrees with the book value.`
                : `Book ${result.bookQuantity}, counted ${result.countedQuantity}: a variance of ${result.variance > 0 ? '+' : ''}${result.variance} unit(s). It appears on the count variance report.`}
            </Alert>
          )}

          {mode === 'count' && <>
            <Typography variant="body2" color="text.secondary">
              Sets the physical quantity to what was counted and posts the balancing movement. The
              difference against the book value is kept and reported on the variance screen.
            </Typography>
            <TextField label="Counted quantity (units)" type="number" size="small" value={counted}
              onChange={e => setCounted(e.target.value)} />
          </>}

          {mode === 'adjust' && <>
            <Typography variant="body2" color="text.secondary">
              A signed change to on-hand stock. Positive adds units, negative removes them. Use a
              count instead when you know the true figure rather than the difference.
            </Typography>
            <TextField label="Change (units, may be negative)" type="number" size="small" value={delta}
              onChange={e => setDelta(e.target.value)} />
          </>}

          {mode === 'reclassify' && <>
            <Typography variant="body2" color="text.secondary">
              Moves units into or out of a non-sellable bucket. On-hand does not change — the units
              are still in the building — but available-to-promise does. A negative quantity releases.
            </Typography>
            <TextField select label="Bucket" size="small" value={bucket}
              onChange={e => setBucket(e.target.value as typeof bucket)}>
              <MenuItem value="Quarantine">Quarantine</MenuItem>
              <MenuItem value="Damaged">Damaged</MenuItem>
              <MenuItem value="Expired">Expired</MenuItem>
            </TextField>
            <TextField label="Quantity (units, negative releases)" type="number" size="small"
              value={bucketQuantity} onChange={e => setBucketQuantity(e.target.value)} />
          </>}

          {mode === 'transfer' && <>
            <Typography variant="body2" color="text.secondary">
              Moves physical stock to another warehouse in this business unit, as one atomic pair of
              movements.
            </Typography>
            <TextField select label="Destination warehouse" size="small" value={destination}
              onChange={e => setDestination(e.target.value)}
              helperText={warehouses.isError ? 'Warehouses could not be loaded.' : undefined}>
              {(warehouses.data ?? [])
                .filter(w => w.warehouseId !== row.warehouseId)
                .map(w => <MenuItem key={w.warehouseId} value={String(w.warehouseId)}>{w.name}</MenuItem>)}
            </TextField>
            <TextField label="Quantity (units)" type="number" size="small" value={transferQuantity}
              onChange={e => setTransferQuantity(e.target.value)} />
          </>}

          {mode === 'levels' && <>
            <Typography variant="body2" color="text.secondary">
              Minimum and maximum are set per item per warehouse, which is the grain the reorder
              alert is evaluated at. Leave a box empty to leave that side unmonitored — empty means
              not configured, which is not the same as zero.
            </Typography>
            <TextField label="Minimum level (units, empty = not set)" type="number" size="small"
              value={minimum} onChange={e => setMinimum(e.target.value)} />
            <TextField label="Maximum level (units, empty = not set)" type="number" size="small"
              value={maximum} onChange={e => setMaximum(e.target.value)} />
            <TextField label="Safety stock (units withheld from availability)" type="number" size="small"
              value={safety} onChange={e => setSafety(e.target.value)} />
            <Typography variant="caption" color="text.secondary">
              Reorder point for this row is {row.reorderPoint ?? 0} units and is set on the product
              record, which pushes it to every warehouse holding the item.
            </Typography>
          </>}

          {mode !== 'levels' && <TextField label="Reason (recorded on the movement)" size="small"
            value={reason} onChange={e => setReason(e.target.value)} />}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Close</Button>
        <Button variant="contained" disabled={!valid || mutation.isPending}
          onClick={() => mutation.mutate()}>
          {mutation.isPending ? 'Saving…' : 'Save'}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
