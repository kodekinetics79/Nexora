import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, FormControl, InputLabel, MenuItem, Paper, Select, Stack,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField,
  Typography,
} from '@mui/material';
import {
  Add, Archive, CheckCircleOutlined, EditNote, History, Publish, Restore, Science,
} from '@mui/icons-material';
import { platformGovernanceService, type GovernedArtifactSummary, type GovernedArtifactType } from '../../api/services/platformGovernanceService';

const definitions: Record<GovernedArtifactType, string> = {
  CommercialTaxonomy: JSON.stringify({ documentType: 'CustomerRfq', fieldGroups: [], lineSchema: [], validationRules: [], confidenceThreshold: 0.85, evidenceRequired: true, labels: { en: 'Customer RFQ' } }, null, 2),
  DocumentSkill: JSON.stringify({ taxonomyKey: 'customer-rfq', processingStrategy: 'NativeParser', parserMappings: [], ocrStrategy: 'WhenRequired', validators: [], downstreamAction: 'LeadReconciliation' }, null, 2),
  Model: JSON.stringify({ modelKind: 'LocalSpecialist', purpose: 'DocumentClassification', endpointReference: '', evaluationDatasetKey: '', external: false }, null, 2),
  Rule: JSON.stringify({ condition: 'confidence < 0.85', outcome: 'HumanReview', evidenceRequired: true }, null, 2),
  Dataset: JSON.stringify({ purpose: 'Evaluation', scope: 'Tenant', retentionDays: 365, sourceReferences: [] }, null, 2),
  Connector: JSON.stringify({ connectorType: 'REST', contractVersion: '1.0', baseUrlReference: '', secretReference: '', actions: [], sandbox: true }, null, 2),
  TestSuite: JSON.stringify({ requirements: [], tests: [], environment: 'Sandbox', passThreshold: 1 }, null, 2),
  ReleaseCandidate: JSON.stringify({ releaseVersion: '1.0.0', requirements: [], testSuiteKeys: [], rollbackArtifactVersion: null }, null, 2),
};

const statusColor = (status: string): 'default' | 'info' | 'success' | 'warning' =>
  status === 'Production' ? 'success' : status === 'Test' ? 'info' : status === 'Archived' ? 'default' : 'warning';

interface Props {
  title: string;
  subtitle: string;
  types: GovernedArtifactType[];
}

