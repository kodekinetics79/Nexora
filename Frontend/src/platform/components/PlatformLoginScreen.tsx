import { useState, type FormEvent } from 'react';
import {
  Alert,
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
  Bolt as BoltIcon,
  LockOutlined as LockIcon,
  MailOutlined as MailIcon,
  Visibility,
  VisibilityOff,
} from '@mui/icons-material';
import { usePlatformAuth } from '../auth/usePlatformAuth';

/**
 * The platform-owner sign-in screen. Rendered in place by `PlatformGuard` when
 * no platform session is present. On success it stores a dedicated platform
 * token; the guard then re-renders the console (the session store drives it).
 */
export default function PlatformLoginScreen() {
  const { platformLogin } = usePlatformAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    setLoading(true);
    setError(null);
    try {
      await platformLogin(email.trim(), password);
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
            <Typography variant="h5" sx={{ fontWeight: 900, letterSpacing: '-0.5px' }}>
              Platform Console
            </Typography>
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
      </Paper>
    </Box>
  );
}
