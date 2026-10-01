import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Table, TableBody, TableCell, TableHead, TableRow, TextField } from '@mui/material';
import dayjs from 'dayjs';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { QueryState, ResponsiveTable, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';
import { ProductsWorkspaceShell, ProductsWorkspaceToolbar } from '../ProductsWorkspaceNav';

const movementEffect = (type: string, quantity: number) => {
  const label = type.replace(/([a-z])([A-Z])/g, '$1 $2');
  if (['Issue', 'AdjustmentDecrease', 'TransferOut'].includes(type)) {
    return { label, quantity: `−${quantity}` };
  }
  if (['Receipt', 'AdjustmentIncrease', 'TransferIn'].includes(type)) {
    return { label, quantity: `+${quantity}` };
  }
  return { label, quantity: `↔ ${quantity}` };
};

export default function MovementsPage() {
  const initialTo = useMemo(() => dayjs().add(1, 'day').format('YYYY-MM-DD'), []);
  const [from, setFrom] = useState(dayjs().subtract(7, 'day').format('YYYY-MM-DD'));
  const [to, setTo] = useState(initialTo);
  const valid = dayjs(from).isBefore(dayjs(to));
  const query = useQuery({
    queryKey: ['inventory-intelligence', 'movements', from, to],
    queryFn: () => commercialIntelligenceService.getMovements({ from, to }),
    enabled: valid,
  });
  const rows = query.data ?? [];

  return (
    <ProductsWorkspaceShell
      title="Stock activity"
      subtitle="An auditable ledger of every stock change in the selected period."
    >
      <ProductsWorkspaceToolbar>
        <TextField
          size="small"
          type="date"
          label="From"
          value={from}
          onChange={(event) => setFrom(event.target.value)}
          slotProps={{ inputLabel: { shrink: true } }}
        />
        <TextField
          size="small"
          type="date"
          label="To"
          value={to}
          onChange={(event) => setTo(event.target.value)}
          error={!valid}
          helperText={!valid ? 'Choose a date after From' : undefined}
          slotProps={{ inputLabel: { shrink: true } }}
        />
      </ProductsWorkspaceToolbar>
      {valid && (
        <QueryState
          loading={query.isLoading}
          error={query.isError}
          empty={!rows.length}
          onRetry={() => void query.refetch()}
          emptyText="No stock movements exist in this period."
          hasData={query.data !== undefined}
          updatedAt={query.dataUpdatedAt}
        >
          <ResponsiveTable label="Stock activity ledger">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Occurred</TableCell>
                <TableCell>Movement</TableCell>
                <TableCell>Product</TableCell>
                <TableCell>Part number</TableCell>
                <TableCell>Warehouse</TableCell>
                <TableCell align="right">Quantity</TableCell>
                <TableCell>Reference type</TableCell>
                <TableCell>Reference number</TableCell>
                <TableCell>Actor</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((row) => {
                const effect = movementEffect(row.movementType, row.quantity);
                return (
                  <TableRow hover key={row.id}>
                    <TableCell>{formatDateTime(row.occurredAt)}</TableCell>
                    <TableCell>{effect.label}</TableCell>
                    <TableCell>{row.productName || '—'}</TableCell>
                    <TableCell>{row.partNumber || '—'}</TableCell>
                    <TableCell>{row.warehouseName}</TableCell>
                    <TableCell align="right" sx={{ fontVariantNumeric: 'tabular-nums', fontWeight: 700 }}>{effect.quantity}</TableCell>
                    <TableCell>{row.referenceType || '—'}</TableCell>
                    <TableCell>{row.reference || 'Not linked'}</TableCell>
                    <TableCell>{row.actorName || 'System'}</TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
          </ResponsiveTable>
        </QueryState>
      )}
    </ProductsWorkspaceShell>
  );
}
