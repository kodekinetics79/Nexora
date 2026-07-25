import { useQuery } from '@tanstack/react-query';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { PageShell, QueryState, ResponsiveTable, StatusChip, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';

export default function IncomingPage() {
  const query = useQuery({ queryKey: ['inventory-intelligence', 'incoming'], queryFn: () => commercialIntelligenceService.getIncoming({ status: 'open' }) });
  const rows = query.data ?? [];
  return <PageShell title="Incoming stock" subtitle="Open supplier commitments and expected warehouse receipts."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No incoming stock is currently recorded."><ResponsiveTable label="Incoming inventory"><Table size="small"><TableHead><TableRow><TableCell>Purchase order</TableCell><TableCell>Supplier</TableCell><TableCell>Part</TableCell><TableCell>Warehouse</TableCell><TableCell align="right">Ordered</TableCell><TableCell align="right">Received</TableCell><TableCell>Expected</TableCell><TableCell>Status</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{row.purchaseOrderNumber}</TableCell><TableCell>{row.supplierName}</TableCell><TableCell>{row.partNumber} - {row.productName}</TableCell><TableCell>{row.warehouseName}</TableCell><TableCell align="right">{row.orderedQuantity}</TableCell><TableCell align="right">{row.receivedQuantity}</TableCell><TableCell>{formatDateTime(row.expectedAt)}</TableCell><TableCell><StatusChip value={row.status} /></TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
