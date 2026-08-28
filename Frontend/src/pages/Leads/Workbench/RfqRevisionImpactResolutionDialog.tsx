import React from 'react';
import {
  Alert,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  Stack,
  TextField,
  Typography,
} from '@mui/material';

interface RfqRevisionImpactResolutionDialogProps {
  open: boolean;
  rfqLabel: string;
  leadRevisionNumber: number;
  saving: boolean;
  onCancel: () => void;
  onConfirm: (reason: string) => void;
}

const MIN_REASON_LENGTH = 15;

const RfqRevisionImpactResolutionDialog: React.FC<RfqRevisionImpactResolutionDialogProps> = ({
  open,
  rfqLabel,
  leadRevisionNumber,
  saving,
  onCancel,
  onConfirm,
}) => {
  const [reason, setReason] = React.useState('');
  const [confirmed, setConfirmed] = React.useState(false);
  const normalizedReason = reason.trim();
  const canConfirm = normalizedReason.length >= MIN_REASON_LENGTH && confirmed && !saving;

  React.useEffect(() => {
    if (open) return;
    setReason('');
    setConfirmed(false);
  }, [open]);

  const resetAndCancel = () => {
    if (saving) return;
    setReason('');
    setConfirmed(false);
    onCancel();
  };

  const submit = () => {
    if (!canConfirm) return;
    onConfirm(normalizedReason);
  };

  return (
    <Dialog open={open} onClose={resetAndCancel} maxWidth="sm" fullWidth>
      <DialogTitle>Complete RFQ amendment review</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Alert severity="warning">
            Review Lead Revision {leadRevisionNumber} against {rfqLabel}. This action records the
            reconciliation outcome; it does not rewrite the original RFQ, its approved lines, or its
            promotion receipt.
          </Alert>
          <TextField
            required
            multiline
            minRows={4}
            label="Reconciliation outcome"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            helperText={`State what changed and how the existing RFQ was handled. At least ${MIN_REASON_LENGTH} characters.`}
            slotProps={{ htmlInput: { maxLength: 2000 } }}
          />
          <FormControlLabel
            control={<Checkbox checked={confirmed} onChange={(event) => setConfirmed(event.target.checked)} />}
            label="I compared the current Lead revision with the RFQ and confirm the historical RFQ lineage remains unchanged."
          />
          <Typography variant="caption" color="text.secondary">
            Your identity, reason, RFQ, Lead revision, and timestamp are retained in the append-only audit trail.
          </Typography>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={resetAndCancel} disabled={saving}>Cancel</Button>
        <Button variant="contained" color="warning" disabled={!canConfirm} onClick={submit}>
          Record review complete
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default RfqRevisionImpactResolutionDialog;
