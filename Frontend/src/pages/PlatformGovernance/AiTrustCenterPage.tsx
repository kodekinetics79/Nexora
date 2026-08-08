import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, Chip, CircularProgress, Paper, Stack, Tab, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, Tabs, Typography,
} from '@mui/material';
import { Security, WarningAmber } from '@mui/icons-material';
import { platformGovernanceService } from '../../api/services/platformGovernanceService';

export default function AiTrustCenterPage() {
  const [tab, setTab] = useState(0);
  const view = useQuery({ queryKey: ['ai-trust'], queryFn: platformGovernanceService.getAiTrust });

  if (view.isLoading) return <Box sx={{ py: 12, textAlign: 'center' }}><CircularProgress /></Box>;
  if (!view.data) return <Alert severity="error">AI governance data is unavailable for this tenant.</Alert>;
  const { policy } = view.data;
  const { usage, requests, audit } = view.data;
  const estimatedCost = Object.entries(usage.estimatedExternalCost).map(([currency, amount]) => `${currency} ${amount.toFixed(4)}`).join(', ') || 'No priced external usage';

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', md: 'row' }} sx={{ justifyContent: 'space-between', gap: 2, mb: 2 }}>
        <Box><Typography variant="h5" sx={{ fontWeight: 750 }}>AI Trust & Governance</Typography><Typography variant="body2" color="text.secondary">Effective policy, egress controls, usage and immutable accountability</Typography></Box>
      </Stack>

      <Alert severity="info" sx={{ mb: 2 }}>
        AI policy and provider authorization are managed by a Platform Admin Owner. Tenant administrators can
        inspect the effective controls and evidence here; request policy changes through your Platform Admin.
      </Alert>
      {!policy.isEnabled && <Alert severity="error" icon={<Security />} sx={{ mb: 2 }}>Emergency shutdown is active. AI processing is disabled for this tenant.</Alert>}
      {usage.dependencyCeilingBreached && <Alert severity="warning" icon={<WarningAmber />} sx={{ mb: 2 }}>External dependency is {usage.externalDependencyPercent.toFixed(2)}%, above the {policy.externalDependencyCeilingPercent.toFixed(2)}% ceiling that governs unauthorized external calls. Calls under an active provider authorization are exempt from the ceiling.</Alert>}

      <Paper variant="outlined" sx={{ mb: 2 }}><Tabs value={tab} onChange={(_event, value) => setTab(value)} variant="scrollable" scrollButtons="auto"><Tab label="Overview" /><Tab label="Policy" /><Tab label="Request ledger" /><Tab label="Audit history" /></Tabs></Paper>

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

      {tab === 3 && <TableContainer component={Paper} variant="outlined"><Table size="small"><TableHead><TableRow><TableCell>Occurred</TableCell><TableCell>Action</TableCell><TableCell>Reason</TableCell><TableCell>Actor</TableCell></TableRow></TableHead><TableBody>
        {audit.map((event) => <TableRow key={event.id}><TableCell>{new Date(event.occurredOn).toLocaleString()}</TableCell><TableCell>{event.action}</TableCell><TableCell>{event.reason}</TableCell><TableCell>User {event.actorUserId}</TableCell></TableRow>)}
        {!audit.length && <TableRow><TableCell colSpan={4} align="center" sx={{ py: 6 }}>No policy changes recorded.</TableCell></TableRow>}
      </TableBody></Table></TableContainer>}
    </Box>
  );
}
