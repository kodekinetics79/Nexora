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
import type { LeadRevisionDifferenceDTO, LeadRevisionDTO, LeadRevisionImpactDTO } from '../../api/services/leadService';

const readable = (value: string): string => value
  .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
  .replaceAll('_', ' ')
  .replace(/\b\w/g, (character) => character.toUpperCase());

/** "$.items[2].quantity" reads as "Line 3 · Quantity"; "$.requiredDeliveryDate" as "Required delivery date". */
export const fieldLabel = (path: string): string => {
  const segments = path.replace(/^\$\.?/, '').split('.').filter(Boolean);
  const parts: string[] = [];
  for (const segment of segments) {
    const indexed = /^(\w+)\[(\d+)\]$/.exec(segment);
    if (indexed) {
      const [, name, index] = indexed;
      parts.push(/^(items|lines|lineItems)$/i.test(name) ? `Line ${Number(index) + 1}` : `${readable(name)} ${Number(index) + 1}`);
    } else {
      const words = readable(segment);
      parts.push(words.charAt(0) + words.slice(1).toLowerCase());
    }
  }
  return parts.join(' · ') || path;
};

const parsed = (value?: string | null): unknown => {
  if (value == null) return undefined;
  try { return JSON.parse(value); } catch { return value; }
};

const display = (value: unknown): string => {
  if (value === undefined || value === null || value === '') return 'Not stated';
  if (typeof value === 'object') return JSON.stringify(value, null, 2);
  return String(value);
};

/**
 * Whether a recorded difference is one a person would call a change. The server compares
 * strings, so a timestamp re-serialised to a different precision shows as "Modified" with the
 * same instant on both sides; that is not a change to what the customer asked for.
 */
export const isRealChange = (difference: LeadRevisionDifferenceDTO): boolean => {
  const type = difference.changeType.toLowerCase();
  if (type === 'unchanged') return false;
  if (type !== 'modified') return true;
  const before = parsed(difference.previousValueJson);
  const after = parsed(difference.currentValueJson);
  if (typeof before === 'string' && typeof after === 'string') {
    const beforeDate = dayjs(before);
    const afterDate = dayjs(after);
    if (beforeDate.isValid() && afterDate.isValid() && /\d{4}-\d{2}-\d{2}/.test(before) && /\d{4}-\d{2}-\d{2}/.test(after)) {
      return beforeDate.valueOf() !== afterDate.valueOf();
    }
  }
  return JSON.stringify(before) !== JSON.stringify(after);
};

const sourceLabel = (processingPath: string): string => {
  const path = processingPath.toLowerCase();
  if (path.includes('human')) return 'Checked by a person';
  if (path.includes('email')) return 'Received by email';
  if (path.includes('upload') || path.includes('manual')) return 'Uploaded';
  return readable(processingPath);
};

