import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Alert, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, FormControl, InputLabel, MenuItem, Select, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material';
import { AssignmentInd as AssignmentIcon, OpenInNew as OpenInNewIcon, PanTool as ClaimIcon, Replay as RerouteIcon, Undo as ReleaseIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { useNavigate, useSearchParams } from 'react-router-dom';
import commercialIntelligenceService, { type RoutingQueueItemDTO } from '../../api/services/commercialIntelligenceService';
import commercialRoutingService from '../../api/services/commercialRoutingService';
import { useAuth } from '../../context/AuthContext';
import { routingDecisionSentence } from '../../utils/routingDecisionReasons';
import { PageShell, QueryState, ResponsiveTable, formatDateTime } from './CommercialPagePrimitives';

/**
 * The shared pool of unowned inquiries, with two lanes on one list.
 *
 * A manager PUSHES: pick an owner and assign, which is what `routing-queue/{id}/assign` does and
 * what this page has always done. A rep PULLS: claim an item so nobody else works it, then a
 * manager confirms the claim into ownership. Claim and release are the only routing verbs a plain
 * rep can reach — assignment is manager-gated on both controllers — so a claim is deliberately a
 * RESERVATION and not ownership. The row says so rather than implying the rep now owns the lead.
 *
 * "Re-route" exists because the reconciliation worker only reconsiders leads that have no routing
 * decision at all. Everything already sitting in this queue has one (an Unassigned decision), so
 * fixing the underlying cause — usually giving somebody a Sales Rep profile — does not release the
 * backlog on its own. This is the only trigger that re-runs the engine over an existing item.
 */
export default function RoutingQueuePage() {
  const [searchParams] = useSearchParams();
  const sourceId = Number(searchParams.get('sourceId')) || undefined;
  const [target, setTarget] = useState<RoutingQueueItemDTO | null>(null);
  const [ownerUserId, setOwnerUserId] = useState<number | ''>('');
  const [reason, setReason] = useState('');
  const mutationIntent = useRef<{ fingerprint: string; key: string } | null>(null);
  const { userData, hasPermission } = useAuth();
  const canEditLeads = hasPermission('Leads', 'edit');
  const canAssign = (userData.isManager === true || userData.isSuperAdmin === true) && canEditLeads;
  const myUserId = userData.id;
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const navigate = useNavigate();
  const query = useQuery({ queryKey: ['commercial-intelligence', 'routing-queue', sourceId], queryFn: () => commercialIntelligenceService.getRoutingQueue({ sourceId }), refetchInterval: 60_000 });
  const owners = useQuery({ queryKey: ['commercial-intelligence', 'routing-owner-options'], queryFn: commercialIntelligenceService.getRoutingOwnerOptions, enabled: canAssign });
  const refreshQueue = () => client.invalidateQueries({ queryKey: ['commercial-intelligence', 'routing-queue'] });

  const mutation = useMutation({
    mutationFn: () => {
      const normalizedReason = reason.trim();
      const fingerprint = `${target!.sourceId}|${ownerUserId}|${target!.version}|${normalizedReason}`;
      if (mutationIntent.current?.fingerprint !== fingerprint) mutationIntent.current = { fingerprint, key: crypto.randomUUID() };
      return commercialIntelligenceService.assignRoutingItem(target!.sourceId, Number(ownerUserId), target!.version, normalizedReason || undefined, mutationIntent.current.key);
    },
    onSuccess: () => { enqueueSnackbar('Lead assigned', { variant: 'success' }); mutationIntent.current = null; setTarget(null); void refreshQueue(); },
    onError: (error: any) => { const conflict = error?.response?.status === 409; enqueueSnackbar(conflict ? 'This queue item changed. Refresh before trying again.' : (error?.response?.data?.error || 'Lead assignment failed'), { variant: conflict ? 'warning' : 'error' }); if (conflict) { mutationIntent.current = null; setTarget(null); void refreshQueue(); } },
  });

  // Claim and release both carry the row version, so two reps racing for the same item produce a
  // 409 for the loser rather than a silent steal. There is nothing to retry on a conflict: the
  // list is reloaded and whoever won is shown.
  const leaseError = (verb: string) => (error: any) => {
    const conflict = error?.response?.status === 409;
    enqueueSnackbar(conflict ? (error?.response?.data?.error || `Someone else changed this item, so it was not ${verb}.`) : `The item could not be ${verb}.`, { variant: conflict ? 'warning' : 'error' });
    void refreshQueue();
  };
  const claim = useMutation({
    mutationFn: (row: RoutingQueueItemDTO) => commercialRoutingService.claimQueueItem(row.sourceId, row.version),
    onSuccess: () => { enqueueSnackbar('Claimed. A manager confirms the claim into ownership.', { variant: 'success' }); void refreshQueue(); },
    onError: leaseError('claimed'),
  });
  const release = useMutation({
    mutationFn: (row: RoutingQueueItemDTO) => commercialRoutingService.releaseQueueItem(row.sourceId, row.version),
    onSuccess: () => { enqueueSnackbar('Released back to the pool', { variant: 'success' }); void refreshQueue(); },
    onError: leaseError('released'),
  });
  const reroute = useMutation({
    mutationFn: (row: RoutingQueueItemDTO) => commercialRoutingService.routeLead(row.leadId),
    onSuccess: result => {
      enqueueSnackbar(result.selectedUserId ? 'Re-routed and assigned' : `Still unassigned: ${result.decisionCode}`, { variant: result.selectedUserId ? 'success' : 'info' });
      void refreshQueue();
    },
    onError: (error: any) => enqueueSnackbar(error?.response?.data?.error || 'The lead could not be re-routed.', { variant: 'error' }),
  });

  const rows = query.data ?? [];
  const busy = mutation.isPending || claim.isPending || release.isPending || reroute.isPending;
  const heldByOther = (row: RoutingQueueItemDTO) => !!row.claimedByUserId && !row.claimExpired && row.claimedByUserId !== myUserId;
  const heldByMe = (row: RoutingQueueItemDTO) => !!row.claimedByUserId && !row.claimExpired && row.claimedByUserId === myUserId;
  const override = !!target?.recommendedOwnerUserId && target.recommendedOwnerUserId !== ownerUserId;
  const canSubmit = ownerUserId !== '' && (!override || reason.trim().length >= 5);
  const closeAssignment = () => { mutationIntent.current = null; setTarget(null); };
  const openAssignment = (row: RoutingQueueItemDTO) => {
    mutationIntent.current = null;
    setTarget(row);
    // A live claim is a rep saying "I am working this". Default to them, then fall back to the
    // engine's recommendation. Neither pre-fills the override reason: if the manager ends up
    // choosing someone other than the recommendation, the justification must still be typed.
    setOwnerUserId((!row.claimExpired && row.claimedByUserId) || row.recommendedOwnerUserId || '');
    setReason('');
  };

  return <PageShell title="Routing queue" subtitle="Unowned inquiries with measured workload and explainable assignment recommendations."><QueryState loading={query.isLoading} error={query.isError} empty={!rows.length} onRetry={() => void query.refetch()} emptyText="No inquiries are waiting for assignment."><ResponsiveTable label="Lead routing queue"><Table size="small"><TableHead><TableRow><TableCell>Nexora Serial</TableCell><TableCell>Customer</TableCell><TableCell>Received / due</TableCell><TableCell>Routing evidence</TableCell><TableCell>Recommended owner</TableCell><TableCell>Claim</TableCell>{canEditLeads && <TableCell>Action</TableCell>}</TableRow></TableHead><TableBody>{rows.map(row => <TableRow hover key={row.sourceId}><TableCell><Button color="inherit" endIcon={<OpenInNewIcon />} onClick={() => navigate(`/procurement/leads/view/${row.leadId}`)}>{row.nexoraSerial}</Button></TableCell><TableCell>{row.customerName || 'Customer unresolved'}</TableCell><TableCell><Typography variant="body2">Received {formatDateTime(row.receivedAt)}</Typography><Typography variant="caption" color={row.overdue ? 'error.main' : 'text.secondary'}>Due {formatDateTime(row.dueAt)}</Typography></TableCell><TableCell><Typography variant="body2">{row.reason}</Typography><Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{routingDecisionSentence(row.recommendationReason)}</Typography><Typography variant="caption" color="text.secondary">{(row.matchConfidence * 100).toFixed(0)}% match | policy {row.policyVersion}</Typography></TableCell><TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{row.recommendedOwnerName || 'No recommendation'}</Typography>{row.recommendedOwnerWorkloadPoints != null && <Typography variant="caption" color="text.secondary">{row.recommendedOwnerWorkloadPoints} workload points | {row.recommendedOwnerCapacityPercent}% capacity</Typography>} {!row.recommendedOwnerAvailable && row.recommendedOwnerUserId && <Chip size="small" color="warning" label="At capacity" />}</TableCell>
    <TableCell>{row.claimedByUserId
      ? <Stack spacing={0.25}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>{heldByMe(row) ? 'Claimed by you' : `Claimed by ${row.claimedByName || `user ${row.claimedByUserId}`}`}</Typography>
          <Typography variant="caption" color={row.claimExpired ? 'text.disabled' : 'text.secondary'}>{row.claimExpired ? `Lease expired ${formatDateTime(row.claimedUntil)} — anyone may claim it` : `Held until ${formatDateTime(row.claimedUntil)}`}</Typography>
        </Stack>
      : <Typography variant="caption" color="text.secondary">Unclaimed</Typography>}
    </TableCell>
    {canEditLeads && <TableCell><Stack direction="row" spacing={0.5} sx={{ flexWrap: 'wrap' }}>
      {heldByMe(row)
        ? <Button size="small" startIcon={<ReleaseIcon />} disabled={busy} onClick={() => release.mutate(row)}>Release</Button>
        : <Button size="small" startIcon={<ClaimIcon />} disabled={busy || heldByOther(row)} onClick={() => claim.mutate(row)}>Claim</Button>}
      {canAssign && <Button size="small" startIcon={<AssignmentIcon />} disabled={busy} onClick={() => openAssignment(row)}>Assign</Button>}
      {canAssign && <Button size="small" color="inherit" startIcon={<RerouteIcon />} disabled={busy} onClick={() => reroute.mutate(row)}>Re-route</Button>}
    </Stack></TableCell>}
  </TableRow>)}</TableBody></Table></ResponsiveTable></QueryState>
    <Dialog open={!!target} onClose={() => !mutation.isPending && closeAssignment()} fullWidth maxWidth="sm"><DialogTitle>Assign {target?.nexoraSerial}</DialogTitle><DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
      {target && !!target.claimedByUserId && !target.claimExpired && <Alert severity="info">{target.claimedByName || `User ${target.claimedByUserId}`} has claimed this item until {formatDateTime(target.claimedUntil)}. A claim reserves the work; confirming it here is what transfers ownership.</Alert>}
      {target?.recommendedOwnerUserId && <Alert severity="info">Recommendation: {target.recommendedOwnerName}. Policy {target.policyVersion}, measured {formatDateTime(target.recommendationMeasuredAt)}.</Alert>}
      {owners.data?.length === 0 && <Alert severity="warning">No user in this business unit holds an eligible governed routing profile, so no assignment can be accepted. Grant one in Sales &gt; Rep directory, then use Re-route.</Alert>}
      <FormControl fullWidth><InputLabel id="routing-owner-label">Owner</InputLabel><Select labelId="routing-owner-label" label="Owner" value={ownerUserId} onChange={event => setOwnerUserId(Number(event.target.value))}>{(owners.data ?? []).map(owner => <MenuItem key={owner.userId} value={owner.userId} disabled={!owner.isAvailable}>{owner.name} - {owner.isAvailable ? `${owner.workload.workloadPoints} points, ${owner.capacityPercent}% capacity` : owner.eligibilityReason}</MenuItem>)}</Select></FormControl>
      {override && <TextField label="Override reason" value={reason} onChange={event => setReason(event.target.value)} required error={reason.length > 0 && reason.trim().length < 5} helperText="Required when choosing someone other than the recommended owner; minimum 5 characters." multiline minRows={2} slotProps={{ htmlInput: { maxLength: 500 } }} />}
    </Stack></DialogContent><DialogActions><Button onClick={closeAssignment} disabled={mutation.isPending}>Cancel</Button><Button variant="contained" startIcon={<AssignmentIcon />} disabled={!canSubmit || mutation.isPending || owners.isLoading} onClick={() => mutation.mutate()}>{override ? 'Confirm override' : 'Assign owner'}</Button></DialogActions></Dialog>
  </PageShell>;
}
