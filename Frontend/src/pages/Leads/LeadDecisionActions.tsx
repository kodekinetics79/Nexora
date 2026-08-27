import React from 'react';
import {
  Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogContentText,
  DialogTitle, Stack, TextField, Tooltip,
} from '@mui/material';
import { CheckCircleOutlined as QualifyIcon, HelpOutlined as ClarifyIcon, NotInterested as PassIcon } from '@mui/icons-material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { toast } from 'react-hot-toast';
import lifecycleService, { type LifecycleTransitionOption } from '../../api/services/commercialLifecycleService';
import leadService from '../../api/services/leadService';
import LeadOutcomeDialog, { type LeadOutcomeCapture } from '../../components/common/LeadOutcomeDialog';
import { presentableErrorMessage } from '../../utils/apiErrors';

interface Props {
  leadId: number;
  reviewVersion: number;
  canEdit: boolean;
  onChanged?: () => void;
}

const LeadDecisionActions: React.FC<Props> = ({ leadId, reviewVersion, canEdit, onChanged }) => {
  const queryClient = useQueryClient();
  const [clarificationOpen, setClarificationOpen] = React.useState(false);
  const [clarificationNote, setClarificationNote] = React.useState('');
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

  if (!canEdit) return null;
  const state = stateQuery.data;
  const closed = Boolean(state?.isTerminal || state?.currentStatusCode === 'CONVERTED_TO_RFQ');
  const availablePass = state?.allowedTransitions.find(option => option.statusCode === 'DISQUALIFIED');
  const availableQualify = state?.allowedTransitions.find(option => option.statusCode === 'QUALIFIED');
  const busy = clarificationMutation.isPending || passMutation.isPending || qualificationMutation.isPending;

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
      </Stack>

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
