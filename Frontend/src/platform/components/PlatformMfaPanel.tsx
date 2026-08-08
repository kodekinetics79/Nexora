import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Checkbox,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  Paper,
  TextField,
  Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from './Flex';
import { ErrorState, LoadingState } from './States';
import { fmtDateTime } from './format';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import {
  beginPlatformMfaEnrollment,
  confirmPlatformMfaEnrollment,
  getPlatformMfaStatus,
  type PlatformMfaEnrollment,
  type PlatformMfaEnrollmentResult,
} from '../auth/platformMfa';
import { usePlatformAuth } from '../auth/usePlatformAuth';

export default function PlatformMfaPanel() {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { platformLogout } = usePlatformAuth();
  const [enrollment, setEnrollment] = useState<PlatformMfaEnrollment | null>(null);
  const [totpCode, setTotpCode] = useState('');
  const [recoveryResult, setRecoveryResult] = useState<PlatformMfaEnrollmentResult | null>(null);
  const [codesSaved, setCodesSaved] = useState(false);

  const statusQuery = useQuery({
    queryKey: platformKeys.platformMfa(),
    queryFn: getPlatformMfaStatus,
  });
  const beginMutation = useMutation({
    mutationFn: beginPlatformMfaEnrollment,
    onSuccess: setEnrollment,
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'MFA enrollment could not be started.'), { variant: 'error' }),
  });
  const confirmMutation = useMutation({
    mutationFn: () => confirmPlatformMfaEnrollment(totpCode),
    onSuccess: (result) => {
      setRecoveryResult(result);
      setEnrollment(null);
      setTotpCode('');
      queryClient.setQueryData(platformKeys.platformMfa(), {
        enabled: true,
        enabledAtUtc: result.enabledAtUtc,
        recoveryCodesRemaining: result.recoveryCodes.length,
      });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'The authenticator code was refused.'), { variant: 'error' }),
  });

  return (
    <Paper sx={{ p: 3, borderRadius: 3, mb: 2.5 }}>
      <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>Multi-factor authentication</Typography>
      <Typography variant="caption" color="text.secondary">
        TOTP protects privileged actions. Recovery codes are single-use and are shown only when enrollment completes.
      </Typography>
      {statusQuery.isLoading ? (
        <LoadingState label="Reading MFA status…" minHeight={120} />
      ) : statusQuery.isError || !statusQuery.data ? (
        <ErrorState
          message={platformErrorMessage(statusQuery.error, 'MFA status could not be read.')}
          onRetry={() => statusQuery.refetch()}
          minHeight={120}
        />
      ) : statusQuery.data.enabled ? (
        <Alert severity={statusQuery.data.recoveryCodesRemaining <= 2 ? 'warning' : 'success'} sx={{ mt: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>MFA enabled</AlertTitle>
          Enabled {statusQuery.data.enabledAtUtc ? fmtDateTime(statusQuery.data.enabledAtUtc) : 'on this account'} ·{' '}
          {statusQuery.data.recoveryCodesRemaining} recovery codes remaining.
        </Alert>
      ) : (
        <Stack spacing={2} alignItems="flex-start" sx={{ mt: 2 }}>
          <Alert severity="warning">
            <AlertTitle sx={{ fontWeight: 800 }}>Enrollment required for MFA-protected actions</AlertTitle>
            You can use the console now, but actions requiring MFA will be refused until an authenticator is enrolled.
          </Alert>
          <Button variant="contained" disabled={beginMutation.isPending} onClick={() => beginMutation.mutate()}>
            {beginMutation.isPending ? 'Starting…' : 'Set up authenticator'}
          </Button>
        </Stack>
      )}

      <Dialog open={Boolean(enrollment)} onClose={() => setEnrollment(null)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Set up authenticator</DialogTitle>
        <DialogContent dividers>
          {enrollment && (
            <Stack spacing={2}>
              <Alert severity="info">
                Add this account to your authenticator using the setup key or URI, then enter its current 6-digit code.
              </Alert>
              <Box>
                <Typography variant="caption" color="text.secondary">Setup key</Typography>
                <Box component="code" sx={{ display: 'block', mt: 0.5, p: 1.5, bgcolor: 'action.hover', borderRadius: 1, overflowWrap: 'anywhere', fontWeight: 800 }}>
                  {enrollment.secret}
                </Box>
              </Box>
              <Box>
                <Typography variant="caption" color="text.secondary">Authenticator URI</Typography>
                <Box component="code" sx={{ display: 'block', mt: 0.5, p: 1.5, bgcolor: 'action.hover', borderRadius: 1, overflowWrap: 'anywhere', fontSize: 12 }}>
                  {enrollment.otpAuthUri}
                </Box>
              </Box>
              <TextField
                required autoFocus label="6-digit authenticator code" value={totpCode}
                onChange={(event) => setTotpCode(event.target.value.replace(/\D/g, '').slice(0, 6))}
                autoComplete="one-time-code"
                slotProps={{ htmlInput: { inputMode: 'numeric', pattern: '[0-9]{6}', maxLength: 6 } }}
              />
            </Stack>
          )}
        </DialogContent>
        <DialogActions>
          <Button color="inherit" onClick={() => setEnrollment(null)}>Cancel</Button>
          <Button variant="contained" disabled={!/^\d{6}$/.test(totpCode) || confirmMutation.isPending} onClick={() => confirmMutation.mutate()}>
            {confirmMutation.isPending ? 'Verifying…' : 'Enable MFA'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={Boolean(recoveryResult)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Save your recovery codes</DialogTitle>
        <DialogContent dividers>
          <Alert severity="warning" sx={{ mb: 2 }}>
            These codes are shown once. Each code can be used only once. Your current session has been revoked and you must sign in again after saving them.
          </Alert>
          <Box component="pre" sx={{ p: 2, bgcolor: 'action.hover', borderRadius: 1, whiteSpace: 'pre-wrap', fontFamily: 'monospace', userSelect: 'all' }}>
            {recoveryResult?.recoveryCodes.join('\n')}
          </Box>
          <FormControlLabel
            control={<Checkbox checked={codesSaved} onChange={(event) => setCodesSaved(event.target.checked)} />}
            label="I saved these recovery codes in a secure place"
          />
        </DialogContent>
        <DialogActions>
          <Button variant="contained" disabled={!codesSaved} onClick={() => platformLogout()}>
            Sign out and continue
          </Button>
        </DialogActions>
      </Dialog>
    </Paper>
  );
}
