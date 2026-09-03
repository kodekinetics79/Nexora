import React from 'react';
import {
  Box, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogContentText,
  DialogTitle, Stack, TextField, Tooltip, Typography,
} from '@mui/material';
import {
  CheckCircleOutlined as QualifyIcon, HelpOutlined as ClarifyIcon,
  NotInterested as PassIcon, RestartAlt as ReopenIcon,
} from '@mui/icons-material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'react-hot-toast';
import lifecycleService, { type LifecycleTransitionOption } from '../../api/services/commercialLifecycleService';
import leadService from '../../api/services/leadService';
import LeadOutcomeDialog, { type LeadOutcomeCapture } from '../../components/common/LeadOutcomeDialog';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { useAuth } from '../../context/AuthContext';
import statusLabel from '../../utils/statusLabels';

interface Props {
  leadId: number;
  reviewVersion: number;
  canEdit: boolean;
  onChanged?: () => void;
}

/** The server refuses a blank reopen reason, so the form does not offer to try. */
export const MINIMUM_REOPEN_REASON = 5;
/** `CommercialLifecycleEvent.ReasonNotes` is varchar(1000); the field stops there rather than 500ing. */
export const MAXIMUM_REOPEN_REASON = 1000;

/**
 * What a rep is told when a closed inquiry offers no way forward.
 *
 * Before this, a passed lead showed three greyed-out buttons whose reasons lived in tooltips, and
 * nothing else — the journey simply ended. "We passed on it and the customer came back" is
 * ordinary trade, and the verb for it (`POST .../reopen`) had existed with no caller since the
 * lifecycle spine shipped.
 *
 * @param canReopen the SERVER's verdict for this state, never inferred from terminal-ness
 * @param isManager reopen carries `[RequireManagerRole]` on top of Leads:Edit
 */
export const reopenBlockedReason = (
  canReopen: boolean,
  isManager: boolean,
  currentStatusCode: string,
): string | null => {
  if (!canReopen) {
    return `This inquiry is finished as ${statusLabel(currentStatusCode).toLowerCase()} and is not `
      + 'reopened. If the customer comes back, take it in as a new inquiry.';
  }
  if (!isManager) {
    return 'Only a manager can reopen an inquiry that was closed. Ask yours to reopen it, or '
      + 'forward the customer\u2019s new message and it will come in as a fresh inquiry.';
  }
  return null;
};

