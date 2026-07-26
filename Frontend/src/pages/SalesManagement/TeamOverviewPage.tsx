import { useQuery } from '@tanstack/react-query';
import { Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { MetricGrid, PageShell, PipelineGroups, QueryState, ResponsiveTable } from './CommercialPagePrimitives';

export default function TeamOverviewPage() {
  const query = useQuery({ queryKey: ['commercial-intelligence', 'team-overview'], queryFn: commercialIntelligenceService.getTeamOverview });
  const rows = query.data?.representatives ?? [];
  return (
    <PageShell title="Team overview" subtitle="Workload and commercial exposure by representative.">
      <MetricGrid metrics={query.data?.metrics ?? []} />
      <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No representatives are available in this business unit.">
        <ResponsiveTable label="Team workload">
          <Table size="small">
            <TableHead><TableRow><TableCell>Representative</TableCell><TableCell align="right">Active leads</TableCell><TableCell align="right">Overdue</TableCell><TableCell align="right">Open RFQs</TableCell><TableCell align="right">Draft quotes</TableCell><TableCell align="right">Follow-ups</TableCell><TableCell align="right">Weighted pipeline</TableCell></TableRow></TableHead>
            <TableBody>{rows.map(row => <TableRow hover key={row.userId}><TableCell><Typography sx={{ fontWeight: 700 }}>{row.name}</Typography><Typography variant="caption" color="text.secondary">{row.email}</Typography></TableCell><TableCell align="right">{row.activeLeads}</TableCell><TableCell align="right">{row.overdueLeads}</TableCell><TableCell align="right">{row.openRfqs}</TableCell><TableCell align="right">{row.draftQuotes}</TableCell><TableCell align="right">{row.followUpsDue}</TableCell><TableCell align="right"><PipelineGroups groups={row.pipelineGroups} /></TableCell></TableRow>)}</TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
    </PageShell>
  );
}
