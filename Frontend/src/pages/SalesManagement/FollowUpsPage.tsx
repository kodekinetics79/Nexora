import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import { useSnackbar } from 'notistack';
import { useSearchParams } from 'react-router-dom';
import commercialIntelligenceService, { type FollowUpDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, StatusChip, formatDateTime } from './CommercialPagePrimitives';

export default function FollowUpsPage() {
  const [searchParams] = useSearchParams();
  const sourceId = Number(searchParams.get('sourceId')) || undefined;
  const canComplete = useAuth().hasPermission('Quotations', 'edit');
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'follow-ups', sourceId], queryFn: () => commercialIntelligenceService.getFollowUps({ status: sourceId ? undefined : 'open', sourceId }) });
  const mutation = useMutation({ mutationFn: (row: FollowUpDTO) => commercialIntelligenceService.completeFollowUp(row.id, row.version, crypto.randomUUID()), onSuccess: () => { enqueueSnackbar('Follow-up completed', { variant: 'success' }); void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'follow-ups'] }); }, onError: () => enqueueSnackbar('Follow-up could not be completed', { variant: 'error' }) });
  const rows = query.data ?? [];
  return <PageShell title="Follow-ups" subtitle="Customer responses and quote conversations due for action."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No open follow-ups are due."><ResponsiveTable label="Quote follow-ups"><Table size="small"><TableHead><TableRow><TableCell>Quote</TableCell><TableCell>Customer</TableCell><TableCell>Owner</TableCell><TableCell>Due</TableCell><TableCell>Reason</TableCell><TableCell>Status</TableCell>{canComplete && <TableCell>Action</TableCell>}</TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.id}><TableCell>{row.nexoraSerial || row.quoteNo}</TableCell><TableCell>{row.customerName}</TableCell><TableCell>{row.ownerName || 'Unassigned'}</TableCell><TableCell>{formatDateTime(row.dueAt)}</TableCell><TableCell>{row.reason}</TableCell><TableCell><StatusChip value={row.status} /></TableCell>{canComplete && <TableCell><Button size="small" disabled={mutation.isPending} onClick={() => mutation.mutate(row)}>Complete</Button></TableCell>}</TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
