import { useState, type FormEvent } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  CircularProgress,
  IconButton,
  InputAdornment,
  LinearProgress,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import {
  CheckCircleOutlined as MetIcon,
  DarkMode as MoonIcon,
  LightMode as SunIcon,
  LockOutlined as LockIcon,
  RadioButtonUnchecked as UnmetIcon,
  Visibility,
  VisibilityOff,
} from '@mui/icons-material';
import { useAppTheme } from '../../context/ThemeContext';
import useDocumentTitle from '../../hooks/useDocumentTitle';
import { presentableErrorMessage } from '../../utils/apiErrors';
import {
  PASSWORD_MAX_LENGTH,
  PASSWORD_RULES,
  isPasswordAcceptable,
  readPasswordStrength,
} from '../../utils/passwordPolicy';
import { completePasswordReset, inspectResetToken, type ResetTokenState } from './passwordResetApi';

/**
 * Copy for every way a reset link can fail.
 *
 * None of it confirms whether an account exists: the person holding the link
 * already knows the address it was sent to, and anyone who does not must learn
 * nothing from the response. "Expired" and "already used" are safe to name
 * because they describe the LINK, not the account.
 *
 * `revoked` is the one that reads differently from its activation twin, and it
 * has to. There is no operator on this flow — nobody at the platform can cause
 * or cancel a reset — so the only way a link becomes superseded is that a newer
 * one was requested for the same account. Saying "an operator withdrew it" would
 * be false; saying "a newer link replaced it" tells the customer exactly which
 * email in their inbox to open instead.
 */
const REJECTION_COPY: Record<Exclude<ResetTokenState, 'valid'>, { title: string; body: string; retry: boolean }> = {
  expired: {
    title: 'This reset link has expired',
    body: 'Reset links are deliberately short-lived. Request a new one and use it straight away.',
    retry: false,
  },
  used: {
    title: 'This reset link has already been used',
    body: 'A new password has already been set with this link. Sign in with it — or if that was not you, request another link now and tell whoever administers your workspace.',
    retry: false,
  },
  revoked: {
    title: 'A newer reset link replaced this one',
    body: 'Only the most recent link works, so that a link somebody else requested cannot stay live. Open the latest reset email, or request a new one.',
    retry: false,
  },
  invalid: {
    title: 'This reset link is not valid',
    body: 'The address may have been copied incompletely. Open the link directly from the email, or request a new one.',
    retry: false,
  },
  unavailable: {
    title: 'We could not check this link just now',
    body: 'This is a problem on our side, not with your link. Please try again in a moment.',
    retry: true,
  },
};

const ERROR_ID = 'reset-password-error';

