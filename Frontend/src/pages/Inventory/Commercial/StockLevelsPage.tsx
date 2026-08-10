import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, Button, FormControlLabel, Paper, Switch, Table, TableBody, TableCell, TableHead,
  TableRow, TextField, Typography,
} from '@mui/material';
import commercialIntelligenceService, {
  type AvailabilityDTO, type StockLevelRowDTO,
} from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable } from '../../SalesManagement/CommercialPagePrimitives';
import StockActionsDialog from './StockActionsDialog';

const BREACH_LABEL: Record<string, string> = {
  OUT_OF_STOCK: 'Out of stock',
  BELOW_MINIMUM: 'Below minimum',
  REORDER_POINT: 'At reorder point',
  OVERSTOCK: 'Above maximum',
};

/**
 * Null is "not configured" and is rendered as a word, not as an empty cell. An empty cell in a
 * numeric column reads as zero or as a loading state, and both readings are wrong: a minimum that
 * has never been set is why an item that has run out raises no alert.
 */
function Level({ value }: { value?: number | null }) {
  if (value == null) {
    return <Typography component="span" variant="body2" color="text.secondary">Not set</Typography>;
  }
  return <>{value}</>;
}

/**
 * FR-INV-04. Minimum and maximum stock levels: what is configured, what it means for each row
 * today, and the place a person sets them.
 *
 * <p>The headline number on this screen is the one nobody expects: how many stock rows have <b>no
 * level configured at all</b>. Those rows can never raise an alert, and an alerts screen showing
 * nothing is indistinguishable from a healthy warehouse unless this number is on the wall next to
 * it.</p>
 */
export default function StockLevelsPage() {
  const canEdit = useAuth().hasPermission('Products', 'edit');
  const [breachedOnly, setBreachedOnly] = useState(false);
  const [search, setSearch] = useState('');
  const [editing, setEditing] = useState<AvailabilityDTO | null>(null);

  const query = useQuery({
    queryKey: ['inventory-intelligence', 'stock-levels', breachedOnly],
    queryFn: () => commercialIntelligenceService.getStockLevels(breachedOnly),
  });

  const rows = useMemo(() => {
    const all = query.data?.rows ?? [];
    const needle = search.trim().toLowerCase();
    if (!needle) return all;
    return all.filter(row =>
      row.partNumber.toLowerCase().includes(needle) || row.productName.toLowerCase().includes(needle));
  }, [query.data, search]);

  // The dialog is written against the availability shape, which is the same row seen from the
  // other side. Mapped rather than duplicated so there is one editor, not two that can diverge.
  const asAvailability = (row: StockLevelRowDTO): AvailabilityDTO => ({
    inventoryId: row.inventoryId,
    productId: row.productId,
    partNumber: row.partNumber,
    productName: row.productName,
    warehouseId: row.warehouseId,
    warehouseName: row.warehouseName,
    onHand: row.onHand,
    reserved: 0,
    available: row.available,
    incoming: row.incoming,
    reorderPoint: row.reorderPoint,
    minimumLevel: row.minimumLevel,
    maximumLevel: row.maximumLevel,
    safetyStock: 0,
    leadTimeDays: null,
  });

  const summary = query.data;

  return (
    <PageShell
      title="Stock levels"
      subtitle="Minimum, maximum and reorder levels by item and warehouse — and which rows nobody is watching."
      actions={
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
          <TextField size="small" label="Search part or product" value={search}
            onChange={e => setSearch(e.target.value)} />
          <FormControlLabel
            control={<Switch checked={breachedOnly} onChange={e => setBreachedOnly(e.target.checked)} />}
            label="Breaching only"
          />
        </Box>
      }
    >
      {summary && summary.unmonitoredCount > 0 && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          {summary.unmonitoredCount} of {summary.rowCount} stock rows have no minimum, maximum or
          reorder point set. Those rows cannot raise a reorder alert, however low they run — an empty
          alerts screen does not mean they are healthy.
        </Alert>
      )}
      {summary && (
        <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
          <Typography variant="body2" color="text.secondary">
            {summary.configuredCount} row(s) configured · {summary.breachedCount} currently breaching
            · levels are held per item per warehouse, because that is the grain the alert is
            evaluated at.
          </Typography>
        </Paper>
      )}

      <QueryState
        loading={query.isLoading}
        error={query.isError}
        empty={!rows.length}
        onRetry={() => void query.refetch()}
        emptyText={breachedOnly
          ? 'No stock row is currently breaching a configured level.'
          : 'No stock rows exist for this business unit.'}
      >
        <ResponsiveTable label="Stock levels by item and warehouse">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Part</TableCell>
                <TableCell>Warehouse</TableCell>
                <TableCell align="right">On hand</TableCell>
                <TableCell align="right">Available</TableCell>
                <TableCell align="right">On order</TableCell>
                <TableCell align="right">Projected</TableCell>
                <TableCell align="right">Minimum</TableCell>
                <TableCell align="right">Reorder point</TableCell>
                <TableCell align="right">Maximum</TableCell>
                <TableCell>Position</TableCell>
                {canEdit && <TableCell>Action</TableCell>}
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map(row => (
                <TableRow hover key={row.inventoryId}>
                  <TableCell>{row.partNumber} — {row.productName}</TableCell>
                  <TableCell>{row.warehouseName}</TableCell>
                  <TableCell align="right">{row.onHand}</TableCell>
                  <TableCell align="right">{row.available}</TableCell>
                  <TableCell align="right">{row.incoming}</TableCell>
                  <TableCell align="right">{row.projected}</TableCell>
                  <TableCell align="right"><Level value={row.minimumLevel} /></TableCell>
                  <TableCell align="right">
                    {row.reorderPoint > 0
                      ? row.reorderPoint
                      : <Typography component="span" variant="body2" color="text.secondary">Not set</Typography>}
                  </TableCell>
                  <TableCell align="right"><Level value={row.maximumLevel} /></TableCell>
                  <TableCell>
                    {row.breach
                      ? <Typography component="span" variant="body2" color="error.main" sx={{ fontWeight: 700 }}>
                          {BREACH_LABEL[row.breach] ?? row.breach}
                        </Typography>
                      : row.monitored
                        ? <Typography component="span" variant="body2" color="success.main">Within levels</Typography>
                        // The distinction the whole screen exists for.
                        : <Typography component="span" variant="body2" color="text.secondary">Not monitored</Typography>}
                  </TableCell>
                  {canEdit && (
                    <TableCell>
                      <Button size="small" onClick={() => setEditing(asAvailability(row))}>Stock actions</Button>
                    </TableCell>
                  )}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>

      {editing && (
        <StockActionsDialog row={editing} open onClose={() => setEditing(null)} />
      )}
    </PageShell>
  );
}
