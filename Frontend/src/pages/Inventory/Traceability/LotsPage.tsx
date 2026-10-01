import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link as RouterLink } from 'react-router-dom';
import {
  Chip, FormControl, FormControlLabel, InputLabel, Link, MenuItem, Select, Switch, Table, TableBody,
  TableCell, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import materialTraceabilityService, {
  LOT_STATUSES, type LotStatus, type CertificateExpiryState,
} from '../../../api/services/materialTraceabilityService';
import {
  QueryState, ResponsiveTable, formatDateTime,
} from '../../SalesManagement/CommercialPagePrimitives';
import { ProductsWorkspaceShell, ProductsWorkspaceToolbar } from '../ProductsWorkspaceNav';

/**
 * FR-MTR-01/02/05 worklist. Every lot the warehouse holds, what it is, where it came from, and
 * whether its compliance paperwork is in date.
 *
 * The "expired certificates only" switch is the FR-MTR-02 watchlist: it is the one query a quality
 * user runs before a shipment goes out, and it is a server-side predicate rather than a client
 * filter so it is answered over every lot, not over the first page of them.
 */

const certificateChip = (state: CertificateExpiryState, count: number) => {
  if (count === 0) return <Chip size="small" variant="outlined" color="warning" label="No certificate" />;
  if (state === 'EXPIRED') return <Chip size="small" variant="outlined" color="error" label={`Expired (${count})`} />;
  if (state === 'EXPIRING_SOON') return <Chip size="small" variant="outlined" color="warning" label={`Expiring soon (${count})`} />;
  if (state === 'NOT_APPLICABLE') return <Chip size="small" variant="outlined" label={`No expiry (${count})`} />;
  return <Chip size="small" variant="outlined" color="success" label={`Valid (${count})`} />;
};

const lotStatusLabel: Record<LotStatus, string> = {
  AVAILABLE: 'Available',
  QUARANTINED: 'Quarantined',
};

export default function LotsPage() {
  const [search, setSearch] = useState('');
  const [status, setStatus] = useState<LotStatus | ''>('');
  const [expiredOnly, setExpiredOnly] = useState(false);

  const query = useQuery({
    queryKey: ['material-lots', search, status, expiredOnly],
    queryFn: () => materialTraceabilityService.searchLots({
      search, status, expiredCertificatesOnly: expiredOnly,
    }),
  });
  const rows = query.data ?? [];

  return (
    <ProductsWorkspaceShell
      title="Traceability"
      subtitle="Every received lot, batch and serial — its supplier purchase order, its certificates and its quarantine state."
    >
      <ProductsWorkspaceToolbar>
        <TextField
          size="small"
          label="Lot, serial, manufacturer or origin"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          sx={{ minWidth: { xs: '100%', sm: 280 } }}
        />
        <FormControl size="small" sx={{ minWidth: 160 }}>
          <InputLabel id="lot-status-label">Status</InputLabel>
          <Select
            labelId="lot-status-label"
            label="Status"
            value={status}
            onChange={(event) => setStatus(event.target.value as LotStatus | '')}
          >
            <MenuItem value="">All statuses</MenuItem>
            {LOT_STATUSES.map((value) => <MenuItem key={value} value={value}>{lotStatusLabel[value]}</MenuItem>)}
          </Select>
        </FormControl>
        <FormControlLabel
          control={<Switch checked={expiredOnly} onChange={(event) => setExpiredOnly(event.target.checked)} />}
          label="Expired certificates only"
        />
      </ProductsWorkspaceToolbar>
      <QueryState
        loading={query.isLoading}
        error={query.isError}
        empty={!rows.length}
        onRetry={() => void query.refetch()}
        emptyText="No material lots match. Lots are created by goods receipt — receive against a supplier purchase order to see them here."
        hasData={query.data !== undefined}
        updatedAt={query.dataUpdatedAt}
      >
        <ResponsiveTable label="Material lots">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Lot / serial</TableCell>
                <TableCell>Tracking</TableCell>
                <TableCell>Product</TableCell>
                <TableCell>Part number</TableCell>
                <TableCell>Warehouse</TableCell>
                <TableCell align="right">Received</TableCell>
                <TableCell align="right">Remaining</TableCell>
                <TableCell>Origin</TableCell>
                <TableCell>Manufacturer</TableCell>
                <TableCell>Supplier</TableCell>
                <TableCell>Supplier PO number</TableCell>
                <TableCell>Certificates</TableCell>
                <TableCell>Status</TableCell>
                <TableCell>Received on</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((row) => (
                <TableRow hover key={row.id}>
                  <TableCell sx={{ fontWeight: 700 }}>
                    <Link component={RouterLink} to={`/inventory/lots/${row.id}`} underline="hover">
                      {row.lotNumber}
                    </Link>
                  </TableCell>
                  <TableCell>{row.trackingMode}</TableCell>
                  <TableCell>{row.productName || '—'}</TableCell>
                  <TableCell>{row.partNumber || '—'}</TableCell>
                  <TableCell>{row.warehouseName ?? row.warehouseId}</TableCell>
                  <TableCell align="right">{row.quantityReceived}</TableCell>
                  <TableCell align="right">{row.quantityRemaining}</TableCell>
                  <TableCell>{row.countryOfOrigin ?? '—'}</TableCell>
                  <TableCell>{row.manufacturerName ?? '—'}</TableCell>
                  <TableCell>
                    <Typography variant="body2">{row.supplierName ?? 'Supplier not named'}</Typography>
                  </TableCell>
                  <TableCell>{row.purchaseOrderNumber || 'Not recorded'}</TableCell>
                  <TableCell>{certificateChip(row.certificateState, row.certificateCount)}</TableCell>
                  <TableCell>
                    {row.status === 'QUARANTINED'
                      ? <Chip size="small" color="error" label="Quarantined" sx={{ fontWeight: 700 }} />
                      : <Chip size="small" color="success" variant="outlined" label="Available" />}
                  </TableCell>
                  <TableCell>{formatDateTime(row.receivedOn)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </ResponsiveTable>
        {rows.length === 50 && (
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
            Showing the first 50 matching lots. Refine the filters to narrow the result.
          </Typography>
        )}
      </QueryState>
    </ProductsWorkspaceShell>
  );
}
