import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Button, Table, TableBody, TableCell, TableHead, TableRow } from '@mui/material';
import { useSnackbar } from 'notistack';
import { useSearchParams } from 'react-router-dom';
import commercialIntelligenceService, { type RoutingQueueItemDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

export default function RoutingQueuePage() {
  const [searchParams] = useSearchParams();
  const sourceId = Number(searchParams.get('sourceId')) || undefined;
  const { userData, hasPermission } = useAuth();
  const canAssign = hasPermission('Leads', 'edit') && !!userData.id;
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'routing-queue', sourceId], queryFn: () => commercialIntelligenceService.getRoutingQueue({ sourceId }), refetchInterval: 60_000 });
  const mutation = useMutation({ mutationFn: (row: RoutingQueueItemDTO) => commercialIntelligenceService.assignRoutingItem(row.leadId, userData.id!, row.version, crypto.randomUUID()), onSuccess: () => { enqueueSnackbar('Lead assigned', { variant: 'success' }); void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'routing-queue'] }); }, onError: () => enqueueSnackbar('Lead assignment failed', { variant: 'error' }) });
  const rows = query.data ?? [];
  return <PageShell title="Routing queue" subtitle="Unowned inquiries and explainable assignment recommendations."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No inquiries are waiting for assignment."><ResponsiveTable label="Lead routing queue"><Table size="small"><TableHead><TableRow><TableCell>Nexora Serial</TableCell><TableCell>Customer</TableCell><TableCell>Received</TableCell><TableCell>Routing reason</TableCell><TableCell>Recommended owner</TableCell>{canAssign && <TableCell>Action</TableCell>}</TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.leadId}><TableCell>{row.nexoraSerial}</TableCell><TableCell>{row.customerName || 'Customer unresolved'}</TableCell><TableCell>{formatDateTime(row.receivedAt)}</TableCell><TableCell>{row.reason}</TableCell><TableCell>{row.recommendedOwnerName || 'No recommendation'}{row.recommendationReason ? ` - ${row.recommendationReason}` : ''}</TableCell>{canAssign && <TableCell><Button size="small" disabled={mutation.isPending} onClick={() => mutation.mutate(row)}>Assign to me</Button></TableCell>}</TableRow>)}</TableBody></Table></ResponsiveTable></QueryState></PageShell>;
}
