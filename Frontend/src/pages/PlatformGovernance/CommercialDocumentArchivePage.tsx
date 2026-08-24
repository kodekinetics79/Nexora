import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, FormControl, InputLabel, MenuItem, Paper, Select, Stack, Tab, Tabs,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import { FileOpen, Gavel, LockOpen, Search } from '@mui/icons-material';
import ArtifactStudioPage from './ArtifactStudioPage';
import {
  platformGovernanceService, type ArchiveDocumentItem,
} from '../../api/services/platformGovernanceService';

const statusColor = (status: string): 'default' | 'success' | 'warning' | 'error' =>
  status === 'Completed' || status === 'Cleared' ? 'success'
    : status === 'Failed' || status === 'Rejected' ? 'error'
      : status === 'AwaitingSecurityScan' || status === 'ReviewRequired' ? 'warning' : 'default';

export default function CommercialDocumentArchivePage() {
  const client = useQueryClient();
  const [tab, setTab] = useState(0);
  const [search, setSearch] = useState('');
  const [documentType, setDocumentType] = useState('');
  const [status, setStatus] = useState('');
  const [sort, setSort] = useState('newest');
  const [selected, setSelected] = useState<ArchiveDocumentItem | null>(null);
  const [reason, setReason] = useState('Reviewed in the commercial document archive.');

  const archive = useQuery({
    queryKey: ['commercial-document-archive', search, documentType, status, sort],
    queryFn: () => platformGovernanceService.searchArchive({ search, documentType, status, sort }),
    enabled: tab === 0,
  });
  const govern = useMutation({
    mutationFn: ({ item, action }: { item: ArchiveDocumentItem; action: string }) =>
      platformGovernanceService.governArchiveDocument(item, action, reason),
    onSuccess: async (result, variables) => {
      setSelected({ ...variables.item, legalHold: result.legalHold,
        deletionRequested: result.deletionRequested, governanceVersion: result.governanceVersion });
      await client.invalidateQueries({ queryKey: ['commercial-document-archive'] });
    },
  });
  const rows = useMemo(() => archive.data?.items ?? [], [archive.data]);

  const openEvidence = (item: ArchiveDocumentItem) => {
    setSelected(item);
    govern.mutate({ item, action: 'ACCESS_RECORDED' });
  };

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Box sx={{ mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 750 }}>Commercial Document Archive</Typography>
        <Typography variant="body2" color="text.secondary">
          Search immutable intake evidence and follow its commercial lineage without exposing stored document bytes.
        </Typography>
      </Box>
      <Tabs value={tab} onChange={(_, value) => setTab(value)} sx={{ mb: 2 }}>
        <Tab label="Archive & Search" />
        <Tab label="Retention Policies" />
      </Tabs>

      {tab === 1 ? (
        <ArtifactStudioPage title="Retention & Legal Hold Policies"
          subtitle="Version, test, publish and roll back tenant-owned archive controls."
          types={['ArchivePolicy']} />
      ) : (
        <>
          <Paper variant="outlined" sx={{ p: 2, mb: 2 }}>
            <Stack direction={{ xs: 'column', md: 'row' }} sx={{ gap: 1.5, alignItems: { md: 'center' } }}>
              <TextField size="small" label="Search evidence or commercial reference" value={search}
                onChange={(event) => setSearch(event.target.value)}
                slotProps={{ input: { startAdornment: <Search fontSize="small" sx={{ mr: 1 }} /> } }}
                sx={{ flex: 1, minWidth: 260 }} />
              <FormControl size="small" sx={{ minWidth: 190 }}><InputLabel>Document type</InputLabel>
                <Select label="Document type" value={documentType} onChange={(event) => setDocumentType(event.target.value)}>
                  <MenuItem value="">All types</MenuItem>
                  {['CustomerRfq', 'CustomerRfqRevision', 'SupplierQuote', 'CustomerQuoteResponse',
                    'CustomerOrder', 'PurchaseOrder', 'SupplierInvoice', 'InventoryFile', 'Unknown']
                    .map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}
                </Select>
              </FormControl>
              <FormControl size="small" sx={{ minWidth: 170 }}><InputLabel>Status</InputLabel>
                <Select label="Status" value={status} onChange={(event) => setStatus(event.target.value)}>
                  <MenuItem value="">All statuses</MenuItem>
                  {['Accepted', 'AwaitingSecurityScan', 'Processing', 'ReviewRequired', 'Resolved',
                    'Completed', 'Failed', 'Rejected'].map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}
                </Select>
              </FormControl>
              <FormControl size="small" sx={{ minWidth: 150 }}><InputLabel>Sort</InputLabel>
                <Select label="Sort" value={sort} onChange={(event) => setSort(event.target.value)}>
                  <MenuItem value="newest">Newest first</MenuItem>
                  <MenuItem value="oldest">Oldest first</MenuItem>
                  <MenuItem value="filename">Filename</MenuItem>
                </Select>
              </FormControl>
            </Stack>
          </Paper>

          {archive.isError && <Alert severity="error" sx={{ mb: 2 }}>Archive search failed. Check the tenant context and evidence store status.</Alert>}
          {govern.isError && <Alert severity="error" sx={{ mb: 2 }}>The governance action was not accepted. Refresh the document and try again.</Alert>}
          {archive.data && <Alert severity="info" sx={{ mb: 2 }}>{archive.data.searchScope}</Alert>}

          <TableContainer component={Paper} variant="outlined">
            {archive.isLoading ? <Box sx={{ p: 7, textAlign: 'center' }}><CircularProgress /></Box> : (
              <Table size="small">
                <TableHead><TableRow>
                  <TableCell>Document</TableCell><TableCell>Ingested</TableCell><TableCell>Type</TableCell>
                  <TableCell>Processing</TableCell><TableCell>Commercial lineage</TableCell>
                  <TableCell>Controls</TableCell><TableCell align="right">Action</TableCell>
                </TableRow></TableHead>
                <TableBody>
                  {rows.map((row) => <TableRow key={row.occurrenceId} hover>
                    <TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{row.fileName}</Typography>
                      <Typography variant="caption" color="text.secondary">{row.mimeType} · {(row.byteSize / 1024).toFixed(1)} KB</Typography></TableCell>
                    <TableCell>{new Date(row.ingestedOn).toLocaleString()}</TableCell>
                    <TableCell><Chip size="small" label={row.documentType} /></TableCell>
                    <TableCell><Stack direction="row" sx={{ gap: .5, flexWrap: 'wrap' }}>
                      <Chip size="small" color={statusColor(row.securityStatus)} label={row.securityStatus} />
                      <Chip size="small" color={statusColor(row.processingStatus)} label={row.processingStatus} />
                    </Stack></TableCell>
                    <TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{row.nexoraSerial ?? 'Not linked'}</Typography>
                      <Typography variant="caption" color="text.secondary">{row.customerRfq ?? (row.commercialLinks.join(' · ') || 'No downstream record')}</Typography></TableCell>
                    <TableCell><Stack direction="row" sx={{ gap: .5, flexWrap: 'wrap' }}>
                      {row.legalHold
                        ? <Chip size="small" color="warning" label="Legal hold" />
                        : <Chip size="small" label="Standard retention" />}
                    </Stack></TableCell>
                    <TableCell align="right"><Button size="small" startIcon={<FileOpen />} onClick={() => openEvidence(row)}>Open evidence</Button></TableCell>
                  </TableRow>)}
                  {!rows.length && <TableRow><TableCell colSpan={7}><Box sx={{ py: 7, textAlign: 'center' }}>
                    <Typography sx={{ fontWeight: 700 }}>No documents match these filters</Typography>
                    <Typography variant="body2" color="text.secondary">Change the search or ingest authorized commercial evidence.</Typography>
                  </Box></TableCell></TableRow>}
                </TableBody>
              </Table>
            )}
          </TableContainer>
          {archive.data && <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
            {archive.data.total} evidence occurrences · definition {archive.data.definitionVersion}
          </Typography>}
        </>
      )}

      <Dialog open={selected !== null} onClose={() => setSelected(null)} fullWidth maxWidth="md">
        <DialogTitle>Evidence lineage</DialogTitle>
        <DialogContent>{selected && <Stack sx={{ gap: 2, pt: 1 }}>
          <Box><Typography variant="subtitle1" sx={{ fontWeight: 750 }}>{selected.fileName}</Typography>
            <Typography variant="body2" color="text.secondary">Occurrence {selected.occurrenceId} · document {selected.sourceDocumentId}</Typography></Box>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)' }, gap: 1.5 }}>
            <Box><Typography variant="caption" color="text.secondary">Immutable SHA-256</Typography><Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>{selected.contentHash}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Nexora Serial</Typography><Typography variant="body2">{selected.nexoraSerial ?? 'Not linked'}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Customer RFQ</Typography><Typography variant="body2">{selected.customerRfq ?? 'Not linked'}</Typography></Box>
            <Box><Typography variant="caption" color="text.secondary">Classification</Typography><Typography variant="body2">{selected.documentType} · {selected.reviewStatus}</Typography></Box>
          </Box>
          <Box><Typography variant="caption" color="text.secondary">Commercial links</Typography>
            <Stack direction="row" sx={{ gap: .75, mt: .5, flexWrap: 'wrap' }}>{selected.commercialLinks.length
              ? selected.commercialLinks.map((link) => <Chip key={link} size="small" label={link} />)
              : <Typography variant="body2">No downstream records yet</Typography>}</Stack></Box>
          <TextField label="Governance reason" value={reason} onChange={(event) => setReason(event.target.value)} multiline minRows={2} />
          <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 1, flexWrap: 'wrap' }}>
            <Button variant="outlined" startIcon={selected.legalHold ? <LockOpen /> : <Gavel />}
              onClick={() => govern.mutate({ item: selected, action: selected.legalHold ? 'HOLD_RELEASED' : 'HOLD_APPLIED' })}>
              {selected.legalHold ? 'Release legal hold' : 'Apply legal hold'}
            </Button>
            <Button variant="outlined" onClick={() => govern.mutate({ item: selected, action: 'EXPORT_REQUESTED' })}>Request metadata export</Button>
            {/*
              "Request deletion review" used to live here. It had no approver anywhere in the
              product, so every request was permanent — and the retention purge read the resulting
              flag as an EXCLUSION, which meant clicking it removed the document from the only
              deletion a tenant could actually perform. A button whose sole effect is to block the
              thing it offers is worse than no button, so it is gone rather than reworded.
            */}
          </Stack>
          <Alert severity="info">
            These controls create a permanent record. Nothing is deleted from this screen — to free
            up space, use Storage &amp; Retention.
          </Alert>
        </Stack>}</DialogContent>
        <DialogActions><Button onClick={() => setSelected(null)}>Close</Button></DialogActions>
      </Dialog>
    </Box>
  );
}
