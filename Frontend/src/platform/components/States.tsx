import type { ReactNode } from 'react';
import { Box, Button, CircularProgress, Skeleton, Typography } from '@mui/material';
import {
  ErrorOutlined as ErrorIcon,
  Inbox as InboxIcon,
  Refresh as RefreshIcon,
} from '@mui/icons-material';

export function LoadingState({ label = 'Loading…', minHeight = 320 }: { label?: string; minHeight?: number | string }) {
  return (
    <Box
      role="status"
      aria-live="polite"
      sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2, minHeight }}
    >
      <CircularProgress size={40} thickness={4} />
      <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
        {label}
      </Typography>
    </Box>
  );
}

export function ErrorState({
  message = 'Something went wrong while loading this view.',
  onRetry,
  minHeight = 320,
}: {
  message?: string;
  onRetry?: () => void;
  minHeight?: number | string;
}) {
  return (
    <Box
      role="alert"
      sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2, minHeight, textAlign: 'center', p: 3 }}
    >
      <ErrorIcon sx={{ fontSize: 48, color: 'error.main', opacity: 0.85 }} />
      <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
        Unable to load data
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 420 }}>
        {message}
      </Typography>
      {onRetry && (
        <Button variant="contained" startIcon={<RefreshIcon />} onClick={onRetry} sx={{ fontWeight: 700 }}>
          Retry
        </Button>
      )}
    </Box>
  );
}

export function EmptyState({
  title = 'Nothing here yet',
  message,
  icon,
  action,
  minHeight = 260,
}: {
  title?: string;
  message?: string;
  icon?: ReactNode;
  action?: ReactNode;
  minHeight?: number | string;
}) {
  return (
    <Box
      sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1.5, minHeight, textAlign: 'center', p: 3 }}
    >
      <Box sx={{ color: 'text.secondary', opacity: 0.6, mb: 0.5 }}>{icon ?? <InboxIcon sx={{ fontSize: 44 }} />}</Box>
      <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>
        {title}
      </Typography>
      {message && (
        <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 400 }}>
          {message}
        </Typography>
      )}
      {action && <Box sx={{ mt: 1 }}>{action}</Box>}
    </Box>
  );
}

export function TilesSkeleton({ count = 4, height = 120 }: { count?: number; height?: number }) {
  return (
    <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr', lg: `repeat(${count}, 1fr)` }, gap: 2.5 }}>
      {Array.from({ length: count }).map((_, i) => (
        <Skeleton key={i} variant="rounded" height={height} sx={{ borderRadius: 3 }} />
      ))}
    </Box>
  );
}
