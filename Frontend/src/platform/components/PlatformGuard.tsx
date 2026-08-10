import type { ReactNode } from 'react';
import { Box, Button, Paper, Typography } from '@mui/material';
import { usePlatformAuth } from '../auth/usePlatformAuth';
import PlatformLoginScreen from './PlatformLoginScreen';
import PlatformMfaPanel from './PlatformMfaPanel';

/**
 * Gates the entire `/platform` tree on a dedicated platform session.
 *
 * The platform console has its OWN auth context (a `scope=platform` JWT from
 * `/api/platform/auth/login`), independent of the tenant RBAC session. When no
 * platform session is present we render the platform login screen IN PLACE —
 * no hard redirect — so signing in (or a 401-driven session clear) simply
 * re-renders this component without a full-page reload.
 *
 * Sec-D2: a session that has NOT completed a second factor is gated the same way, one step
 * further in. The server's `PlatformPolicies.PlatformScope` now requires `amr=mfa`, so such a
 * session can reach exactly two things — its own MFA status and enrollment, and logout — and
 * every console screen behind this guard would answer 403. Rendering the enrollment step in
 * place is what turns "the whole console is broken" into "finish setting up your account", and
 * it is derived from the same `amr` claim the server reads rather than from a separate flag that
 * could disagree with it.
 */
export default function PlatformGuard({ children }: { children: ReactNode }) {
  const { isPlatformAuthed, isPlatformMfaAuthenticated, platformLogout } = usePlatformAuth();

  if (!isPlatformAuthed) {
    return <PlatformLoginScreen />;
  }

  if (!isPlatformMfaAuthenticated) {
    return (
      <Box sx={{ maxWidth: 720, mx: 'auto', p: 3 }}>
        <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
          <Typography variant="h5" sx={{ fontWeight: 800, mb: 1 }}>
            Set up multi-factor authentication
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
            The platform control plane reads and administers every tenant on this deployment, so a
            password on its own does not open it. Enrol an authenticator below to continue.
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Completing enrollment signs this session out on purpose — sign in again and you will be
            asked for a code from your authenticator. Save the recovery codes shown at the end; they
            are the only way back in if you lose the device.
          </Typography>
        </Paper>

        <PlatformMfaPanel />

        <Box sx={{ mt: 2, textAlign: 'right' }}>
          <Button onClick={() => { void platformLogout(); }}>Sign out</Button>
        </Box>
      </Box>
    );
  }

  return <>{children}</>;
}
