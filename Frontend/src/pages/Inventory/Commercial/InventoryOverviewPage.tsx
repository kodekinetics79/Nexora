import { useQuery } from '@tanstack/react-query';
import { Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import commercialIntelligenceService from '../../../api/services/commercialIntelligenceService';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, StatusChip, formatDateTime } from '../../SalesManagement/CommercialPagePrimitives';

export default function InventoryOverviewPage() {
  const query = useQuery({ queryKey: ['inventory-intelligence', 'overview'], queryFn: commercialIntelligenceService.getInventoryOverview, refetchInterval: 60_000 });
  const rows = query.data?.exceptions ?? [];
  return <PageShell title="Inventory overview" subtitle="Availability and supply exceptions affecting current commercial demand."><MetricGrid metrics={query.data?.metrics ?? []} /><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No inventory exceptions require attention."><ResponsiveTable label="Inventory exceptions"><Table size="small"><TableHead><TableRow><TableCell>Part</TableCell><TableCell>Product</TableCell><TableCell>Warehouse</TableCell><TableCell>Exception</TableCell><TableCell align="right">Available</TableCell><TableCell align="right">Required</TableCell><TableCell>Due</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{row.partNumber}</TableCell><TableCell>{row.productName}</TableCell><TableCell>{row.warehouseName || 'All warehouses'}</TableCell><TableCell><StatusChip value={row.exceptionType} /></TableCell><TableCell align="right">{row.availableQuantity}</TableCell><TableCell align="right">{row.requiredQuantity ?? 'Not recorded'}</TableCell><TableCell>{formatDateTime(row.dueAt)}</TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
