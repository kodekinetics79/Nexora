import React from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, Button, CircularProgress, Collapse, Divider, Typography,
} from '@mui/material';
import { ExpandMore as ExpandMoreIcon, ExpandLess as ExpandLessIcon, History as HistoryIcon } from '@mui/icons-material';
import commercialRoutingService, { type LeadAssignmentHistoryEntry } from '../../api/services/commercialRoutingService';
import { routingDecisionSentence } from '../../utils/routingDecisionReasons';
import { formatDateSafe } from '../../utils/dates';

/**
 * Who has owned this inquiry, and why it moved.
 *
 * `GET /api/commercial-intelligence/leads/{id}/assignment-history` has been recording every
 * reassignment — previous owner, new owner, reason and timestamp — since governed routing shipped,
 * and had ZERO frontend callers. A complete audit trail was being written for nobody to read, so
 * the question "who had this before me, and who moved it" could only be answered from the
 * database. This renders it, collapsed, under the control that writes it.
 *
 * Collapsed by default on purpose: the answer a rep needs 99 times out of 100 is the CURRENT
 * owner, which is the button above. History is the rare-but-important case, so it is present and
 * quiet rather than absent or loud.
 */

/**
 * Plain English for `LeadAssignment.AssignmentScope`, which crosses the wire as its C# enum name.
 * "LeadOnly" is by far the common case and says nothing a reader needs, so it renders as nothing
 * at all rather than as jargon. An unrecognised scope is dropped, never printed raw.
 */
const SCOPE_SENTENCES: Record<string, string> = {
  CustomerPermanent: 'Also made the permanent owner of this customer',
  CustomerTemporary: 'Temporary cover for this customer',
  Branch: 'Assigned as part of a branch rule',
  ProductCategory: 'Assigned as part of a product-category rule',
  SharedBackup: 'Assigned as the backup owner',
};

export const assignmentScopeLabel = (scope?: string | null): string | null =>
  (scope && SCOPE_SENTENCES[scope.trim()]) || null;

/** One line saying what happened, in the words a person would use. */
export const ownerChangeSentence = (entry: LeadAssignmentHistoryEntry): string => {
  const to = entry.ownerName?.trim() || 'someone no longer in this business unit';
  const from = entry.previousOwnerName?.trim();
  if (entry.previousOwnerUserId == null) return `Assigned to ${to}`;
  return `Moved from ${from || 'a former owner'} to ${to}`;
};

const HistoryRow: React.FC<{ entry: LeadAssignmentHistoryEntry }> = ({ entry }) => {
  const scope = assignmentScopeLabel(entry.scope);
  // The stored reason is a decision CODE (`MANUAL_ASSIGNMENT`, `PRIMARY_OWNER_ASSIGNED`, …).
  // The shared mapper turns it into the sentence it stands for; a free-text comment a person
  // typed beats it, because that is the actual reason somebody gave.
  const comment = entry.comment?.trim();
  const reason = comment || routingDecisionSentence(entry.reasonCode);
  return (
    <Box sx={{ py: 1, '&:not(:last-of-type)': { borderBottom: '1px solid', borderColor: 'divider' } }}>
      <Typography variant="body2" sx={{ fontWeight: 700 }}>{ownerChangeSentence(entry)}</Typography>
      <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
        {formatDateSafe(entry.effectiveFrom)}
        {entry.effectiveTo ? ` — until ${formatDateSafe(entry.effectiveTo)}` : ' — still in force'}
      </Typography>
      {reason && (
        <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.25 }}>{reason}</Typography>
      )}
      {scope && (
        <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>{scope}</Typography>
      )}
    </Box>
  );
};

const LeadOwnerHistory: React.FC<{ leadId: number }> = ({ leadId }) => {
  const [open, setOpen] = React.useState(false);

  // Fetched only once opened: this is the rare case, and a detail page should not pay for it
  // on every load.
  const history = useQuery({
    queryKey: ['lead-assignment-history', leadId],
    queryFn: () => commercialRoutingService.getLeadAssignmentHistory(leadId),
    enabled: open,
    staleTime: 30_000,
  });

  const entries = history.data ?? [];

  return (
    <Box sx={{ mt: 1 }}>
      <Button
        size="small"
        variant="text"
        startIcon={<HistoryIcon fontSize="small" />}
        endIcon={open ? <ExpandLessIcon /> : <ExpandMoreIcon />}
        onClick={() => setOpen((current) => !current)}
        aria-expanded={open}
        sx={{ fontWeight: 700, textTransform: 'none', px: 0.5 }}
      >
        Owner history
      </Button>
      <Collapse in={open} unmountOnExit>
        <Box sx={{ pl: 0.5, pr: 1, pb: 1, maxWidth: 520 }}>
          <Divider sx={{ mb: 1 }} />

          {history.isLoading && (
            <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 1 }}>
              <CircularProgress size={16} />
              <Typography variant="body2" color="text.secondary">Reading the trail…</Typography>
            </Box>
          )}

          {/* A trail that failed to load is not an empty trail. Saying "never reassigned" over a
              failed request would be a claim about the record that nobody checked. */}
          {history.isError && (
            <Alert
              severity="error"
              sx={{ borderRadius: 2 }}
              action={
                <Button color="inherit" size="small" onClick={() => history.refetch()} sx={{ fontWeight: 700 }}>
                  Retry
                </Button>
              }
            >
              We couldn&apos;t load the owner history. No empty history has been assumed.
            </Alert>
          )}

          {!history.isLoading && !history.isError && entries.length === 0 && (
            <Typography variant="body2" color="text.secondary" sx={{ py: 1 }}>
              This inquiry has never changed hands.
            </Typography>
          )}

          {!history.isError && entries.map((entry) => <HistoryRow key={entry.id} entry={entry} />)}
        </Box>
      </Collapse>
    </Box>
  );
};

export default LeadOwnerHistory;
