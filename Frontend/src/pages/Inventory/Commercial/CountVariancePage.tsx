import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, FormControlLabel, Paper, Stack, Switch, Table, TableBody, TableCell, TableHead,
  TableRow, TextField, Tooltip, Typography,
} from '@mui/material';
import dayjs from 'dayjs';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';

/**
 * FR-INV-05. The cycle-count variance report: what the book said, what the counter found, and the
 * gap between them, for every count posted in the window.
 *
 * <p>Rebuilt from the append-only movement ledger rather than from a parallel stock-take table, so
 * it cannot disagree with the stock it explains. What that costs is stated on the screen: there is
 * no count SHEET and no counting session, so a count is one product in one warehouse per posting
 * and there is no blind count, no second count and no approval step before it is posted. That is a
 * named gap, not an implied capability.</p>
 */
export default function CountVariancePage() {
  const initialTo = useMemo(() => dayjs().add(1, 'day').format('YYYY-MM-DD'), []);
  const [from, setFrom] = useState(dayjs().subtract(90, 'day').format('YYYY-MM-DD'));
  const [to, setTo] = useState(initialTo);
  const [varianceOnly, setVarianceOnly] = useState(true);
  const valid = dayjs(from).isBefore(dayjs(to));

  const query = useQuery({
    queryKey: ['inventory-intelligence', 'count-variance', from, to, varianceOnly],
    queryFn: () => commercialIntelligenceService.getCountVariance({ from, to, varianceOnly }),
    enabled: valid,
  });

  const data = query.data;
  const rows = data?.rows ?? [];

  return (
    <PageShell
      title="Count variance"
      subtitle="Counted against book value for every stock count posted in the period."
      actions={
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <TextField size="small" type="date" label="From" value={from}
            onChange={e => setFrom(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
          <TextField size="small" type="date" label="To" value={to} error={!valid}
            onChange={e => setTo(e.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
          <FormControlLabel
            control={<Switch checked={varianceOnly} onChange={e => setVarianceOnly(e.target.checked)} />}
            label="Variances only"
          />
        </Stack>
      }
    >
      {data && (
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(3, minmax(0, 1fr))' }, gap: 1.5, mb: 2.5 }}>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>Counted rows</Typography>
            <Typography variant="h6" sx={{ fontWeight: 900 }}>{data.countedRows}</Typography>
          </Paper>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>Net variance (units)</Typography>
            <Typography variant="h6" sx={{ fontWeight: 900 }}>{data.netVariance}</Typography>
          </Paper>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Tooltip title="Overs and unders added without cancelling out. A net of zero built from a large absolute figure is not an accurate warehouse; it is two errors of opposite sign.">
              <Box>
                <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>Absolute variance (units)</Typography>
                <Typography variant="h6" sx={{ fontWeight: 900 }}>{data.absoluteVariance}</Typography>
              </Box>
            </Tooltip>
          </Paper>
        </Box>
      )}

      <Alert severity="info" sx={{ mb: 2 }}>
        A count is posted per item per warehouse. There is no count sheet or counting session yet, so
        blind counts, second counts and an approval step before posting are not modelled — the
        variance below is the record of what was posted, not of a governed stock take.
      </Alert>

      <QueryState
        loading={query.isLoading}
        error={query.isError || !valid}
        empty={valid && !rows.length}
        onRetry={() => void query.refetch()}
        emptyText={varianceOnly
          ? 'No count in this period disagreed with the book value.'
          : 'No stock counts were posted in this period.'}
      >
        <ResponsiveTable label="Stock count variance">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Counted</TableCell>
                <TableCell>Part</TableCell>
                <TableCell>Warehouse</TableCell>
                <TableCell align="right">Book (units)</TableCell>
                <TableCell align="right">Counted (units)</TableCell>
                <TableCell align="right">Variance (units)</TableCell>
                <TableCell align="right">Variance %</TableCell>
                <TableCell>Counted by</TableCell>
                <TableCell>Reason</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map(row => (
                <TableRow hover key={`${row.inventoryId}-${row.countedOn}`}>
                  <TableCell>{formatDateTime(row.countedOn)}</TableCell>
                  <TableCell>{row.partNumber} — {row.productName}</TableCell>
                  <TableCell>{row.warehouseName}</TableCell>
                  <TableCell align="right">{row.bookQuantity}</TableCell>
                  <TableCell align="right">{row.countedQuantity}</TableCell>
                  <TableCell align="right">
                    <Typography component="span" variant="body2" sx={{
                      fontWeight: 700,
                      color: row.variance === 0 ? 'text.primary' : row.variance < 0 ? 'error.main' : 'warning.main',
                    }}>
                      {row.variance > 0 ? `+${row.variance}` : row.variance}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    {row.variancePercent == null
                      // Not 0% and not 500%: a count that finds units where the system knew of none
                      // is an infinite percentage, and inventing a figure would be worse than
                      // saying the ratio does not exist.
                      ? <Tooltip title="The book value was zero, so there is no ratio to express. The absolute variance is the honest number here.">
                          <Typography component="span" variant="body2" color="text.secondary">No ratio</Typography>
                        </Tooltip>
                      : `${row.variancePercent}%`}
                  </TableCell>
                  <TableCell>{row.countedBy}</TableCell>
                  <TableCell>{row.reason ?? '—'}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
    </PageShell>
  );
}
