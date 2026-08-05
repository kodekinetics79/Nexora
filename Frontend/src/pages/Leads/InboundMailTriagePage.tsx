import { Fragment, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  Paper,
  Stack,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Tabs,
  TextField,
  Typography,
} from '@mui/material';
import {
  ExpandLess as CollapseIcon,
  ExpandMore as ExpandIcon,
  MarkEmailRead as InboxIcon,
  OpenInNew as OpenIcon,
  Refresh as RefreshIcon,
  Undo as ReprocessIcon,
} from '@mui/icons-material';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import { EmptyState, LoadingState } from '../../platform/components/States';
import { useAuth } from '../../context/AuthContext';
import emailTriageService, {
  describeTriageOutcome,
  describeTriageReason,
  isTriageUnavailable,
  TRIAGE_REASON_DESCRIPTIONS,
  type EmailTriageOutcome,
  type EmailTriageRow,
} from '../../api/services/emailTriageService';

/**
 * Inbound Mail — what the system decided about every message that reached the ingestion mailbox.
 *
 * The default tab is deliberately "Rejected as noise". A rejection is the only triage outcome that
 * produces nothing downstream, so it is the only one that can hide a lost deal. Making it the
 * landing view is the whole point of the screen: a rep can see, in one place, every enquiry the
 * machine decided not to look at — and put any of them back with a reason.
 *
 * Two rules govern the copy here:
 *  - a field the backend has not shipped renders as "Not reported", never as 0, blank or a guess;
 *  - for a conversational enquiry the prose IS the evidence, so the original message text is shown
 *    beside what came out of it, and the body/attachment split is always stated.
 */

const PAGE_SIZE = 25;

interface TriageTab {
  outcome: EmailTriageOutcome;
  label: string;
  blurb: string;
  emptyTitle: string;
  emptyMessage: string;
}

const TABS: TriageTab[] = [
  {
    outcome: 'Noise',
    label: 'Rejected as noise',
    blurb:
      'Messages stopped before any AI was spent — auto-replies, mailing lists, no-reply senders, calendar invites and replies with nothing new in them. Nothing was extracted, but every original email is retained and can be put back.',
    emptyTitle: 'No message has been rejected',
    emptyMessage: 'Nothing that reached the mailbox was classed as noise. Nothing is being hidden from you.',
  },
  {
    outcome: 'CommercialNonInquiry',
    label: 'Routed as supplier document',
    blurb:
      'Messages from a supplier that read as a quotation or an invoice. They are handled as commercial documents instead of being turned into a customer inquiry.',
    emptyTitle: 'No supplier document has been routed',
    emptyMessage: 'No inbound message has been recognised as a supplier quotation or invoice yet.',
  },
  {
    outcome: 'Uncertain',
    label: 'Uncertain',
    blurb:
      'Nothing decisive was found either way, so the message was extracted anyway and flagged. Uncertainty never stops a message — only positive evidence of noise does.',
    emptyTitle: 'No uncertain message',
    emptyMessage: 'Every message so far carried enough evidence for a definite decision.',
  },
  {
    outcome: 'Inquiry',
    label: 'Extracted',
    blurb:
      'Messages recognised as customer enquiries and sent for extraction — including free-prose enquiries written straight into the email body.',
    emptyTitle: 'No message has been extracted as an inquiry',
    emptyMessage: 'No inbound email has been recognised as a customer enquiry yet.',
  },
];

const NOT_REPORTED = 'Not reported';

const VISUALLY_HIDDEN = {
  position: 'absolute',
  width: 1,
  height: 1,
  p: 0,
  m: '-1px',
  overflow: 'hidden',
  whiteSpace: 'nowrap',
  border: 0,
  clipPath: 'inset(50%)',
} as const;

/** Absent, unparseable and present are three different things; only the last one shows a date. */
export const formatReceived = (value: string | null): string => {
  if (!value) return NOT_REPORTED;
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
};

const readableHint = (hint: string): string =>
  hint
    .replaceAll('_', ' ')
    .toLowerCase()
    .replace(/(^|\s)\S/g, (letter) => letter.toUpperCase());

