import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Alert, Button, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material';
import { OpenInNew as OpenInNewIcon } from '@mui/icons-material';
import dayjs from 'dayjs';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { MetricGrid, PageShell, PipelineGroups, QueryState, ResponsiveTable } from './CommercialPagePrimitives';

export default function PerformancePage() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const canOpenRepRecords = hasPermission('Users');
  const initialTo = useMemo(() => dayjs().add(1, 'day').format('YYYY-MM-DD'), []);
  const [from, setFrom] = useState(dayjs().subtract(30, 'day').format('YYYY-MM-DD'));
  const [to, setTo] = useState(initialTo);
  const valid = dayjs(from).isBefore(dayjs(to));
  const query = useQuery({ queryKey: ['commercial-intelligence', 'performance', from, to], queryFn: () => commercialIntelligenceService.getPerformance(from, to), enabled: valid });
  const rows = query.data?.representatives ?? [];
  return (
    <PageShell title="Sales performance" subtitle="Verified results for the selected half-open reporting period." actions={<Stack direction="row" spacing={1}><TextField size="small" type="date" label="From" value={from} onChange={event => setFrom(event.target.value)} slotProps={{ inputLabel: { shrink: true } }} /><TextField size="small" type="date" label="To" value={to} onChange={event => setTo(event.target.value)} error={!valid} slotProps={{ inputLabel: { shrink: true } }} /></Stack>}>
      {!valid && <Alert severity="warning" sx={{ mb: 2 }}>The From date must be earlier than the To date.</Alert>}
      {query.data?.outcomeReconciliation?.isTenantComplete && (query.data.outcomeReconciliation.unattributedOutcomes ?? 0) > 0 && <Alert severity="warning" sx={{ mb: 2 }}>
        {query.data.outcomeReconciliation.unattributedOutcomes} decided Quote outcome(s) have no Sales Rep attribution. Team conversion excludes those records until ownership is reconciled.
      </Alert>}
      {valid && <><Stack direction="row" spacing={1} sx={{ mb: 1, flexWrap: 'wrap' }}><Typography variant="caption" color="text.secondary">Scope: {query.data?.scope === 'assigned_to_me' ? 'your assigned work' : 'tenant team'}.</Typography><Tooltip title="Won and lost count distinct recorded commercial outcomes in the selected half-open period. Conversion is shown only when at least five outcomes are decided."><Typography component="button" variant="caption" sx={{ border: 0, bgcolor: 'transparent', color: 'primary.main', cursor: 'help' }}>How these KPIs are calculated</Typography></Tooltip></Stack><MetricGrid metrics={query.data?.metrics ?? []} /></>}
      <QueryState loading={valid && query.isLoading} error={valid && query.isError} empty={valid && !rows.length} onRetry={() => void query.refetch()} emptyText="No performance records exist in this period.">
        <ResponsiveTable label="Representative performance">
          <Table size="small">
            <TableHead><TableRow><TableCell>Representative</TableCell><TableCell align="right">Won / lost</TableCell><TableCell align="right">Conversion</TableCell><TableCell align="right">Response</TableCell><TableCell align="right">Follow-up effectiveness</TableCell><TableCell align="right">Pipeline</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead>
            <TableBody>{rows.map(row => { const completionRate = row.completedFollowUps === 0 ? null : row.followUpsCompletedOnTime * 100 / row.completedFollowUps; return <TableRow hover key={row.userId}><TableCell><Typography sx={{ fontWeight: 700 }}>{row.name}</Typography><Typography variant="caption" color="text.secondary">{row.activityCount} activities | {row.opportunities} opportunities</Typography></TableCell><TableCell align="right">{row.wonQuotes} / {row.lostQuotes}<Typography sx={{ display: 'block' }} variant="caption" color="text.secondary">{row.decidedQuotes} decided</Typography></TableCell><TableCell align="right">{row.conversionEligible && row.conversionRate != null ? `${row.conversionRate.toFixed(1)}%` : `Insufficient data (${row.decidedQuotes}/${query.data?.minimumConversionSample ?? 5})`}</TableCell><TableCell align="right">{row.averageResponseHours == null ? 'No paired response' : `${row.averageResponseHours.toFixed(1)} h`}<Typography sx={{ display: 'block' }} variant="caption" color="text.secondary">{row.customerResponses}/{row.quoteSent} responses/sent</Typography></TableCell><TableCell align="right">{completionRate == null ? 'No completions' : `${completionRate.toFixed(0)}% on time`}<Typography sx={{ display: 'block' }} variant="caption" color="text.secondary">{row.completedFollowUps}/{row.followUpsCreated} completed | {row.overdueFollowUps} overdue</Typography></TableCell><TableCell align="right"><PipelineGroups groups={row.pipelineGroups} /></TableCell><TableCell>{canOpenRepRecords ? <Button size="small" endIcon={<OpenInNewIcon />} onClick={() => navigate(`/sales/reps/${row.userId}?from=${from}&to=${to}`)}>Open records</Button> : <Typography variant="caption" color="text.secondary">Users permission required</Typography>}</TableCell></TableRow>; })}</TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>
    </PageShell>
  );
}
