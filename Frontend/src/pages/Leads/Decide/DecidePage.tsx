import React from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Accordion,
  AccordionDetails,
  AccordionSummary,
  Alert,
  AlertTitle,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Divider,
  FormControlLabel,
  FormGroup,
  Link,
  Paper,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material';
import { ExpandMore as ExpandIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import leadDecisionService, {
  type LeadDecisionWorkbenchDTO,
} from '../../../api/services/leadDecisionService';
import decisionService from '../../../api/services/decisionService';
import lifecycleService from '../../../api/services/commercialLifecycleService';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { formatDateSafe } from '../../../utils/dates';
import {
  commercialActionPermissions,
  hasCommercialDecisionAuthority,
} from '../../../utils/commercialActionPermissions';
import { useUnsavedWorkGuard } from '../../../hooks/useUnsavedWorkGuard';
import ResolveClientDialog from '../ResolveClientDialog';
import FullNoBidCommitDialog from '../Workbench/FullNoBidCommitDialog';
import RfqRevisionImpactResolutionDialog from '../Workbench/RfqRevisionImpactResolutionDialog';
import LegacyDecisionRecordNotice from '../Workbench/LegacyDecisionRecordNotice';
import SourceEvidencePanel from '../Workbench/SourceEvidencePanel';
import { retryOperation, type RetryOperation } from '../Workbench/retryIdempotency';
import {
  countDecisions,
  decisionRecordIsLocked,
  initializeDecisionMap,
  type DecisionMap,
  type EditableLineDecision,
} from '../Workbench/workbenchRules';
import LinesTable from './LinesTable';
import {
  buildFitRequest,
  buildParticipationRequest,
  concernFromSaved,
  CONCERN_LABELS,
  criterionCodes,
  daysUntil,
  dueSentence,
  fitMatchesSaved,
  newId,
  nextThing,
  QUALIFIED,
  qualificationStep,
  recommendationLabel,
  TERMINAL_BLOCKERS,
  type ConcernState,
} from './decideRules';

type Mode = 'rfq' | 'draft' | 'decline';

interface FormValue { decisions: DecisionMap; concern: ConcernState }

const Fact: React.FC<{ label: string; value: React.ReactNode; tone?: 'default' | 'due' | 'late' }> = ({ label, value, tone = 'default' }) => (
  <Box sx={{ minWidth: 0 }}>
    <Typography variant="caption" color="text.secondary" sx={{ letterSpacing: '.06em', textTransform: 'uppercase', fontWeight: 700 }}>
      {label}
    </Typography>
    <Typography
      sx={{
        fontWeight: 600,
        fontVariantNumeric: 'tabular-nums',
        overflowWrap: 'anywhere',
        color: tone === 'late' ? 'error.main' : tone === 'due' ? 'warning.main' : 'text.primary',
      }}
    >
      {value}
    </Typography>
  </Box>
);

/**
 * The lead decision on one screen.
 *
 * Who is asking, what they want, one choice per line, one optional question, one button. The
 * button chains the fit assessment, the committed participation decision, the lifecycle
 * qualification and the RFQ promotion the server requires, in that order, with generated
 * idempotency keys. The rep never sees the four as separate stages, and the audit trail records
 * all four exactly as before.
 */
const DecidePage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const leadId = Number(id);
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission, userData } = useAuth();
  const [searchParams] = useSearchParams();
  const commercialAccess = commercialActionPermissions(hasPermission);
  const isManager = hasCommercialDecisionAuthority(userData);
  const canEdit = commercialAccess.canEditLeadDecision;
  const canPromote = commercialAccess.canPromoteLeadToRfq && isManager;

  const [decisions, setDecisions] = React.useState<DecisionMap>({});
  const [concern, setConcern] = React.useState<ConcernState>({ raised: false, codes: [], note: '' });
  const [busy, setBusy] = React.useState<string | null>(null);
  const [customerDialogOpen, setCustomerDialogOpen] = React.useState(false);
  const [declineOpen, setDeclineOpen] = React.useState(false);
  const [rfqImpactOpen, setRfqImpactOpen] = React.useState(false);
  const [historyOpen, setHistoryOpen] = React.useState(() => ['evidence', 'validate'].includes(searchParams.get('stage') ?? ''));
  const seed = React.useRef<string | null>(null);
  const fitOperation = React.useRef<RetryOperation | null>(null);
  const participationOperation = React.useRef<RetryOperation | null>(null);
  const promotionKey = React.useRef<string | null>(null);
  const promotionRevision = React.useRef<number | null>(null);
  const rfqImpactKey = React.useRef<string | null>(null);

  const workbenchQuery = useQuery({
    queryKey: ['lead-decision-workbench', leadId],
    queryFn: () => leadDecisionService.getWorkbench(leadId),
    enabled: Number.isFinite(leadId) && leadId > 0,
    retry: 1,
  });
  const briefQuery = useQuery({
    queryKey: ['lead-decision-brief', leadId],
    queryFn: () => decisionService.getDecisionBrief(leadId),
    enabled: Number.isFinite(leadId) && leadId > 0,
    retry: false,
  });
  const lifecycleQuery = useQuery({
    queryKey: ['lifecycle', 'leads', leadId],
    queryFn: () => lifecycleService.getState('leads', leadId),
    enabled: Number.isFinite(leadId) && leadId > 0,
    retry: false,
  });

  const workbench = workbenchQuery.data;

  React.useEffect(() => {
    if (!workbench) return;
    const nextSeed = [workbench.leadRevisionId, workbench.participationVersion ?? 'none', workbench.participationStatus, workbench.fitAssessment?.version ?? 0].join(':');
    if (seed.current !== nextSeed) {
      setDecisions(initializeDecisionMap(workbench));
      setConcern(concernFromSaved(workbench.fitAssessment));
      seed.current = nextSeed;
    }
    if (promotionRevision.current !== workbench.leadRevisionId) {
      promotionKey.current = `lead-promotion:${leadId}:${workbench.leadRevisionId}:${newId()}`;
      rfqImpactKey.current = `rfq-impact-review:${leadId}:${workbench.leadRevisionId}:${newId()}`;
      promotionRevision.current = workbench.leadRevisionId;
    }
  }, [leadId, workbench]);

  const formValue = React.useMemo<FormValue>(() => ({ decisions, concern }), [decisions, concern]);
  const guard = useUnsavedWorkGuard<FormValue>({
    storageKey: workbench ? `nexora.lead-decision.${leadId}.revision.${workbench.leadRevisionId}` : '',
    value: formValue,
    enabled: Boolean(workbench && seed.current),
    leaveMessage: 'You have unsaved choices on this request. Leave without saving them?',
  });

  const refresh = React.useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ['lead-decision-workbench', leadId] });
    await queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] });
    await queryClient.invalidateQueries({ queryKey: ['lifecycle', 'leads', leadId] });
  }, [leadId, queryClient]);

  const freshWorkbench = React.useCallback(
    () => queryClient.fetchQuery({
      queryKey: ['lead-decision-workbench', leadId],
      queryFn: () => leadDecisionService.getWorkbench(leadId),
      staleTime: 0,
    }),
    [leadId, queryClient],
  );

  const updateLine = React.useCallback((revisionLineId: number, patch: Partial<EditableLineDecision>) => {
    setDecisions((current) => ({
      ...current,
      [revisionLineId]: { ...(current[revisionLineId] ?? { decision: 'Pending' }), ...patch },
    }));
  }, []);

  const openDocument = React.useCallback(() => navigate(`/procurement/extraction/review/${leadId}`), [leadId, navigate]);

  /**
   * One click, four governed writes. Each write is idempotent and each refetch re-reads the
   * versions the next write must quote, so a failure half-way leaves a record the page can
   * simply re-derive its next sentence from.
   */
  const run = React.useCallback(async (mode: Mode, header?: { reasonCode: string; notes?: string }) => {
    if (!workbench) return;
    const codes = criterionCodes(workbench.fitAssessment);
    let current: LeadDecisionWorkbenchDTO = workbench;
    try {
      let fitSaved = false;
      if (!fitMatchesSaved(current.fitAssessment, concern, codes)) {
        setBusy('Recording the assessment…');
        const request = buildFitRequest(current, concern, codes);
        const operation = retryOperation(fitOperation.current, 'lead-fit', leadId, request);
        fitOperation.current = operation;
        await leadDecisionService.saveFitAssessment(leadId, request, operation.key);
        fitOperation.current = null;
        fitSaved = true;
        current = await freshWorkbench();
      }

      const commit = mode !== 'draft';
      const alreadyCommitted = current.participationStatus === 'COMMITTED' && !guard.isDirty && !fitSaved && !header;
      if (!(commit && alreadyCommitted)) {
        setBusy(commit ? 'Recording the decision…' : 'Saving for a manager…');
        const request = buildParticipationRequest(current, decisions, commit, header);
        const scope = commit ? 'lead-participation-commit' : 'lead-participation-draft';
        const operation = retryOperation(participationOperation.current, scope, leadId, request);
        participationOperation.current = operation;
        await leadDecisionService.saveParticipation(leadId, request, operation.key);
        participationOperation.current = null;
        guard.markSaved({ decisions, concern });
        current = await freshWorkbench();
      }

      if (mode === 'draft') {
        enqueueSnackbar('Saved. A manager can create the RFQ from here.', { variant: 'success' });
        await refresh();
        return;
      }
      if (mode === 'decline') {
        enqueueSnackbar('Request declined and recorded.', { variant: 'success' });
        await refresh();
        return;
      }

      const lifecycle = lifecycleQuery.data;
      if (lifecycle && qualificationStep(lifecycle) === 'transition') {
        setBusy('Qualifying the lead…');
        const option = lifecycle.allowedTransitions.find((candidate) => candidate.statusCode === QUALIFIED)!;
        await lifecycleService.transition('leads', leadId, lifecycle, option);
      }

      if (!current.participationVersion || !promotionKey.current) {
        throw new Error('The decision was recorded but no committed version came back. Refresh and try again.');
      }
      setBusy('Creating the RFQ…');
      const receipt = await leadDecisionService.promoteToRfq(leadId, {
        expectedLeadRevisionId: current.leadRevisionId,
        expectedDecisionVersion: current.decisionVersion,
        expectedParticipationVersion: current.participationVersion,
        idempotencyKey: promotionKey.current,
      });
      guard.markSaved({ decisions, concern });
      enqueueSnackbar(
        `RFQ ${receipt.rfqNumber || `#${receipt.rfqId}`} created with ${receipt.promotedLineCount} line${receipt.promotedLineCount === 1 ? '' : 's'}.`,
        { variant: 'success' },
      );
      await refresh();
      if (commercialAccess.canViewPromotedRfq) navigate(`/procurement/rfqs/view/${receipt.rfqId}`);
    } catch (error: unknown) {
      enqueueSnackbar(presentableErrorMessage(error, 'That did not go through. Nothing was changed.'), { variant: 'error' });
      await refresh();
    } finally {
      setBusy(null);
    }
  }, [commercialAccess.canViewPromotedRfq, concern, decisions, enqueueSnackbar, freshWorkbench, guard, leadId, lifecycleQuery.data, navigate, refresh, workbench]);

  const resolveRfqImpact = React.useCallback(async (reason: string) => {
    if (!workbench?.promotion || !rfqImpactKey.current) return;
    setBusy('Recording the review…');
    try {
      const result = await leadDecisionService.resolveRfqRevisionImpact(leadId, {
        rfqId: workbench.promotion.rfqId,
        expectedLeadRevisionId: workbench.leadRevisionId,
        reconciliationReason: reason,
        confirmedHistoricalRfqUnchanged: true,
      }, rfqImpactKey.current);
      setRfqImpactOpen(false);
      enqueueSnackbar(result.resolvedImpactCount > 0 ? 'Amendment review recorded.' : 'This amendment review was already recorded.', { variant: 'success' });
      await refresh();
    } catch (error: unknown) {
      enqueueSnackbar(presentableErrorMessage(error, 'The review could not be recorded. Nothing was changed.'), { variant: 'error' });
    } finally {
      setBusy(null);
    }
  }, [enqueueSnackbar, leadId, refresh, workbench]);

  if (workbenchQuery.isLoading) {
    return (
      <Box sx={{ minHeight: '60vh', display: 'grid', placeItems: 'center' }}>
        <Stack spacing={1.5} sx={{ alignItems: 'center' }}>
          <CircularProgress />
          <Typography color="text.secondary">Loading the request…</Typography>
        </Stack>
      </Box>
    );
  }

  if (workbenchQuery.isError || !workbench) {
    return (
      <Box sx={{ p: { xs: 1, sm: 3 }, maxWidth: 760, mx: 'auto' }}>
        <Alert severity="error" action={<Button color="inherit" onClick={() => workbenchQuery.refetch()}>Retry</Button>}>
          <AlertTitle>This request could not be loaded</AlertTitle>
          Nothing was changed. Retry, or go back to the lead.
        </Alert>
        <Button onClick={() => navigate(`/procurement/leads/view/${leadId}`)} sx={{ mt: 2 }}>Back to the lead</Button>
      </Box>
    );
  }

  const counts = countDecisions(decisions);
  const locked = decisionRecordIsLocked(workbench, decisions);
  const declined = locked && !workbench.promotion && counts.total > 0 && counts.noBid === counts.total;
  const terminal = workbench.blockers.filter((blocker) => TERMINAL_BLOCKERS.has(blocker.code));
  const rfqRevisionBlocker = terminal.find((blocker) => blocker.code === 'RFQ_REVISION_REQUIRED');
  const legacyBlocker = terminal.find((blocker) => blocker.code === 'LEGACY_RFQ');
  const inconsistentBlocker = terminal.find((blocker) => blocker.code === 'INCONSISTENT_CONVERTED_STATE');
  // Locked records and view-only roles read as text. A save in flight keeps the controls on
  // screen and only the button changes, so the page does not flicker mid-click.
  const readOnly = locked || !canEdit;
  const next = nextThing({ workbench, decisions, concern, lifecycle: lifecycleQuery.data, leadId });
  const days = daysUntil(workbench.bidClosingDate);
  const dueTone = days == null ? 'default' : days < 0 ? 'late' : days <= 3 ? 'due' : 'default';
  const brief = briefQuery.data;
  const codes = criterionCodes(workbench.fitAssessment);
  const quoted = counts.bid;
  const reference = workbench.customerRfqReference || `Lead #${leadId}`;
  const rfqLabel = workbench.promotion ? (workbench.promotion.rfqNumber || `RFQ #${workbench.promotion.rfqId}`) : null;

  const primary = (() => {
    if (busy) return { label: busy, disabled: true, onClick: () => undefined };
    if (!canEdit) return { label: 'Create RFQ', disabled: true, onClick: () => undefined };
    if (next.kind === 'decline') return { label: 'Decline request', disabled: false, onClick: () => setDeclineOpen(true) };
    if (next.kind === 'concern') return { label: 'Save for review', disabled: false, onClick: () => run('draft') };
    if (!canPromote) return { label: 'Save for a manager', disabled: next.kind !== 'ready', onClick: () => run('draft') };
    return { label: 'Create RFQ', disabled: next.kind !== 'ready', onClick: () => run('rfq') };
  })();

  const footerSentence = (() => {
    if (busy) return 'Please wait.';
    if (!canEdit) return 'Your role can view this request but not decide it.';
    if (next.kind === 'blocked' || next.kind === 'closed') return next.sentence;
    if (next.kind === 'decline') return 'Every line is skipped. Declining records the reason and closes the request without an RFQ.';
    if (next.kind === 'concern') return 'A concern stops the RFQ. Saving records it for a manager to review, or skip every line to decline.';
    if (!canPromote) return 'Records the assessment and your choices. A manager creates the RFQ.';
    return 'Records the assessment, the decision and the RFQ together.';
  })();

  return (
    <Box sx={{ p: { xs: 1, sm: 2, md: 3 }, maxWidth: 1120, mx: 'auto', minWidth: 0 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1.5, flexWrap: 'wrap' }}>
        <Link component="button" type="button" variant="caption" onClick={() => navigate('/procurement/leads/all')} sx={{ fontWeight: 700, textTransform: 'uppercase', textDecoration: 'none', color: 'text.secondary' }}>
          Leads
        </Link>
        <Typography variant="caption" color="text.disabled">›</Typography>
        <Link component="button" type="button" variant="caption" onClick={() => navigate(`/procurement/leads/view/${leadId}`)} sx={{ fontWeight: 700, textTransform: 'uppercase', textDecoration: 'none', color: 'text.secondary' }}>
          {reference}
        </Link>
      </Stack>

      {guard.recoveredDraft && !locked ? (
        <Alert
          severity="info"
          sx={{ mb: 1.5 }}
          action={(
            <Stack direction="row" spacing={0.5}>
              <Button color="inherit" onClick={() => { setDecisions(guard.recoveredDraft!.value.decisions); setConcern(guard.recoveredDraft!.value.concern); guard.acceptRecovered(); }}>Restore</Button>
              <Button color="inherit" onClick={guard.discardRecovered}>Discard</Button>
            </Stack>
          )}
        >
          You left unsaved choices here on {formatDateSafe(guard.recoveredDraft.savedAt)}. Restore them, or keep what is saved.
        </Alert>
      ) : null}

      {workbench.promotion ? (
        <Alert
          severity="success"
          sx={{ mb: 1.5 }}
          action={commercialAccess.canViewPromotedRfq
            ? <Button color="inherit" onClick={() => navigate(`/procurement/rfqs/view/${workbench.promotion!.rfqId}`)}>Open the RFQ</Button>
            : undefined}
        >
          <AlertTitle>RFQ {rfqLabel} created</AlertTitle>
          {workbench.promotion.promotedLineCount} of {workbench.lines.length} line{workbench.lines.length === 1 ? '' : 's'} carried over on {formatDateSafe(workbench.promotion.promotedAtUtc)}
          {workbench.promotion.promotedBy ? ` by ${workbench.promotion.promotedBy}` : ''}. Already promoted, so nothing here can create a second one.
        </Alert>
      ) : null}

      {declined ? (
        <Alert severity="info" sx={{ mb: 1.5 }}>
          <AlertTitle>Request declined</AlertTitle>
          Every line was skipped and the reason is recorded. No RFQ was created.
        </Alert>
      ) : null}

      {rfqRevisionBlocker && workbench.promotion ? (
        <Alert
          severity="warning"
          sx={{ mb: 1.5 }}
          action={commercialAccess.canResolveRfqRevisionImpact && isManager
            ? <Button color="inherit" onClick={() => setRfqImpactOpen(true)}>Review the change</Button>
            : undefined}
        >
          <AlertTitle>The customer changed this request after the RFQ was created</AlertTitle>
          {rfqRevisionBlocker.message}
        </Alert>
      ) : null}

      {legacyBlocker ? (
        <LegacyDecisionRecordNotice
          message={legacyBlocker.message}
          actionLabel={commercialAccess.canViewPromotedRfq ? legacyBlocker.actionLabel : null}
          onOpenRfq={legacyBlocker.actionPath ? () => navigate(legacyBlocker.actionPath!) : undefined}
        />
      ) : null}

      {inconsistentBlocker ? (
        <Alert severity="error" sx={{ mb: 1.5 }}>
          <AlertTitle>This record needs an administrator</AlertTitle>
          {inconsistentBlocker.message}
        </Alert>
      ) : null}

      <Paper variant="outlined" sx={{ borderRadius: 3, overflow: 'hidden' }}>
        {/* WHO IS ASKING */}
        <Box component="section" aria-labelledby="decide-customer" sx={{ p: { xs: 2, sm: 3 }, borderBottom: 1, borderColor: 'divider' }}>
          <Typography variant="caption" color="text.secondary" sx={{ letterSpacing: '.12em', textTransform: 'uppercase', fontWeight: 700 }}>
            Request
          </Typography>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'baseline', flexWrap: 'wrap' }}>
            <Typography id="decide-customer" component="h1" variant="h5" sx={{ fontWeight: 700, letterSpacing: '-0.01em' }}>
              {workbench.customerName || 'Customer not matched yet'}
            </Typography>
            {!locked && commercialAccess.canLinkLeadClient ? (
              <Link component="button" type="button" onClick={() => setCustomerDialogOpen(true)} sx={{ fontWeight: 700 }}>
                {workbench.customerId ? 'Not them? Change' : 'Choose the customer'}
              </Link>
            ) : null}
          </Stack>
          <Stack direction="row" spacing={{ xs: 2.5, sm: 4 }} sx={{ mt: 2, flexWrap: 'wrap', rowGap: 1.5 }}>
            <Fact label="Their reference" value={workbench.customerRfqReference || 'Not stated'} />
            <Fact label="Received" value={formatDateSafe(workbench.receivedAtUtc)} />
            <Fact
              label="Quote due"
              tone={dueTone}
              value={workbench.bidClosingDate ? `${formatDateSafe(workbench.bidClosingDate)} · ${dueSentence(days)}` : 'No deadline stated'}
            />
            <Fact label="Deliver to" value={workbench.deliveryLocation || 'Not stated'} />
            <Fact label="Needed by" value={workbench.requiredDeliveryDate ? formatDateSafe(workbench.requiredDeliveryDate) : 'Not stated'} />
            {workbench.assignedToName ? <Fact label="Owner" value={workbench.assignedToName} /> : null}
          </Stack>
        </Box>

        {/* WHAT THEY WANT */}
        <Box component="section" aria-labelledby="decide-lines" sx={{ pt: 2 }}>
          <Stack direction="row" spacing={2} sx={{ alignItems: 'baseline', justifyContent: 'space-between', px: { xs: 2, sm: 3 }, pb: 1 }}>
            <Typography id="decide-lines" component="h2" variant="subtitle1" sx={{ fontWeight: 700 }}>What they want</Typography>
            <Typography variant="body2" color="text.secondary" sx={{ fontVariantNumeric: 'tabular-nums' }}>
              <Box component="b" sx={{ color: 'text.primary' }}>{quoted}</Box> of {workbench.lines.length} lines to quote
            </Typography>
          </Stack>
          <LinesTable
            leadId={leadId}
            lines={workbench.lines}
            decisions={decisions}
            unitOptions={workbench.unitOptions ?? []}
            currencyOptions={workbench.currencyOptions ?? []}
            reasonCodes={workbench.reasonCodes}
            readOnly={readOnly}
            onChange={updateLine}
            onOpenDocument={openDocument}
          />
        </Box>

        {/* THE DECISION */}
        {!locked ? (
          <Box component="section" aria-labelledby="decide-question" sx={{ p: { xs: 2, sm: 3 }, bgcolor: 'action.hover', borderTop: 1, borderColor: 'divider' }}>
            {brief ? (
              <Paper variant="outlined" sx={{ p: 2, mb: 2, borderLeft: 3, borderLeftColor: 'primary.main', borderRadius: 2 }}>
                <Stack direction="row" spacing={1.5} sx={{ alignItems: 'baseline', flexWrap: 'wrap' }}>
                  <Typography sx={{ fontWeight: 600 }}>
                    Nexora&apos;s read: <Box component="b" sx={{ color: 'primary.main' }}>{recommendationLabel(brief.recommendation)}</Box>
                  </Typography>
                  <Typography variant="caption" color="text.secondary">from the decision brief for this lead</Typography>
                </Stack>
                {brief.reasons?.length ? (
                  <Stack component="ul" spacing={0.25} sx={{ m: 0, mt: 0.75, pl: 2.25 }}>
                    {brief.reasons.slice(0, 4).map((reason) => (
                      <Typography key={reason} component="li" variant="body2" color="text.secondary">{reason}</Typography>
                    ))}
                  </Stack>
                ) : null}
              </Paper>
            ) : null}

            <Typography id="decide-question" component="h2" sx={{ fontWeight: 700, mb: 1 }}>
              {concern.raised ? 'What concerns you about taking this job on?' : 'Any concern about taking this job on?'}
            </Typography>
            <ToggleButtonGroup
              exclusive
              size="small"
              value={concern.raised ? 'some' : 'none'}
              aria-labelledby="decide-question"
              disabled={readOnly}
              onChange={(_event, value: 'none' | 'some' | null) => {
                if (!value) return;
                setConcern((current) => value === 'none' ? { raised: false, codes: [], note: '' } : { ...current, raised: true });
              }}
            >
              <ToggleButton value="none" sx={{ px: 2, fontWeight: 700 }}>No concerns</ToggleButton>
              <ToggleButton value="some" sx={{ px: 2, fontWeight: 700 }}>Yes, raise a concern</ToggleButton>
            </ToggleButtonGroup>

            {concern.raised ? (
              <Paper variant="outlined" sx={{ p: 2, mt: 1.5, borderRadius: 2 }}>
                <FormGroup row aria-label="Which part concerns you" sx={{ gap: 1 }}>
                  {codes.map((code) => (
                    <FormControlLabel
                      key={code}
                      control={(
                        <Checkbox
                          size="small"
                          checked={concern.codes.includes(code)}
                          disabled={readOnly}
                          onChange={(event) => setConcern((current) => ({
                            ...current,
                            codes: event.target.checked
                              ? [...current.codes, code]
                              : current.codes.filter((existing) => existing !== code),
                          }))}
                        />
                      )}
                      label={CONCERN_LABELS[code] ?? code}
                    />
                  ))}
                </FormGroup>
                <TextField
                  fullWidth
                  multiline
                  minRows={2}
                  size="small"
                  label="What is the concern?"
                  placeholder="One or two lines is enough."
                  value={concern.note}
                  disabled={readOnly}
                  onChange={(event) => setConcern((current) => ({ ...current, note: event.target.value.slice(0, 1000) }))}
                  sx={{ mt: 1.5 }}
                />
              </Paper>
            ) : null}

            <Divider sx={{ my: 2 }} />
            <Stack direction="row" spacing={2} sx={{ alignItems: 'center', flexWrap: 'wrap', rowGap: 1 }}>
              <Button
                variant="contained"
                size="large"
                disabled={primary.disabled}
                onClick={primary.onClick}
                sx={{ fontWeight: 800, px: 3 }}
              >
                {primary.label}
              </Button>
              <Typography
                role="status"
                variant="body2"
                sx={{ color: next.kind === 'ready' || next.kind === 'decline' || busy ? 'text.secondary' : 'warning.dark', fontWeight: next.kind === 'blocked' ? 600 : 400 }}
              >
                {footerSentence}
                {next.kind === 'blocked' && next.action ? (
                  <>
                    {' '}
                    <Link component="button" type="button" onClick={() => navigate(next.action!.path)} sx={{ fontWeight: 700, verticalAlign: 'baseline' }}>
                      {next.action.label}
                    </Link>
                  </>
                ) : null}
              </Typography>
            </Stack>
            {workbench.participationStatus === 'DRAFT' && !busy ? (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                Saved as a draft{workbench.assignedToName ? ` for ${workbench.assignedToName}` : ''}. A manager creates the RFQ from here.
              </Typography>
            ) : null}
          </Box>
        ) : null}
      </Paper>

      {/* HISTORY: the machinery, off the path */}
      <Accordion
        expanded={historyOpen}
        onChange={(_event, expanded) => setHistoryOpen(expanded)}
        variant="outlined"
        disableGutters
        sx={{ mt: 2, borderRadius: 3, '&::before': { display: 'none' } }}
      >
        <AccordionSummary expandIcon={<ExpandIcon />} aria-controls="decide-history" id="decide-history-summary">
          <Typography sx={{ fontWeight: 600 }}>History and evidence</Typography>
          <Chip size="small" label={`Revision ${workbench.leadRevisionNumber}`} variant="outlined" sx={{ ml: 1.5 }} />
        </AccordionSummary>
        <AccordionDetails id="decide-history">
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: '62ch' }}>
            Everything the system recorded about where this request came from. Auditors need it. Nobody deciding whether to bid does.
          </Typography>
          <Stack direction="row" spacing={{ xs: 2.5, sm: 4 }} sx={{ flexWrap: 'wrap', rowGap: 1.5, mb: 2 }}>
            <Fact label="Nexora serial" value={workbench.nexoraSerial || 'Not issued'} />
            <Fact label="Lead status" value={workbench.lifecycleStatusLabel || workbench.lifecycleStatusCode} />
            <Fact
              label="Lines checked against source"
              value={workbench.sourceCoverage ? `${workbench.sourceCoverage.coveredLines} of ${workbench.sourceCoverage.totalLines}` : 'Not recorded'}
            />
            <Fact
              label="Verified by"
              value={workbench.verifiedBy ? `${workbench.verifiedBy} · ${formatDateSafe(workbench.verifiedAtUtc)}` : 'Not yet'}
            />
            <Fact
              label="Assessment"
              value={workbench.fitAssessment && workbench.fitAssessment.version > 0
                ? `v${workbench.fitAssessment.version} · ${workbench.fitAssessment.assessedBy || 'unknown'} · ${formatDateSafe(workbench.fitAssessment.assessedAtUtc)}`
                : 'Not yet recorded'}
            />
            <Fact label="Decision" value={`${workbench.participationStatus.toLowerCase()}${workbench.participationVersion ? ` · v${workbench.participationVersion}` : ''}`} />
          </Stack>
          {historyOpen ? <SourceEvidencePanel workbench={workbench} compact /> : null}
        </AccordionDetails>
      </Accordion>

      <ResolveClientDialog
        open={customerDialogOpen}
        leadId={customerDialogOpen ? leadId : null}
        prefill={{ email: workbench.senderEmail, contactName: workbench.buyerName }}
        onClose={() => setCustomerDialogOpen(false)}
        onResolved={() => { setCustomerDialogOpen(false); void refresh(); }}
      />

      <FullNoBidCommitDialog
        open={declineOpen}
        lineCount={workbench.lines.length}
        reasonCodes={workbench.reasonCodes}
        lines={workbench.lines}
        decisions={decisions}
        onCancel={() => setDeclineOpen(false)}
        onConfirm={(reasonCode, notes) => { setDeclineOpen(false); void run('decline', { reasonCode, notes }); }}
      />

      {workbench.promotion ? (
        <RfqRevisionImpactResolutionDialog
          open={rfqImpactOpen}
          rfqLabel={rfqLabel!}
          leadRevisionNumber={workbench.leadRevisionNumber}
          saving={Boolean(busy)}
          onCancel={() => setRfqImpactOpen(false)}
          onConfirm={(reason) => { void resolveRfqImpact(reason); }}
        />
      ) : null}
    </Box>
  );
};

export default DecidePage;