export default function ArtifactStudioPage({ title, subtitle, types }: Props) {
  const client = useQueryClient();
  const [search, setSearch] = useState('');
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [open, setOpen] = useState(false);
  const [versionOpen, setVersionOpen] = useState(false);
  const [type, setType] = useState<GovernedArtifactType>(types[0]);
  const [name, setName] = useState('');
  const [artifactKey, setArtifactKey] = useState('');
  const [description, setDescription] = useState('');
  const [definitionJson, setDefinitionJson] = useState(definitions[types[0]]);
  const [changeSummary, setChangeSummary] = useState('Initial governed version');

  const list = useQuery({
    queryKey: ['governed-artifacts', types, search],
    queryFn: () => platformGovernanceService.listArtifacts(types, search),
  });
  const detail = useQuery({
    queryKey: ['governed-artifact', selectedId],
    queryFn: () => platformGovernanceService.getArtifact(selectedId!),
    enabled: selectedId !== null,
  });
  const refresh = async (id?: number) => {
    await client.invalidateQueries({ queryKey: ['governed-artifacts'] });
    if (id) await client.invalidateQueries({ queryKey: ['governed-artifact', id] });
  };
  const create = useMutation({
    mutationFn: platformGovernanceService.createArtifact,
    onSuccess: async (result: { artifact: GovernedArtifactSummary }) => {
      setOpen(false);
      setSelectedId(result.artifact.id);
      await refresh(result.artifact.id);
    },
  });
  const transition = useMutation({
    mutationFn: ({ artifact, action, targetVersionNumber }: { artifact: GovernedArtifactSummary; action: 'TEST' | 'PUBLISH' | 'ROLLBACK' | 'ARCHIVE' | 'RESTORE'; targetVersionNumber?: number }) =>
      platformGovernanceService.transitionArtifact(artifact.id, {
        expectedVersion: artifact.version,
        action,
        reason: `${action} approved in ${title}`,
        targetVersionNumber,
      }),
    onSuccess: async (_result, input) => refresh(input.artifact.id),
  });
  const createVersion = useMutation({
    mutationFn: ({ artifact, definition, summary }: { artifact: GovernedArtifactSummary; definition: string; summary: string }) =>
      platformGovernanceService.createVersion(artifact.id, {
        expectedVersion: artifact.version,
        definitionJson: definition,
        changeSummary: summary,
      }),
    onSuccess: async (_result, input) => {
      setVersionOpen(false);
      await refresh(input.artifact.id);
    },
  });
  const rows = useMemo(() => list.data ?? [], [list.data]);
  const selected = detail.data?.artifact;

  const startCreate = () => {
    const nextType = types[0];
    setType(nextType);
    setDefinitionJson(definitions[nextType]);
    setName(''); setArtifactKey(''); setDescription(''); setChangeSummary('Initial governed version');
    setOpen(true);
  };

  const startVersion = () => {
    const current = detail.data?.versions.find((version) => version.versionNumber === selected?.currentVersionNumber);
    setDefinitionJson(current?.definitionJson ?? definitions[selected?.artifactType ?? types[0]]);
    setChangeSummary('Describe the governed change');
    setVersionOpen(true);
  };

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', md: 'row' }} sx={{ justifyContent: 'space-between', gap: 2, mb: 2 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 750 }}>{title}</Typography>
          <Typography variant="body2" color="text.secondary">{subtitle}</Typography>
        </Box>
        <Button variant="contained" startIcon={<Add />} onClick={startCreate}>Create governed artifact</Button>
      </Stack>

      {(list.isError || create.isError || createVersion.isError || transition.isError) && (
        <Alert severity="error" sx={{ mb: 2 }}>The governance request could not be completed. Review the definition, permissions, and current version.</Alert>
      )}

      <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
        <TextField size="small" label="Search name or key" value={search}
          onChange={(event) => setSearch(event.target.value)} sx={{ width: { xs: '100%', sm: 360 } }} />
      </Paper>

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 1.4fr) minmax(340px, .8fr)' }, gap: 2 }}>
        <TableContainer component={Paper} variant="outlined">
          {list.isLoading ? <Box sx={{ p: 6, textAlign: 'center' }}><CircularProgress /></Box> : (
            <Table size="small">
              <TableHead><TableRow><TableCell>Name</TableCell><TableCell>Type</TableCell><TableCell>Status</TableCell><TableCell>Version</TableCell><TableCell>Updated</TableCell></TableRow></TableHead>
              <TableBody>
                {rows.map((row) => (
                  <TableRow key={row.id} hover selected={selectedId === row.id} onClick={() => setSelectedId(row.id)} sx={{ cursor: 'pointer' }}>
                    <TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{row.name}</Typography><Typography variant="caption" color="text.secondary">{row.artifactKey}</Typography></TableCell>
                    <TableCell>{row.artifactType}</TableCell>
                    <TableCell><Chip size="small" color={statusColor(row.status)} label={row.status} /></TableCell>
                    <TableCell>v{row.currentVersionNumber}</TableCell>
                    <TableCell>{new Date(row.updatedOn).toLocaleString()}</TableCell>
                  </TableRow>
                ))}
                {!rows.length && <TableRow><TableCell colSpan={5}><Box sx={{ py: 6, textAlign: 'center' }}><Typography sx={{ fontWeight: 700 }}>No governed artifacts yet</Typography><Typography variant="body2" color="text.secondary">Create the first tenant-owned version to begin.</Typography></Box></TableCell></TableRow>}
              </TableBody>
            </Table>
          )}
        </TableContainer>

        <Paper variant="outlined" sx={{ p: 2, minHeight: 360 }}>
          {!selected ? <Box sx={{ py: 8, textAlign: 'center' }}><History color="disabled" /><Typography color="text.secondary">Select an artifact to inspect versions and controls.</Typography></Box> : (
            <Stack sx={{ gap: 2 }}>
              <Box><Typography variant="h6" sx={{ fontWeight: 750 }}>{selected.name}</Typography><Typography variant="body2" color="text.secondary">{selected.description || 'No description'}</Typography></Box>
              <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap' }}>
                {selected.status !== 'Archived' && <Button size="small" variant="outlined" startIcon={<EditNote />} onClick={startVersion}>Create version</Button>}
                {selected.status === 'Draft' && <Button size="small" variant="outlined" startIcon={<Science />} onClick={() => transition.mutate({ artifact: selected, action: 'TEST' })}>Send to test</Button>}
                {selected.status === 'Test' && <Button size="small" variant="contained" startIcon={<Publish />} onClick={() => transition.mutate({ artifact: selected, action: 'PUBLISH' })}>Publish</Button>}
                {selected.status !== 'Archived' && <Button size="small" color="inherit" startIcon={<Archive />} onClick={() => transition.mutate({ artifact: selected, action: 'ARCHIVE' })}>Archive</Button>}
                {selected.status === 'Archived' && <Button size="small" startIcon={<Restore />} onClick={() => transition.mutate({ artifact: selected, action: 'RESTORE' })}>Restore</Button>}
                {detail.data?.versions.filter((version) => version.publishedOn && version.versionNumber !== selected.productionVersionNumber).map((version) => (
                  <Button key={version.id} size="small" color="warning" startIcon={<Restore />} onClick={() => transition.mutate({ artifact: selected, action: 'ROLLBACK', targetVersionNumber: version.versionNumber })}>Rollback to v{version.versionNumber}</Button>
                ))}
              </Stack>
              <Divider />
              <Typography variant="subtitle2">Version history</Typography>
              {detail.data?.versions.map((version) => <Box key={version.id}><Stack direction="row" sx={{ justifyContent: 'space-between' }}><Typography variant="body2" sx={{ fontWeight: 700 }}>v{version.versionNumber} · {version.status}</Typography>{version.publishedOn && <CheckCircleOutlined color="success" fontSize="small" />}</Stack><Typography variant="caption" color="text.secondary">{version.changeSummary}</Typography></Box>)}
              <Divider />
              <Typography variant="subtitle2">Immutable activity</Typography>
              {detail.data?.events.slice(0, 6).map((event) => <Typography key={event.id} variant="caption">{new Date(event.occurredOn).toLocaleString()} · {event.action} · {event.reason}</Typography>)}
            </Stack>
          )}
        </Paper>
      </Box>

      <Dialog open={open} onClose={() => setOpen(false)} fullWidth maxWidth="md">
        <DialogTitle>Create governed artifact</DialogTitle>
        <DialogContent><Stack sx={{ pt: 1, gap: 2 }}>
          <FormControl size="small"><InputLabel>Artifact type</InputLabel><Select label="Artifact type" value={type} onChange={(event) => { const next = event.target.value as GovernedArtifactType; setType(next); setDefinitionJson(definitions[next]); }}>{types.map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}</Select></FormControl>
          <TextField label="Name" value={name} onChange={(event) => setName(event.target.value)} required />
          <TextField label="Stable key" value={artifactKey} onChange={(event) => setArtifactKey(event.target.value)} required helperText="Letters, numbers, hyphens, underscores and periods" />
          <TextField label="Description" value={description} onChange={(event) => setDescription(event.target.value)} multiline minRows={2} />
          <TextField label="Definition JSON" value={definitionJson} onChange={(event) => setDefinitionJson(event.target.value)} multiline minRows={10} required slotProps={{ htmlInput: { spellCheck: false } }} />
          <TextField label="Change summary" value={changeSummary} onChange={(event) => setChangeSummary(event.target.value)} required />
        </Stack></DialogContent>
        <DialogActions><Button onClick={() => setOpen(false)}>Cancel</Button><Button variant="contained" disabled={!name.trim() || !artifactKey.trim() || create.isPending} onClick={() => create.mutate({ artifactType: type, artifactKey, name, description, definitionJson, changeSummary })}>Create draft</Button></DialogActions>
      </Dialog>

      <Dialog open={versionOpen} onClose={() => setVersionOpen(false)} fullWidth maxWidth="md">
        <DialogTitle>Create version {selected ? `for ${selected.name}` : ''}</DialogTitle>
        <DialogContent><Stack sx={{ pt: 1, gap: 2 }}>
          <Alert severity="info">The current version remains immutable. This creates a new draft for testing and approval.</Alert>
          <TextField label="Definition JSON" value={definitionJson} onChange={(event) => setDefinitionJson(event.target.value)} multiline minRows={12} required slotProps={{ htmlInput: { spellCheck: false } }} />
          <TextField label="Change summary" value={changeSummary} onChange={(event) => setChangeSummary(event.target.value)} required />
        </Stack></DialogContent>
        <DialogActions>
          <Button onClick={() => setVersionOpen(false)}>Cancel</Button>
          <Button variant="contained" disabled={!selected || !changeSummary.trim() || createVersion.isPending}
            onClick={() => selected && createVersion.mutate({ artifact: selected, definition: definitionJson, summary: changeSummary })}>Create draft version</Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
