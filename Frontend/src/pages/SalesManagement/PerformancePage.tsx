import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField } from '@mui/material';
import dayjs from 'dayjs';
import commercialIntelligenceService from '../../api/services/commercialIntelligenceService';
import { MetricGrid, PageShell, QueryState, ResponsiveTable } from './CommercialPagePrimitives';

export default function PerformancePage() {
  const initialTo = useMemo(() => dayjs().add(1, 'day').format('YYYY-MM-DD'), []);
  const [from, setFrom] = useState(dayjs().subtract(30, 'day').format('YYYY-MM-DD'));
  const [to, setTo] = useState(initialTo);
  const valid = dayjs(from).isBefore(dayjs(to));
  const query = useQuery({ queryKey: ['commercial-intelligence', 'performance', from, to], queryFn: () => commercialIntelligenceService.getPerformance(from, to), enabled: valid });
  const rows = query.data?.representatives ?? [];
  return <PageShell title="Sales performance" subtitle="Verified results for the selected half-open reporting period." actions={<Stack direction="row" spacing={1}><TextField size="small" type="date" label="From" value={from} onChange={event => setFrom(event.target.value)} slotProps={{ inputLabel: { shrink: true } }} /><TextField size="small" type="date" label="To" value={to} onChange={event => setTo(event.target.value)} error={!valid} slotProps={{ inputLabel: { shrink: true } }} /></Stack>}><MetricGrid metrics={query.data?.metrics ?? []} /><QueryState loading={query.isLoading} error={query.isError || !valid} empty={valid && !rows.length} onRetry={() => void query.refetch()} emptyText="No performance records exist in this period."><ResponsiveTable label="Representative performance"><Table size="small"><TableHead><TableRow><TableCell>Representative</TableCell><TableCell align="right">Won</TableCell><TableCell align="right">Lost</TableCell><TableCell align="right">Conversion</TableCell><TableCell align="right">Pipeline</TableCell><TableCell align="right">Overdue leads</TableCell></TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.userId}><TableCell>{row.name}</TableCell><TableCell align="right">{row.wonQuotes}</TableCell><TableCell align="right">{row.lostQuotes}</TableCell><TableCell align="right">{row.conversionRate == null ? 'Insufficient data' : `${row.conversionRate.toFixed(1)}%`}</TableCell><TableCell align="right">{row.currencyCode} {row.weightedPipeline.toLocaleString()}</TableCell><TableCell align="right">{row.overdueLeads}</TableCell></TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
