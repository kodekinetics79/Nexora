import React from 'react';
import {
  Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, Stack, TextField, Typography,
} from '@mui/material';
import type { QuoteRevisionLineChangeDTO } from '../../../api/services/quoteService';
import { describeRevisionChange } from './revisionImpactText';

const REASONS = ['Buyer confirmed the old quantities', 'Portal is closed for changes', 'Quoting the old quantities on purpose'];

export interface KeepAsQuotedDialogProps {
  open: boolean;
  /** 'send' when the rep pressed Send with a newer buyer revision still open. */
  mode: 'keep' | 'send';
  /** The server's sentence: "The buyer sent a newer version (rev 4): 2 lines changed." */
  message?: string | null;
  changes?: QuoteRevisionLineChangeDTO[] | null;
  canApply?: boolean;
  busy?: boolean;
  /** The button in 'send' mode. Default "Send anyway". */
  proceedLabel?: string;
  onApply?: () => void;
  onCancel: () => void;
  onKeep: (reason: string) => void;
}

/**
 * "Keep as quoted" with the reason on record (pilot audit D-04): a quote went to the buyer with
 * 20 contactors against their revision's 35 and the only trace was "resolved". The rep can still
 * send — Nexora informs, it does not block — but says why, in one line.
 */
export default function KeepAsQuotedDialog({
  open, mode, message, changes, canApply, busy, proceedLabel, onApply, onCancel, onKeep,
}: KeepAsQuotedDialogProps) {
  const [reason, setReason] = React.useState('');
  const [other, setOther] = React.useState(false);
  React.useEffect(() => { if (open) { setReason(''); setOther(false); } }, [open]);
  const shown = (changes ?? []).slice(0, 6);

  return (
    <Dialog open={open} onClose={busy ? undefined : onCancel} maxWidth="sm" fullWidth>
      <DialogTitle sx={{ fontWeight: 800 }}>
        {mode === 'send' ? 'The buyer sent a newer version' : 'Keep as quoted'}
      </DialogTitle>
      <DialogContent>
        {message && <Typography variant="body2" sx={{ mb: 1 }}>{message}</Typography>}
        {shown.length > 0 && (
          <Stack component="ul" spacing={0.25} sx={{ m: 0, pl: 2.5, mb: 1.5 }}>
            {shown.map((change, index) => (
              <Typography key={`${change.line}-${change.field}-${index}`} component="li" variant="body2">
                {describeRevisionChange(change)}
              </Typography>
            ))}
          </Stack>
        )}
        <Typography variant="body2" sx={{ fontWeight: 700, mb: 0.75 }}>Why keep the old quantities?</Typography>
        <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap' }}>
          {REASONS.map((text) => (
            <Chip key={text} size="small" label={text} clickable
              color={!other && reason === text ? 'primary' : 'default'}
              variant={!other && reason === text ? 'filled' : 'outlined'}
              onClick={() => { setOther(false); setReason(text); }} />
          ))}
          <Chip size="small" label="Other" clickable color={other ? 'primary' : 'default'} variant={other ? 'filled' : 'outlined'}
            onClick={() => { setOther(true); setReason(''); }} />
        </Stack>
        {other && (
          <TextField size="small" fullWidth label="Reason" value={reason} sx={{ mt: 1 }}
            onChange={(event) => setReason(event.target.value)}
            slotProps={{ htmlInput: { maxLength: 500, 'aria-label': 'Reason for keeping the old quantities' } }} />
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button onClick={onCancel} disabled={busy}>Cancel</Button>
        {canApply && onApply && (
          <Button variant="outlined" onClick={onApply} disabled={busy}>Use the new quantities</Button>
        )}
        <Button variant="contained" disabled={busy || reason.trim().length === 0}
          startIcon={busy ? <CircularProgress size={16} color="inherit" /> : undefined}
          onClick={() => onKeep(reason.trim())}>
          {mode === 'send' ? (proceedLabel ?? 'Send anyway') : 'Keep as quoted'}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