const Change = ({ difference }: { difference: LeadRevisionDifferenceDTO }) => {
  const type = difference.changeType.toLowerCase();
  const before = display(parsed(difference.previousValueJson));
  const after = display(parsed(difference.currentValueJson));
  const multiline = before.includes('\n') || after.includes('\n');
  return (
    <Box component="li" sx={{ py: 1, listStyle: 'none', borderTop: 1, borderColor: 'divider' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', flexWrap: 'wrap' }}>
        <Typography variant="body2" sx={{ fontWeight: 700 }}>{fieldLabel(difference.path)}</Typography>
        {difference.scope && !/^(header|lead)$/i.test(difference.scope) ? (
          <Typography variant="caption" color="text.secondary">{readable(difference.scope)}</Typography>
        ) : null}
      </Stack>
      {type === 'added' ? (
        <Typography variant="body2" sx={{ whiteSpace: multiline ? 'pre-wrap' : 'normal', overflowWrap: 'anywhere' }}>
          Added: {after}
        </Typography>
      ) : type === 'removed' ? (
        <Typography variant="body2" sx={{ whiteSpace: multiline ? 'pre-wrap' : 'normal', overflowWrap: 'anywhere', textDecoration: 'line-through' }}>
          {before}
        </Typography>
      ) : (
        <Typography variant="body2" sx={{ whiteSpace: multiline ? 'pre-wrap' : 'normal', overflowWrap: 'anywhere' }}>
          <Box component="span" sx={{ color: 'text.secondary', textDecoration: 'line-through' }}>{before}</Box>
          {' → '}
          <Box component="span" sx={{ fontWeight: 600 }}>{after}</Box>
        </Typography>
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

const Revision = ({ revision, isCurrent }: { revision: LeadRevisionDTO; isCurrent: boolean }) => {
  const changes = revision.differences.filter(isRealChange);
  const when = dayjs(revision.createdAtUtc).isValid() ? dayjs(revision.createdAtUtc).format('DD MMM YYYY, HH:mm') : 'Date unavailable';
  const summary = changes.length === 0
    ? (revision.revisionNumber === 1 ? 'First version' : 'No changes to the request')
    : `${changes.length} change${changes.length === 1 ? '' : 's'}`;
  return (
    <Accordion disableGutters sx={{ border: '1px solid', borderColor: 'divider', borderRadius: '8px !important', '&::before': { display: 'none' } }}>
      <AccordionSummary expandIcon={<ExpandIcon />} aria-label={`Revision ${revision.revisionNumber}, ${summary}`}>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={{ xs: 0.5, sm: 1.5 }} sx={{ width: '100%', alignItems: { xs: 'flex-start', sm: 'center' }, pr: 1 }}>
          <Chip label={`Revision ${revision.revisionNumber}`} color={isCurrent ? 'primary' : 'default'} size="small" sx={{ fontWeight: 800 }} />
          <Typography variant="body2" sx={{ fontWeight: 700 }}>{when}</Typography>
          <Typography variant="body2" color="text.secondary">{sourceLabel(revision.processingPath)}</Typography>
          <Box sx={{ flex: 1 }} />
          <Chip size="small" variant="outlined" label={summary} color={changes.length > 0 ? 'warning' : 'default'} />
          {revision.externalAiUsed ? <Chip size="small" variant="outlined" color="warning" label="External processing used" /> : null}
        </Stack>
      </AccordionSummary>
      <AccordionDetails sx={{ pt: 0 }}>
        {changes.length === 0 ? (
          <Typography variant="body2" color="text.secondary">
            {revision.revisionNumber === 1
              ? 'What the customer asked for, as first received.'
              : `Nothing the customer asked for changed. This revision records that it was ${sourceLabel(revision.processingPath).toLowerCase()}.`}
          </Typography>
        ) : (
          <Box component="ul" sx={{ m: 0, p: 0 }}>
            {changes.map((difference, index) => (
              <Change key={`${difference.scope}-${difference.path}-${index}`} difference={difference} />
            ))}
          </Box>
        )}

        {revision.impacts.length > 0 ? (
          <Stack spacing={1} sx={{ mt: 2 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Affected downstream</Typography>
            {revision.impacts.map((impact) => <Impact key={`${impact.aggregateType}-${impact.aggregateId}-${impact.impactType}`} impact={impact} />)}
          </Stack>
        ) : null}

        <Typography
          variant="caption"
          color="text.disabled"
          title={revision.fingerprint}
          sx={{ display: 'block', mt: 2, fontFamily: 'monospace' }}
        >
          {revision.customerRfqReference ? `${revision.customerRfqReference} · ` : ''}fingerprint {revision.fingerprint.slice(0, 12)}…
        </Typography>
      </AccordionDetails>
    </Accordion>
  );
};

/**
 * What changed, revision by revision. Every field the server compared used to be printed,
 * changed or not, so a rep read forty "Unchanged" rows to find the one line the customer
 * amended. Only real changes are shown; the rest of the record is one collapsed row.
 */
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
        <Typography variant="h6" component="h2" sx={{ fontWeight: 900 }}>Revision history</Typography>
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
        <Alert severity="info">No revisions have been recorded yet.</Alert>
      )}

      <Stack spacing={1.5}>
        {revisionsQuery.data?.map((revision, index) => (
          <Revision key={revision.id} revision={revision} isCurrent={index === 0} />
        ))}
      </Stack>
    </Box>
  );
}