const LeadDecisionActions: React.FC<Props> = ({ leadId, reviewVersion, canEdit, onChanged }) => {
  const queryClient = useQueryClient();
  const { userData } = useAuth();
  const [clarificationOpen, setClarificationOpen] = React.useState(false);
  const [clarificationNote, setClarificationNote] = React.useState('');
  const [reopenOpen, setReopenOpen] = React.useState(false);
  const [reopenReason, setReopenReason] = React.useState('');
  const [passOption, setPassOption] = React.useState<LifecycleTransitionOption | null>(null);
  const [qualifyOption, setQualifyOption] = React.useState<LifecycleTransitionOption | null>(null);
  const stateQuery = useQuery({
    queryKey: ['lifecycle', 'leads', leadId],
    queryFn: () => lifecycleService.getState('leads', leadId),
    enabled: canEdit,
  });

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] }),
      queryClient.invalidateQueries({ queryKey: ['lifecycle', 'leads', leadId] }),
      queryClient.invalidateQueries({ queryKey: ['leads'] }),
      queryClient.invalidateQueries({ queryKey: ['leads-assigned'] }),
      queryClient.invalidateQueries({ queryKey: ['leads-outstanding'] }),
    ]);
    onChanged?.();
  };

  const clarificationMutation = useMutation({
    mutationFn: () => leadService.requestClarification(leadId, {
      expectedReviewVersion: reviewVersion,
      note: clarificationNote.trim(),
    }),
    onSuccess: async () => {
      toast.success('Clarification request recorded in the lead history.');
      setClarificationOpen(false);
      setClarificationNote('');
      await refresh();
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(
      error,
      'The clarification request could not be recorded. Refresh and try again.',
    )),
  });

  const passMutation = useMutation({
    mutationFn: (capture: LeadOutcomeCapture) => lifecycleService.transition(
      'leads', leadId, stateQuery.data!, passOption!, capture.reasonCode, capture.reasonNotes,
    ),
    onSuccess: async () => {
      toast.success('Lead passed and the outcome was recorded.');
      setPassOption(null);
      await refresh();
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(
      error,
      'The lead could not be passed. Refresh and try again.',
    )),
  });

  const qualificationMutation = useMutation({
    mutationFn: () => lifecycleService.transition('leads', leadId, stateQuery.data!, qualifyOption!),
    onSuccess: async () => {
      toast.success('Lead qualified through the governed lifecycle.');
      setQualifyOption(null);
      await refresh();
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(
      error,
      'The lead could not be qualified. Complete the stated validation requirement, refresh, and try again.',
    )),
  });

  const reopenMutation = useMutation({
    mutationFn: () => lifecycleService.reopen('leads', leadId, stateQuery.data!, reopenReason.trim()),
    onSuccess: async () => {
      toast.success('Inquiry reopened. It is back under review.');
      setReopenOpen(false);
      setReopenReason('');
      await refresh();
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(
      error,
      'The inquiry could not be reopened. It is still closed \u2014 refresh and try again.',
    )),
  });

  if (!canEdit) return null;
  const state = stateQuery.data;
  const closed = Boolean(state?.isTerminal || state?.currentStatusCode === 'CONVERTED_TO_RFQ');
  const availablePass = state?.allowedTransitions.find(option => option.statusCode === 'DISQUALIFIED');
  const availableQualify = state?.allowedTransitions.find(option => option.statusCode === 'QUALIFIED');
  const busy = clarificationMutation.isPending || passMutation.isPending
    || qualificationMutation.isPending || reopenMutation.isPending;

  // The server resolves authority; a role NAME is display text, never a permission signal.
  const isManager = userData?.isManager === true || Boolean(userData?.isSuperAdmin);
  /**
   * The Reopen control appears only on a state the server would actually accept, so it is never a
   * button that fails after the click. When it appears and cannot be used, the reason is PRINTED
   * beside it rather than hidden in a tooltip a mouse has to find.
   */
  const closedWithNoMoveLeft = Boolean(state?.isTerminal);
  const canReopenHere = Boolean(state?.canReopen);
  const reopenBlocked = state && closedWithNoMoveLeft
    ? reopenBlockedReason(canReopenHere, isManager, state.currentStatusCode)
    : null;

  return (
    <>
      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Tooltip title={!state ? 'Loading lead decision…' : availableQualify ? 'Advance this Lead to Qualified through the governed lifecycle. Commercial facts must already be approved when review is required.' : state.currentStatusCode === 'QUALIFIED' ? 'This Lead is already qualified.' : 'Qualification is not an allowed transition from the current lifecycle state.'}>
          <span>
            <Button
              size="small"
              variant="contained"
              color="success"
              startIcon={qualificationMutation.isPending || stateQuery.isLoading ? <CircularProgress size={16} /> : <QualifyIcon />}
              disabled={!availableQualify || busy}
              onClick={() => setQualifyOption(availableQualify ?? null)}
              sx={{ fontWeight: 800, borderRadius: 2 }}
            >
              Qualify Lead
            </Button>
          </span>
        </Tooltip>
        <Tooltip title={closed ? 'This lead decision is already complete.' : 'Record the missing information needed from the customer.'}>
          <span>
            <Button
              size="small"
              variant="outlined"
              startIcon={clarificationMutation.isPending ? <CircularProgress size={16} /> : <ClarifyIcon />}
              disabled={!state || closed || busy}
              onClick={() => setClarificationOpen(true)}
              sx={{ fontWeight: 800, borderRadius: 2 }}
            >
              Request clarification
            </Button>
          </span>
        </Tooltip>
        <Tooltip title={!state ? 'Loading lead decision…' : availablePass ? 'Close this inquiry with a governed outcome reason.' : 'Pass is not available for this completed lead.'}>
          <span>
            <Button
              size="small"
              color="inherit"
              startIcon={passMutation.isPending || stateQuery.isLoading ? <CircularProgress size={16} /> : <PassIcon />}
              disabled={!availablePass || busy}
              onClick={() => setPassOption(availablePass ?? null)}
              sx={{ fontWeight: 800, borderRadius: 2 }}
            >
              Pass
            </Button>
          </span>
        </Tooltip>
        {/* Only on a closed inquiry: while a lead is still live the three verbs above are the
            job, and a Reopen among them would be noise. */}
        {closedWithNoMoveLeft && (
          <Button
            size="small"
            variant={reopenBlocked ? 'outlined' : 'contained'}
            startIcon={reopenMutation.isPending ? <CircularProgress size={16} /> : <ReopenIcon />}
            disabled={Boolean(reopenBlocked) || busy}
            onClick={() => setReopenOpen(true)}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Reopen this inquiry
          </Button>
        )}
      </Stack>

      {/* A disabled control that will not say why is a support ticket. */}
      {reopenBlocked && (
        <Typography
          variant="caption"
          sx={{ display: 'block', mt: 0.75, color: 'text.secondary', maxWidth: 420, whiteSpace: 'normal' }}
        >
          {reopenBlocked}
        </Typography>
      )}
      {closedWithNoMoveLeft && !reopenBlocked && (
        <Typography
          variant="caption"
          sx={{ display: 'block', mt: 0.75, color: 'text.secondary', maxWidth: 420, whiteSpace: 'normal' }}
        >
          This inquiry is closed. Reopening puts it back under review so it can be worked again.
        </Typography>
      )}

      <Dialog
        open={Boolean(qualifyOption)}
        onClose={qualificationMutation.isPending ? undefined : () => setQualifyOption(null)}
        fullWidth
        maxWidth="xs"
      >
        <DialogTitle sx={{ fontWeight: 800 }}>Qualify this Lead?</DialogTitle>
        <DialogContent dividers>
          <DialogContentText>
            This records a governed lifecycle transition to Qualified. It does not create an RFQ; participation must still be committed and promoted from the decision workbench.
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button color="inherit" disabled={qualificationMutation.isPending} onClick={() => setQualifyOption(null)}>Cancel</Button>
          <Button
            variant="contained"
            color="success"
            disabled={qualificationMutation.isPending}
            onClick={() => qualificationMutation.mutate()}
            sx={{ fontWeight: 800 }}
          >
            {qualificationMutation.isPending ? 'Qualifying…' : 'Confirm qualification'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog
        open={clarificationOpen}
        onClose={clarificationMutation.isPending ? undefined : () => setClarificationOpen(false)}
        fullWidth
        maxWidth="xs"
      >
        <DialogTitle sx={{ fontWeight: 800 }}>Request clarification</DialogTitle>
        <DialogContent dividers>
          <DialogContentText sx={{ mb: 2 }}>
            Record exactly what the customer needs to clarify. This keeps the lead open and adds an immutable audit entry.
          </DialogContentText>
          <TextField
            required
            fullWidth
            multiline
            minRows={3}
            label="Information needed"
            value={clarificationNote}
            onChange={event => setClarificationNote(event.target.value.slice(0, 1000))}
            helperText={`${clarificationNote.length}/1000`}
          />
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button color="inherit" disabled={clarificationMutation.isPending} onClick={() => setClarificationOpen(false)}>
            Cancel
          </Button>
          <Button
            variant="contained"
            disabled={clarificationNote.trim().length < 3 || clarificationMutation.isPending}
            onClick={() => clarificationMutation.mutate()}
            sx={{ fontWeight: 800 }}
          >
            Record request
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog
        open={reopenOpen}
        onClose={reopenMutation.isPending ? undefined : () => setReopenOpen(false)}
        fullWidth
        maxWidth="xs"
      >
        <DialogTitle sx={{ fontWeight: 800 }}>Reopen this inquiry?</DialogTitle>
        <DialogContent dividers>
          <DialogContentText sx={{ mb: 2 }}>
            It goes back under review so it can be worked again. The original outcome stops
            counting as a loss, and what you type below is recorded against your name.
          </DialogContentText>
          <TextField
            required
            fullWidth
            multiline
            minRows={3}
            label="Why is it coming back?"
            placeholder="e.g. Customer re-issued the tender with new quantities"
            value={reopenReason}
            onChange={event => setReopenReason(event.target.value.slice(0, MAXIMUM_REOPEN_REASON))}
            helperText={`${reopenReason.length}/${MAXIMUM_REOPEN_REASON}`}
          />
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button color="inherit" disabled={reopenMutation.isPending} onClick={() => setReopenOpen(false)}>
            Cancel
          </Button>
          <Button
            variant="contained"
            disabled={reopenReason.trim().length < MINIMUM_REOPEN_REASON || reopenMutation.isPending}
            onClick={() => reopenMutation.mutate()}
            sx={{ fontWeight: 800 }}
          >
            {reopenMutation.isPending ? 'Reopening\u2026' : 'Reopen'}
          </Button>
        </DialogActions>
        {reopenReason.trim().length < MINIMUM_REOPEN_REASON && (
          <Box sx={{ px: 3, pb: 2 }}>
            <Typography variant="caption" sx={{ color: 'text.secondary' }}>
              Type at least {MINIMUM_REOPEN_REASON} characters to enable Reopen.
            </Typography>
          </Box>
        )}
      </Dialog>

      <LeadOutcomeDialog
        open={Boolean(passOption)}
        targetLabel="Passed"
        saving={passMutation.isPending}
        onCancel={() => setPassOption(null)}
        onConfirm={capture => passMutation.mutate(capture)}
      />
    </>
  );
};

export default LeadDecisionActions;
