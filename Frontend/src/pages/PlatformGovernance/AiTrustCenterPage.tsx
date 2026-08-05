import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, FormControlLabel, Paper, Stack, Switch, Tab, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, Tabs, TextField, Typography,
} from '@mui/material';
import { Edit, Restore, Security, WarningAmber } from '@mui/icons-material';
import {
  platformGovernanceService, type AiTrustPolicy,
} from '../../api/services/platformGovernanceService';

const nullableNumber = (value: string) => value.trim() ? Number(value) : null;

export default function AiTrustCenterPage() {
  const client = useQueryClient();
  const [tab, setTab] = useState(0);
  const [editOpen, setEditOpen] = useState(false);
  const [policy, setPolicy] = useState<AiTrustPolicy | null>(null);
  const [reason, setReason] = useState('');
  const view = useQuery({ queryKey: ['ai-trust'], queryFn: platformGovernanceService.getAiTrust });
  useEffect(() => { if (view.data) setPolicy(view.data.policy); }, [view.data]);
  const refresh = async () => { setEditOpen(false); setReason(''); await client.invalidateQueries({ queryKey: ['ai-trust'] }); };
  const update = useMutation({
    mutationFn: () => platformGovernanceService.updateAiTrustPolicy(policy!, reason),
    onSuccess: refresh,
  });
  const rollback = useMutation({
    mutationFn: (auditEventId: number) => platformGovernanceService.rollbackAiTrustPolicy(view.data!.policy, auditEventId, reason || 'Restore prior approved AI policy'),
    onSuccess: refresh,
  });

  if (view.isLoading) return <Box sx={{ py: 12, textAlign: 'center' }}><CircularProgress /></Box>;
  if (!view.data || !policy) return <Alert severity="error">AI governance data is unavailable for this tenant.</Alert>;
  const { usage, requests, audit } = view.data;
  const estimatedCost = Object.entries(usage.estimatedExternalCost).map(([currency, amount]) => `${currency} ${amount.toFixed(4)}`).join(', ') || 'No priced external usage';

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', md: 'row' }} sx={{ justifyContent: 'space-between', gap: 2, mb: 2 }}>
        <Box><Typography variant="h5" sx={{ fontWeight: 750 }}>AI Trust & Governance</Typography><Typography variant="body2" color="text.secondary">Tenant policy, egress controls, usage and immutable accountability</Typography></Box>
        <Button variant="contained" startIcon={<Edit />} onClick={() => { setPolicy(view.data.policy); setReason(''); setEditOpen(true); }}>Edit policy</Button>
      </Stack>

      {(update.isError || rollback.isError) && <Alert severity="error" sx={{ mb: 2 }}>The policy change could not be applied. Refresh the current version and review all safeguards.</Alert>}
      {!policy.isEnabled && <Alert severity="error" icon={<Security />} sx={{ mb: 2 }}>Emergency shutdown is active. AI processing is disabled for this tenant.</Alert>}
      {usage.dependencyCeilingBreached && <Alert severity="warning" icon={<WarningAmber />} sx={{ mb: 2 }}>External dependency is {usage.externalDependencyPercent.toFixed(2)}%, above the {policy.externalDependencyCeilingPercent.toFixed(2)}% ceiling that governs unauthorized external calls. Calls under an active provider authorization are exempt from the ceiling.</Alert>}

      <Paper variant="outlined" sx={{ mb: 2 }}><Tabs value={tab} onChange={(_event, value) => setTab(value)} variant="scrollable" scrollButtons="auto"><Tab label="Overview" /><Tab label="Policy" /><Tab label="Request ledger" /><Tab label="Audit & rollback" /></Tabs></Paper>

      {tab === 0 && <>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', lg: 'repeat(4, 1fr)' }, gap: 1.5, mb: 2 }}>
          {[['Monthly requests', usage.requests], ['Local / external', `${usage.localRequests} / ${usage.externalRequests}`], ['Tokens settled', usage.settledTokens.toLocaleString()], ['Estimated external cost', estimatedCost]].map(([label, value]) => <Paper key={label} variant="outlined" sx={{ p: 2 }}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="h6" sx={{ fontWeight: 750 }}>{value}</Typography></Paper>)}
        </Box>
        <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="subtitle1" sx={{ fontWeight: 750, mb: 1 }}>Trust posture</Typography>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(3, 1fr)' }, gap: 2 }}>
            <Box><Typography variant="caption" color="text.secondary">Inference posture</Typography><Typography>{view.data.inferencePosture === 'LocalFirst' ? 'Local-first (no third-party egress)' : 'External, allow-list authorized'}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Processing</Typography><Typography>{policy.isEnabled ? 'Enabled' : 'Emergency shutdown'}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">External fallback</Typography><Typography>{policy.externalProcessingAllowed ? 'Policy controlled' : 'Disabled'}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Egress</Typography><Typography>{policy.egressPolicy}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Residency</Typography><Typography>{policy.dataResidency}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Retention</Typography><Typography>{policy.retentionDays} days</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Violations</Typography><Typography>{usage.deniedRequests + usage.injectionDetections} denied or injection flagged</Typography></Box>
          </Box>
        </Paper>
      </>}

      {tab === 1 && <Paper variant="outlined" sx={{ p: 2 }}><Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(2, 1fr)' }, gap: 2 }}>
        {[['Allowed purposes', policy.allowedPurposes], ['Provider allowlist', policy.allowedProvider || 'No external provider'], ['Model allowlist', policy.allowedModel || 'No external model'], ['Data classifications', policy.allowedDataClassifications], ['Redaction', policy.redactionRequired ? 'Required' : 'Not required'], ['Privacy review', policy.privacyReviewRequired ? 'Required' : 'Not required'], ['Input/output audit', policy.inputOutputAuditAllowed ? 'Permitted by policy' : 'Hashes and metadata only'], ['Document token cap', policy.maxTokensPerDocument?.toLocaleString() || 'No cap'], ['Monthly soft / hard cap', `${policy.monthlySoftTokenLimit?.toLocaleString() || 'None'} / ${policy.monthlyHardTokenLimit?.toLocaleString() || 'None'}`], ['Policy version', `v${policy.version}`]].map(([label, value]) => <Box key={label}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body2" sx={{ fontWeight: 650 }}>{value}</Typography></Box>)}
      </Box></Paper>}

      {tab === 2 && <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Created</TableCell><TableCell>Operation</TableCell><TableCell>Path</TableCell><TableCell>Provider / model</TableCell><TableCell>Status</TableCell><TableCell>Tokens</TableCell><TableCell>Cost status</TableCell></TableRow></TableHead><TableBody>
        {requests.map((request) => <TableRow key={request.id}><TableCell>{new Date(request.createdOn).toLocaleString()}</TableCell><TableCell>{request.operation}<Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Prompt {request.promptVersion}</Typography></TableCell><TableCell><Chip size="small" color={request.providerClass === 'External' ? 'warning' : 'success'} label={request.providerClass} /></TableCell><TableCell>{request.provider}<Typography variant="caption" sx={{ display: 'block' }}>{request.model}</Typography></TableCell><TableCell>{request.status}{request.injectionDetected && <Chip size="small" color="error" label="Injection" sx={{ ml: 1 }} />}</TableCell><TableCell>{(request.inputTokens + request.outputTokens).toLocaleString()}</TableCell><TableCell>{request.estimatedCost != null ? `${request.costCurrency} ${request.estimatedCost.toFixed(4)}` : request.costStatus}</TableCell></TableRow>)}
        {!requests.length && <TableRow><TableCell colSpan={7} align="center" sx={{ py: 6 }}>No AI requests in the current monthly cohort.</TableCell></TableRow>}
      </TableBody></Table></TableContainer>}

      {tab === 3 && <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Occurred</TableCell><TableCell>Action</TableCell><TableCell>Reason</TableCell><TableCell>Actor</TableCell><TableCell align="right">Control</TableCell></TableRow></TableHead><TableBody>
        {audit.map((event) => <TableRow key={event.id}><TableCell>{new Date(event.occurredOn).toLocaleString()}</TableCell><TableCell>{event.action}</TableCell><TableCell>{event.reason}</TableCell><TableCell>User {event.actorUserId}</TableCell><TableCell align="right"><Button size="small" startIcon={<Restore />} disabled={rollback.isPending} onClick={() => rollback.mutate(event.id)}>Restore prior state</Button></TableCell></TableRow>)}
        {!audit.length && <TableRow><TableCell colSpan={5} align="center" sx={{ py: 6 }}>No policy changes recorded.</TableCell></TableRow>}
      </TableBody></Table></TableContainer>}

      <Dialog open={editOpen} onClose={() => setEditOpen(false)} fullWidth maxWidth="md"><DialogTitle>Edit AI trust policy</DialogTitle><DialogContent><Stack sx={{ pt: 1, gap: 2 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 2 }}><FormControlLabel control={<Switch checked={policy.isEnabled} onChange={(_event, checked) => setPolicy({ ...policy, isEnabled: checked })} />} label="AI processing enabled" /><FormControlLabel control={<Switch checked={policy.externalProcessingAllowed} onChange={(_event, checked) => setPolicy({ ...policy, externalProcessingAllowed: checked })} />} label="External processing allowed" /><FormControlLabel control={<Switch checked={policy.redactionRequired} onChange={(_event, checked) => setPolicy({ ...policy, redactionRequired: checked })} />} label="Redaction required" /></Stack>
        <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 2 }}><FormControlLabel control={<Switch checked={policy.privacyReviewRequired} onChange={(_event, checked) => setPolicy({ ...policy, privacyReviewRequired: checked })} />} label="Privacy review required" /><FormControlLabel control={<Switch checked={policy.inputOutputAuditAllowed} onChange={(_event, checked) => setPolicy({ ...policy, inputOutputAuditAllowed: checked })} />} label="Input/output audit permitted" /></Stack>
        <TextField label="Allowed purposes" value={policy.allowedPurposes} onChange={(event) => setPolicy({ ...policy, allowedPurposes: event.target.value })} required />
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 2 }}><TextField label="Allowed provider" value={policy.allowedProvider ?? ''} onChange={(event) => setPolicy({ ...policy, allowedProvider: event.target.value || null })} /><TextField label="Allowed model" value={policy.allowedModel ?? ''} onChange={(event) => setPolicy({ ...policy, allowedModel: event.target.value || null })} /><TextField label="Unauthorized external dependency ceiling (%)" helperText="Applies to external calls without an active provider authorization; authorized calls are exempt" type="number" value={policy.externalDependencyCeilingPercent} onChange={(event) => setPolicy({ ...policy, externalDependencyCeilingPercent: Number(event.target.value) })} /><TextField label="Document token cap" type="number" value={policy.maxTokensPerDocument ?? ''} onChange={(event) => setPolicy({ ...policy, maxTokensPerDocument: nullableNumber(event.target.value) })} /><TextField label="Monthly soft token cap" type="number" value={policy.monthlySoftTokenLimit ?? ''} onChange={(event) => setPolicy({ ...policy, monthlySoftTokenLimit: nullableNumber(event.target.value) })} /><TextField label="Monthly hard token cap" type="number" value={policy.monthlyHardTokenLimit ?? ''} onChange={(event) => setPolicy({ ...policy, monthlyHardTokenLimit: nullableNumber(event.target.value) })} /><TextField label="Data classifications" value={policy.allowedDataClassifications} onChange={(event) => setPolicy({ ...policy, allowedDataClassifications: event.target.value })} /><TextField label="Egress policy" value={policy.egressPolicy} onChange={(event) => setPolicy({ ...policy, egressPolicy: event.target.value })} /><TextField label="Data residency" value={policy.dataResidency} onChange={(event) => setPolicy({ ...policy, dataResidency: event.target.value })} /><TextField label="Retention days" type="number" value={policy.retentionDays} onChange={(event) => setPolicy({ ...policy, retentionDays: Number(event.target.value) })} /></Box>
        <TextField label="Change reason" value={reason} onChange={(event) => setReason(event.target.value)} multiline minRows={2} required />
      </Stack></DialogContent><DialogActions><Button onClick={() => setEditOpen(false)}>Cancel</Button><Button variant="contained" disabled={!reason.trim() || update.isPending} onClick={() => update.mutate()}>Save governed policy</Button></DialogActions></Dialog>
    </Box>
  );
}
