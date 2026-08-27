import { useCallback, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  TextField,
} from '@mui/material';
import { isAxiosError } from 'axios';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';

/**
 * The console's half of `POST /api/platform/auth/reauthenticate`.
 *
 * <b>What was broken.</b> Five high-risk operations — tenant export, tenant purge,
 * personal-data erasure, legal-hold release and subscription-invoice finalisation — carry
 * `[PlatformHighRiskOperation]` on the server. When MFA enforcement is relaxed (the only way a
 * platform session can lack `amr=mfa`), that filter refuses the request with a 403 whose body
 * says, in words: "POST your current password to /api/platform/auth/reauthenticate, then retry
 * within N minutes." Nothing in this codebase ever called that endpoint. The console rendered
 * the sentence faithfully and the operator had no control anywhere that could carry it out, so
 * every one of those five verbs was simply dead on a relaxed deployment — which is exactly the
 * deployment where somebody is most likely to be doing lifecycle work.
 *
 * <b>The shape.</b> `guard` wraps the call rather than replacing it, so each call site changes
 * by one line and the request contract, the typed confirmations and the reason fields are all
 * untouched. On an MFA-bound session the server never asks, `guard` never opens anything, and
 * the behaviour is byte-identical to before. The password is held only in this component's
 * state for the moment it is submitted; it is never put into the retried request.
 *
 * <b>Why it retries automatically.</b> The step-up window is minutes long and the operator has
 * already typed the tenant name, the reason and the confirmation phrase. Making them re-open
 * the dialog and retype all of it is how a control gets routed around.
 */

/**
 * Thrown when the operator dismisses the step-up dialog. Call sites treat it as "nothing
 * happened" rather than as a failure, because nothing did.
 */
export class StepUpCancelled extends Error {
  constructor() {
    super('Step-up re-authentication was cancelled, so the action was not carried out.');
    this.name = 'StepUpCancelled';
  }
}

export const isStepUpCancelled = (error: unknown): boolean => error instanceof StepUpCancelled;

/** True when the server refused because this session must prove its password first. */
export const isStepUpRequired = (error: unknown): boolean => {
  if (!isAxiosError(error) || error.response?.status !== 403) return false;
  const data = error.response.data;
  return Boolean(data && typeof data === 'object'
    && (data as Record<string, unknown>).reauthenticationRequired === true);
};

/** The operation name the server put in the refusal, for the dialog to name it back. */
const refusedOperation = (error: unknown): string | null => {
  if (!isAxiosError(error)) return null;
  const value = (error.response?.data as Record<string, unknown> | undefined)?.operation;
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
};

interface PendingPrompt {
  operation: string | null;
  resolve: () => void;
  reject: (reason: unknown) => void;
}

export interface StepUpReauthentication {
  /**
   * Runs `action`. If the server answers "re-authenticate first", asks for the platform
   * password, proves it, and runs `action` once more. Any other failure propagates untouched,
   * and so does a refusal after the retry.
   */
  guard: <T>(action: () => Promise<T>) => Promise<T>;
  /** Render this once per screen — it is inert until a step-up is actually demanded. */
  dialog: React.ReactNode;
}

export function useStepUpReauthentication(): StepUpReauthentication {
  const [pending, setPending] = useState<PendingPrompt | null>(null);
  const [password, setPassword] = useState('');
  const [problem, setProblem] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Held in a ref as well so `guard` can settle the promise without re-reading state it closed
  // over — a second high-risk action started while the first dialog is open must not be lost.
  const pendingRef = useRef<PendingPrompt | null>(null);

  const settle = useCallback((outcome: 'ok' | 'cancel') => {
    const prompt = pendingRef.current;
    pendingRef.current = null;
    setPending(null);
    setPassword('');
    setProblem(null);
    setBusy(false);
    if (!prompt) return;
    if (outcome === 'ok') prompt.resolve();
    else prompt.reject(new StepUpCancelled());
  }, []);

  const prompt = useCallback((operation: string | null) => new Promise<void>((resolve, reject) => {
    const next: PendingPrompt = { operation, resolve, reject };
    pendingRef.current = next;
    setPassword('');
    setProblem(null);
    setPending(next);
  }), []);

  const guard = useCallback(async <T,>(action: () => Promise<T>): Promise<T> => {
    try {
      return await action();
    } catch (error) {
      if (!isStepUpRequired(error)) throw error;
      await prompt(refusedOperation(error));
      return action();
    }
  }, [prompt]);

  const submit = async () => {
    if (password.length === 0 || busy) return;
    setBusy(true);
    setProblem(null);
    try {
      await platformApi.reauthenticate(password);
      settle('ok');
    } catch (error) {
      setBusy(false);
      setProblem(platformErrorMessage(error, 'That step-up was refused'));
    }
  };

  const dialog = (
    <Dialog
      open={pending !== null}
      onClose={() => !busy && settle('cancel')}
      maxWidth="xs"
      fullWidth
    >
      <DialogTitle sx={{ fontWeight: 800 }}>Confirm it is still you</DialogTitle>
      <DialogContent>
        <DialogContentText sx={{ mb: 2 }}>
          Multi-factor enforcement is relaxed on this deployment, so a high-risk action needs your
          platform password again before it will run
          {pending?.operation ? ` (${pending.operation})` : ''}. Your action is remembered — it
          continues by itself once the password is accepted, and nothing you typed is lost.
        </DialogContentText>
        <TextField
          fullWidth
          type="password"
          label="Platform password"
          autoComplete="current-password"
          value={password}
          disabled={busy}
          onChange={(event) => setPassword(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter') {
              event.preventDefault();
              void submit();
            }
          }}
          error={Boolean(problem)}
        />
        {problem && (
          <Alert severity="error" sx={{ mt: 2, borderRadius: 2 }}>
            {problem}
          </Alert>
        )}
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button onClick={() => settle('cancel')} disabled={busy}>
          Cancel
        </Button>
        <Button variant="contained" onClick={() => void submit()} disabled={busy || password.length === 0}>
          {busy ? 'Checking…' : 'Confirm and continue'}
        </Button>
      </DialogActions>
    </Dialog>
  );

  return { guard, dialog };
}
