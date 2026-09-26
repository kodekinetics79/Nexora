import { Fragment, useMemo, useState } from 'react';
import { useMutation, useQueries, useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Collapse,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TableSortLabel,
  Tooltip,
  Typography,
} from '@mui/material';
import { ExpandLess, ExpandMore, OpenInNew, Refresh } from '@mui/icons-material';
import leadService, { type DuplicateUploadDTO, type LeadResponseDTO } from '../../api/services/leadService';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import RefreshFailedNotice from '../../components/common/RefreshFailedNotice';
import { useAuth } from '../../context/AuthContext';
import { formatDateTimeSafe } from '../../utils/dates';
import { statusLabel } from '../../utils/statusLabels';

/**
 * DUPLICATE UPLOADS — the rep's view first, the accountant's figures behind a fold.
 *
 * This screen answers one question for a salesperson: "this file came in again — which inquiry is
 * it a copy of, and who has it?" Each row therefore says the file, when it arrived, what kind of
 * copy it is, and "Same as <RFQ no> · <customer> · owned by <name>", with one button that opens
 * the original. Occurrence numbers, batch ids, hashing milliseconds, physical/logical bytes and
 * six-decimal costs are kept — finance reads them — but behind a per-row "Details" fold, because a
 * rep never needs them and reading past them cost the one sentence that mattered.
 *
 * The duplicates endpoint carries only the ORIGINAL's lead id and serial. Its RFQ number, customer
 * and owner are read from the lead itself, once per distinct original, so the sentence is built
 * from what the product already knows rather than asking the server for a new shape.
 */

const bytes = (value: number): string => value >= 1024 * 1024
  ? `${(value / (1024 * 1024)).toFixed(2)} MB`
  : `${(value / 1024).toFixed(1)} KB`;

/** Copies still waiting on a security verdict; the list re-reads itself while any of these exist. */
const HELD_TYPES: ReadonlySet<string> = new Set([
  'EXACT_DUPLICATE_PENDING_SECURITY',
  'DUPLICATE_RESCAN_REQUIRED',
  'SECURITY_SCAN_BLOCKED',
]);

/** What kind of copy this is, in a rep's words. Anything the server adds later falls back to the shared label. */
export const duplicateKind = (type: string): { label: string; held: boolean } => {
  switch ((type ?? '').trim().toUpperCase()) {
    case 'EXACT_DUPLICATE_CONFIRMED': return { label: 'Same file uploaded again', held: false };
    case 'BUSINESS_DUPLICATE_CONFIRMED': return { label: 'Same inquiry, sent again', held: false };
    case 'EXACT_DUPLICATE_PENDING_SECURITY': return { label: 'Same file, scan still running', held: true };
    case 'DUPLICATE_RESCAN_REQUIRED': return { label: 'Same file, needs a fresh scan', held: true };
    case 'SECURITY_SCAN_BLOCKED': return { label: 'Held by the security scan', held: true };
    default: return { label: statusLabel(type), held: HELD_TYPES.has(type) };
  }
};

/**
 * "Same as RFQ-7781 · Saudi Aramco · owned by Sara Bin Ali". Says only what is known: while the
 * original is still loading it names the serial alone rather than claiming "customer not yet
 * known" about a customer it simply has not read yet.
 */
export const sameAsSentence = (
  row: Pick<DuplicateUploadDTO, 'canonicalLeadId' | 'nexoraSerial'>,
  original: LeadResponseDTO | null | undefined,
  originalLoaded: boolean,
): string => {
  if (row.canonicalLeadId == null) return 'Original still being processed';
  const reference = original?.rfqno?.trim() || row.nexoraSerial?.trim() || `inquiry #${row.canonicalLeadId}`;
  if (!originalLoaded) return `Same as ${reference}`;
  const customer = original?.customerName?.trim() || 'customer not yet known';
  const owner = original?.assignedToFullName?.trim() || 'nobody yet';
  return `Same as ${reference} · ${customer} · owned by ${owner}`;
};

type SortKey = 'ingestedAt' | 'fileName' | 'duplicateType';

