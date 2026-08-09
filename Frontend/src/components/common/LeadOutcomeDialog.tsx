import React from 'react';
import {
  Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogContentText,
  DialogTitle, MenuItem, Stack, TextField,
} from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import lifecycleService from '../../api/services/commercialLifecycleService';

export interface LeadOutcomeCapture { reasonCode: string; reasonNotes?: string }

interface Props {
  open: boolean;
  /** The transition being recorded, e.g. "Disqualified". */
  targetLabel: string;
  saving?: boolean;
  onCancel: () => void;
  onConfirm: (capture: LeadOutcomeCapture) => void;
}

/**
 * Why an inquiry ended before it was ever quoted. A reason is required — the server refuses the
 * transition without one, and refuses anything outside the governed picklist — so the list is
 * always fetched, never hardcoded. It is the same picklist the quote outcome dialog uses, which is
 * what lets one win/loss report span the whole commercial cycle.
 */
const LeadOutcomeDialog: React.FC<Props> = ({ open, targetLabel, saving = false, onCancel, onConfirm }) => {
  const [reasonCode, setReasonCode] = React.useState('');
  const [reasonNotes, setReasonNotes] = React.useState('');

  React.useEffect(() => {
    if (open) {
      setReasonCode('');
      setReasonNotes('');
    }
  }, [open]);

  const { data: reasons = [], isLoading, isError } = useQuery({
    queryKey: ['lead-outcome-reasons'],
    queryFn: () => lifecycleService.getLeadOutcomeReasons(),
    enabled: open,
    staleTime: 5 * 60 * 1000,
  });

  // AUTO_EXPIRED is stamped by the SLA sweep, never chosen by a person.
  const selectableReasons = reasons.filter((reason) => reason.code !== 'AUTO_EXPIRED');

  return (
    <Dialog open={open} onClose={saving ? undefined : onCancel} fullWidth maxWidth="xs">
      <DialogTitle sx={{ fontWeight: 800 }}>Why is this inquiry ending?</DialogTitle>
      <DialogContent dividers>
        <DialogContentText sx={{ mb: 2 }}>
          Moving to {targetLabel} closes the case before any quotation. Record the reason so the
          loss still counts in win/loss reporting.
        </DialogContentText>
        <Stack spacing={2}>
          <TextField
            select
            required
            fullWidth
            size="small"
            label="Reason"
            value={reasonCode}
            onChange={(event) => setReasonCode(event.target.value)}
            disabled={isLoading || isError}
            error={isError}
            helperText={isError ? 'The reason list could not be loaded. Close and try again.'
              : isLoading ? 'Loading reasons…' : undefined}
          >
            {selectableReasons.map((reason) => (
              <MenuItem key={reason.code} value={reason.code}>{reason.label}</MenuItem>
            ))}
          </TextField>
          <TextField
            fullWidth
            size="small"
            multiline
            minRows={2}
            label="Note (optional)"
            value={reasonNotes}
            onChange={(event) => setReasonNotes(event.target.value.slice(0, 500))}
            helperText={`${reasonNotes.length}/500`}
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onCancel} color="inherit" disabled={saving}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!reasonCode || saving}
          startIcon={saving ? <CircularProgress size={16} color="inherit" /> : undefined}
          onClick={() => onConfirm({ reasonCode, reasonNotes: reasonNotes || undefined })}
          sx={{ fontWeight: 800 }}
        >
          Record outcome
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default LeadOutcomeDialog;
