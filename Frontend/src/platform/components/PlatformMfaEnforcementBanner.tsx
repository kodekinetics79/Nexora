import { useQuery } from '@tanstack/react-query';
import { Alert, Box, Typography } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';
import { platformApi } from '../api/client';
import { platformKeys } from '../api/queryKeys';
import { fmtDateTime } from './format';

/** The exact sentence the control owes an operator. Kept as a constant because it is asserted
 *  by the certification harness and must not drift with a copy edit. */
export const MFA_DISABLED_BANNER_TEXT = 'MFA enforcement is disabled in this test environment.';

export const MFA_DISABLED_BANNER_TEST_ID = 'platform-mfa-disabled-banner';

/**
 * A persistent banner on every platform screen while MFA enforcement is disabled.
 *
 * <p><b>Why it is persistent and not a toast.</b> The failure mode this control exists for is not
 * somebody disabling MFA — that is a legitimate thing to do on a test rig. It is that nobody
 * notices it is still off. A notification that can be dismissed is a notification that is dismissed
 * on day one and absent for the rest of the window.</p>
 *
 * <p>It is driven by the SERVER's effective policy, not by a local flag: the same read the
 * authorization layer makes. A console that decided this for itself could be wrong in the one
 * direction that matters — showing "enforced" while the backend is not enforcing.</p>
 *
 * <p>Renders nothing at all when enforcement is on, which is the ordinary case and must cost
 * nothing visually.</p>
 */
export default function PlatformMfaEnforcementBanner() {
  const policyQuery = useQuery({
    queryKey: platformKeys.platformAuthPolicyEffective(),
    queryFn: () => platformApi.getEffectiveMfaPolicy(),
    // Polled rather than fetched once: a bypass expires on the clock, and the banner has to
    // disappear on its own when it does. One minute is far below any permitted bypass window.
    refetchInterval: 60_000,
    retry: false,
  });

  const policy = policyQuery.data;
  if (!policy?.enforcementDisabled) return null;

  return (
    <Box sx={{ px: { xs: 2, md: 3 }, pt: 2 }}>
      <Alert
        severity="error"
        role="alert"
        variant="filled"
        data-testid={MFA_DISABLED_BANNER_TEST_ID}
        sx={{ borderRadius: 2, fontWeight: 700 }}
      >
        {MFA_DISABLED_BANNER_TEXT}
        <Typography variant="body2" sx={{ fontWeight: 400, mt: 0.5 }}>
          {policy.environmentName} · set by {policy.changedBy ?? 'an operator'}
          {policy.expiresAtUtc ? ` · returns to REQUIRED at ${fmtDateTime(policy.expiresAtUtc)}` : ''} ·{' '}
          <Box component={RouterLink} to="/platform/security/authentication" sx={{ color: 'inherit' }}>
            Platform Authentication
          </Box>
        </Typography>
      </Alert>
    </Box>
  );
}
