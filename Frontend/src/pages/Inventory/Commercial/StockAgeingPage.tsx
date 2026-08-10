import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Box, MenuItem, Paper, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField,
  Tooltip, Typography,
} from '@mui/material';
import commercialIntelligenceService, {
  type StockAgeingRowDTO,
} from '../../../api/services/commercialIntelligenceService';
import { PageShell, QueryState, ResponsiveTable } from '../../SalesManagement/CommercialPagePrimitives';

const BAND_LABEL: Record<StockAgeingRowDTO['band'], string> = {
  CURRENT: 'Current (moved within 90 days)',
  SLOW_MOVING: 'Slow moving (90–180 days)',
  VERY_SLOW: 'Very slow (180–365 days)',
  OBSOLETE: 'Obsolete (over a year)',
  UNDATED: 'Undated (no movement on record)',
};

const BAND_COLOUR: Record<StockAgeingRowDTO['band'], string> = {
  CURRENT: 'success.main',
  SLOW_MOVING: 'warning.main',
  VERY_SLOW: 'warning.main',
  OBSOLETE: 'error.main',
  UNDATED: 'text.secondary',
};

const formatDay = (value?: string | null) =>
  value ? new Date(value).toLocaleDateString(undefined, { dateStyle: 'medium' }) : 'Never';

/**
 * FR-INV-06. Slow-moving and obsolete stock.
 *
 * <p><b>Aged from the last issue, not the last receipt.</b> A line that is received every month and
 * never sold is the definition of obsolete, and receipt-based ageing would report it as the freshest
 * stock in the warehouse. The screen shows both dates side by side precisely so that case is
 * visible: a recent receipt next to an old issue is the row worth an argument.</p>
 */
export default function StockAgeingPage() {
  const [band, setBand] = useState('');
  const [warehouseId, setWarehouseId] = useState('');

  const warehouses = useQuery({
    queryKey: ['inventory-intelligence', 'warehouses'],
    queryFn: () => commercialIntelligenceService.getWarehouses(),
  });

  const query = useQuery({
    queryKey: ['inventory-intelligence', 'stock-ageing', band, warehouseId],
    queryFn: () => commercialIntelligenceService.getStockAgeing({
      band: band || undefined,
      warehouseId: warehouseId ? Number(warehouseId) : undefined,
    }),
  });

  const data = query.data;
  const rows = data?.rows ?? [];

  return (
    <PageShell
      title="Stock ageing"
      subtitle="How long each stock row has sat without moving, and the capital tied up in it."
      actions={
        <Stack direction="row" spacing={1}>
          <TextField select size="small" label="Warehouse" value={warehouseId} sx={{ minWidth: 180 }}
            onChange={e => setWarehouseId(e.target.value)}>
            <MenuItem value="">All warehouses</MenuItem>
            {(warehouses.data ?? []).map(w =>
              <MenuItem key={w.warehouseId} value={String(w.warehouseId)}>{w.name}</MenuItem>)}
          </TextField>
          <TextField select size="small" label="Band" value={band} sx={{ minWidth: 220 }}
            onChange={e => setBand(e.target.value)}>
            <MenuItem value="">All bands</MenuItem>
            {(Object.keys(BAND_LABEL) as StockAgeingRowDTO['band'][]).map(key =>
              <MenuItem key={key} value={key}>{BAND_LABEL[key]}</MenuItem>)}
          </TextField>
        </Stack>
      }
    >
      {data && (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(auto-fit, minmax(180px, 1fr))' }, gap: 1.5, mb: 2.5 }}>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>Total carrying value</Typography>
            <Typography variant="h6" sx={{ fontWeight: 900 }}>
              {data.carryingValue.toLocaleString(undefined, { maximumFractionDigits: 2 })}
            </Typography>
          </Paper>
          {data.bands.map(group => (
            <Paper key={group.band} variant="outlined" sx={{ p: 2 }}>
              <Typography variant="caption" sx={{ fontWeight: 700, color: BAND_COLOUR[group.band as StockAgeingRowDTO['band']] ?? 'text.secondary' }}>
                {BAND_LABEL[group.band as StockAgeingRowDTO['band']] ?? group.band}
              </Typography>
              <Typography variant="h6" sx={{ fontWeight: 900 }}>
                {group.carryingValue.toLocaleString(undefined, { maximumFractionDigits: 2 })}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                {group.rowCount} row(s) · {group.units} units
              </Typography>
            </Paper>
          ))}
        </Box>
      )}

      <QueryState
        loading={query.isLoading}
        error={query.isError}
        empty={!rows.length}
        onRetry={() => void query.refetch()}
        emptyText="No stock rows hold a positive quantity in this selection. Ageing describes stock that is sitting there; a row at zero is empty, not slow-moving."
      >
        <ResponsiveTable label="Stock ageing">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Part</TableCell>
                <TableCell>Warehouse</TableCell>
                <TableCell align="right">On hand (units)</TableCell>
                <TableCell align="right">Unit cost</TableCell>
                <TableCell align="right">Carrying value</TableCell>
                <TableCell>Last issued</TableCell>
                <TableCell>Last received</TableCell>
                <TableCell align="right">Days since issue</TableCell>
                <TableCell>Band</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map(row => (
                <TableRow hover key={row.inventoryId}>
                  <TableCell>{row.partNumber} — {row.productName}</TableCell>
                  <TableCell>{row.warehouseName}</TableCell>
                  <TableCell align="right">{row.onHand}</TableCell>
                  <TableCell align="right">{row.unitCost}</TableCell>
                  <TableCell align="right">
                    {row.carryingValue.toLocaleString(undefined, { maximumFractionDigits: 2 })}
                  </TableCell>
                  <TableCell>{formatDay(row.lastIssueOn)}</TableCell>
                  <TableCell>{formatDay(row.lastReceiptOn)}</TableCell>
                  <TableCell align="right">
                    {row.daysSinceLastIssue == null
                      // Never issued is not "zero days old". Where nothing has ever gone out, the
                      // clock runs from the first arrival, and where there is neither the row is
                      // UNDATED rather than being given an age it does not have.
                      ? <Tooltip title="Nothing has ever been issued from this row. It is aged from its first receipt instead, or reported as undated when there is no movement at all.">
                          <Typography component="span" variant="body2" color="text.secondary">Never issued</Typography>
                        </Tooltip>
                      : row.daysSinceLastIssue}
                  </TableCell>
                  <TableCell>
                    <Typography component="span" variant="body2"
                      sx={{ fontWeight: 700, color: BAND_COLOUR[row.band] }}>
                      {BAND_LABEL[row.band]}
                    </Typography>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
    </PageShell>
  );
}
