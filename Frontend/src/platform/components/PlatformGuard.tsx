import type { ReactNode } from 'react';
import { Box, Button, Paper, Stack, Typography } from '@mui/material';
import { ShieldMoon as ShieldIcon, VpnKey as KeyIcon } from '@mui/icons-material';
import { useAuth } from '../../context/AuthContext';
import { usePlatformAuth } from '../auth/usePlatformAuth';
import { useAppTheme } from '../../context/ThemeContext';

/**
 * Gates the entire `/platform` tree on platform scope.
 *
 * Two checks, in order:
 *   1. The user must be authenticated at all (tenant session token present).
 *   2. The principal must hold the `scope=platform` claim (placeholder today —
 *      see usePlatformAuth's TODO to wire the real platform token).
 *
 * When scope is missing we render an in-place elevation prompt rather than a
 * hard redirect, so an owner can grant scope without bouncing to /login.
 */
export default function PlatformGuard({ children }: { children: ReactNode }) {
  const { token } = useAuth();
  const { isPlatformOwner, enterPlatform } = usePlatformAuth();
  const { primaryColor } = useAppTheme();

  if (!token) {
    // Not signed in at all — the app-level guard convention is to send to login.
    window.location.href = '/login';
    return null;
  }

  if (!isPlatformOwner) {
    return (
      <Box
        sx={{
          minHeight: '100vh',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          p: 3,
          background: `radial-gradient(1200px 600px at 50% -10%, ${primaryColor}14, transparent 60%)`,
        }}
      >
        <Paper sx={{ maxWidth: 460, width: '100%', p: 4, borderRadius: 4, textAlign: 'center' }}>
          <Box
            sx={{
              width: 64,
              height: 64,
              borderRadius: 3,
              mx: 'auto',
              mb: 2,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              bgcolor: `${primaryColor}18`,
              color: primaryColor,
            }}
          >
            <ShieldIcon sx={{ fontSize: 34 }} />
          </Box>
          <Typography variant="h5" sx={{ fontWeight: 800, mb: 1 }}>
            Platform Console
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
            This is the Nexora operator control plane. Access requires the{' '}
            <Box component="code" sx={{ px: 0.6, py: 0.2, borderRadius: 1, bgcolor: 'action.hover', fontWeight: 700 }}>
              scope=platform
            </Box>{' '}
            claim, which sits above tenant permissions.
          </Typography>
          <Stack spacing={1.5}>
            <Button variant="contained" size="large" startIcon={<KeyIcon />} onClick={enterPlatform} sx={{ fontWeight: 700 }}>
              Elevate to Platform Owner
            </Button>
            <Typography variant="caption" color="text.secondary">
              Placeholder elevation — production wires this to a platform-scoped token.
            </Typography>
          </Stack>
        </Paper>
      </Box>
    );
  }

  return <>{children}</>;
}