export default function ResetPasswordPage() {
  useDocumentTitle('Choose a New Password');
  const { token = '' } = useParams();
  const navigate = useNavigate();
  const { mode, setMode } = useAppTheme();

  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const challenge = useQuery({
    queryKey: ['password-reset', token],
    queryFn: () => inspectResetToken(token),
    enabled: token.length > 0,
    // A token verdict is deterministic; retrying only delays the explanation.
    retry: false,
    refetchOnWindowFocus: false,
  });

  const reset = useMutation({
    mutationFn: () => completePasswordReset(token, password),
    onSuccess: (outcome) => {
      if (!outcome.ok) {
        // The token was spent or superseded between loading the page and
        // submitting it — which on this flow is ordinary, because requesting a
        // second link is exactly what the previous screen tells people to do.
        // Re-reading it swaps the form for the right explanation.
        challenge.refetch();
        return;
      }
      // LoginPage renders `authNotice` once and clears it, so the customer lands
      // on the sign-in screen already knowing why they are there. Unlike
      // activation there is no "is the workspace open yet?" question to ask: the
      // account was already live — that is a precondition of having been sent a
      // link at all — so sign-in is the correct and only next step.
      sessionStorage.setItem('authNotice', 'Your password has been changed. Sign in to continue.');
      navigate('/login', { replace: true });
    },
    onError: (error) =>
      setFailure(presentableErrorMessage(error, 'We could not change your password. Please try again.')),
  });

  const strength = readPasswordStrength(password);
  const mismatch = confirmation.length > 0 && confirmation !== password;
  const canSubmit = isPasswordAcceptable(password) && confirmation === password && !reset.isPending;

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    setSubmitted(true);
    setFailure(null);
    if (!canSubmit) return;
    reset.mutate();
  };

  const state: ResetTokenState | null = token.length === 0 ? 'invalid' : challenge.data?.state ?? null;

  return (
    <Box
      component="main"
      id="main-content"
      tabIndex={-1}
      sx={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        p: 3,
        bgcolor: 'background.default',
        background: 'radial-gradient(1200px 600px at 50% -10%, rgba(99,102,241,0.14), transparent 60%)',
        '&:focus': { outline: 'none' },
      }}
    >
      <Paper sx={{ maxWidth: 520, width: '100%', p: { xs: 3, sm: 4 }, borderRadius: 4, position: 'relative' }}>
        <IconButton
          onClick={() => setMode(mode === 'dark' ? 'light' : 'dark')}
          aria-label={mode === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
          sx={{ position: 'absolute', top: 12, right: 12 }}
        >
          {mode === 'dark' ? <SunIcon fontSize="small" /> : <MoonIcon fontSize="small" />}
        </IconButton>

        {challenge.isLoading && state === null ? (
          <Stack spacing={2} role="status" aria-live="polite" sx={{ alignItems: 'center', py: 6 }}>
            <CircularProgress />
            <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
              Checking your link…
            </Typography>
          </Stack>
        ) : state !== 'valid' && state !== null ? (
          <Stack spacing={2.5}>
            <Typography variant="h5" component="h1" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
              {REJECTION_COPY[state].title}
            </Typography>
            <Alert severity={REJECTION_COPY[state].retry ? 'warning' : 'info'} sx={{ borderRadius: 2 }}>
              {REJECTION_COPY[state].body}
            </Alert>
            <Stack direction="row" spacing={1.5}>
              {REJECTION_COPY[state].retry ? (
                <Button variant="contained" onClick={() => challenge.refetch()} sx={{ fontWeight: 700 }}>
                  Try again
                </Button>
              ) : (
                // Every non-retryable verdict has the same fix — get a new link —
                // so the primary action goes there rather than to /login, where
                // somebody who cannot remember their password has nothing to do.
                <Button
                  variant="contained"
                  onClick={() => navigate('/forgot-password')}
                  sx={{ fontWeight: 700 }}
                >
                  Request a new link
                </Button>
              )}
              <Button variant="outlined" onClick={() => navigate('/login')} sx={{ fontWeight: 700 }}>
                Go to sign in
              </Button>
            </Stack>
          </Stack>
        ) : (
          <Stack spacing={2.5}>
            <Box>
              <Typography variant="h5" component="h1" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
                Choose a new password
              </Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75 }}>
                {challenge.data?.firstName
                  ? `Hi ${challenge.data.firstName} — pick a password you have not used here before.`
                  : 'Pick a password you have not used here before.'}
              </Typography>
            </Box>

            {challenge.data?.email && (
              <Stack spacing={0.5}>
                <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary' }}>
                  Your sign-in email
                </Typography>
                {/* Masked by the server, with no opt-out. A reset link is caused
                    by whoever typed an address into a public form — possibly not
                    the owner — so the exact string is never echoed back. */}
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.95rem' }}>
                  {challenge.data.email}
                </Typography>
              </Stack>
            )}

            <form onSubmit={handleSubmit} noValidate>
              <Stack spacing={2}>
                <TextField
                  label="New password"
                  id="reset-password"
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="new-password"
                  fullWidth
                  required
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  error={submitted && !isPasswordAcceptable(password)}
                  slotProps={{
                    htmlInput: {
                      maxLength: PASSWORD_MAX_LENGTH,
                      'aria-describedby': 'reset-password-policy',
                    },
                    input: {
                      startAdornment: (
                        <InputAdornment position="start">
                          <LockIcon sx={{ color: 'primary.main', opacity: 0.7 }} fontSize="small" />
                        </InputAdornment>
                      ),
                      endAdornment: (
                        <InputAdornment position="end">
                          <IconButton
                            onClick={() => setShowPassword((visible) => !visible)}
                            edge="end"
                            size="small"
                            aria-label={showPassword ? 'Hide password' : 'Show password'}
                            aria-pressed={showPassword}
                          >
                            {showPassword ? <VisibilityOff fontSize="small" /> : <Visibility fontSize="small" />}
                          </IconButton>
                        </InputAdornment>
                      ),
                    },
                  }}
                />

                {password.length > 0 && (
                  <Box>
                    <LinearProgress
                      variant="determinate"
                      value={strength.score}
                      aria-hidden
                      color={strength.strength === 'weak' ? 'error' : strength.strength === 'fair' ? 'warning' : 'success'}
                      sx={{ height: 6, borderRadius: 3 }}
                    />
                    {/* The bar is decorative; this line is what gets announced. */}
                    <Typography variant="caption" role="status" aria-live="polite" sx={{ fontWeight: 700, color: 'text.secondary' }}>
                      Password strength: {strength.label}
                    </Typography>
                  </Box>
                )}

                {/* The same checklist the activation page renders, from the same
                    module, because the server applies literally the same policy
                    at the same floor to both doors. Two lists would drift, and
                    the drift would show up as a rejection the form said was
                    fine. */}
                <Box id="reset-password-policy">
                  <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary' }}>
                    Your password must contain:
                  </Typography>
                  <Box component="ul" sx={{ listStyle: 'none', pl: 0, m: 0, mt: 0.5 }}>
                    {PASSWORD_RULES.map((rule) => {
                      const met = rule.satisfied(password);
                      return (
                        <Box component="li" key={rule.id} sx={{ display: 'flex', alignItems: 'center', gap: 0.75, py: 0.15 }}>
                          {met ? (
                            <MetIcon sx={{ fontSize: 16, color: 'success.main' }} titleAccess="Requirement met" />
                          ) : (
                            <UnmetIcon sx={{ fontSize: 16, color: 'text.disabled' }} titleAccess="Requirement not met" />
                          )}
                          <Typography variant="caption" sx={{ color: met ? 'text.primary' : 'text.secondary' }}>
                            {rule.label}
                          </Typography>
                        </Box>
                      );
                    })}
                  </Box>
                </Box>

                <TextField
                  label="Confirm new password"
                  id="reset-password-confirm"
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="new-password"
                  fullWidth
                  required
                  value={confirmation}
                  onChange={(event) => setConfirmation(event.target.value)}
                  error={mismatch || (submitted && confirmation !== password)}
                  helperText={mismatch || (submitted && confirmation !== password) ? 'Both passwords must match.' : ' '}
                />

                {failure && (
                  <Alert id={ERROR_ID} role="alert" severity="error" sx={{ borderRadius: 2 }}>
                    <AlertTitle sx={{ fontWeight: 800 }}>We could not change your password</AlertTitle>
                    {failure}
                  </Alert>
                )}

                <Button
                  type="submit"
                  variant="contained"
                  size="large"
                  disabled={reset.isPending}
                  sx={{ py: 1.5, fontWeight: 700 }}
                >
                  {reset.isPending ? <CircularProgress size={22} color="inherit" /> : 'Change password and sign in'}
                </Button>
              </Stack>
            </form>
          </Stack>
        )}
      </Paper>
    </Box>
  );
}
