import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useSnackbar } from 'notistack';
import dayjs from 'dayjs';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import {
  ArrowForward as OpenIcon,
  FactCheckOutlined as EvidenceIcon,
  InfoOutlined as InfoIcon,
  Refresh as RefreshIcon,
  WarningAmber as ExceptionIcon,
} from '@mui/icons-material';
import { isAxiosError } from 'axios';
import { useAuth } from '../../context/AuthContext';
import commercialExceptionService, {
  createCommercialCommandIdentity,
  type CommercialCommandIdentity,
  type CommercialExceptionFilters,
  type CommercialExceptionItem,
  type CommercialExceptionSeverity,
  type CommercialExceptionStatus,
  type CommercialExceptionType,
} from '../../services/commercialExceptionService';

const PAGE_SIZE = 25;
const ACTIVE_STATUSES: CommercialExceptionStatus[] = ['Open', 'Acknowledged'];
const TERMINAL_STATUSES: CommercialExceptionStatus[] = ['Resolved', 'Dismissed'];

const readable = (value: string) => value
  .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  .replaceAll('_', ' ')
  .replaceAll('-', ' ')
  .replace(/\b\w/g, (letter) => letter.toUpperCase());

const severityColor = (severity: CommercialExceptionSeverity) => {
  if (severity === 'Critical') return 'error';
  if (severity === 'High') return 'warning';
  if (severity === 'Medium') return 'info';
  return 'default';
};

const sourceRoute = (item: CommercialExceptionItem): string | null => {
  switch (item.sourceType.toLowerCase()) {
    case 'lead': return `/procurement/leads/view/${item.sourceId}`;
    case 'rfq': return `/procurement/rfqs/view/${item.sourceId}`;
    case 'quote': return `/sales/quotes/view/${item.sourceId}`;
    case 'order': return `/sales/orders/${item.sourceId}`;
    case 'followuptask': return `/sales/follow-ups?sourceId=${item.sourceId}`;
    case 'unassignedworkitem': return `/sales/routing?sourceId=${item.sourceId}`;
    default: return item.commercialCaseId ? `/commercial-cases/${item.commercialCaseId}` : null;
  }
};

const parseEvidence = (evidenceJson: string): Record<string, unknown> | null => {
  try {
    const value: unknown = JSON.parse(evidenceJson);
    return value !== null && typeof value === 'object' && !Array.isArray(value)
      ? value as Record<string, unknown>
      : null;
  } catch {
    return null;
  }
};

const evidenceValue = (value: unknown) => {
  if (value === null || value === undefined || value === '') return 'Not recorded';
  if (typeof value === 'object') return JSON.stringify(value);
  return String(value);
};

const errorMessage = (error: unknown, fallback: string) => {
  if (!isAxiosError(error)) return fallback;
  const detail = error.response?.data?.detail
    ?? error.response?.data?.message
    ?? error.response?.data?.error;
  return typeof detail === 'string' && detail.trim() ? detail : fallback;
};

const retryTransportFailure = (failureCount: number, error: unknown) =>
  failureCount < 1 && isAxiosError(error) && !error.response;

interface DecisionState {
  item: CommercialExceptionItem;
  targetStatus: Exclude<CommercialExceptionStatus, 'Open'>;
  commandIdentity: CommercialCommandIdentity;
}

