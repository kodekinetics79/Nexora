import { useMemo, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TableSortLabel,
  Typography,
} from '@mui/material';
import { OpenInNew, Refresh } from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import { useAuth } from '../../context/AuthContext';

const readable = (value: string): string => value.replaceAll('_', ' ').toLowerCase()
  .replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());

const bytes = (value: number): string => value >= 1024 * 1024
  ? `${(value / (1024 * 1024)).toFixed(2)} MB`
  : `${(value / 1024).toFixed(1)} KB`;

type SortKey = 'ingestedAt' | 'fileName' | 'duplicateType' | 'securityStatus';

export default function DuplicateUploadsPage() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const canCreateLeads = hasPermission('Leads', 'create');
  const [sortKey, setSortKey] = useState<SortKey>('ingestedAt');
  const [sortDirection, setSortDirection] = useState<'asc' | 'desc'>('desc');
  const query = useQuery({
    queryKey: ['duplicate-uploads'],
    queryFn: leadService.getDuplicateUploads,
    refetchInterval: (state) => state.state.data?.some((row) =>
      row.duplicateType === 'EXACT_DUPLICATE_PENDING_SECURITY'
      || row.duplicateType === 'DUPLICATE_RESCAN_REQUIRED'
      || row.duplicateType === 'SECURITY_SCAN_BLOCKED') ? 5000 : false,
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

  if (query.isLoading) return <Box sx={{ p: 4, textAlign: 'center' }}><CircularProgress /></Box>;
  if (query.isError) return <Alert severity="error">Duplicate uploads could not be loaded.</Alert>;

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mb: 2, justifyContent: 'space-between' }}>
        <Box>
          <Typography variant="h5" component="h1">Duplicate Uploads</Typography>
          <Typography variant="body2" color="text.secondary">
            Exact file copies and business duplicates, including uploads awaiting security verification.
          </Typography>
        </Box>
        <Button variant="outlined" startIcon={<Refresh />} onClick={() => query.refetch()} disabled={query.isFetching}>
          Refresh
        </Button>
      </Stack>

      {retryMutation.isError && (
        <Alert severity="error" sx={{ mb: 2 }}>The blocked files could not be retried.</Alert>
      )}
      {retryMutation.data && (
        <Alert severity={retryMutation.data.stillAwaiting > 0 || retryMutation.data.sourceObjectUnavailable > 0 ? 'warning' : 'success'} sx={{ mb: 2 }}>
          Retry complete: {retryMutation.data.queued} queued, {retryMutation.data.stillAwaiting} awaiting scan,
          {' '}{retryMutation.data.rejected} rejected, {retryMutation.data.sourceObjectUnavailable} source objects unavailable.
        </Alert>
      )}

      {rows.length === 0 ? (
        <Alert severity="info">No duplicate upload occurrences are recorded for this tenant.</Alert>
      ) : (
        <TableContainer component={Paper} variant="outlined">
          <Table size="small" sx={{ minWidth: 1500 }}>
            <TableHead>
              <TableRow>
                <TableCell>{sortableHeader('File and receipt', 'fileName')}</TableCell>
                <TableCell>{sortableHeader('Ingested', 'ingestedAt')}</TableCell>
                <TableCell>{sortableHeader('Duplicate type', 'duplicateType')}</TableCell>
                <TableCell>Original / canonical</TableCell>
                <TableCell>{sortableHeader('Security', 'securityStatus')}</TableCell>
                <TableCell>Processing reuse</TableCell>
                <TableCell>Resources consumed</TableCell>
                <TableCell>Resources avoided</TableCell>
                <TableCell align="right">Actions</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((row) => (
                <TableRow key={row.occurrenceId} hover>
                  <TableCell>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>{row.fileName}</Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>Occurrence #{row.occurrenceId}</Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>{row.source} | {row.uploadedBy}</Typography>
                    <Typography variant="caption" color="text.secondary">Batch {row.uploadBatch}</Typography>
                  </TableCell>
                  <TableCell>{new Date(row.ingestedAt).toLocaleString()}</TableCell>
                  <TableCell><Chip size="small" label={readable(row.duplicateType)} color="info" variant="outlined" /></TableCell>
                  <TableCell>
                    <Typography variant="body2">Original #{row.originalOccurrenceId ?? 'Pending'}</Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>
                      {row.nexoraSerial ?? 'Canonical Lead pending'}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Chip size="small" label={readable(row.securityStatus)}
                      color={row.securityStatus === 'Cleared' ? 'success' : 'warning'} />
                    <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
                      Scan {row.resources.malwareScanReused ? 'reused' : row.resources.malwareScanRerun ? 'rerun' : 'pending'}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">{row.processingReused ? 'Reused' : 'Not reused'}</Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>
                      Parser {row.resources.parserReused ? 'yes' : 'no'} | OCR {row.resources.ocrReused ? 'yes' : 'no'}
                    </Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>
                      Local model {row.resources.localModelReused ? 'yes' : 'no'} | External {row.resources.externalModelReused ? 'yes' : 'no'}
                    </Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">Upload {bytes(row.resources.bytesUploaded)}</Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>Hash {row.resources.hashingDurationMs} ms</Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>
                      Physical {bytes(row.resources.storagePhysicalBytes)} | Logical {bytes(row.resources.storageLogicalBytes)}
                    </Typography>
                    <Typography variant="caption" sx={{ display: 'block' }}>
                      Actual {row.resources.totalActualCost.toFixed(6)} | External {row.resources.externalCost.toFixed(6)}
                    </Typography>
                    <Typography variant="caption" color="text.secondary">{readable(row.resources.costStatus)}</Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">
                      {row.resources.costStatus === 'LOCAL_COMPUTE_UNPRICED'
                        ? 'Avoided cost unpriced'
                        : `Estimated ${row.resources.estimatedProcessingAvoided.toFixed(6)}`}
                    </Typography>
                    <Typography variant="caption" color="text.secondary">
                      {row.processingReused ? 'Extraction work avoided' : 'Awaiting reusable result'}
                    </Typography>
                  </TableCell>
                  <TableCell align="right">
                    <Stack spacing={0.5} sx={{ alignItems: 'flex-end' }}>
                      <Button size="small" startIcon={<OpenInNew />}
                        onClick={() => navigate(`/procurement/leads/ingestion/${row.uploadBatch}`)}>
                        Batch
                      </Button>
                      {row.actions.includes('Retry security scan') && canCreateLeads && (
                        <Button size="small" startIcon={<Refresh />}
                          disabled={retryMutation.isPending && retryMutation.variables === row.uploadBatch}
                          onClick={() => retryMutation.mutate(row.uploadBatch)}>
                          Retry scan
                        </Button>
                      )}
                      {row.canonicalLeadId && (
                        <Button size="small" onClick={() => navigate(`/procurement/leads/view/${row.canonicalLeadId}`)}>
                          Lead
                        </Button>
                      )}
                    </Stack>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}
