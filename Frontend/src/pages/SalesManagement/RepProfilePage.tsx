import { useQuery } from '@tanstack/react-query';
import { Alert, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import { useParams } from 'react-router-dom';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { MetricGrid, PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function RepProfilePage() {
  const userId = Number(useParams<{ userId: string }>().userId);
  const query = useQuery({ queryKey: ['commercial-intelligence', 'rep', userId], queryFn: () => commercialIntelligenceService.getRepProfile(userId), enabled: Number.isInteger(userId) && userId > 0 });
  if (!Number.isInteger(userId) || userId <= 0) return <PageShell title="Rep profile" subtitle="Representative commercial workload."><Alert severity="error">The representative identifier is invalid.</Alert></PageShell>;
  const rep = query.data;
  const activity = rep?.recentActivity ?? [];
  const metrics = rep ? [
    { key: 'accounts', label: 'Owned accounts', value: rep.accountCount, unit: 'count' },
    { key: 'leads', label: 'Active leads', value: rep.activeLeads, unit: 'count' },
    { key: 'follow-ups', label: 'Follow-ups due', value: rep.followUpsDue, unit: 'count' },
    { key: 'won', label: 'Won value', value: rep.wonValue, unit: 'currency', currencyCode: rep.currencyCode },
  ] : [];
  return <PageShell title={rep?.name || 'Rep profile'} subtitle={rep?.email || 'Representative commercial workload.'}><MetricGrid metrics={metrics} /><QueryState loading={query.isLoading} error={query.isError} empty={!!rep && !activity.length} onRetry={() => void query.refetch()} emptyText="No recent commercial activity is recorded for this representative."><ResponsiveTable label="Representative activity"><Table size="small"><TableHead><TableRow><TableCell>Reference</TableCell><TableCell>Type</TableCell><TableCell>Customer</TableCell><TableCell>Reason</TableCell><TableCell>Due</TableCell></TableRow></TableHead><TableBody>{activity.map(item => <TableRow hover key={`${item.recordType}-${item.id}`}><TableCell>{item.nexoraSerial || item.reference}</TableCell><TableCell>{item.recordType}</TableCell><TableCell>{item.customerName || 'Unresolved'}</TableCell><TableCell>{item.reason}</TableCell><TableCell>{formatDateTime(item.dueAt)}</TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
