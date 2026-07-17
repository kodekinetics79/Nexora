import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Dialog, DialogTitle, DialogContent, DialogActions, Button,
  RadioGroup, FormControlLabel, Radio, TextField, MenuItem,
  Typography, CircularProgress, Stack,
} from '@mui/material';
import { toast } from 'react-hot-toast';
import quoteService, { type QuoteOutcome } from '../../../api/services/quoteService';

interface QuoteOutcomeDialogProps {
  open: boolean;
  onClose: () => void;
  quoteId: number | null;
  quoteNo?: string;
  /** Extra query keys to invalidate after saving (list/detail pages differ). */
  invalidateKeys?: unknown[][];
}

/**
 * 3-click outcome capture (WP-A4): Won / Lost / Expired -> reason -> Save.
 * A reason is required for Lost/Expired (the backend enforces it too); the note
 * is always optional. Reasons come from the governed SetupMaster picklist.
 */
const QuoteOutcomeDialog: React.FC<QuoteOutcomeDialogProps> = ({
  open, onClose, quoteId, quoteNo, invalidateKeys = [['quotes']],
}) => {
  const queryClient = useQueryClient();
  const [outcome, setOutcome] = React.useState<QuoteOutcome>('won');
  const [reasonCode, setReasonCode] = React.useState('');
  const [note, setNote] = React.useState('');

  React.useEffect(() => {
    if (open) {
      setOutcome('won');
      setReasonCode('');
      setNote('');
    }
  }, [open]);

  const { data: reasons = [], isLoading: reasonsLoading } = useQuery({
    queryKey: ['quote-outcome-reasons'],
    queryFn: () => quoteService.getOutcomeReasons(),
    enabled: open,
    staleTime: 5 * 60 * 1000,
  });

  // Hide the system-only reason from the manual flow.
  const selectableReasons = reasons.filter((r) => r.code !== 'AUTO_EXPIRED');

  const reasonRequired = outcome !== 'won';

  const saveMutation = useMutation({
    mutationFn: () => quoteService.setOutcome(quoteId!, outcome, reasonCode || undefined, note || undefined),
    onSuccess: () => {
      toast.success(
        outcome === 'won' ? 'Marked as won — nice work!'
          : outcome === 'lost' ? 'Marked as lost'
          : 'Marked as expired',
      );
      invalidateKeys.forEach((key) => queryClient.invalidateQueries({ queryKey: key }));
      onClose();
    },
    onError: (error: any) => {
      const message = error?.response?.data?.message || error?.response?.data || 'Failed to save the outcome';
      toast.error(typeof message === 'string' ? message : 'Failed to save the outcome', { duration: 5000 });
    },
  });

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="xs">
      <DialogTitle sx={{ fontWeight: 800 }}>
        How did {quoteNo ? `quote ${quoteNo}` : 'this quote'} end?
      </DialogTitle>
      <DialogContent dividers>
        <RadioGroup
          value={outcome}
          onChange={(e) => setOutcome(e.target.value as QuoteOutcome)}
          sx={{ mb: 2 }}
        >
          <FormControlLabel value="won" control={<Radio color="success" />} label={<Typography sx={{ fontWeight: 700 }}>We won it</Typography>} />
          <FormControlLabel value="lost" control={<Radio color="error" />} label={<Typography sx={{ fontWeight: 700 }}>We lost it</Typography>} />
          <FormControlLabel value="expired" control={<Radio />} label={<Typography sx={{ fontWeight: 700 }}>It expired without a decision</Typography>} />
        </RadioGroup>

        <Stack spacing={2}>
          <TextField
            select
            fullWidth
            size="small"
            label={reasonRequired ? 'Why? (required)' : 'Why? (optional)'}
            value={reasonCode}
            onChange={(e) => setReasonCode(e.target.value)}
            disabled={reasonsLoading}
            helperText={reasonsLoading ? 'Loading reasons…' : undefined}
          >
            {!reasonRequired && <MenuItem value="">No particular reason</MenuItem>}
            {selectableReasons.map((r) => (
              <MenuItem key={r.code} value={r.code}>{r.label}</MenuItem>
            ))}
          </TextField>

          <TextField
            fullWidth
            size="small"
            multiline
            minRows={2}
            label="Note (optional)"
            value={note}
            onChange={(e) => setNote(e.target.value.slice(0, 500))}
            helperText={`${note.length}/500`}
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} color="inherit">Cancel</Button>
        <Button
          variant="contained"
          onClick={() => saveMutation.mutate()}
          disabled={!quoteId || saveMutation.isPending || (reasonRequired && !reasonCode)}
          startIcon={saveMutation.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
          sx={{ fontWeight: 800 }}
        >
          Save
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default QuoteOutcomeDialog;
