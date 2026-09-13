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
  FormControlLabel,
  FormGroup,
  Link,
  Menu,
  MenuItem,
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
import leadService from '../../../api/services/leadService';
import LeadOwnerControl from '../LeadOwnerControl';
import NextStepPanel from '../../../components/common/NextStepPanel';
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
import CheckDocumentDialog, { type ConfirmedLine } from './CheckDocumentDialog';
import { decisionLabel } from '../decisionRead';
import {
  applyUnitToUnitless,
  buildFitRequest,
  buildParticipationRequest,
  carryChoices,
  concernFromSaved,
  CONCERN_LABELS,
  criterionCodes,
  daysUntil,
  decisionsByLineKey,
  decisionsFromLineKeys,
  dueSentence,
  fitMatchesSaved,
  keepTenantUnits,
  lineKeys,
  lineLabel,
  newId,
  nextThing,
  normalizeConcern,
  qualificationTransition,
  TERMINAL_BLOCKERS,
  withAcknowledgement,
  type ConcernState,
  type NextAction,
} from './decideRules';

type Mode = 'rfq' | 'draft' | 'decline';

/**
 * What the unsaved-work guard compares and keeps. Choices are keyed by line number, not by this
 * revision's line ids, so a draft left behind when the rep went to approve the extraction still
 * lands on the right lines of the new revision that approval creates.
 */
interface FormValue { revisionId: number; decisions: Record<string, EditableLineDecision>; concern: ConcernState }

const formOf = (
  workbench: Pick<LeadDecisionWorkbenchDTO, 'leadRevisionId' | 'lines'> | undefined,
  decisions: DecisionMap,
  concern: ConcernState,
): FormValue => ({
  revisionId: workbench?.leadRevisionId ?? 0,
  decisions: workbench ? decisionsByLineKey(workbench.lines, decisions) : {},
  concern: normalizeConcern(concern),
});

const upperCodes = (options?: Array<{ code: string }>): Set<string> =>
  new Set((options ?? []).map((option) => option.code.toUpperCase()));

/**
 * Grants that do not blink. The session re-reads its permissions every minute and, while that
 * read is in flight, answers every edit grant as withdrawn. On this screen that turned the whole
 * decision into plain text for about a second each minute: the unit picker closed under the
 * mouse, the cursor left the quantity box, "Quote all" vanished. A grant this page already held
 * is kept while the re-read is pending; a settled answer — a revocation included — and a failed
 * read apply at once. The server authorises every write regardless.
 */
