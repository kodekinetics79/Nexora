import { useQuery } from '@tanstack/react-query';
import { Alert, Box, Button, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material';
import { OpenInNew as OpenInNewIcon } from '@mui/icons-material';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { CurrencyAmounts, MetricGrid, PageShell, PipelineGroups, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';
import { useAuth } from '../../context/AuthContext';

export default function RepProfilePage() {
  const userId = Number(useParams<{ userId: string }>().userId);
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const [searchParams] = useSearchParams();
  const from = searchParams.get('from') || undefined;
  const to = searchParams.get('to') || undefined;
  const query = useQuery({ queryKey: ['commercial-intelligence', 'rep', userId, from, to], queryFn: () => commercialIntelligenceService.getRepProfile(userId, from, to), enabled: Number.isInteger(userId) && userId > 0 });
  if (!Number.isInteger(userId) || userId <= 0) return <PageShell title="Rep profile" subtitle="Representative commercial workload."><Alert severity="error">The representative identifier is invalid.</Alert></PageShell>;
  const rep = query.data;
  const activity = rep?.recentActivity ?? [];
  const metrics = rep ? [
    { key: 'accounts', label: 'Owned accounts', value: rep.accountCount, unit: 'count' },
    { key: 'leads', label: 'Active leads', value: rep.activeLeads, unit: 'count' },
    { key: 'follow-ups', label: 'Follow-ups due', value: rep.followUpsDue, unit: 'count' },
    { key: 'overdue', label: 'Overdue leads', value: rep.overdueLeads, unit: 'count' },
    { key: 'rfqs', label: 'Open RFQs', value: rep.openRfqs, unit: 'count' },
    { key: 'draft-quotes', label: 'Draft quotes', value: rep.draftQuotes, unit: 'count' },
  ] : [];
  return (
    <PageShell title={rep?.name || 'Rep profile'} subtitle={rep?.email || 'Representative commercial workload.'}>
      <MetricGrid metrics={metrics} />
      {rep && <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(3, 1fr)' }, gap: 2, mb: 2.5 }}><Box><Typography variant="caption" color="text.secondary">Weighted active pipeline</Typography><PipelineGroups groups={rep.pipelineGroups} /></Box><Box><Typography variant="caption" color="text.secondary">Won value, contribution weighted</Typography><CurrencyAmounts groups={rep.wonValueGroups} /></Box><Box><Typography variant="caption" color="text.secondary">Decided-outcome conversion</Typography><Typography>{rep.conversionEligible && rep.conversionRate != null ? `${rep.conversionRate.toFixed(1)}%` : `Insufficient data (${rep.decidedQuotes}/5)`}</Typography></Box></Box>}
      <QueryState loading={query.isLoading} error={query.isError} empty={!!rep && !activity.length} onRetry={() => void query.refetch()} emptyText="No recent commercial activity is recorded for this representative.">
        <ResponsiveTable label="Representative activity"><Table size="small"><TableHead><TableRow><TableCell>Reference</TableCell><TableCell>Type</TableCell><TableCell>Customer</TableCell><TableCell>Reason</TableCell><TableCell>Occurred</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead><TableBody>{activity.map(item => { const canOpen = !!item.actionRoute && (!item.requiredModule || hasPermission(item.requiredModule)); return <TableRow hover key={`${item.recordType}-${item.id}`}><TableCell>{item.nexoraSerial || item.reference}</TableCell><TableCell>{item.recordType}</TableCell><TableCell>{item.customerName || 'Unresolved'}</TableCell><TableCell>{item.reason}</TableCell><TableCell>{formatDateTime(item.dueAt)}</TableCell><TableCell>{canOpen ? <Button size="small" endIcon={<OpenInNewIcon />} onClick={() => navigate(item.actionRoute!)}>Open</Button> : <Typography variant="caption" color="text.secondary">Permission required</Typography>}</TableCell></TableRow>; })}</TableBody></Table></ResponsiveTable>
      </QueryState>
    </PageShell>
  );
}
