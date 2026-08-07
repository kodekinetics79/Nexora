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
import { completeActivation, inspectActivationToken, type ActivationState } from './activationApi';

/**
 * Copy for every way an activation link can fail.
 *
 * None of it confirms whether an account exists: the person holding the link
 * already knows the address they were sent, and anyone who does not must learn
 * nothing from the response. "Expired" and "already used" are safe to name
 * because they describe the link, not the account. A withdrawn invitation says
 * only that — never who withdrew it or why.
 */
const REJECTION_COPY: Record<Exclude<ActivationState, 'valid'>, { title: string; body: string; retry: boolean }> = {
  expired: {
    title: 'This activation link has expired',
    body: 'Activation links are short-lived for security. Ask whoever set up your workspace to send a new invitation.',
    retry: false,
  },
  used: {
    title: 'This activation link has already been used',
    body: 'A password has already been set with this link. Sign in with it, or use "forgot password" on the sign-in page if you do not have it.',
    retry: false,
  },
  revoked: {
    title: 'This invitation is no longer active',
    body: 'The invitation was withdrawn. Contact whoever set up your workspace to have a new one issued.',
    retry: false,
  },
  invalid: {
    title: 'This activation link is not valid',
    body: 'The address may have been copied incompletely. Open the link directly from the invitation, or ask for a new one.',
    retry: false,
  },
  unavailable: {
    title: 'We could not check this link just now',
    body: 'This is a problem on our side, not with your link. Please try again in a moment.',
    retry: true,
  },
};

const ERROR_ID = 'activation-error';

export default function ActivateAccountPage() {
  useDocumentTitle('Activate your account');
  const { token = '' } = useParams();
  const navigate = useNavigate();
  const { mode, setMode } = useAppTheme();

  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [submitted, setSubmitted] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  const challenge = useQuery({
    queryKey: ['tenant-activation', token],
    queryFn: () => inspectActivationToken(token),
    enabled: token.length > 0,
    // A token verdict is deterministic; retrying only delays the explanation.
    retry: false,
    refetchOnWindowFocus: false,
  });

  const activation = useMutation({
    mutationFn: () => completeActivation(token, password),
    onSuccess: (outcome) => {
      if (!outcome.ok) {
        // The token was spent or withdrawn between loading the page and
        // submitting it. Re-reading it swaps the form for the right explanation.
        challenge.refetch();
        return;
      }
      // LoginPage renders `authNotice` once and clears it, so the customer lands
      // on the sign-in screen already knowing why they are there.
      sessionStorage.setItem('authNotice', 'Your password is set. Sign in to continue.');
      navigate('/login', { replace: true });
    },
    onError: (error) =>
      setFailure(presentableErrorMessage(error, 'We could not set your password. Please try again.')),
  });

  const strength = readPasswordStrength(password);
  const mismatch = confirmation.length > 0 && confirmation !== password;
  const canSubmit = isPasswordAcceptable(password) && confirmation === password && !activation.isPending;

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    setSubmitted(true);
    setFailure(null);
    if (!canSubmit) return;
    activation.mutate();
  };

  const state: ActivationState | null = token.length === 0 ? 'invalid' : challenge.data?.state ?? null;

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
              Checking your invitation…
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
              {REJECTION_COPY[state].retry && (
                <Button variant="contained" onClick={() => challenge.refetch()} sx={{ fontWeight: 700 }}>
                  Try again
                </Button>
              )}
              <Button variant={REJECTION_COPY[state].retry ? 'outlined' : 'contained'} onClick={() => navigate('/login')} sx={{ fontWeight: 700 }}>
                Go to sign in
              </Button>
            </Stack>
          </Stack>
        ) : (
          <Stack spacing={2.5}>
            <Box>
              <Typography variant="h5" component="h1" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
                Set your password
              </Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75 }}>
                {challenge.data?.tenantName
                  ? `You are the administrator of ${challenge.data.tenantName}. Choose a password and your workspace is yours.`
                  : 'Choose a password and your workspace is yours.'}
              </Typography>
            </Box>

            {challenge.data?.email && (
              <Stack spacing={0.5}>
                <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary' }}>
                  Your sign-in email
                </Typography>
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.95rem' }}>{challenge.data.email}</Typography>
              </Stack>
            )}

            <form onSubmit={handleSubmit} noValidate>
              <Stack spacing={2}>
                <TextField
                  label="New password"
                  id="activation-password"
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
                      'aria-describedby': 'password-policy',
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

                <Box id="password-policy">
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
                  label="Confirm password"
                  id="activation-password-confirm"
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
                    <AlertTitle sx={{ fontWeight: 800 }}>We could not set your password</AlertTitle>
                    {failure}
                  </Alert>
                )}

                <Button
                  type="submit"
                  variant="contained"
                  size="large"
                  disabled={activation.isPending}
                  sx={{ py: 1.5, fontWeight: 700 }}
                >
                  {activation.isPending ? <CircularProgress size={22} color="inherit" /> : 'Set password and continue'}
                </Button>
              </Stack>
            </form>
          </Stack>
        )}
      </Paper>
    </Box>
  );
}
