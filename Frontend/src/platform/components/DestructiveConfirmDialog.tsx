import { useEffect, useId, useState, type ReactNode } from 'react';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  TextField,
  Typography,
} from '@mui/material';
import { WarningAmberRounded as WarningIcon } from '@mui/icons-material';
import Stack from './Flex';
import {
  confirmationMatches,
  confirmationProblem,
  destructiveReasonProblem,
} from './destructiveConfirm';

interface Props {
  open: boolean;
  title: string;
  /** What is about to be destroyed, in the operator's terms. */
  description: ReactNode;
  /** The exact string the server demands back — the tenant's name, straight from the API. */
  confirmationRequired: string;
  confirmLabel: string;
  /** The blast radius: what the purge preview says will go, or what erasure replaces. */
  blastRadius?: ReactNode;
  /** The API's own disclosures. Rendered verbatim — they are the legal shape of the act. */
  disclosures?: string[];
  /**
   * An external fail-closed prerequisite, such as a fresh server blast-radius preview.
   * The dialog must not infer that showing warning copy is the same thing as enforcing it.
   */
  blocked?: boolean;
  busy?: boolean;
  onClose: () => void;
  onConfirm: (payload: { reason: string; confirmation: string }) => void;
}

/**
 * The confirmation used by the two operations that cannot be undone.
 *
 * <p>Three separate gates, each guarding something different. The <b>reason</b> makes the
 * decision attributable — it is the field somebody reads three months later. The <b>typed
 * tenant name</b> makes it deliberate against THIS customer: a generated token can be
 * echoed back by a script without anyone reading it, whereas typing the name requires
 * knowing which customer is about to be destroyed. The <b>blast radius</b> makes it
 * informed — the operator sees the row counts before, not after.</p>
 */
export default function DestructiveConfirmDialog({
  open,
  title,
  description,
  confirmationRequired,
  confirmLabel,
  blastRadius,
  disclosures = [],
  blocked = false,
  busy = false,
  onClose,
  onConfirm,
}: Props) {
  const [reason, setReason] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [reasonTouched, setReasonTouched] = useState(false);
  const titleId = useId();

  useEffect(() => {
    if (!open) return;
    setReason('');
    setConfirmation('');
    setReasonTouched(false);
  }, [open]);

  const reasonMessage = reasonTouched ? destructiveReasonProblem(reason) : null;
  const confirmationMessage = confirmationProblem(confirmation, confirmationRequired);
  const ready =
    destructiveReasonProblem(reason) === null &&
    confirmationMatches(confirmation, confirmationRequired) &&
    !blocked &&
    !busy;

  return (
    <Dialog
      open={open}
      onClose={() => (busy ? undefined : onClose())}
      maxWidth="sm"
      fullWidth
      aria-labelledby={titleId}
    >
      <DialogTitle id={titleId} sx={{ fontWeight: 800, display: 'flex', alignItems: 'center', gap: 1 }}>
        <WarningIcon color="error" />
        {title}
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Alert severity="error" sx={{ borderRadius: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>There is no undo</AlertTitle>
            <Typography variant="body2" component="div">
              {description}
            </Typography>
          </Alert>

          {blastRadius}

          <TextField
            fullWidth
            required
            multiline
            minRows={2}
            label="Why is this being done?"
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            onBlur={() => setReasonTouched(true)}
            error={Boolean(reasonMessage)}
            helperText={reasonMessage ?? 'Recorded in the platform audit trail and the tenant lifecycle history.'}
          />

          <Box>
            <TextField
              fullWidth
              required
              label={`Type ${confirmationRequired} to confirm`}
              value={confirmation}
              onChange={(event) => setConfirmation(event.target.value)}
              error={Boolean(confirmationMessage)}
              helperText={
                confirmationMessage ??
                'The tenant name, exactly as shown, including capitalisation.'
              }
              slotProps={{ htmlInput: { autoComplete: 'off', spellCheck: false } }}
            />
            {/* Announced as well as shown: a keyboard user should learn the confirmation
                matched without having to discover that the button became enabled. */}
            <Box role="status" aria-live="polite" sx={{ minHeight: 20, mt: 0.5 }}>
              {confirmationMatches(confirmation, confirmationRequired) && (
                <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
                  Name matched — {confirmLabel} is now enabled.
                </Typography>
              )}
            </Box>
          </Box>

          {disclosures.length > 0 && (
            <Box sx={{ p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
              <Typography variant="overline" sx={{ fontWeight: 800, letterSpacing: '0.06em' }}>
                What this does and does not do
              </Typography>
              <Stack spacing={1} sx={{ mt: 0.5 }}>
                {disclosures.map((line) => (
                  <Typography key={line} variant="caption" color="text.secondary">
                    {line}
                  </Typography>
                ))}
              </Stack>
            </Box>
          )}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} color="inherit" disabled={busy}>
          Cancel
        </Button>
        <Button
          variant="contained"
          color="error"
          onClick={() => onConfirm({ reason: reason.trim(), confirmation: confirmation.trim() })}
          disabled={!ready}
          sx={{ fontWeight: 700 }}
        >
          {busy ? 'Working…' : confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
