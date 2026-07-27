import { useQuery } from '@tanstack/react-query';
import { Button, Chip, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import { OpenInNew } from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function SalesTodayPage() {
  const navigate = useNavigate();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'sales-today'], queryFn: commercialIntelligenceService.getSalesToday, refetchInterval: 60_000 });
  const items = query.data?.attentionItems ?? [];
  return <PageShell title="Sales today" subtitle={query.data?.scope === 'tenant' ? 'Team-wide commercial work that needs attention now.' : 'Your assigned commercial work that needs attention now.'}>
    <MetricGrid metrics={query.data?.metrics ?? []} />
    <QueryState loading={query.isLoading} error={query.isError} empty={!items.length} onRetry={() => void query.refetch()} emptyText="Nothing requires sales attention right now.">
      <ResponsiveTable label="Sales attention queue"><Table size="small"><TableHead><TableRow><TableCell>Priority</TableCell><TableCell>Reference</TableCell><TableCell>Customer</TableCell><TableCell>Owner</TableCell><TableCell>Why it needs attention</TableCell><TableCell>Due</TableCell><TableCell align="right">Action</TableCell></TableRow></TableHead><TableBody>
        {items.map(item => { const target = item.recordType.toLowerCase() === 'quote' ? `/sales/quotes/view/${item.recordId}` : item.recordType.toLowerCase() === 'lead' ? `/procurement/leads/view/${item.recordId}` : item.nexoraSerial ? `/commercial-cases?search=${encodeURIComponent(item.nexoraSerial)}` : null; return <TableRow hover key={`${item.recordType}-${item.id}`}><TableCell><Chip size="small" label={item.priority} color={item.priority.toLowerCase() === 'critical' ? 'error' : 'warning'} /></TableCell><TableCell>{item.nexoraSerial || item.reference}</TableCell><TableCell>{item.customerName || 'Customer unresolved'}</TableCell><TableCell>{item.ownerName || 'Unassigned'}</TableCell><TableCell>{item.reason}</TableCell><TableCell>{formatDateTime(item.dueAt)}</TableCell><TableCell align="right">{target && <Button size="small" endIcon={<OpenInNew />} onClick={() => navigate(target)}>Open</Button>}</TableCell></TableRow>; })}
      </TableBody></Table></ResponsiveTable>
    </QueryState>
  </PageShell>;
}