/**
 * States the body/attachment split without inventing it. `Noise` is the one case the frontend can
 * state from the contract alone: a rejected message enqueues no jobs at all.
 */
const describeBodyRouting = (row: EmailTriageRow): string => {
  if (row.bodySubmitted === true) return 'The message text was submitted for extraction.';
  if (row.bodySubmitted === false) return 'The message text was not submitted for extraction.';
  if (row.outcome === 'Noise') return 'No extraction was attempted for this message.';
  return `Whether the message text was submitted is ${NOT_REPORTED.toLowerCase()}.`;
};

const describeAttachmentRouting = (row: EmailTriageRow): string => {
  if (row.hasAttachments === false) return 'No attachments came with this message.';
  if (row.hasAttachments === null) return `Attachments are ${NOT_REPORTED.toLowerCase()} for this message.`;
  if (row.attachmentCount === null) return 'Attachments came with this message and are extracted separately from the text.';
  return row.attachmentCount === 1
    ? '1 attachment came with this message and is extracted separately from the text.'
    : `${row.attachmentCount} attachments came with this message and are extracted separately from the text.`;
};

export default function InboundMailTriagePage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const canReprocess = hasPermission('Leads', 'create') || hasPermission('Leads', 'edit');

  const [tabIndex, setTabIndex] = useState(0);
  const [page, setPage] = useState(1);
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const [target, setTarget] = useState<EmailTriageRow | null>(null);
  const [reason, setReason] = useState('');
  const [reasonError, setReasonError] = useState<string | null>(null);
  const [confirmation, setConfirmation] = useState<string | null>(null);

  const activeTab = TABS[tabIndex] ?? TABS[0];

  const query = useQuery({
    queryKey: ['email-triage', activeTab.outcome, page],
    queryFn: () => emailTriageService.listTriage({ outcome: activeTab.outcome, page, pageSize: PAGE_SIZE }),
  });

  const reprocessMutation = useMutation({
    mutationFn: ({ id, why }: { id: number; why: string }) => emailTriageService.reprocess(id, why),
    onSuccess: (result, variables) => {
      setTarget(null);
      setReason('');
      setReasonError(null);
      setConfirmation(
        result.replayed === true
          ? `Message #${variables.id} was already sent back for extraction. Nothing was queued twice.`
          : `Message #${variables.id} was sent back through extraction as an inquiry.`,
      );
      void queryClient.invalidateQueries({ queryKey: ['email-triage'] });
    },
  });

  const rows = query.data?.items ?? [];
  const pageSize = query.data?.pageSize ?? PAGE_SIZE;
  const totalCount = query.data?.totalCount ?? null;
  const hasNextPage = useMemo(() => {
    if (totalCount !== null && pageSize) return page * pageSize < totalCount;
    return rows.length >= pageSize;
  }, [page, pageSize, rows.length, totalCount]);

  const changeTab = (nextIndex: number) => {
    setTabIndex(nextIndex);
    setPage(1);
    setExpandedId(null);
    setConfirmation(null);
  };

  const openReprocess = (row: EmailTriageRow) => {
    setTarget(row);
    setReason('');
    setReasonError(null);
    reprocessMutation.reset();
  };

  const submitReprocess = () => {
    if (!target) return;
    const trimmed = reason.trim();
    if (trimmed.length === 0) {
      // Overturning a machine decision is an audited act; the reason IS the record of who
      // disagreed and why. Never let it through empty.
      setReasonError('Give a reason. It is recorded against the override.');
      return;
    }
    setReasonError(null);
    reprocessMutation.mutate({ id: target.id, why: trimmed });
  };

  const unavailable = query.isError && isTriageUnavailable(query.error);

  return (
    <Box sx={{ maxWidth: 1500, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={2}
        sx={{ mb: 2, justifyContent: 'space-between', alignItems: { sm: 'flex-start' } }}
      >
        <Box>
          <Typography variant="h5" component="h1">
            Inbound Mail
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 780 }}>
            What the system decided about each message that arrived in the ingestion mailbox, and why. A decision here
            is reversible — every original email is kept.
          </Typography>
        </Box>
        <Button
          variant="outlined"
          startIcon={<RefreshIcon />}
          onClick={() => void query.refetch()}
          disabled={query.isFetching}
        >
          Refresh
        </Button>
      </Stack>

      <Tabs
        value={tabIndex}
        onChange={(_event, next: number) => changeTab(next)}
        aria-label="Triage decision"
        variant="scrollable"
        scrollButtons="auto"
        allowScrollButtonsMobile
        sx={{ borderBottom: 1, borderColor: 'divider', mb: 2 }}
      >
        {TABS.map((tab) => (
          <Tab key={tab.outcome} id={`triage-tab-${tab.outcome}`} aria-controls="triage-panel" label={tab.label} />
        ))}
      </Tabs>

      <Box role="tabpanel" id="triage-panel" aria-labelledby={`triage-tab-${activeTab.outcome}`}>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: 900 }}>
          {activeTab.blurb}
        </Typography>

        {confirmation && (
          <Alert severity="success" role="status" onClose={() => setConfirmation(null)} sx={{ mb: 2 }}>
            {confirmation}
          </Alert>
        )}

        {reprocessMutation.isError && (
          <ApiErrorNotice
            error={reprocessMutation.error}
            fallbackMessage="That message could not be sent back for extraction. Its stored original and current decision are unchanged — try again."
            sx={{ mb: 2 }}
          />
        )}

        {!canReprocess && (
          <Alert severity="info" sx={{ mb: 2 }}>
            You can review these decisions. Sending a message back through extraction needs the Leads permission.
          </Alert>
        )}

        {query.isLoading && <LoadingState label="Loading inbound mail decisions…" />}

        {unavailable && (
          <EmptyState
            title="Inbound mail triage is not available in this deployment yet"
            message="This screen lists what the mailbox decided about each message. The service behind it has not been enabled here, so there is nothing to show — no message has been hidden."
            icon={<InboxIcon sx={{ fontSize: 44 }} />}
          />
        )}

        {query.isError && !unavailable && (
          <ApiErrorNotice
            error={query.error}
            fallbackMessage="Inbound mail decisions could not be loaded. Nothing was changed — try again."
            onRetry={() => void query.refetch()}
          />
        )}

        {!query.isLoading && !query.isError && rows.length === 0 && (
          <EmptyState title={activeTab.emptyTitle} message={activeTab.emptyMessage} icon={<InboxIcon sx={{ fontSize: 44 }} />} />
        )}

        {!query.isError && rows.length > 0 && (
          <>
            <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
              <Table size="small" aria-label={`Messages ${activeTab.label.toLowerCase()}`} sx={{ minWidth: 1080 }}>
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ width: 48 }}>
                      {/* A column header that reads as empty is announced as nothing; name it for screen readers. */}
                      <Box component="span" sx={VISUALLY_HIDDEN}>
                        Show message
                      </Box>
                    </TableCell>
                    <TableCell>Received</TableCell>
                    <TableCell>From</TableCell>
                    <TableCell>Subject</TableCell>
                    <TableCell>Decision</TableCell>
                    <TableCell>Why</TableCell>
                    <TableCell align="right">Action</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {rows.map((row) => {
                    const outcome = describeTriageOutcome(row.outcome);
                    const expanded = expandedId === row.id;
                    const detailId = `triage-detail-${row.id}`;
                    const subject = row.subject ?? '(no subject)';
                    return (
                      <Fragment key={row.id}>
                      <TableRow hover>
                        <TableCell>
                          <IconButton
                            size="small"
                            aria-expanded={expanded}
                            aria-controls={detailId}
                            aria-label={expanded ? `Hide message ${subject}` : `Show message ${subject}`}
                            onClick={() => setExpandedId(expanded ? null : row.id)}
                          >
                            {expanded ? <CollapseIcon fontSize="small" /> : <ExpandIcon fontSize="small" />}
                          </IconButton>
                        </TableCell>
                        <TableCell>{formatReceived(row.receivedOn)}</TableCell>
                        <TableCell sx={{ overflowWrap: 'anywhere' }}>{row.from ?? NOT_REPORTED}</TableCell>
                        <TableCell sx={{ overflowWrap: 'anywhere' }}>
                          <Typography variant="body2" sx={{ fontWeight: row.subject ? 600 : 400 }}>
                            {subject}
                          </Typography>
                          {row.threadContinuation === true && (
                            <Typography variant="caption" color="text.secondary">
                              Reply in an existing thread
                            </Typography>
                          )}
                        </TableCell>
                        <TableCell>
                          <Chip size="small" color={outcome.chipColor} variant="outlined" label={outcome.label} />
                          {row.commercialDocumentTypeHint && (
                            <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
                              {readableHint(row.commercialDocumentTypeHint)}
                            </Typography>
                          )}
                        </TableCell>
                        <TableCell>
                          {row.reasonCodes.length === 0 ? (
                            <Typography variant="caption" color="text.secondary">
                              No reason recorded
                            </Typography>
                          ) : (
                            <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap' }}>
                              {row.reasonCodes.map((code) => (
                                <Chip key={code} size="small" variant="outlined" label={describeTriageReason(code)} />
                              ))}
                            </Stack>
                          )}
                        </TableCell>
                        <TableCell align="right">
                          {canReprocess && (
                            <Button
                              size="small"
                              startIcon={<ReprocessIcon />}
                              onClick={() => openReprocess(row)}
                              disabled={reprocessMutation.isPending}
                            >
                              Reprocess as inquiry
                            </Button>
                          )}
                        </TableCell>
                      </TableRow>
                      {expanded && (
                        <TableRow>
                          <TableCell colSpan={7} sx={{ bgcolor: 'action.hover' }}>
                            <Box
                              id={detailId}
                              role="region"
                              aria-label={`Message and extraction for ${subject}`}
                              sx={{
                                display: 'grid',
                                gap: 2,
                                gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' },
                                py: 1,
                              }}
                            >
                              <Box>
                                <Typography variant="subtitle2" component="h2" sx={{ fontWeight: 700 }}>
                                  Original message
                                </Typography>
                                {row.bodyPreview ? (
                                  <Typography
                                    variant="body2"
                                    component="pre"
                                    sx={{
                                      m: 0,
                                      mt: 0.5,
                                      p: 1.5,
                                      maxHeight: 320,
                                      overflow: 'auto',
                                      borderRadius: 1,
                                      bgcolor: 'background.paper',
                                      border: 1,
                                      borderColor: 'divider',
                                      whiteSpace: 'pre-wrap',
                                      overflowWrap: 'anywhere',
                                      fontFamily: 'inherit',
                                    }}
                                  >
                                    {row.bodyPreview}
                                  </Typography>
                                ) : (
                                  <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                                    The message text is not exposed by this deployment yet. The original email is
                                    retained, so nothing has been lost.
                                  </Typography>
                                )}
                                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                                  {describeBodyRouting(row)} {describeAttachmentRouting(row)}
                                </Typography>
                                {row.attachmentNamesReported && row.attachmentNames.length > 0 && (
                                  <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap', mt: 1 }}>
                                    {row.attachmentNames.map((name) => (
                                      <Chip key={name} size="small" label={name} variant="outlined" />
                                    ))}
                                  </Stack>
                                )}
                              </Box>

                              <Box>
                                <Typography variant="subtitle2" component="h2" sx={{ fontWeight: 700 }}>
                                  What the system did with it
                                </Typography>
                                <Typography variant="body2" sx={{ mt: 0.5 }}>
                                  {describeTriageOutcome(row.outcome).meaning}
                                </Typography>
                                {row.reasonCodes.length > 0 && (
                                  <Box component="ul" sx={{ pl: 2.5, mt: 1, mb: 0 }}>
                                    {row.reasonCodes.map((code) => (
                                      <Typography key={code} component="li" variant="caption" color="text.secondary">
                                        <strong>{describeTriageReason(code)}</strong>
                                        {TRIAGE_REASON_DESCRIPTIONS[code] ? ` — ${TRIAGE_REASON_DESCRIPTIONS[code]}` : ''}
                                      </Typography>
                                    ))}
                                  </Box>
                                )}
                                <Typography variant="body2" sx={{ mt: 1 }}>
                                  {row.extractedItemCount === null
                                    ? `Line items extracted: ${NOT_REPORTED.toLowerCase()}.`
                                    : `Line items extracted from this message: ${row.extractedItemCount}.`}
                                </Typography>
                                <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', mt: 1 }}>
                                  {row.linkedBatchId && (
                                    <Button
                                      size="small"
                                      startIcon={<OpenIcon />}
                                      onClick={() => navigate(`/procurement/leads/ingestion/${row.linkedBatchId}`)}
                                    >
                                      Open ingestion batch
                                    </Button>
                                  )}
                                  {row.leadId !== null && (
                                    <Button
                                      size="small"
                                      startIcon={<OpenIcon />}
                                      onClick={() => navigate(`/procurement/leads/view/${row.leadId}`)}
                                    >
                                      Open lead
                                    </Button>
                                  )}
                                </Stack>
                                {row.decidedOn && (
                                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                                    Decided {formatReceived(row.decidedOn)}
                                  </Typography>
                                )}
                              </Box>
                            </Box>
                          </TableCell>
                        </TableRow>
                      )}
                      </Fragment>
                    );
                  })}
                </TableBody>
              </Table>
            </TableContainer>

            <Stack
              direction="row"
              spacing={2}
              sx={{ mt: 2, alignItems: 'center', justifyContent: 'flex-end' }}
            >
              <Typography variant="body2" color="text.secondary" aria-live="polite">
                {totalCount === null
                  ? `Page ${page} — ${rows.length} message${rows.length === 1 ? '' : 's'} shown`
                  : `Page ${page} — showing ${rows.length} of ${totalCount}`}
              </Typography>
              <Button size="small" disabled={page <= 1 || query.isFetching} onClick={() => setPage((value) => Math.max(1, value - 1))}>
                Previous
              </Button>
              <Button size="small" disabled={!hasNextPage || query.isFetching} onClick={() => setPage((value) => value + 1)}>
                Next
              </Button>
            </Stack>
          </>
        )}
      </Box>

      <Dialog
        open={target !== null}
        onClose={() => (reprocessMutation.isPending ? undefined : setTarget(null))}
        aria-labelledby="reprocess-dialog-title"
        fullWidth
        maxWidth="sm"
      >
        <DialogTitle id="reprocess-dialog-title">Reprocess as inquiry</DialogTitle>
        <DialogContent>
          <DialogContentText component="div">
            <Typography variant="body2" component="p">
              {target?.subject ?? '(no subject)'} — from {target?.from ?? NOT_REPORTED}
            </Typography>
            <Typography variant="body2" component="p" sx={{ mt: 1 }}>
              The stored original is put back through ingestion and treated as uncertain, so it is extracted and
              flagged for review rather than dropped. Nothing already recorded is deleted.
            </Typography>
          </DialogContentText>
          <TextField
            label="Why is this an inquiry?"
            value={reason}
            onChange={(event) => {
              setReason(event.target.value);
              if (reasonError) setReasonError(null);
            }}
            required
            fullWidth
            multiline
            minRows={2}
            error={reasonError !== null}
            helperText={reasonError ?? 'Recorded against the override so the decision can be audited later.'}
            sx={{ mt: 2 }}
          />
          {reprocessMutation.isError && (
            <ApiErrorNotice
              error={reprocessMutation.error}
              fallbackMessage="That message could not be sent back for extraction. Its stored original and current decision are unchanged — try again."
              sx={{ mt: 2 }}
            />
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setTarget(null)} disabled={reprocessMutation.isPending}>
            Cancel
          </Button>
          <Button variant="contained" onClick={submitReprocess} disabled={reprocessMutation.isPending}>
            {reprocessMutation.isPending ? 'Sending…' : 'Reprocess as inquiry'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
