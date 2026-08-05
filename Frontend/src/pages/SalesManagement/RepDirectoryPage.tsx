import { useQuery } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { PageShell, PipelineGroups, QueryState, ResponsiveTable } from './CommercialPagePrimitives';

export default function RepDirectoryPage() {
  const navigate = useNavigate();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'reps'], queryFn: commercialIntelligenceService.getRepDirectory });
  const rows = query.data ?? [];
  return (
    <PageShell title="Rep directory" subtitle="Sales ownership, workload, and current pipeline by team member.">
      <QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No sales representatives were returned.">
        <ResponsiveTable label="Sales representatives">
          <Table size="small">
            <TableHead><TableRow><TableCell>Representative</TableCell><TableCell>Role</TableCell><TableCell align="right">Active leads</TableCell><TableCell align="right">Follow-ups due</TableCell><TableCell align="right">Pipeline</TableCell><TableCell>Profile</TableCell></TableRow></TableHead>
            <TableBody>{rows.map(row => <TableRow hover key={row.userId}><TableCell><Typography sx={{ fontWeight: 700 }}>{row.name}</Typography><Typography variant="caption" color="text.secondary">{row.email}</Typography></TableCell><TableCell>{row.roleName || 'Sales representative'}</TableCell><TableCell align="right">{row.activeLeads}</TableCell><TableCell align="right">{row.followUpsDue}</TableCell><TableCell align="right"><PipelineGroups groups={row.pipelineGroups} /></TableCell><TableCell><Button size="small" onClick={() => navigate(`/sales/reps/${row.userId}`)}>Open</Button></TableCell></TableRow>)}</TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
    </PageShell>
  );
}
