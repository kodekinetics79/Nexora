import { useQuery } from '@tanstack/react-query';
import { Chip, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function SalesTodayPage() {
  const query = useQuery({ queryKey: ['commercial-intelligence', 'sales-today'], queryFn: commercialIntelligenceService.getSalesToday, refetchInterval: 60_000 });
  const items = query.data?.attentionItems ?? [];
  return <PageShell title="Sales today" subtitle="The commercial work that needs attention now, from persisted pipeline records.">
    <MetricGrid metrics={query.data?.metrics ?? []} />
    <QueryState loading={query.isLoading} error={query.isError} empty={!items.length} onRetry={() => void query.refetch()} emptyText="Nothing requires sales attention right now.">
      <ResponsiveTable label="Sales attention queue"><Table size="small"><TableHead><TableRow><TableCell>Priority</TableCell><TableCell>Reference</TableCell><TableCell>Customer</TableCell><TableCell>Owner</TableCell><TableCell>Why it needs attention</TableCell><TableCell>Due</TableCell></TableRow></TableHead><TableBody>
        {items.map(item => <TableRow hover key={`${item.recordType}-${item.id}`}><TableCell><Chip size="small" label={item.priority} color={item.priority.toLowerCase() === 'critical' ? 'error' : 'warning'} /></TableCell><TableCell>{item.nexoraSerial || item.reference}</TableCell><TableCell>{item.customerName || 'Customer unresolved'}</TableCell><TableCell>{item.ownerName || 'Unassigned'}</TableCell><TableCell>{item.reason}</TableCell><TableCell>{formatDateTime(item.dueAt)}</TableCell></TableRow>)}
      </TableBody></Table></ResponsiveTable>
    </QueryState>
  </PageShell>;
}
