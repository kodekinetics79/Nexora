import { useCallback, useEffect, useState } from 'react';
import { Box, Button, Typography } from '@mui/material';
import { Logout as ExitIcon, SupervisorAccount as ImpersonateIcon } from '@mui/icons-material';
import {
  clearImpersonation,
  getImpersonation,
  type ImpersonationRecord,
} from '../../api/impersonation';
import platformHttp from '../../platform/api/platformHttp';
import { getPlatformToken } from '../../platform/auth/platformSession';

const fmtRemaining = (expiresAt: string): string => {
  const ms = Date.parse(expiresAt) - Date.now();
  if (!Number.isFinite(ms) || ms <= 0) return 'now';
  const totalSeconds = Math.floor(ms / 1000);
  const minutes = Math.floor(totalSeconds / 60);
  const seconds = totalSeconds % 60;
  return minutes > 0 ? `${minutes}m ${seconds.toString().padStart(2, '0')}s` : `${seconds}s`;
};

/**
 * Fixed, always-visible banner shown on every tenant page while a platform
 * impersonation session is active. Exiting clears the sessionStorage record,
 * best-effort revokes the session server-side (platform token permitting), and
 * returns the operator to the platform console.
 */
export default function ImpersonationBanner() {
  const [record, setRecord] = useState<ImpersonationRecord | null>(() => getImpersonation());
  const [, setTick] = useState(0);
  const [exiting, setExiting] = useState(false);

  const exit = useCallback(async (revoke: boolean) => {
    setExiting(true);
    const jti = record?.jti;
    clearImpersonation();
    if (revoke && jti && getPlatformToken()) {
      try {
        await platformHttp.post(`/api/platform/impersonation/${jti}/revoke`);
      } catch {
        // Best effort — the token expires on its own and stays read-only.
      }
    }
    // Full navigation so the tenant app re-initialises without the
    // impersonation session.
    window.location.assign('/platform/tenants');
  }, [record]);

  // 1s tick drives the countdown; when the record expires (getImpersonation
  // clears it on read) the banner exits back to the platform console.
  useEffect(() => {
    if (!record) return;
    const timer = window.setInterval(() => {
      const current = getImpersonation();
      if (!current) {
        setRecord(null);
        // Token already expired server-side; no revoke needed.
        clearImpersonation();
        window.location.assign('/platform/tenants');
        return;
      }
      setTick((t) => t + 1);
    }, 1000);
    return () => window.clearInterval(timer);
  }, [record]);

  if (!record) return null;

  return (
    <Box
      role="status"
      sx={{
        position: 'fixed',
        left: 0,
        right: 0,
        bottom: 0,
        zIndex: (t) => t.zIndex.modal + 1,
        display: 'flex',
        alignItems: 'center',
        flexWrap: 'wrap',
        gap: 1.5,
        px: 2,
        py: 1,
        bgcolor: '#7c2d12',
        color: '#fff',
        borderTop: '2px solid #f97316',
        boxShadow: '0 -6px 18px rgba(0,0,0,0.35)',
      }}
    >
      <ImpersonateIcon sx={{ color: '#fdba74' }} />
      <Typography variant="body2" sx={{ fontWeight: 700 }}>
        Impersonating {record.tenantName} — read-only — expires in {fmtRemaining(record.expiresAt)}
      </Typography>
      <Typography variant="caption" sx={{ color: '#fed7aa', flex: 1, minWidth: 160 }}>
        Reason: {record.reason}
      </Typography>
      <Button
        size="small"
        variant="contained"
        color="warning"
        startIcon={<ExitIcon fontSize="small" />}
        onClick={() => void exit(true)}
        disabled={exiting}
        sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}
      >
        {exiting ? 'Exiting…' : 'Exit impersonation'}
      </Button>
    </Box>
  );
}