export default function CommercialExceptionCenterPage() {
  const theme = useTheme();
  const isCompact = useMediaQuery(theme.breakpoints.down('md'));
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { userData, hasPermission } = useAuth();
  const canDecide = hasPermission('Leads', 'edit');
  const canReconcile = userData.isManager === true && canDecide;
  const actorId = userData.email ?? userData.userName ?? String(userData.id ?? 'unknown');

  const [status, setStatus] = useState<CommercialExceptionStatus | ''>('');
  const [type, setType] = useState<CommercialExceptionType | ''>('');
  const [minimumSeverity, setMinimumSeverity] = useState<CommercialExceptionSeverity | ''>('');
  const [overdueOnly, setOverdueOnly] = useState(false);
  const [pageNumber, setPageNumber] = useState(1);
  const [decision, setDecision] = useState<DecisionState | null>(null);
  const [reason, setReason] = useState('');
  const [evidenceItem, setEvidenceItem] = useState<CommercialExceptionItem | null>(null);
  const [refreshIdentity, setRefreshIdentity] = useState<CommercialCommandIdentity | null>(null);

  const filters = useMemo<CommercialExceptionFilters>(() => ({
    status: status || undefined,
    type: type || undefined,
    minimumSeverity: minimumSeverity || undefined,
    overdueOnly: overdueOnly || undefined,
    pageNumber,
    pageSize: PAGE_SIZE,
  }), [minimumSeverity, overdueOnly, pageNumber, status, type]);

  const queryKey = ['commercial-exceptions', filters] as const;
  const exceptions = useQuery({
    queryKey,
    queryFn: () => commercialExceptionService.getPage(filters),
    retry: 1,
  });

  const refreshMutation = useMutation({
    mutationFn: (identity: CommercialCommandIdentity) => commercialExceptionService.refresh(actorId, identity),
    retry: retryTransportFailure,
    onSuccess: (result) => {
      setRefreshIdentity(null);
      void queryClient.invalidateQueries({ queryKey: ['commercial-exceptions'] });
      enqueueSnackbar(
        `Reconciled: ${result.detected} detected, ${result.reopened} reopened, ${result.resolved} resolved.`,
        { variant: 'success' },
      );
    },
    onError: (error) => {
      if (isAxiosError(error) && error.response?.status === 409) {
        setRefreshIdentity(null);
        void queryClient.invalidateQueries({ queryKey: ['commercial-exceptions'] });
      }
      enqueueSnackbar(
        errorMessage(error, 'Commercial exception sources could not be reconciled.'),
        { variant: 'error' },
      );
    },
  });

  const transitionMutation = useMutation({
    mutationFn: ({ item, targetStatus, transitionReason, commandIdentity }: DecisionState & { transitionReason: string }) =>
      commercialExceptionService.transition(
        item.id,
        item.version,
        targetStatus,
        transitionReason,
        actorId,
        commandIdentity,
      ),
    retry: retryTransportFailure,
    onSuccess: (updated) => {
      queryClient.setQueryData(queryKey, (current: typeof exceptions.data) => {
        if (!current) return current;
        return { ...current, items: current.items.map((item) => item.id === updated.id ? updated : item) };
      });
      void queryClient.invalidateQueries({ queryKey: ['commercial-exceptions'] });
      enqueueSnackbar(`Exception ${updated.status.toLowerCase()}.`, { variant: 'success' });
      setDecision(null);
      setReason('');
    },
    onError: (error) => {
      if (isAxiosError(error) && error.response?.status === 409) {
        setDecision(null);
        setReason('');
        void queryClient.invalidateQueries({ queryKey: ['commercial-exceptions'] });
      }
      enqueueSnackbar(
        errorMessage(error, 'The exception changed or the decision could not be recorded. Refresh and try again.'),
        { variant: 'error' },
      );
    },
  });

  const data = exceptions.data;
  const totalPages = Math.max(1, Math.ceil((data?.total ?? 0) / PAGE_SIZE));
  const coverageStatus = data?.coverageStatus ?? 'Unavailable';
  const hasCompleteCoverage = coverageStatus === 'Complete';
  const coverageDetails = data?.sourceCoverage
    ?.filter((source) => !source.isAvailable)
    .map((source) => `${readable(source.sourceType)}: ${source.detail}`)
    .filter(Boolean) ?? [];
  const evidence = evidenceItem ? parseEvidence(evidenceItem.evidenceJson) : null;
  const metricCards = [
    { key: 'total', label: 'Matching current filters', value: data?.total },
    { key: 'active', label: 'Active in scope', value: data?.active },
    { key: 'critical', label: 'Critical active', value: data?.critical },
    { key: 'overdue', label: 'SLA overdue active', value: data?.overdue },
  ] as const;

  const openDecision = (
    item: CommercialExceptionItem,
    targetStatus: Exclude<CommercialExceptionStatus, 'Open'>,
  ) => {
    setDecision({ item, targetStatus, commandIdentity: createCommercialCommandIdentity() });
    setReason('');
  };

  const reconcileSources = () => {
    const identity = refreshIdentity ?? createCommercialCommandIdentity();
    setRefreshIdentity(identity);
    refreshMutation.mutate(identity);
  };

  const renderActions = (item: CommercialExceptionItem) => {
    const route = sourceRoute(item);
    return (
      <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap', rowGap: 0.75 }}>
        <Button size="small" variant="outlined" startIcon={<EvidenceIcon />} onClick={() => setEvidenceItem(item)}>
          Evidence
        </Button>
        {route && (
          <Button size="small" endIcon={<OpenIcon />} onClick={() => navigate(route)}>
            Open source
          </Button>
        )}
        {canDecide && item.status === 'Open' && (
          <Button size="small" onClick={() => openDecision(item, 'Acknowledged')}>Acknowledge</Button>
        )}
        {canDecide && ACTIVE_STATUSES.includes(item.status) && (
          <Button size="small" color="success" onClick={() => openDecision(item, 'Resolved')}>Resolve</Button>
        )}
        {canDecide && !TERMINAL_STATUSES.includes(item.status) && (
          <Button size="small" color="inherit" onClick={() => openDecision(item, 'Dismissed')}>Dismiss</Button>
        )}
      </Stack>
    );
  };

  const renderMobileItem = (item: CommercialExceptionItem) => (
    <Paper key={item.id} component="article" variant="outlined" sx={{ p: 2, borderRadius: 1 }}>
      <Stack direction="row" spacing={1} sx={{ justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{item.title}</Typography>
          <Typography variant="caption" color="text.secondary">{item.nexoraSerial}</Typography>
        </Box>
        <Chip size="small" label={item.severity} color={severityColor(item.severity)} />
      </Stack>
      <Typography variant="body2" sx={{ my: 1.25 }}>{item.summary}</Typography>
      <Stack direction="row" spacing={0.75} sx={{ mb: 1.5, flexWrap: 'wrap', rowGap: 0.75 }}>
        <Chip size="small" label={readable(item.exceptionType)} variant="outlined" />
        <Chip size="small" label={item.status} variant="outlined" />
        <Chip size="small" label={item.isOverdue ? 'SLA overdue' : `Due ${dayjs(item.slaDueAtUtc).format('DD MMM, HH:mm')}`} color={item.isOverdue ? 'error' : 'default'} variant="outlined" />
      </Stack>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Owner</Typography>
      <Typography variant="body2" sx={{ fontWeight: 700, mb: 1 }}>{item.ownerName || 'Unassigned'}</Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>Recommended action</Typography>
      <Typography variant="body2" sx={{ fontWeight: 700, mb: 1.5 }}>{readable(item.recommendedActionCode)}</Typography>
      {renderActions(item)}
    </Paper>
  );

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 1, sm: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { md: 'flex-start' }, mb: 2.5 }}>
        <Box>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <ExceptionIcon color="warning" />
            <Typography variant="h4" sx={{ fontWeight: 900 }}>Commercial Exception Center</Typography>
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            Verified coordination exceptions across the RFQ-to-revenue journey.
          </Typography>
          {data && (
            <Typography variant="caption" color="text.secondary">
              {data.scope} scope | Generated {dayjs(data.generatedAtUtc).format('DD MMM YYYY, HH:mm')} | {data.ruleVersion}
            </Typography>
          )}
        </Box>
        {canReconcile && (
          <Button
            variant="contained"
            startIcon={refreshMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <RefreshIcon />}
            onClick={reconcileSources}
            disabled={refreshMutation.isPending}
          >
            {refreshMutation.isError ? 'Retry reconciliation' : 'Reconcile sources'}
          </Button>
        )}
      </Stack>

      {data && !hasCompleteCoverage && (
        <Alert severity={coverageStatus === 'Unavailable' ? 'error' : 'warning'} sx={{ mb: 2 }}>
          Source coverage: {coverageStatus}. {coverageDetails.length
            ? coverageDetails.join(' ')
            : 'Unavailable sources are not counted as healthy.'}
        </Alert>
      )}

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: 'repeat(2, 1fr)', md: 'repeat(4, 1fr)' }, gap: 1, mb: 2 }}>
        {metricCards.map(({ key, label, value }) => (
          <Paper key={key} variant="outlined" sx={{ p: 1.5, borderRadius: 1 }}>
            <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
              <Typography variant="caption" color="text.secondary">{label}</Typography>
              <Tooltip title={data?.metricDefinitions[key] ?? 'Metric definition unavailable.'} arrow>
                <InfoIcon
                  color="action"
                  fontSize="inherit"
                  tabIndex={0}
                  aria-label={`Definition: ${label}. ${data?.metricDefinitions[key] ?? 'Metric definition unavailable.'}`}
                />
              </Tooltip>
            </Stack>
            <Typography variant="h5" sx={{ fontWeight: 900 }}>{value ?? '-'}</Typography>
          </Paper>
        ))}
      </Box>

      <Paper variant="outlined" sx={{ p: 1.5, mb: 2, borderRadius: 1 }}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ alignItems: { sm: 'center' }, flexWrap: 'wrap' }}>
          <FormControl size="small" sx={{ minWidth: 150 }}>
            <InputLabel id="commercial-exception-status-label">Status</InputLabel>
            <Select
              id="commercial-exception-status"
              labelId="commercial-exception-status-label"
              value={status}
              label="Status"
              onChange={(event) => { setStatus(event.target.value as CommercialExceptionStatus | ''); setPageNumber(1); }}
            >
              <MenuItem value="">All statuses</MenuItem>
              {(['Open', 'Acknowledged', 'Resolved', 'Dismissed'] as CommercialExceptionStatus[]).map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}
            </Select>
          </FormControl>
          <FormControl size="small" sx={{ minWidth: 180 }}>
            <InputLabel id="commercial-exception-type-label">Exception type</InputLabel>
            <Select
              id="commercial-exception-type"
              labelId="commercial-exception-type-label"
              value={type}
              label="Exception type"
              onChange={(event) => { setType(event.target.value as CommercialExceptionType | ''); setPageNumber(1); }}
            >
              <MenuItem value="">All types</MenuItem>
              {(['UnassignedLead', 'OverdueFollowUp'] as CommercialExceptionType[]).map((value) => <MenuItem key={value} value={value}>{readable(value)}</MenuItem>)}
            </Select>
          </FormControl>
          <FormControl size="small" sx={{ minWidth: 175 }}>
            <InputLabel id="commercial-exception-severity-label">Minimum severity</InputLabel>
            <Select
              id="commercial-exception-severity"
              labelId="commercial-exception-severity-label"
              value={minimumSeverity}
              label="Minimum severity"
              onChange={(event) => { setMinimumSeverity(event.target.value as CommercialExceptionSeverity | ''); setPageNumber(1); }}
            >
              <MenuItem value="">Any severity</MenuItem>
              {(['Low', 'Medium', 'High', 'Critical'] as CommercialExceptionSeverity[]).map((value) => <MenuItem key={value} value={value}>{value}</MenuItem>)}
            </Select>
          </FormControl>
          <Button
            variant={overdueOnly ? 'contained' : 'outlined'}
            color={overdueOnly ? 'error' : 'primary'}
            aria-pressed={overdueOnly}
            onClick={() => { setOverdueOnly((current) => !current); setPageNumber(1); }}
          >
            SLA overdue only
          </Button>
          <Button onClick={() => { setStatus(''); setType(''); setMinimumSeverity(''); setOverdueOnly(false); setPageNumber(1); }}>
            Clear filters
          </Button>
        </Stack>
      </Paper>

      {exceptions.isLoading ? (
        <Box sx={{ minHeight: 320, display: 'grid', placeItems: 'center' }}><CircularProgress /></Box>
      ) : exceptions.isError ? (
        <Alert severity="error" action={<Button color="inherit" onClick={() => void exceptions.refetch()}>Retry</Button>}>
          {errorMessage(exceptions.error, 'Commercial exceptions could not be loaded. No inferred results are shown.')}
        </Alert>
      ) : !data?.items.length && hasCompleteCoverage ? (
        <Alert severity="success" action={<Button color="inherit" onClick={() => void exceptions.refetch()}>Refresh</Button>}>
          No commercial exceptions match this scope and filter.
        </Alert>
      ) : !data?.items.length ? (
        <Alert severity={coverageStatus === 'Unavailable' ? 'error' : 'warning'} action={<Button color="inherit" onClick={() => void exceptions.refetch()}>Retry</Button>}>
          No exceptions are shown because source coverage is {coverageStatus.toLowerCase()}.
        </Alert>
      ) : isCompact ? (
        <Stack spacing={1.25}>{data.items.map(renderMobileItem)}</Stack>
      ) : (
        <TableContainer component={Paper} variant="outlined" sx={{ borderRadius: 1 }}>
          <Table size="small" aria-label="Commercial exceptions">
            <TableHead>
              <TableRow>
                <TableCell>Exception</TableCell>
                <TableCell>Severity / status</TableCell>
                <TableCell>SLA</TableCell>
                <TableCell>Owner</TableCell>
                <TableCell>Recommendation</TableCell>
                <TableCell sx={{ minWidth: 300 }}>Actions</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {data.items.map((item) => (
                <TableRow hover key={item.id}>
                  <TableCell sx={{ maxWidth: 360 }}>
                    <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{item.title}</Typography>
                    <Typography variant="caption" color="text.secondary">{item.nexoraSerial} | {readable(item.exceptionType)}</Typography>
                    <Typography variant="body2" sx={{ mt: 0.5 }}>{item.summary}</Typography>
                  </TableCell>
                  <TableCell>
                    <Stack spacing={0.75} sx={{ alignItems: 'flex-start' }}>
                      <Chip size="small" label={item.severity} color={severityColor(item.severity)} />
                      <Chip size="small" label={item.status} variant="outlined" />
                    </Stack>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2" color={item.isOverdue ? 'error.main' : 'text.primary'} sx={{ fontWeight: 700 }}>
                      {item.isOverdue ? 'Overdue' : 'Within SLA'}
                    </Typography>
                    <Typography variant="caption" color="text.secondary">{dayjs(item.slaDueAtUtc).format('DD MMM YYYY, HH:mm')}</Typography>
                  </TableCell>
                  <TableCell>{item.ownerName || 'Unassigned'}</TableCell>
                  <TableCell sx={{ maxWidth: 220 }}>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>{readable(item.recommendedActionCode)}</Typography>
                    <Typography variant="caption" color="text.secondary">{readable(item.reasonCode)}</Typography>
                  </TableCell>
                  <TableCell>{renderActions(item)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      {data && data.total > PAGE_SIZE && (
        <Stack direction="row" spacing={1} sx={{ mt: 2, justifyContent: 'flex-end', alignItems: 'center' }}>
          <Button variant="outlined" disabled={pageNumber <= 1 || exceptions.isFetching} onClick={() => setPageNumber((current) => current - 1)}>Previous</Button>
          <Typography variant="body2">Page {pageNumber} of {totalPages}</Typography>
          <Button variant="outlined" disabled={pageNumber >= totalPages || exceptions.isFetching} onClick={() => setPageNumber((current) => current + 1)}>Next</Button>
        </Stack>
      )}

      <Dialog open={!!decision} onClose={() => !transitionMutation.isPending && setDecision(null)} fullWidth maxWidth="sm">
        <DialogTitle>{decision ? `${decision.targetStatus === 'Dismissed' ? 'Dismiss' : decision.targetStatus} exception` : 'Record decision'}</DialogTitle>
        <DialogContent dividers>
          {decision && (
            <>
              <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{decision.item.title}</Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>{decision.item.nexoraSerial} | Version {decision.item.version}</Typography>
            </>
          )}
          <TextField
            fullWidth
            required
            multiline
            minRows={3}
            label="Decision reason"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            slotProps={{ htmlInput: { maxLength: 1000 } }}
          />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setDecision(null)} disabled={transitionMutation.isPending}>Cancel</Button>
          <Button
            variant="contained"
            color={decision?.targetStatus === 'Dismissed' ? 'inherit' : 'primary'}
            disabled={!decision || !reason.trim() || transitionMutation.isPending}
            onClick={() => decision && transitionMutation.mutate({ ...decision, transitionReason: reason.trim() })}
          >
            {transitionMutation.isPending ? 'Recording...' : 'Record decision'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={!!evidenceItem} onClose={() => setEvidenceItem(null)} fullWidth maxWidth="sm">
        <DialogTitle>Source evidence</DialogTitle>
        <DialogContent dividers>
          {evidenceItem && (
            <Stack spacing={1.5}>
              <Box>
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{evidenceItem.nexoraSerial}</Typography>
                <Typography variant="caption" color="text.secondary">
                  {evidenceItem.sourceType} #{evidenceItem.sourceId} | Source version {evidenceItem.sourceVersion} | Last detected {dayjs(evidenceItem.lastDetectedAtUtc).format('DD MMM YYYY, HH:mm')}
                </Typography>
              </Box>
              {evidence && Object.keys(evidence).length ? Object.entries(evidence).map(([key, value]) => (
                <Box key={key} sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '160px 1fr' }, gap: 0.5, borderBottom: '1px solid', borderColor: 'divider', pb: 1 }}>
                  <Typography variant="caption" color="text.secondary">{readable(key)}</Typography>
                  <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>{evidenceValue(value)}</Typography>
                </Box>
              )) : (
                <Alert severity="warning">Structured source evidence is unavailable for this exception.</Alert>
              )}
            </Stack>
          )}
        </DialogContent>
        <DialogActions><Button onClick={() => setEvidenceItem(null)}>Close</Button></DialogActions>
      </Dialog>
    </Box>
  );
}
