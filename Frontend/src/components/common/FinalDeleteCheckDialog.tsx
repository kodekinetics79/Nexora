import React, { useEffect, useState } from 'react';
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle, Stack,
  TextField, Typography,
} from '@mui/material';
import { DeleteForeverOutlined } from '@mui/icons-material';

/**
 * The second of two confirmations before an irreversible deletion.
 *
 * The first dialog says what goes and what stays and asks for a fixed word. This one asks for the
 * NUMBER of items being deleted — a figure specific to this deletion, read off the screen and typed
 * back — and the server verifies the same number against its own count before it deletes anything.
 * A word can be typed by reflex; the count of this particular deletion cannot, and a stale or
 * misread figure fails on the server, so nothing goes.
 *
 * Deliberately a popup rather than an emailed code: an emailed gate would make deletion impossible
 * for every workspace whose outbound mail is not configured. The email is sent AFTER, as a receipt
 * to every administrator, which is the thing mail is good at.
 */
export interface FinalDeleteCheckDialogProps {
  open: boolean;
  /** The exact number the administrator must type. */
  count: number;
  /** What the number counts, plural, e.g. "documents" or "messages and files". */
  noun: string;
  /** One line of consequence, e.g. "This frees 1.8 GB. Nexora keeps no backup." */
  consequence: React.ReactNode;
  /** The action button's label, e.g. "Delete 47 documents for good". */
  confirmLabel: string;
  busy?: boolean;
  /** A server refusal, already made presentable. */
  error?: string | null;
  onBack: () => void;
  onConfirm: (typedCount: number) => void;
}

const FinalDeleteCheckDialog: React.FC<FinalDeleteCheckDialogProps> = ({
  open, count, noun, consequence, confirmLabel, busy = false, error = null, onBack, onConfirm,
}) => {
  const [typed, setTyped] = useState('');
  // A fresh field every time the dialog opens: a number left over from an earlier attempt must
  // never satisfy the check for a new one.
  useEffect(() => { if (open) setTyped(''); }, [open]);
  const typedCount = Number(typed.trim());
  const hasTyped = typed.trim() !== '';
  const matches = hasTyped && Number.isInteger(typedCount) && typedCount === count;

  return (
    <Dialog
      open={open}
      onClose={busy ? undefined : onBack}
      fullWidth
      maxWidth="xs"
      aria-labelledby="final-delete-check-title"
      aria-describedby="final-delete-check-description"
    >
      <DialogTitle id="final-delete-check-title" sx={{ fontWeight: 800 }}>
        Last check before {count.toLocaleString()} {noun} go
      </DialogTitle>
      <DialogContent>
        <DialogContentText id="final-delete-check-description" component="div">
          <Typography variant="body2" sx={{ mb: 1.5 }}>{consequence}</Typography>
          <Typography variant="body2" sx={{ mb: 2 }}>
            To confirm you have read the figure, type the number of {noun} being deleted:{' '}
            <strong className="tabular-nums">{count.toLocaleString()}</strong>. The server checks the
            same number against what it is about to delete, so a figure that has changed since your
            preview deletes nothing.
          </Typography>
        </DialogContentText>
        <Stack spacing={1.5}>
          {error && <Alert severity="error">{error}</Alert>}
          <TextField
            label={`Type ${count.toLocaleString()} to confirm`}
            value={typed}
            onChange={(event) => setTyped(event.target.value.replace(/[^0-9]/g, ''))}
            fullWidth
            autoComplete="off"
            slotProps={{ htmlInput: { inputMode: 'numeric', pattern: '[0-9]*' } }}
            helperText={hasTyped && !matches
              ? 'That is not the number shown above. Nothing has been deleted.'
              : 'Digits only. This is the final step; there is no undo.'}
            error={hasTyped && !matches}
          />
        </Stack>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button onClick={onBack} disabled={busy}>Go back</Button>
        <Button
          variant="contained"
          color="error"
          startIcon={<DeleteForeverOutlined />}
          disabled={!matches || busy}
          onClick={() => onConfirm(typedCount)}
        >
          {busy ? 'Deleting…' : confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default FinalDeleteCheckDialog;
