import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField } from '@mui/material';
import dayjs from 'dayjs';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';

export default function MovementsPage() {
  const initialTo = useMemo(() => dayjs().add(1, 'day').format('YYYY-MM-DD'), []);
  const [from, setFrom] = useState(dayjs().subtract(7, 'day').format('YYYY-MM-DD'));
  const [to, setTo] = useState(initialTo);
  const valid = dayjs(from).isBefore(dayjs(to));
  const query = useQuery({ queryKey: ['inventory-intelligence', 'movements', from, to], queryFn: () => commercialIntelligenceService.getMovements({ from, to }), enabled: valid });
  const rows = query.data ?? [];
  return <PageShell title="Inventory movements" subtitle="Auditable stock changes in the selected period." actions={<Stack direction="row" spacing={1}><TextField size="small" type="date" label="From" value={from} onChange={event => setFrom(event.target.value)} slotProps={{ inputLabel: { shrink: true } }} /><TextField size="small" type="date" label="To" value={to} onChange={event => setTo(event.target.value)} error={!valid} slotProps={{ inputLabel: { shrink: true } }} /></Stack>}><QueryState loading={query.isLoading} error={query.isError || !valid} empty={valid && !rows.length} onRetry={() => void query.refetch()} emptyText="No stock movements exist in this period."><ResponsiveTable label="Inventory movement ledger"><Table size="small"><TableHead><TableRow><TableCell>Occurred</TableCell><TableCell>Movement</TableCell><TableCell>Part</TableCell><TableCell>Warehouse</TableCell><TableCell align="right">Quantity</TableCell><TableCell>Reference</TableCell><TableCell>Actor</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{formatDateTime(row.occurredAt)}</TableCell><TableCell>{row.movementType}</TableCell><TableCell>{row.partNumber} - {row.productName}</TableCell><TableCell>{row.warehouseName}</TableCell><TableCell align="right">{row.quantity}</TableCell><TableCell>{row.reference ? `${row.referenceType || ''} ${row.reference}` : 'Not linked'}</TableCell><TableCell>{row.actorName || 'System'}</TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
