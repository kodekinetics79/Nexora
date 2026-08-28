import React from 'react';
import { Box, Typography, Button, Paper } from '@mui/material';
import { ReportProblemOutlined as ErrorIcon, Refresh as RefreshIcon } from '@mui/icons-material';
import { claimChunkRecovery, isStaleDeploymentChunkError } from '../../utils/chunkRecovery';

interface ErrorBoundaryProps {
  children: React.ReactNode;
}

interface ErrorBoundaryState {
  hasError: boolean;
  error?: Error;
}

class ErrorBoundary extends React.Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false };

  static getDerivedStateFromError(error: Error): ErrorBoundaryState {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    // Surface the error for diagnostics; a real logging sink can be wired here later.
    console.error('Unhandled UI error captured by ErrorBoundary:', error, errorInfo);

    // A user can keep an older shell open while Vercel atomically moves the production alias
    // to a new build. Lazy navigation can then request a hashed chunk that belonged to the old
    // deployment. Recover once per route and cooldown window; a repeated failure remains visible
    // instead of creating a reload loop.
    if (isStaleDeploymentChunkError(error)) {
      try {
        const locationKey = `${window.location.pathname}${window.location.search}`;
        if (claimChunkRecovery(window.sessionStorage, locationKey)) {
          window.location.reload();
        }
      } catch {
        // Storage can be unavailable in hardened/private contexts. The manual recovery action
        // remains available and no application data is changed.
      }
    }
  }

  handleReload = () => {
    window.location.reload();
  };

  render() {
    if (this.state.hasError) {
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
            <ErrorIcon sx={{ fontSize: 56, color: 'error.main', mb: 2 }} />
            <Typography variant="h5" sx={{ fontWeight: 800, mb: 1 }}>
              Something went wrong
            </Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary', mb: 3 }}>
              An unexpected error stopped this page from rendering. Your data is safe. Please reload
              to continue.
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
