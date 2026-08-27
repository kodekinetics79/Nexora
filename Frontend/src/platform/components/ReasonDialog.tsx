import { useEffect, useId, useState, type ReactNode } from 'react';
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  TextField,
  Typography,
} from '@mui/material';
import Stack from './Flex';

/**
 * The shortest reason worth storing. Below this the field is satisfied and the audit row
 * still answers "what" while refusing to answer "why", which is the only question anybody
 * asks it six months later.
 */
export const MIN_REASON_LENGTH = 5;

export const reasonProblem = (reason: string, minLength = MIN_REASON_LENGTH): string | null => {
  const trimmed = reason.trim();
  if (trimmed.length === 0) return 'A reason is required. It is recorded in the platform audit trail.';
  if (trimmed.length < minLength) return `Give at least ${minLength} characters — this is read by a person later.`;
  return null;
};

interface Props {
  open: boolean;
  title: string;
  /** What the action does, in the operator's terms. Rendered above the reason field. */
  description: ReactNode;
  confirmLabel: string;
  confirmColor?: 'primary' | 'error' | 'warning' | 'success' | 'inherit';
  reasonLabel?: string;
  reasonHelper?: string;
  minReasonLength?: number;
  /** Extra inputs the action needs, e.g. a retention window. */
  extra?: ReactNode;
  /** Blocks confirmation for reasons the caller owns, e.g. an invalid retention window. */
  extraProblem?: string | null;
  busy?: boolean;
  onClose: () => void;
  onConfirm: (reason: string) => void;
}

/**
 * The single reason-collecting confirmation used by every privileged, reversible action in
 * the console. Centralised so the reason floor, the audit disclosure and the error wiring
 * cannot drift between one lifecycle verb and the next.
 */
export default function ReasonDialog({
  open,
  title,
  description,
  confirmLabel,
  confirmColor = 'primary',
  reasonLabel = 'Reason',
  reasonHelper,
  minReasonLength = MIN_REASON_LENGTH,
  extra,
  extraProblem = null,
  busy = false,
  onClose,
  onConfirm,
}: Props) {
  const [reason, setReason] = useState('');
  const [touched, setTouched] = useState(false);
  const titleId = useId();

  useEffect(() => {
    if (!open) return;
    setReason('');
    setTouched(false);
  }, [open]);

  const problem = reasonProblem(reason, minReasonLength);
  const message = touched ? problem : null;
  const blocked = Boolean(problem) || Boolean(extraProblem) || busy;

  return (
    <Dialog
      open={open}
      onClose={() => (busy ? undefined : onClose())}
      maxWidth="sm"
      fullWidth
      aria-labelledby={titleId}
    >
      <DialogTitle id={titleId} sx={{ fontWeight: 800 }}>
        {title}
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Typography variant="body2" component="div" color="text.secondary">
            {description}
          </Typography>
          {extra}
          <TextField
            fullWidth
            required
            multiline
            minRows={2}
            label={reasonLabel}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            onBlur={() => setTouched(true)}
            error={Boolean(message)}
            helperText={message ?? reasonHelper ?? ' '}
          />
          {/* Stated on the control itself rather than in a policy document nobody has open. */}
          <Alert severity="info" sx={{ borderRadius: 2 }}>
            This action and the reason you give are written to the platform audit trail against
            your operator account.
          </Alert>
          {extraProblem && (
            <Alert role="alert" severity="error" sx={{ borderRadius: 2 }}>
              {extraProblem}
            </Alert>
          )}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} color="inherit" disabled={busy}>
          Cancel
        </Button>
        <Button
          variant="contained"
          color={confirmColor}
          onClick={() => {
            setTouched(true);
            if (!blocked) onConfirm(reason.trim());
          }}
          disabled={blocked}
          sx={{ fontWeight: 700 }}
        >
          {busy ? 'Working…' : confirmLabel}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
