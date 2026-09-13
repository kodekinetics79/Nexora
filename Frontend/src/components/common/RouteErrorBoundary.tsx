import React from 'react';
import { useLocation } from 'react-router-dom';
import { Box, Button, CircularProgress, Paper, Typography } from '@mui/material';
import { ReportProblemOutlined as ErrorIcon, Refresh as RefreshIcon } from '@mui/icons-material';
import { isStaleDeploymentChunkError } from '../../utils/chunkRecovery';
import {
  markDeploymentUpdated,
  pageReloader,
  reloadForDeploymentUpdate,
  setRecoveryCardShown,
} from '../../utils/deploymentUpdate';
import { isRouteChunkLoadError } from '../../utils/lazyWithRetry';

interface RouteErrorBoundaryInnerProps {
  /** The current path. A change means the person navigated, so a failed screen gets a fresh start. */
  resetKey: string;
  children: React.ReactNode;
}

interface RouteErrorBoundaryInnerState {
  error: Error | null;
  /** A navigation-time reload has started; show a quiet status rather than a failure. */
  reloading: boolean;
}

/**
 * Catches a failure inside ONE screen and keeps the navigation shell around it.
 *
 * The only boundary used to wrap the whole application, so any crash — or a code file missing
 * after a deploy — replaced the sidebar and top bar too, and nothing but a full reload brought
 * them back. This one sits inside the shell: the rail stays usable, and moving to another address
 * clears the failure.
 *
 * A screen whose code belongs to an older deployment is loaded fresh as part of the navigation
 * the person just made, unless a form still holds unsaved work or the same address was already
 * reloaded moments ago. Otherwise the card explains and offers the reload.
 */
export class RouteErrorBoundaryInner extends React.Component<
  RouteErrorBoundaryInnerProps,
  RouteErrorBoundaryInnerState
> {
  state: RouteErrorBoundaryInnerState = { error: null, reloading: false };

  static getDerivedStateFromError(error: Error): Partial<RouteErrorBoundaryInnerState> {
    return { error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    console.error('A screen failed to render; the navigation shell stays usable:', error, errorInfo);
    if (!isStaleDeploymentChunkError(error)) return;
    markDeploymentUpdated();
    // Only a route screen throws RouteChunkLoadError, and it only loads when someone opens it —
    // so loading the address fresh IS the navigation they asked for, not a reload by itself.
    if (isRouteChunkLoadError(error) && reloadForDeploymentUpdate(this.props.resetKey)) {
      this.setState({ reloading: true });
    }
  }

  componentDidMount() {
    this.syncRecoveryCard();
  }

  componentDidUpdate(prevProps: RouteErrorBoundaryInnerProps, prevState: RouteErrorBoundaryInnerState) {
    // Reset only an error that was ALREADY on screen before the address changed. An error caught in
    // the same update as the navigation belongs to the new address and must stay caught.
    if (
      this.state.error !== null
      && prevState.error !== null
      && prevProps.resetKey !== this.props.resetKey
    ) {
      this.setState({ error: null, reloading: false });
      return;
    }
    this.syncRecoveryCard();
  }

  componentWillUnmount() {
    setRecoveryCardShown(this, false);
  }

  private syncRecoveryCard() {
    setRecoveryCardShown(this, this.state.error !== null && !this.state.reloading);
  }

  render() {
    const { error, reloading } = this.state;
    if (error === null) return this.props.children;

    if (reloading) {
      return (
        <Box
          role="status"
          sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1.5, minHeight: '50vh' }}
        >
          <CircularProgress size={28} aria-hidden />
          <Typography variant="body2" color="text.secondary">Opening the latest version of Nexora…</Typography>
        </Box>
      );
    }

    const stale = isStaleDeploymentChunkError(error);
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', px: 2, py: { xs: 4, md: 8 } }}>
        <Paper
          variant="outlined"
          sx={{ maxWidth: 520, width: '100%', p: { xs: 3, md: 4 }, borderRadius: 3, textAlign: 'center' }}
        >
          <ErrorIcon aria-hidden sx={{ fontSize: 48, color: stale ? 'warning.main' : 'error.main', mb: 1.5 }} />
          <Typography variant="h5" component="h1" sx={{ fontWeight: 800, mb: 1 }}>
            {stale ? 'Nexora was updated' : 'This screen stopped working'}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
            {stale
              ? 'A newer version was released while this tab was open, and this screen needs it. Reload to open it. Anything you have saved is safe.'
              : 'Something on this screen failed to display. Anything you have saved is safe. Reload this screen, or use the menu to go somewhere else.'}
          </Typography>
          <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => pageReloader.reload()}>
            Reload this screen
          </Button>
        </Paper>
      </Box>
    );
  }
}

export default function RouteErrorBoundary({ children }: { children: React.ReactNode }) {
  const { pathname } = useLocation();
  return <RouteErrorBoundaryInner resetKey={pathname}>{children}</RouteErrorBoundaryInner>;
}