const useSteadyGrants = <T extends Record<string, boolean>>(live: T, revalidating: boolean): T => {
  const [held, setHeld] = React.useState<T>(live);
  if (!revalidating && JSON.stringify(held) !== JSON.stringify(live)) setHeld(live);
  if (!revalidating) return live;
  return Object.fromEntries(Object.entries(live).map(([key, value]) => [key, value || held[key] === true])) as T;
};

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
  const { hasPermission, userData, permissionsStale, permissionsError } = useAuth();
  const [searchParams] = useSearchParams();
  const commercialAccess = useSteadyGrants(
    commercialActionPermissions(hasPermission),
    Boolean(permissionsStale) && !permissionsError,
  );
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
  const [checkOpen, setCheckOpen] = React.useState(false);
  const [checkFocus, setCheckFocus] = React.useState<number | null>(null);
  /** A request to take the rep to a unit picker; each click is a new object. */
  const [unitFocus, setUnitFocus] = React.useState<{ lineId?: number; nonce: number } | null>(null);
  /** Which server participation state the choices on screen were seeded from. */
  const decisionSeed = React.useRef<string | null>(null);
  /** Which saved fit assessment the concern controls were seeded from. */
  const concernSeed = React.useRef<string | null>(null);
  /** The lines and choices of the revision last shown, so a new revision can inherit the choices. */
  const previous = React.useRef<{ revisionId: number; byKey: Map<string, EditableLineDecision>; concern: ConcernState } | null>(null);
  /** The lines currently on screen, for callbacks that must not close over a stale workbench. */
  const linesRef = React.useRef<LeadDecisionWorkbenchDTO['lines']>([]);
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
    // The page says both failures itself: the whole-page notice when nothing ever loaded, and a
    // quiet line above the lines when a re-read fails while the rep is working.
    meta: { silenceGlobalError: true },
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

  // Who owns the request. The same record the lead page reads, so Take it / Give to… here and
  // there are one control over one fact; the owner control refreshes it after every change.
  const leadQuery = useQuery({
    queryKey: ['lead-detail', leadId],
    queryFn: () => leadService.getById(leadId),
    enabled: Number.isFinite(leadId) && leadId > 0,
    retry: false,
  });

  const workbench = workbenchQuery.data;

  React.useEffect(() => {
    if (!workbench) return;
    linesRef.current = workbench.lines;
    const revisionChanged = previous.current != null && previous.current.revisionId !== workbench.leadRevisionId;
    // Choices are re-read from the server only when the server's participation record moved
    // (a save, a commit, a new revision), never because the fit assessment alone was re-saved:
    // the fit save is the first step of the button, and the choices must survive it.
    const nextDecisionSeed = [workbench.leadRevisionId, workbench.participationVersion ?? 'none', workbench.participationStatus].join(':');
    if (decisionSeed.current !== nextDecisionSeed) {
      // Only a unit the tenant quotes in is pre-selected; a word kept as written is shown beside
      // an empty picker, never as a value that renders blank.
      let initial = keepTenantUnits(initializeDecisionMap(workbench), workbench.unitOptions ?? []);
      // A document check mints a new immutable revision with new line ids. What the rep chose
      // carries across by line number: Quote/Skip with its reason and note, and a quantity, unit
      // or currency picked for a line the new revision still has none for. Corrected values win.
      if (revisionChanged && previous.current) {
        initial = carryChoices(workbench.lines, initial, previous.current.byKey,
          upperCodes(workbench.unitOptions), upperCodes(workbench.currencyOptions));
      }
      setDecisions(initial);
      decisionSeed.current = nextDecisionSeed;
    }
    const nextConcernSeed = [workbench.leadRevisionId, workbench.fitAssessment?.version ?? 0].join(':');
    if (concernSeed.current !== nextConcernSeed) {
      const saved = concernFromSaved(workbench.fitAssessment);
      // A new revision has no assessment of its own yet; an unsaved concern typed on the old
      // one is still the rep's concern.
      const unsavedSurvives = revisionChanged && previous.current?.concern.raised && !(workbench.fitAssessment && workbench.fitAssessment.version > 0);
      setConcern(unsavedSurvives && previous.current ? previous.current.concern : saved);
      concernSeed.current = nextConcernSeed;
    }
    if (promotionRevision.current !== workbench.leadRevisionId) {
      promotionKey.current = `lead-promotion:${leadId}:${workbench.leadRevisionId}:${newId()}`;
      rfqImpactKey.current = `rfq-impact-review:${leadId}:${workbench.leadRevisionId}:${newId()}`;
      promotionRevision.current = workbench.leadRevisionId;
    }
  }, [leadId, workbench]);

  // The guard compares JSON strings, so what it sees is canonical: the same choices serialise
  // the same way whether the rep built them by clicking or the server sent them back.
  const formValue = React.useMemo<FormValue>(() => formOf(workbench, decisions, concern), [workbench, decisions, concern]);
  const guard = useUnsavedWorkGuard<FormValue>({
    // Keyed by the request, not its revision: a rep who leaves to approve the extraction and comes
    // back to the new revision it created still gets their unsaved choices back.
    storageKey: workbench ? `nexora.lead-decision.${leadId}` : '',
    value: formValue,
    enabled: Boolean(workbench && decisionSeed.current),
    leaveMessage: 'You have unsaved choices on this request. Leave without saving them?',
  });

  const refresh = React.useCallback(async (options: { workbench?: boolean } = {}) => {
    await Promise.all([
      options.workbench === false ? Promise.resolve() : queryClient.invalidateQueries({ queryKey: ['lead-decision-workbench', leadId] }),
      queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] }),
      queryClient.invalidateQueries({ queryKey: ['lifecycle', 'leads', leadId] }),
    ]);
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
    setDecisions((current) => {
      const existing = current[revisionLineId];
      const merged: EditableLineDecision = { ...(existing ?? { decision: 'Pending' }), ...patch };
      // Quoting a warned line needs an acknowledgement. "Quote all" writes one; quoting the line
      // on its own used to leave the note empty and red, so a one-line request could not be
      // quoted without typing. The note says what happened on the line — a catalogue warning, or
      // the unit the request did not give and the rep chose — and stays true as the unit is
      // chosen. A note the rep typed is theirs.
      const line = workbench?.lines.find((candidate) => candidate.revisionLineId === revisionLineId);
      const next = line && !('note' in patch) ? withAcknowledgement(line, existing, merged, patch.decision === 'Bid') : merged;
      return { ...current, [revisionLineId]: next };
    });
  }, [workbench]);

  // One decision for the whole request. A real bid list runs to 1,500 lines; nobody presses
  // 1,500 buttons. "Quote all" says yes to everything and the rep then skips the exceptions;
  // "Skip all" needs one reason, which every skipped line carries.
  const [skipAllAnchor, setSkipAllAnchor] = React.useState<HTMLElement | null>(null);
  const updateEveryLine = React.useCallback((patch: Partial<EditableLineDecision>) => {
    if (!workbench) return;
    setDecisions((current) => {
      const next: DecisionMap = { ...current };
      for (const line of workbench.lines) {
        const existing = current[line.revisionLineId];
        const merged: EditableLineDecision = { ...(existing ?? { decision: 'Pending' }), ...patch };
        // Quoting a warned line needs an acknowledgement; "Quote all" is that acknowledgement,
        // written on the line where it can be read and changed.
        next[line.revisionLineId] = withAcknowledgement(line, existing, merged, patch.decision === 'Bid');
      }
      return next;
    });
  }, [workbench]);

  React.useEffect(() => {
    if (!workbench) return;
    const keys = lineKeys(workbench.lines);
    previous.current = {
      revisionId: workbench.leadRevisionId,
      byKey: new Map(workbench.lines.flatMap((line) => {
        const decision = decisions[line.revisionLineId];
        return decision ? [[keys.get(line.revisionLineId)!, decision] as [string, EditableLineDecision]] : [];
      })),
      concern,
    };
  }, [workbench, decisions, concern]);

  // Choices a rep had not saved when the page went away (a reload, a closed tab) come back on
  // their own. Asking "Restore or Discard?" was one more thing to read and click; the rep can
  // change any restored choice, and what is saved on the server is untouched until they save.
  React.useEffect(() => {
    const draft = guard.recoveredDraft;
    if (!draft || !workbench || decisionRecordIsLocked(workbench, decisions)) return;
    const saved = draft.value;
    const byKey = saved?.decisions ?? {};
    if (saved?.revisionId === workbench.leadRevisionId) {
      setDecisions((current) => decisionsFromLineKeys(workbench.lines, byKey, current));
      if (saved.concern) setConcern(saved.concern);
    } else {
      // A draft from before a document check or an extraction approval: carried by line number
      // onto the new revision, with the values that approval corrected winning.
      setDecisions((current) => carryChoices(workbench.lines, current, new Map(Object.entries(byKey)),
        upperCodes(workbench.unitOptions), upperCodes(workbench.currencyOptions)));
      if (saved?.concern?.raised && !(workbench.fitAssessment && workbench.fitAssessment.version > 0)) setConcern(saved.concern);
    }
    guard.acceptRecovered();
    enqueueSnackbar(`Restored the choices you had not saved yet (from ${formatDateSafe(draft.savedAt)}).`, { variant: 'info' });
  }, [guard, workbench, decisions, enqueueSnackbar]);

  const openDocument = React.useCallback((line?: { revisionLineId: number }) => {
    setCheckFocus(line?.revisionLineId ?? null);
    setCheckOpen(true);
  }, []);

  /** One unit, chosen by the rep, for every quoted line that has none the tenant quotes in. */
  const setUnitOnUnitless = React.useCallback((code: string) => {
    if (!workbench) return;
    const unitCodes = upperCodes(workbench.unitOptions);
    setDecisions((current) => applyUnitToUnitless(workbench.lines, current, code, unitCodes));
  }, [workbench]);

  /** What the next step's button does: open the check, go to a unit picker, or go elsewhere. */
  const runAction = React.useCallback((action: NextAction) => {
    if (action.intent === 'check-document') openDocument();
    else if (action.intent === 'choose-unit') setUnitFocus((current) => ({ lineId: action.lineId, nonce: (current?.nonce ?? 0) + 1 }));
    else navigate(action.path);
  }, [navigate, openDocument]);

  /**
   * Applies the values a rep confirmed against the document to the lines now on screen. Keyed
   * by line number against the current lines, and only onto entries that already exist, because
   * the check mints a new revision and the ids this callback was created with may be gone.
   */
  const applyConfirmed = React.useCallback((confirmed: ConfirmedLine[]) => {
    const byLabel = new Map(confirmed.map((entry) => [entry.lineItemNo, entry]));
    setDecisions((current) => {
      const next = { ...current };
      for (const line of linesRef.current) {
        const values = byLabel.get(lineLabel(line));
        const existing = current[line.revisionLineId];
        if (!values || !existing) continue;
        next[line.revisionLineId] = {
          ...existing,
          ...(values.quantity != null ? { quantity: values.quantity } : {}),
          ...(values.unitOfMeasure ? { unitOfMeasure: values.unitOfMeasure } : {}),
          ...(values.currency ? { currency: values.currency } : {}),
        };
      }
      return next;
    });
  }, []);

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

      // The server refuses to commit a Bid line on a lead that is not yet QUALIFIED, so the
      // lead is qualified before the decision is committed, not after.
      if (mode === 'rfq') {
        const lifecycle = lifecycleQuery.data;
        const option = qualificationTransition(lifecycle);
        if (lifecycle && option) {
          setBusy('Qualifying the lead…');
          await lifecycleService.transition('leads', leadId, lifecycle, option);
          await queryClient.invalidateQueries({ queryKey: ['lifecycle', 'leads', leadId] });
        }
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
        guard.markSaved(formOf(workbench, decisions, concern));
        current = await freshWorkbench();
      }

      if (mode === 'draft') {
        enqueueSnackbar('Saved. A manager can create the RFQ from here.', { variant: 'success' });
        await refresh({ workbench: false });
        return;
      }
      if (mode === 'decline') {
        enqueueSnackbar('Request declined and recorded.', { variant: 'success' });
        await refresh({ workbench: false });
        return;
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
      guard.markSaved(formOf(workbench, decisions, concern));
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
  }, [commercialAccess.canViewPromotedRfq, concern, decisions, enqueueSnackbar, freshWorkbench, guard, leadId, lifecycleQuery.data, navigate, queryClient, refresh, workbench]);

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

  // Only a request that never loaded is replaced by the failure notice. A background re-read that
  // fails keeps the lines and the rep's choices on screen (TanStack keeps the last good data).
  if (!workbench) {
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
  // The order is upload, assign, decide. Until somebody owns the request nothing on it can be
  // decided — the lines are shown, not editable, and the owner control says what to do.
  const ownerKnown = leadQuery.data != null;
  const unowned = ownerKnown && leadQuery.data!.assignedToId == null;
  const readOnly = locked || !canEdit || unowned;
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
    // THE ONE BUTTON DOES THE NEXT THING. A grey "Create RFQ" beside a sentence with a link in
    // it left a rep stuck: the action was there, but not where a button is expected. Until the
    // request can be promoted, the button IS the next step — assign, check, choose — and it
    // becomes "Create RFQ" the moment nothing stands in the way.
    if (unowned && !locked) return { label: 'Assign an owner', disabled: false, onClick: () => document.querySelector('[data-testid="decide-owner"]')?.scrollIntoView({ block: 'center', behavior: 'smooth' }) };
    if (next.kind === 'blocked' && next.action) {
      const action = next.action;
      return { label: action.label, disabled: false, onClick: () => runAction(action) };
    }
    // Declining is a committed decision, which the server allows only to commercial authority;
    // a rep's skip-everything is saved as a draft for a manager to decline.
    if (next.kind === 'decline' && canPromote) return { label: 'Decline request', disabled: false, onClick: () => setDeclineOpen(true) };
    if (next.kind === 'concern' || next.kind === 'decline') return { label: next.kind === 'concern' ? 'Save for review' : 'Save for a manager', disabled: false, onClick: () => run('draft') };
    if (!canPromote) return { label: 'Save for a manager', disabled: next.kind !== 'ready', onClick: () => run('draft') };
    return { label: 'Create RFQ', disabled: next.kind !== 'ready', onClick: () => run('rfq') };
  })();

  const footerSentence = (() => {
    if (busy) return 'Please wait.';
    if (!canEdit) return 'Your role can view this request but not decide it.';
    if (next.kind === 'blocked' || next.kind === 'closed') return next.sentence;
    if (next.kind === 'decline' && !canPromote) return 'Every line is skipped. Saving records your reasons; a manager declines the request.';
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

      {workbenchQuery.isRefetchError ? (
        <Alert
          severity="warning"
          sx={{ mb: 1.5 }}
          action={<Button color="inherit" size="small" onClick={() => workbenchQuery.refetch()}>Try again</Button>}
        >
          Couldn&apos;t refresh this request just now. Your choices are kept; the lines are as of{' '}
          {new Date(workbenchQuery.dataUpdatedAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.
        </Alert>
      ) : null}

      {/* The next step, first. The same sentence repeats beside the one button in the sticky bar
          under the lines, so a rep never has to scroll to learn what the request is waiting for. */}
      <NextStepPanel
        tone={busy ? 'info' : next.kind === 'ready' ? 'success' : next.kind === 'blocked' || (unowned && !locked) ? 'warning' : next.kind === 'closed' ? 'info' : 'info'}
        title="Next step"
        sentence={unowned && !locked ? 'Assign an owner first: take it, or give it to someone, at the top of this request.' : footerSentence}
        testId="decide-next-step"
      />

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
            {/* A resolved customer is immutable on the server (a database rule refuses any change),
                so the picker is offered only while the request has none. */}
            {!locked && commercialAccess.canLinkLeadClient && !workbench.customerId ? (
              <Link component="button" type="button" onClick={() => setCustomerDialogOpen(true)} sx={{ fontWeight: 700 }}>
                Choose the customer
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
          </Stack>
          {ownerKnown ? (
            <Box sx={{ mt: 2 }} data-testid="decide-owner">
              <Typography variant="overline" sx={{ color: unowned && !locked ? 'warning.dark' : 'text.secondary', fontWeight: 700, letterSpacing: '0.08em' }}>
                {unowned ? "Who's on it — nobody yet" : "Who's on it"}
              </Typography>
              <LeadOwnerControl
                leadId={leadId}
                assignedToId={leadQuery.data!.assignedToId}
                assignedToName={leadQuery.data!.assignedToFullName}
                assignmentMethod={leadQuery.data!.assignmentMethod}
                assignmentVersion={leadQuery.data!.assignmentVersion ?? 1}
                canEdit={canEdit && !locked}
              />
            </Box>
          ) : null}
        </Box>

        {/* WHAT THEY WANT */}
        <Box component="section" aria-labelledby="decide-lines" sx={{ pt: 2 }}>
          <Stack direction="row" spacing={2} sx={{ alignItems: 'baseline', justifyContent: 'space-between', px: { xs: 2, sm: 3 }, pb: 1 }}>
            <Typography id="decide-lines" component="h2" variant="subtitle1" sx={{ fontWeight: 700 }}>What they want</Typography>
            <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
              {!readOnly && workbench.lines.length > 1 ? (
                <Stack direction="row" spacing={1}>
                  <Button size="small" variant="outlined" onClick={() => updateEveryLine({ decision: 'Bid', reasonCode: undefined })} sx={{ fontWeight: 700 }}>
                    Quote all
                  </Button>
                  <Button
                    size="small"
                    variant="outlined"
                    aria-haspopup="menu"
                    aria-expanded={skipAllAnchor ? 'true' : undefined}
                    onClick={(event) => setSkipAllAnchor(event.currentTarget)}
                    sx={{ fontWeight: 700 }}
                  >
                    Skip all…
                  </Button>
                  <Menu anchorEl={skipAllAnchor} open={Boolean(skipAllAnchor)} onClose={() => setSkipAllAnchor(null)} aria-label="Why skip every line">
                    {workbench.reasonCodes.filter((reason) => reason.appliesTo.includes('NoBid')).map((reason) => (
                      <MenuItem key={reason.code} onClick={() => { updateEveryLine({ decision: 'NoBid', reasonCode: reason.code }); setSkipAllAnchor(null); }}>
                        {reason.label}
                      </MenuItem>
                    ))}
                  </Menu>
                </Stack>
              ) : null}
              {!locked && canEdit && workbench.lines.some((line) => line.verificationStatus === 'NEEDS_CHECK') ? (
                <Button size="small" variant="outlined" onClick={() => openDocument()} sx={{ fontWeight: 700 }}>
                  Check against the document
                </Button>
              ) : null}
              <Typography variant="body2" color="text.secondary" sx={{ fontVariantNumeric: 'tabular-nums' }}>
                <Box component="b" sx={{ color: 'text.primary' }}>{quoted}</Box> of {workbench.lines.length} lines to quote
              </Typography>
            </Stack>
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
            onBulkUnit={setUnitOnUnitless}
            focusUnit={unitFocus}
          />
        </Box>

        {/* THE DECISION */}
        {!locked ? (
          <Box component="section" aria-labelledby="decide-question" sx={{ p: { xs: 2, sm: 3 }, bgcolor: 'action.hover', borderTop: 1, borderColor: 'divider' }}>
            {brief ? (
              <Paper variant="outlined" sx={{ p: 2, mb: 2, borderLeft: 3, borderLeftColor: 'primary.main', borderRadius: 2 }}>
                <Stack direction="row" spacing={1.5} sx={{ alignItems: 'baseline', flexWrap: 'wrap' }}>
                  <Typography sx={{ fontWeight: 600 }}>
                    Nexora&apos;s read: <Box component="b" sx={{ color: 'primary.main' }}>{decisionLabel(brief.recommendation)}</Box>
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

            {/* Pinned to the bottom of the window: a 32-line request must not hide its one
                button under a scroll. The bar is part of the page, so it ends where the page ends. */}
            <Box sx={{ position: 'sticky', bottom: 0, zIndex: 2, bgcolor: 'background.paper', borderTop: 1, borderColor: 'divider', boxShadow: '0 -8px 20px -16px rgba(15,18,24,0.55)', mt: 2, pt: 1.5, pb: 0.5, mx: { xs: -2, sm: -3 }, px: { xs: 2, sm: 3 } }}>
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
                aria-label="Next step"
                variant="body2"
                sx={{ color: next.kind === 'ready' || next.kind === 'decline' || busy ? 'text.secondary' : 'warning.dark', fontWeight: next.kind === 'blocked' ? 600 : 400 }}
              >
                {unowned && !locked ? 'Assign an owner first: take it, or give it to someone, at the top of this request.' : footerSentence}
                {/* The unit step's button sits right beside this sentence; a second control with
                    the same name would only be read twice. */}
                {!unowned && next.kind === 'blocked' && next.action && next.action.intent !== 'choose-unit' ? (
                  <>
                    {' '}
                    <Link
                      component="button"
                      type="button"
                      onClick={() => runAction(next.action!)}
                      sx={{ fontWeight: 700, verticalAlign: 'baseline' }}
                    >
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

      <CheckDocumentDialog
        open={checkOpen}
        leadId={leadId}
        workbench={workbench}
        focusLineId={checkFocus}
        onClose={() => setCheckOpen(false)}
        onConfirmed={applyConfirmed}
        decisions={decisions}
      />

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
