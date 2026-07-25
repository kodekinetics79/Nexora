import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  Paper,
  Stack,
  Typography,
} from '@mui/material';
import {
  ExpandMore as ExpandIcon,
  History as HistoryIcon,
  OpenInNew as OpenIcon,
  Refresh as RefreshIcon,
  WarningAmber as ImpactIcon,
} from '@mui/icons-material';
import dayjs from 'dayjs';
import leadService from '../../api/services/leadService';
import type { LeadRevisionDifferenceDTO, LeadRevisionImpactDTO } from '../../api/services/leadService';

const readable = (value: string): string => value
  .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  .replaceAll('_', ' ')
  .replace(/\b\w/g, (character) => character.toUpperCase());

const jsonValue = (value?: string | null): string => {
  if (!value) return 'Not present';
  try {
    const parsed: unknown = JSON.parse(value);
    if (typeof parsed === 'string' || typeof parsed === 'number' || typeof parsed === 'boolean') return String(parsed);
    return JSON.stringify(parsed, null, 2);
  } catch {
    return value;
  }
};

const Difference = ({ difference }: { difference: LeadRevisionDifferenceDTO }) => {
  const color = difference.changeType.toLowerCase() === 'added' ? 'success'
    : difference.changeType.toLowerCase() === 'removed' ? 'error'
      : difference.changeType.toLowerCase() === 'modified' ? 'warning' : 'default';

  return (
    <Box sx={{ py: 1.5 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', mb: 1 }}>
        <Chip size="small" label={readable(difference.changeType)} color={color} variant="outlined" />
        <Typography variant="body2" sx={{ fontWeight: 800 }}>{difference.scope}</Typography>
        <Typography variant="caption" color="text.secondary" sx={{ fontFamily: 'monospace', overflowWrap: 'anywhere' }}>{difference.path}</Typography>
      </Stack>
      {difference.changeType.toLowerCase() !== 'unchanged' && (
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Typography variant="caption" color="text.secondary">Previous</Typography>
            <Typography component="pre" sx={{ m: 0, mt: 0.5, p: 1.25, bgcolor: 'action.hover', fontSize: '0.72rem', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxHeight: 180, overflow: 'auto' }}>
              {jsonValue(difference.previousValueJson)}
            </Typography>
          </Box>
          <Box sx={{ flex: 1, minWidth: 0 }}>
            <Typography variant="caption" color="text.secondary">Current</Typography>
            <Typography component="pre" sx={{ m: 0, mt: 0.5, p: 1.25, bgcolor: 'action.hover', fontSize: '0.72rem', whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxHeight: 180, overflow: 'auto' }}>
              {jsonValue(difference.currentValueJson)}
            </Typography>
          </Box>
        </Stack>
      )}
    </Box>
  );
};

const impactRoute = (impact: LeadRevisionImpactDTO): string | null => {
  const type = impact.aggregateType.toLowerCase();
  if (type === 'rfq') return `/procurement/rfqs/view/${impact.aggregateId}`;
  if (type === 'quote') return `/sales/quotes/view/${impact.aggregateId}`;
  if (type === 'order') return `/sales/orders/${impact.aggregateId}`;
  return null;
};

const Impact = ({ impact }: { impact: LeadRevisionImpactDTO }) => {
  const navigate = useNavigate();
  const route = impactRoute(impact);
  return (
    <Paper variant="outlined" sx={{ p: 1.5, borderRadius: 2 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ alignItems: { xs: 'stretch', sm: 'center' } }}>
        <ImpactIcon color="warning" fontSize="small" />
        <Box sx={{ flex: 1 }}>
          <Typography variant="body2" sx={{ fontWeight: 800 }}>{readable(impact.impactType)}</Typography>
          <Typography variant="caption" color="text.secondary">
            {impact.aggregateType} #{impact.aggregateId} | {readable(impact.status)}
          </Typography>
        </Box>
        {route && <Button size="small" endIcon={<OpenIcon />} onClick={() => navigate(route)}>Open</Button>}
      </Stack>
    </Paper>
  );
};

export default function LeadRevisionTimeline({ leadId }: { leadId: number }) {
  const revisionsQuery = useQuery({
    queryKey: ['lead-revisions', leadId],
    queryFn: () => leadService.getRevisions(leadId),
    enabled: leadId > 0,
  });

  return (
    <Box sx={{ mt: 4 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1.5 }}>
        <HistoryIcon color="primary" />
        <Typography variant="h6" sx={{ fontWeight: 900 }}>Revision history</Typography>
        {revisionsQuery.data && <Chip size="small" label={revisionsQuery.data.length} />}
      </Stack>

      {revisionsQuery.isLoading && (
        <Paper variant="outlined" sx={{ p: 3, textAlign: 'center', borderRadius: 2 }}>
          <CircularProgress size={24} />
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>Loading revision history...</Typography>
        </Paper>
      )}

      {revisionsQuery.isError && (
        <Alert severity="warning" action={<Button color="inherit" startIcon={<RefreshIcon />} onClick={() => revisionsQuery.refetch()}>Retry</Button>}>
          Revision history is temporarily unavailable. The current Lead details remain unchanged.
        </Alert>
      )}

      {revisionsQuery.data?.length === 0 && (
        <Alert severity="info">No immutable Lead revisions have been recorded yet.</Alert>
      )}

      <Stack spacing={1.5}>
        {revisionsQuery.data?.map((revision, index) => (
          <Accordion key={revision.id} defaultExpanded={index === 0} disableGutters sx={{ border: '1px solid', borderColor: 'divider', borderRadius: '8px !important', '&::before': { display: 'none' } }}>
            <AccordionSummary expandIcon={<ExpandIcon />}>
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={{ xs: 0.5, sm: 1.5 }} sx={{ width: '100%', alignItems: { xs: 'flex-start', sm: 'center' }, pr: 1 }}>
                <Chip label={`Revision ${revision.revisionNumber}`} color={index === 0 ? 'primary' : 'default'} size="small" sx={{ fontWeight: 800 }} />
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {dayjs(revision.createdAtUtc).isValid() ? dayjs(revision.createdAtUtc).format('DD MMM YYYY, HH:mm') : 'Date unavailable'}
                </Typography>
                <Typography variant="caption" color="text.secondary">{readable(revision.processingPath)}</Typography>
                <Box sx={{ flex: 1 }} />
                <Chip size="small" variant="outlined" label={revision.externalAiUsed ? 'External processing recorded' : 'No external processing'} color={revision.externalAiUsed ? 'warning' : 'default'} />
              </Stack>
            </AccordionSummary>
            <AccordionDetails>
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ mb: 2 }}>
                <Box>
                  <Typography variant="caption" color="text.secondary">Customer RFQ reference</Typography>
                  <Typography variant="body2" sx={{ fontWeight: 800 }}>{revision.customerRfqReference || 'Not provided'}</Typography>
                </Box>
                <Box sx={{ minWidth: 0 }}>
                  <Typography variant="caption" color="text.secondary">Immutable fingerprint</Typography>
                  <Typography variant="body2" sx={{ fontFamily: 'monospace', overflowWrap: 'anywhere' }}>{revision.fingerprint}</Typography>
                </Box>
              </Stack>

              <Divider />
              <Typography variant="subtitle2" sx={{ fontWeight: 900, mt: 2 }}>Changes</Typography>
              {revision.differences.length === 0
                ? <Typography variant="body2" color="text.secondary" sx={{ my: 1 }}>No structured differences were recorded.</Typography>
                : revision.differences.map((difference, differenceIndex) => (
                  <Difference key={`${difference.scope}-${difference.path}-${differenceIndex}`} difference={difference} />
                ))}

              <Typography variant="subtitle2" sx={{ fontWeight: 900, mt: 2, mb: 1 }}>Downstream commercial impact</Typography>
              {revision.impacts.length === 0
                ? <Typography variant="body2" color="text.secondary">No downstream RFQ, Quote, or Order impact is recorded.</Typography>
                : <Stack spacing={1}>{revision.impacts.map((impact) => <Impact key={`${impact.aggregateType}-${impact.aggregateId}-${impact.impactType}`} impact={impact} />)}</Stack>}
            </AccordionDetails>
          </Accordion>
        ))}
      </Stack>
    </Box>
  );
}
