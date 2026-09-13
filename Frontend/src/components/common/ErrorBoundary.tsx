import React from 'react';
import { Box, Typography, Button, Paper, CircularProgress } from '@mui/material';
import { ReportProblemOutlined as ErrorIcon, Refresh as RefreshIcon } from '@mui/icons-material';
import { isStaleDeploymentChunkError } from '../../utils/chunkRecovery';
import {
  markDeploymentUpdated,
  pageReloader,
  reloadForDeploymentUpdate,
  setRecoveryCardShown,
} from '../../utils/deploymentUpdate';
import { isRouteChunkLoadError } from '../../utils/lazyWithRetry';

interface ErrorBoundaryProps {
  children: React.ReactNode;
  /** The current path. When it changes after a failure, the application gets a fresh attempt. */
  resetKey?: string;
}

interface ErrorBoundaryState {
  hasError: boolean;
  error?: Error;
  reloading: boolean;
}

/**
 * The last-resort boundary around the whole application.
 *
 * Tenant screens now fail inside their own shell (`RouteErrorBoundary`), so this only sees screens
 * outside it — sign-in, the platform console, the printable invoice — and failures in the shell
 * itself. It never used to reset, so even pressing Back left the crash card up; it now clears when
 * the address changes.
 */
class ErrorBoundary extends React.Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false, reloading: false };

  static getDerivedStateFromError(error: Error): Partial<ErrorBoundaryState> {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    // Surface the error for diagnostics; a real logging sink can be wired here later.
    console.error('Unhandled UI error captured by ErrorBoundary:', error, errorInfo);

    // A user can keep an older shell open while Vercel atomically moves the production alias
    // to a new build. Opening a screen can then request a code file that belonged to the old
    // deployment. That screen is loaded fresh as part of the navigation that asked for it — never
    // over unsaved work, and once per address per recovery window so a broken build cannot loop.
    if (isStaleDeploymentChunkError(error)) {
      markDeploymentUpdated();
      if (isRouteChunkLoadError(error) && reloadForDeploymentUpdate(this.props.resetKey)) {
        this.setState({ reloading: true });
      }
    }
  }

  componentDidMount() {
    setRecoveryCardShown(this, this.state.hasError && !this.state.reloading);
  }

  componentDidUpdate(prevProps: ErrorBoundaryProps, prevState: ErrorBoundaryState) {
    if (this.state.hasError && prevState.hasError && prevProps.resetKey !== this.props.resetKey) {
      this.setState({ hasError: false, error: undefined, reloading: false });
      return;
    }
    setRecoveryCardShown(this, this.state.hasError && !this.state.reloading);
  }

  componentWillUnmount() {
    setRecoveryCardShown(this, false);
  }

  handleReload = () => {
    pageReloader.reload();
  };

  render() {
    if (this.state.reloading) {
      return (
        <Box
          role="status"
          sx={{ minHeight: '100vh', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1.5 }}
        >
          <CircularProgress size={28} aria-hidden />
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>Opening the latest version of Nexora…</Typography>
        </Box>
      );
    }

    if (this.state.hasError) {
      const stale = isStaleDeploymentChunkError(this.state.error);
      return (
        <Box
          sx={{
            minHeight: '100vh',
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            p: 3,
            bgcolor: 'background.default',
          }}
        >
          <Paper
            sx={{
              maxWidth: 480,
              width: '100%',
              p: 4,
              borderRadius: 3,
              textAlign: 'center',
              border: '1px solid',
              borderColor: 'divider',
            }}
          >
            <ErrorIcon sx={{ fontSize: 56, color: stale ? 'warning.main' : 'error.main', mb: 2 }} />
            <Typography variant="h5" sx={{ fontWeight: 800, mb: 1 }}>
              {stale ? 'Nexora was updated' : 'Something went wrong'}
            </Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mb: 3 }}>
              {stale
                ? 'A newer version was released while this tab was open. Reload to continue. Anything you have saved is safe.'
                : 'An unexpected error stopped this page from rendering. Your data is safe. Please reload to continue.'}
            </Typography>
            <Button
              variant="contained"
              startIcon={<RefreshIcon />}
              onClick={this.handleReload}
              sx={{ borderRadius: 2, fontWeight: 700 }}
            >
              Reload Page
            </Button>
          </Paper>
        </Box>
      );
    }

    return this.props.children;
  }
}

export default ErrorBoundary;