/** One engineering figure inside the Details fold. */
const Figure = ({ label, value }: { label: string; value: string }) => (
  <Box sx={{ minWidth: 0 }}>
    <Typography variant="caption" color="text.secondary" sx={{ display: 'block', fontWeight: 700 }}>{label}</Typography>
    <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>{value}</Typography>
  </Box>
);

export default function DuplicateUploadsPage() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const canCreateLeads = hasPermission('Leads', 'create');
  const [sortKey, setSortKey] = useState<SortKey>('ingestedAt');
  const [sortDirection, setSortDirection] = useState<'asc' | 'desc'>('desc');
  const [openDetails, setOpenDetails] = useState<ReadonlySet<number>>(new Set());
  /** Busy only for a refresh the reader asked for — never for the automatic 5 s poll. */
  const [manualRefreshing, setManualRefreshing] = useState(false);
  const query = useQuery({
    queryKey: ['duplicate-uploads'],
    queryFn: leadService.getDuplicateUploads,
    // This page renders its own failure; a missed background poll raises no toast.
    meta: { silenceGlobalError: true },
    refetchInterval: (state) => state.state.data?.some((row) => HELD_TYPES.has(row.duplicateType)) ? 5000 : false,
  });
  const retryMutation = useMutation({
    mutationFn: (batchId: string) => leadService.retryBlockedFiles(batchId),
    onSuccess: () => query.refetch(),
  });

  const rows = useMemo(() => {
    const direction = sortDirection === 'asc' ? 1 : -1;
    return [...(query.data ?? [])].sort((left, right) => {
      if (sortKey === 'ingestedAt')
        return (new Date(left.ingestedAt).getTime() - new Date(right.ingestedAt).getTime()) * direction;
      return left[sortKey].localeCompare(right[sortKey], undefined, { sensitivity: 'base' }) * direction;
    });
  }, [query.data, sortDirection, sortKey]);

  // The originals, once each. Silent on failure: the row then shows the serial it already has.
  const originalIds = useMemo(
    () => Array.from(new Set(rows.map((row) => row.canonicalLeadId).filter((id): id is number => id != null))),
    [rows],
  );
  const originals = useQueries({
    queries: originalIds.map((id) => ({
      queryKey: ['lead', id],
      queryFn: () => leadService.getById(id),
      staleTime: 60_000,
      retry: false,
      meta: { silenceGlobalError: true },
    })),
  });
  const originalById = useMemo(() => {
    const map = new Map<number, { data: LeadResponseDTO | undefined; settled: boolean }>();
    originalIds.forEach((id, index) => {
      const result = originals[index];
      map.set(id, { data: result?.data, settled: result ? !result.isPending : false });
    });
    return map;
  }, [originalIds, originals]);

  const changeSort = (next: SortKey) => {
    if (next === sortKey) setSortDirection((value) => value === 'asc' ? 'desc' : 'asc');
    else {
      setSortKey(next);
      setSortDirection(next === 'ingestedAt' ? 'desc' : 'asc');
    }
  };
  const sortableHeader = (label: string, key: SortKey) => (
    <TableSortLabel active={sortKey === key} direction={sortKey === key ? sortDirection : 'asc'}
      onClick={() => changeSort(key)}>
      {label}
    </TableSortLabel>
  );
  const toggleDetails = (occurrenceId: number) => setOpenDetails((current) => {
    const next = new Set(current);
    if (next.has(occurrenceId)) next.delete(occurrenceId);
    else next.add(occurrenceId);
    return next;
  });

  const refreshNow = async () => {
    setManualRefreshing(true);
    try {
      await query.refetch();
    } finally {
      setManualRefreshing(false);
    }
  };

  if (query.isLoading) return <Box sx={{ p: 4, textAlign: 'center' }}><CircularProgress /></Box>;
  // Only a list that never loaded is replaced by the error; a failed poll keeps the rows on screen.
  if (query.isError && query.data === undefined) return (
    <ApiErrorNotice
      error={query.error}
      fallbackMessage="Duplicate uploads could not be loaded. Nothing was changed — try again."
      onRetry={() => query.refetch()}
    />
  );

  return (
    <Box sx={{ maxWidth: 1400, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mb: 2, justifyContent: 'space-between' }}>
        <Box>
          <Typography variant="h5" component="h1">Duplicate Uploads</Typography>
          <Typography variant="body2" color="text.secondary">
            Files that came in again. Nothing here became a new inquiry; each row points at the inquiry it copies.
          </Typography>
        </Box>
        <Button variant="outlined" startIcon={manualRefreshing ? <CircularProgress size={16} /> : <Refresh />} onClick={() => void refreshNow()} disabled={manualRefreshing}>
          Refresh
        </Button>
      </Stack>

      {query.isError && <RefreshFailedNotice updatedAt={query.dataUpdatedAt} retryHint="Press Refresh to try again." />}

      {retryMutation.isError && (
        <ApiErrorNotice
          error={retryMutation.error}
          fallbackMessage="Held files could not be retried. Their stored originals and current status are unchanged — try again shortly."
          sx={{ mb: 2 }}
        />
      )}
      {retryMutation.data && (
        <Alert severity={retryMutation.data.stillAwaiting > 0 || retryMutation.data.sourceObjectUnavailable > 0 ? 'warning' : 'success'} sx={{ mb: 2 }}>
          Retry complete: {retryMutation.data.queued} queued, {retryMutation.data.stillAwaiting} awaiting scan,
          {' '}{retryMutation.data.rejected} rejected, {retryMutation.data.sourceObjectUnavailable} source objects unavailable.
        </Alert>
      )}

      {rows.length === 0 ? (
        <Alert severity="info">No file has come in twice. When one does, it appears here with the inquiry it copies.</Alert>
      ) : (
        <TableContainer component={Paper} variant="outlined">
          <Table size="small" sx={{ minWidth: 900 }} aria-label="Duplicate uploads">
            <TableHead>
              <TableRow>
                <TableCell>{sortableHeader('File', 'fileName')}</TableCell>
                <TableCell>{sortableHeader('Uploaded', 'ingestedAt')}</TableCell>
                <TableCell>{sortableHeader('What it is', 'duplicateType')}</TableCell>
                <TableCell>Same as</TableCell>
                <TableCell align="right">Action</TableCell>
                <TableCell align="right" sx={{ width: 96 }}>Details</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((row) => {
                const kind = duplicateKind(row.duplicateType);
                const original = row.canonicalLeadId != null ? originalById.get(row.canonicalLeadId) : undefined;
                const sentence = sameAsSentence(row, original?.data, original?.settled ?? false);
                const canRetryScan = row.actions.includes('Retry security scan') && canCreateLeads;
                const detailsOpen = openDetails.has(row.occurrenceId);
                const detailsId = `duplicate-details-${row.occurrenceId}`;
                return (
                  <Fragment key={row.occurrenceId}>
                    <TableRow hover>
                      <TableCell>
                        <Typography variant="body2" sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>{row.fileName}</Typography>
                        <Typography variant="caption" color="text.secondary">
                          {row.uploadedBy} · {row.source}
                        </Typography>
                      </TableCell>
                      <TableCell sx={{ whiteSpace: 'nowrap' }}>{formatDateTimeSafe(row.ingestedAt)}</TableCell>
                      <TableCell>
                        <Chip size="small" label={kind.label} color={kind.held ? 'warning' : 'info'} variant="outlined" />
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2">{sentence}</Typography>
                      </TableCell>
                      <TableCell align="right" sx={{ whiteSpace: 'nowrap' }}>
                        {/* One action per row state: open the original when there is one; retry the scan
                            when the copy is held and there is nothing to open yet. */}
                        {canRetryScan && row.canonicalLeadId == null ? (
                          <Button size="small" startIcon={<Refresh />}
                            disabled={retryMutation.isPending && retryMutation.variables === row.uploadBatch}
                            onClick={() => retryMutation.mutate(row.uploadBatch)}>
                            Retry scan
                          </Button>
                        ) : (
                          <Tooltip title={row.canonicalLeadId == null ? 'The original inquiry is still being processed.' : ''}>
                            <span>
                              <Button size="small" startIcon={<OpenInNew />}
                                disabled={row.canonicalLeadId == null}
                                onClick={() => navigate(`/procurement/leads/view/${row.canonicalLeadId}`)}>
                                Open the original
                              </Button>
                            </span>
                          </Tooltip>
                        )}
                      </TableCell>
                      <TableCell align="right">
                        <Button
                          size="small"
                          color="inherit"
                          endIcon={detailsOpen ? <ExpandLess /> : <ExpandMore />}
                          aria-expanded={detailsOpen}
                          aria-controls={detailsId}
                          aria-label={`${detailsOpen ? 'Hide' : 'Show'} details for ${row.fileName}`}
                          onClick={() => toggleDetails(row.occurrenceId)}
                          sx={{ fontWeight: 600, whiteSpace: 'nowrap' }}
                        >
                          Details
                        </Button>
                      </TableCell>
                    </TableRow>
                    <TableRow>
                      <TableCell colSpan={6} sx={{ p: 0, borderBottom: detailsOpen ? undefined : 0 }}>
                        <Collapse in={detailsOpen} unmountOnExit>
                          {/* The accountant's figures: everything the row used to shout, kept verbatim. */}
                          <Box id={detailsId} sx={{ px: 2, py: 1.5, bgcolor: 'action.hover' }}>
                            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(4, minmax(0, 1fr))' }, gap: 1.5 }}>
                              <Figure label="Occurrence" value={`#${row.occurrenceId}`} />
                              <Figure label="Original occurrence" value={row.originalOccurrenceId != null ? `#${row.originalOccurrenceId}` : 'Pending'} />
                              <Figure label="Original serial" value={row.nexoraSerial ?? 'Canonical lead pending'} />
                              <Figure label="Upload batch" value={row.uploadBatch} />
                              <Figure label="Security" value={`${statusLabel(row.securityStatus)} · scan ${row.resources.malwareScanReused ? 'reused' : row.resources.malwareScanRerun ? 'rerun' : 'pending'}`} />
                              <Figure label="Processing reuse" value={`${row.processingReused ? 'Reused' : 'Not reused'} · parser ${row.resources.parserReused ? 'yes' : 'no'} · OCR ${row.resources.ocrReused ? 'yes' : 'no'} · local model ${row.resources.localModelReused ? 'yes' : 'no'} · external ${row.resources.externalModelReused ? 'yes' : 'no'}`} />
                              <Figure label="Upload" value={`${bytes(row.resources.bytesUploaded)} · hash ${row.resources.hashingDurationMs} ms`} />
                              <Figure label="Storage" value={`Physical ${bytes(row.resources.storagePhysicalBytes)} · logical ${bytes(row.resources.storageLogicalBytes)}`} />
                              <Figure label="Cost" value={`Actual ${row.resources.totalActualCost.toFixed(6)} · external ${row.resources.externalCost.toFixed(6)} · ${statusLabel(row.resources.costStatus)}`} />
                              <Figure label="Cost avoided" value={row.resources.costStatus === 'LOCAL_COMPUTE_UNPRICED'
                                ? 'Unpriced (local compute)'
                                : `Estimated ${row.resources.estimatedProcessingAvoided.toFixed(6)}`} />
                              <Figure label="Extraction" value={row.processingReused ? 'Work avoided' : 'Awaiting reusable result'} />
                            </Box>
                            <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap' }}>
                              <Button size="small" startIcon={<OpenInNew />}
                                onClick={() => navigate(`/procurement/leads/ingestion/${row.uploadBatch}`)}>
                                Open the upload batch
                              </Button>
                              {canRetryScan && row.canonicalLeadId != null && (
                                <Button size="small" startIcon={<Refresh />}
                                  disabled={retryMutation.isPending && retryMutation.variables === row.uploadBatch}
                                  onClick={() => retryMutation.mutate(row.uploadBatch)}>
                                  Retry scan
                                </Button>
                              )}
                            </Stack>
                          </Box>
                        </Collapse>
                      </TableCell>
                    </TableRow>
                  </Fragment>
                );
              })}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
