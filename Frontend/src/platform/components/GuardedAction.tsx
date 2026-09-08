import type { ReactNode } from 'react';
import { Alert, AlertTitle, Box, Button, CircularProgress } from '@mui/material';
import type { ButtonProps } from '@mui/material';

/**
 * An action that CANNOT be switched off silently.
 *
 * WHY THIS EXISTS. A walk of the platform console found four disabled primary actions and three
 * different conventions between them:
 *
 *   - Activation ("Activate tenant") prints every blocker inline, in the operator's words, with a
 *     button beside each one that goes and fixes it. This is the right answer and it already
 *     shipped — on exactly one tab.
 *   - Billing ("Compute statement") and Email ("Save email settings") put the reason in a hover
 *     tooltip. The sentence is good; the delivery is not. A tooltip is invisible until somebody
 *     guesses that hovering a dead control will explain it, is unreachable by keyboard, and does
 *     not exist at all on a touch screen.
 *   - Email ("Send test email") says nothing anywhere.
 *
 * A greyed-out button with no visible reason is not a safety feature. It is a support ticket: the
 * operator cannot tell "you are missing a step" from "this product is broken", and the only way
 * out is to ask somebody. That is exactly the training cost this console is supposed to avoid.
 *
 * HOW IT ENFORCES THAT. There is no `disabled` prop. The only way to make this button inert is to
 * hand it `blockers`, and every blocker must carry a `reason` — so "disabled" and "the operator
 * has been told why" are the same act, and the silent version does not compile. Each blocker may
 * also carry the control that clears it, which turns a dead end into a next step.
 *
 * The reason renders BELOW the button, in the layout, not on hover — visible at rest, to everyone,
 * on every input device.
 */
export interface ActionBlocker {
  /** What is missing, in the operator's words. A sentence, not a field name. */
  reason: string;
  /** The control that clears it, when one exists. Turns "you cannot" into "here is how". */
  fix?: { label: string; onClick: () => void };
}

export interface GuardedActionProps {
  label: string;
  onClick: () => void;
  /** Empty or absent means actionable. Any entry disables the button AND is printed. */
  blockers?: readonly ActionBlocker[];
  busy?: boolean;
  busyLabel?: string;
  startIcon?: ReactNode;
  variant?: ButtonProps['variant'];
  color?: ButtonProps['color'];
  size?: ButtonProps['size'];
  /** Width of the reason block. Defaults to sitting under the button. */
  fullWidth?: boolean;
}

export default function GuardedAction({
  label, onClick, blockers, busy = false, busyLabel,
  startIcon, variant = 'contained', color, size, fullWidth = false,
}: GuardedActionProps) {
  const reasons = (blockers ?? []).filter((b) => b.reason.trim().length > 0);
  const blocked = reasons.length > 0;

  return (
    <Box sx={{ display: 'inline-flex', flexDirection: 'column', gap: 1, width: fullWidth ? '100%' : 'auto' }}>
      <Button
        variant={variant}
        color={color}
        size={size}
        startIcon={busy ? undefined : startIcon}
        disabled={blocked || busy}
        onClick={onClick}
        sx={{ fontWeight: 700, alignSelf: 'flex-start' }}
      >
        {busy ? <CircularProgress size={20} color="inherit" /> : label}
        {busy && busyLabel ? <Box component="span" sx={{ ml: 1 }}>{busyLabel}</Box> : null}
      </Button>

      {blocked && (
        <Alert
          severity="info"
          variant="outlined"
          sx={{ py: 0.5, '& .MuiAlert-message': { py: 0.5, width: '100%' } }}
        >
          {reasons.length > 1 && (
            <AlertTitle sx={{ fontWeight: 700, mb: 0.5, fontSize: '0.8125rem' }}>
              {reasons.length} things to do first
            </AlertTitle>
          )}
          <Box
            component={reasons.length > 1 ? 'ul' : 'div'}
            sx={reasons.length > 1 ? { m: 0, pl: 2.5 } : { m: 0 }}
          >
            {reasons.map((b) => (
              <Box
                component={reasons.length > 1 ? 'li' : 'div'}
                key={b.reason}
                sx={{
                  display: 'flex', alignItems: 'baseline', gap: 1,
                  flexWrap: 'wrap', fontSize: '0.8125rem', mb: 0.25,
                }}
              >
                <span>{b.reason}</span>
                {b.fix && (
                  <Button size="small" onClick={b.fix.onClick} sx={{ py: 0, minWidth: 0, fontWeight: 700 }}>
                    {b.fix.label}
                  </Button>
                )}
              </Box>
            ))}
          </Box>
        </Alert>
      )}
    </Box>
  );
}
