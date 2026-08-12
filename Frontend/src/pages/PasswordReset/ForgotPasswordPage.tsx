import { useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  CircularProgress,
  IconButton,
  InputAdornment,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import {
  DarkMode as MoonIcon,
  LightMode as SunIcon,
  MailOutlined as MailIcon,
  MarkEmailReadOutlined as SentIcon,
} from '@mui/icons-material';
import { useAppTheme } from '../../context/ThemeContext';
import useDocumentTitle from '../../hooks/useDocumentTitle';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { requestPasswordReset } from './passwordResetApi';

const ERROR_ID = 'forgot-password-error';

/**
 * "I forgot my password."
 *
 * <p><b>The rule this page exists to keep, stated where the next person will read
 * it.</b> The confirmation below is shown for EVERY submitted address — one that
 * belongs to an account, one that does not, one belonging to a deactivated user.
 * The server is built to answer identically in all three cases (see
 * PasswordResetController's enumeration rule); if this page ever renders
 * something different for a "real" address, that whole defence is undone from
 * the client, and a public form becomes a way to confirm which addresses are
 * Nexora customers, one submit at a time.</p>
 *
 * <p>The wording is conditional — "if that address belongs to an account" —
 * because a flat "we've sent you an email" would be a lie in the common case
 * where somebody typed the wrong address, and would leave them waiting for a
 * message that is never coming instead of trying another one.</p>
 *
 * <p>Failures shown here are ours, never theirs: a network error or a 429. Those
 * are about whether the REQUEST happened, which the user genuinely needs to
 * know, and they say nothing about any account.</p>
 */
export default function ForgotPasswordPage() {
  useDocumentTitle('Forgot Your Password');
  const navigate = useNavigate();
  const { mode, setMode } = useAppTheme();

  const [email, setEmail] = useState('');
  const [submitted, setSubmitted] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [sent, setSent] = useState(false);

  const request = useMutation({
    mutationFn: () => requestPasswordReset(email.trim()),
    // No `onSuccess` branch on anything the server said, because the server
    // deliberately says nothing. Reaching here means the request was accepted.
    onSuccess: () => setSent(true),
    onError: (error) =>
      setFailure(
        presentableErrorMessage(
          error,
          'We could not send that request just now. Please try again in a moment.',
        ),
      ),
  });

  const trimmed = email.trim();
  const looksLikeAddress = trimmed.length > 2 && trimmed.includes('@');

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault();
    setSubmitted(true);
    setFailure(null);
    // A client-side shape check only, and only to save the user a pointless round
    // trip on an obvious typo. It is NOT a claim about whether the account
    // exists, and the server accepts anything: the check must never grow into
    // "we don't recognise that address".
    if (!looksLikeAddress || request.isPending) return;
    request.mutate();
  };

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

        {sent ? (
          <Stack spacing={2.5}>
            <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
              <SentIcon sx={{ color: 'success.main' }} />
              <Typography variant="h5" component="h1" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
                Check your email
              </Typography>
            </Stack>
            <Alert severity="success" sx={{ borderRadius: 2 }} role="status">
              {/* Conditional by design. See the component comment: this exact
                  sentence is shown whether or not the address is known, and it
                  has to be true in both cases. */}
              If <strong>{trimmed}</strong> belongs to a Nexora account, we have sent it a link to
              choose a new password. The link works once and expires shortly.
            </Alert>
            <Typography variant="body2" color="text.secondary">
              Nothing arrived? Check the spam folder, confirm you typed the right address, then try
              again.
            </Typography>
            <Stack direction="row" spacing={1.5}>
              <Button variant="contained" onClick={() => navigate('/login')} sx={{ fontWeight: 700 }}>
                Back to sign in
              </Button>
              <Button
                variant="outlined"
                onClick={() => {
                  setSent(false);
                  setSubmitted(false);
                }}
                sx={{ fontWeight: 700 }}
              >
                Use a different address
              </Button>
            </Stack>
          </Stack>
        ) : (
          <Stack spacing={2.5}>
            <Box>
              <Typography variant="h5" component="h1" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
                Forgot your password?
              </Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75 }}>
                Enter the email address you sign in with and we will send you a link to choose a new
                password. Nobody at Nexora can see or set your password for you.
              </Typography>
            </Box>

            <form onSubmit={handleSubmit} noValidate>
              <Stack spacing={2}>
                <TextField
                  label="Email address"
                  id="forgot-password-email"
                  type="email"
                  // Named as the account identifier so password managers and
                  // browser autofill offer the right value (SC 1.3.5).
                  autoComplete="username"
                  inputMode="email"
                  fullWidth
                  required
                  value={email}
                  onChange={(event) => setEmail(event.target.value)}
                  error={submitted && !looksLikeAddress}
                  helperText={
                    submitted && !looksLikeAddress ? 'Enter the email address you sign in with.' : ' '
                  }
                  slotProps={{
                    htmlInput: {
                      maxLength: 256,
                      'aria-invalid': submitted && !looksLikeAddress,
                      'aria-describedby': failure ? ERROR_ID : undefined,
                    },
                    input: {
                      startAdornment: (
                        <InputAdornment position="start">
                          <MailIcon sx={{ color: 'primary.main', opacity: 0.7 }} fontSize="small" />
                        </InputAdornment>
                      ),
                    },
                  }}
                />

                {failure && (
                  <Alert id={ERROR_ID} role="alert" severity="error" sx={{ borderRadius: 2 }}>
                    <AlertTitle sx={{ fontWeight: 800 }}>We could not send that request</AlertTitle>
                    {failure}
                  </Alert>
                )}

                <Button
                  type="submit"
                  variant="contained"
                  size="large"
                  disabled={request.isPending}
                  sx={{ py: 1.5, fontWeight: 700 }}
                >
                  {request.isPending ? <CircularProgress size={22} color="inherit" /> : 'Send reset link'}
                </Button>

                <Button variant="text" onClick={() => navigate('/login')} sx={{ fontWeight: 700 }}>
                  Back to sign in
                </Button>
              </Stack>
            </form>
          </Stack>
        )}
      </Paper>
    </Box>
  );
}
