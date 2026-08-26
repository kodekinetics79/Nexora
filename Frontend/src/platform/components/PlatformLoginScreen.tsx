import { useState, type FormEvent } from 'react';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  CircularProgress,
  FormControlLabel,
  IconButton,
  InputAdornment,
  Paper,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import {
  Bolt as BoltIcon,
  LockOutlined as LockIcon,
  MailOutlined as MailIcon,
  Visibility,
  VisibilityOff,
} from '@mui/icons-material';
import { usePlatformAuth } from '../auth/usePlatformAuth';
import type { PlatformMfaChallenge } from '../auth/usePlatformAuth';
import { platformErrorMessage } from '../api/apiError';
import { fmtTrustWindow } from './format';
import FeatureHelp from '../../components/common/FeatureHelp';

/**
 * The platform-owner sign-in screen. Rendered in place by `PlatformGuard` when
 * no platform session is present. On success it stores a dedicated platform
 * token; the guard then re-renders the console (the session store drives it).
 */
export default function PlatformLoginScreen() {
  const { platformLogin, platformCompleteMfa } = usePlatformAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [challenge, setChallenge] = useState<PlatformMfaChallenge | null>(null);
  const [useRecoveryCode, setUseRecoveryCode] = useState(false);
  const [rememberBrowser, setRememberBrowser] = useState(false);
  const [verificationCode, setVerificationCode] = useState('');

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      const result = await platformLogin(email.trim(), password);
      if (result.mfaRequired) setChallenge(result.challenge);
      // No navigation needed — PlatformGuard re-renders on the session change.
    } catch (err: unknown) {
      const status =
        typeof err === 'object' && err !== null && 'response' in err
          ? (err as { response?: { status?: number } }).response?.status
          : undefined;
      setError(
        status === 401 || status === 403
          ? 'Invalid platform credentials, or this account lacks platform scope.'
          : 'Unable to reach the platform control plane. Please try again.',
      );
    } finally {
      setLoading(false);
    }
  };

  const handleMfaSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!challenge) return;
    setLoading(true);
    setError(null);
    try {
      await platformCompleteMfa(
        challenge,
        useRecoveryCode
          ? { recoveryCode: verificationCode.trim(), rememberBrowser }
          : { totpCode: verificationCode.trim(), rememberBrowser },
      );
    } catch (err: unknown) {
      setError(platformErrorMessage(err, 'The verification code was refused. Try again before the challenge expires.'));
    } finally {
      setLoading(false);
    }
  };

  return (
    <Box
      sx={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        p: 3,
        background:
          'radial-gradient(1200px 600px at 50% -10%, rgba(99,102,241,0.14), transparent 60%)',
      }}
    >
      <Paper sx={{ maxWidth: 440, width: '100%', p: { xs: 3, sm: 4 }, borderRadius: 4 }}>
        <Stack spacing={1.5} sx={{ mb: 3, textAlign: 'center', alignItems: 'center' }}>
          <Box
            sx={{
              width: 56,
              height: 56,
              borderRadius: 3,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              background: 'linear-gradient(135deg, #6366f1 0%, #0ea5e9 100%)',
              color: '#fff',
              boxShadow: '0 10px 24px -8px rgba(99,102,241,0.6)',
            }}
          >
            <BoltIcon sx={{ fontSize: 30 }} />
          </Box>
          <Box>
            <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 0.25 }}>
              <Typography variant="h5" sx={{ fontWeight: 900, letterSpacing: '-0.5px' }}>
                Platform Console
              </Typography>
              <FeatureHelp
                label="Platform Console"
                description="A separate control plane for authorized platform operators. Platform Owners can administer, offboard, and permanently delete tenants here; a tenant administrator cannot delete an entire tenant."
                placement="right"
              />
            </Box>
            <Typography
              sx={{
                fontWeight: 700,
                fontSize: 10.5,
                letterSpacing: '0.16em',
                color: 'primary.main',
                textTransform: 'uppercase',
              }}
            >
              Operator Control Plane
            </Typography>
          </Box>
          <Typography variant="body2" color="text.secondary">
            Sign in with your{' '}
            <Box
              component="code"
              sx={{ px: 0.6, py: 0.2, borderRadius: 1, bgcolor: 'action.hover', fontWeight: 700 }}
            >
              scope=platform
            </Box>{' '}
            operator account.
          </Typography>
        </Stack>

        {challenge ? (
          <form onSubmit={handleMfaSubmit}>
            <Stack spacing={2.5}>
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                Password accepted. Complete multi-factor verification before{' '}
                {new Date(challenge.expiresAtUtc).toLocaleTimeString()}.
              </Alert>
              <TextField
                label={useRecoveryCode ? 'Recovery code' : '6-digit authenticator code'}
                value={verificationCode}
                onChange={(event) => setVerificationCode(event.target.value)}
                required
                fullWidth
                autoFocus
                autoComplete="one-time-code"
                slotProps={{ htmlInput: useRecoveryCode ? {} : { inputMode: 'numeric', pattern: '[0-9]{6}', maxLength: 6 } }}
              />
              <Button
                type="button"
                color="inherit"
                onClick={() => {
                  setUseRecoveryCode((current) => !current);
                  setVerificationCode('');
                  setError(null);
                }}
              >
                {useRecoveryCode ? 'Use authenticator code instead' : 'Use a recovery code'}
              </Button>
              {/* One challenge per trusted browser per window, instead of one per session. The
                  alternative — challenging every 30 minutes for a whole working day — does not make
                  an operator safer; it teaches them to approve prompts without reading them, which
                  is the reflex MFA-fatigue attacks are built on. The server bounds the window and
                  stores only a hash of the token this produces.

                  Two things about it are the SERVER's to say, and both arrive on the challenge
                  response. Whether to offer it at all: a platform Owner can switch browser trust
                  off, and a checkbox that outlived the switch would be ticked by an operator who
                  then gets challenged again tomorrow with nothing explaining why. And how long:
                  the window can now be set anywhere from 8 hours to 30 days, so "Remember this
                  browser" with no number attached is asking somebody to agree to a duration
                  neither of us named. */}
              {challenge.browserTrustOffered && (
                <FormControlLabel
                  control={
                    <Checkbox
                      checked={rememberBrowser}
                      onChange={(event) => setRememberBrowser(event.target.checked)}
                    />
                  }
                  label={`Don't ask again on this browser for ${fmtTrustWindow(challenge.browserTrustHours)}`}
                />
              )}
              {error && <Alert severity="error">{error}</Alert>}
              <Button
                type="submit"
                variant="contained"
                size="large"
                disabled={loading || (!useRecoveryCode && !/^\d{6}$/.test(verificationCode)) || (useRecoveryCode && !verificationCode.trim())}
                sx={{ py: 1.5, fontWeight: 700 }}
              >
                {loading ? <CircularProgress size={22} color="inherit" /> : 'Verify and enter'}
              </Button>
              <Button
                color="inherit"
                onClick={() => {
                  setChallenge(null);
                  setPassword('');
                  setVerificationCode('');
                  setError(null);
                }}
              >
                Back to password sign-in
              </Button>
            </Stack>
          </form>
        ) : (
        <form onSubmit={handleSubmit}>
          <Stack spacing={2.5}>
            <TextField
              label="Email"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
              fullWidth
              autoComplete="username"
              autoFocus
              slotProps={{
                input: {
                  startAdornment: (
                    <InputAdornment position="start">
                      <MailIcon sx={{ color: 'primary.main', opacity: 0.7 }} fontSize="small" />
                    </InputAdornment>
                  ),
                },
              }}
            />
            <TextField
              label="Password"
              type={showPassword ? 'text' : 'password'}
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
              fullWidth
              autoComplete="current-password"
              slotProps={{
                input: {
                  startAdornment: (
                    <InputAdornment position="start">
                      <LockIcon sx={{ color: 'primary.main', opacity: 0.7 }} fontSize="small" />
                    </InputAdornment>
                  ),
                  endAdornment: (
                    <InputAdornment position="end">
                      <IconButton onClick={() => setShowPassword((s) => !s)} edge="end" size="small">
                        {showPassword ? <VisibilityOff fontSize="small" /> : <Visibility fontSize="small" />}
                      </IconButton>
                    </InputAdornment>
                  ),
                },
              }}
            />

            {error && (
              <Alert severity="error" sx={{ borderRadius: 2 }}>
                {error}
              </Alert>
            )}

            <Button
              type="submit"
              variant="contained"
              size="large"
              disabled={loading}
              sx={{ py: 1.5, fontWeight: 700 }}
            >
              {loading ? <CircularProgress size={22} color="inherit" /> : 'Enter Control Plane'}
            </Button>
          </Stack>
        </form>
        )}

        <Button
          component="a"
          href="/login"
          fullWidth
          color="inherit"
          sx={{ mt: 2, textTransform: 'none' }}
        >
          Back to tenant sign-in
        </Button>
      </Paper>
    </Box>
  );
}
