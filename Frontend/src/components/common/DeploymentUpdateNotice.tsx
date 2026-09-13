import { useEffect, useRef, useSyncExternalStore } from 'react';
import { useLocation } from 'react-router-dom';
import { Button, Paper, Stack, Typography } from '@mui/material';
import {
  isDeploymentUpdated,
  pageReloader,
  reloadForDeploymentUpdate,
  shouldShowDeploymentNotice,
  subscribeDeploymentUpdate,
} from '../../utils/deploymentUpdate';

export const DEPLOYMENT_NOTICE_TEXT = 'Nexora was updated. Reload when you are ready.';

/**
 * The quiet half of stale-deployment recovery: a small bar that offers the reload and never takes it.
 *
 * While it is showing, the next time the person moves to a different screen that screen is loaded
 * fresh — the moment they are already leaving what they were looking at. A form holding unsaved
 * work blocks that, and the loop guard in `reloadForDeploymentUpdate` stops a repeat on the same
 * address. Query-string changes (tabs, filters) are not navigations here, so switching a tab
 * never reloads.
 */
export default function DeploymentUpdateNotice() {
  const { pathname } = useLocation();
  const updated = useSyncExternalStore(subscribeDeploymentUpdate, isDeploymentUpdated, () => false);
  const visible = useSyncExternalStore(subscribeDeploymentUpdate, shouldShowDeploymentNotice, () => false);
  const pathWhenFlagged = useRef<string | null>(null);

  useEffect(() => {
    if (!updated) {
      pathWhenFlagged.current = null;
      return;
    }
    if (pathWhenFlagged.current === null) {
      pathWhenFlagged.current = pathname;
      return;
    }
    if (pathWhenFlagged.current === pathname) return;
    // Remember this address either way: if the reload is refused (unsaved work), the NEXT
    // navigation gets its own chance rather than retrying against the same screen.
    pathWhenFlagged.current = pathname;
    reloadForDeploymentUpdate(pathname);
  }, [updated, pathname]);

  if (!visible) return null;

  return (
    <Paper
      role="status"
      aria-live="polite"
      elevation={6}
      sx={{
        position: 'fixed',
        left: '50%',
        transform: 'translateX(-50%)',
        bottom: { xs: 12, md: 20 },
        zIndex: (theme) => theme.zIndex.snackbar,
        px: 2,
        py: 1,
        borderRadius: 2,
        maxWidth: 'calc(100vw - 24px)',
      }}
    >
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
        <Typography variant="body2" sx={{ fontWeight: 600 }}>{DEPLOYMENT_NOTICE_TEXT}</Typography>
        <Button size="small" variant="outlined" onClick={() => pageReloader.reload()}>
          Reload
        </Button>
      </Stack>
    </Paper>
  );
}
