import React from 'react';
import { Box, Button, Paper, Typography } from '@mui/material';
import { InboxOutlined } from '@mui/icons-material';

interface EmptyStateProps {
  title?: React.ReactNode;
  message?: React.ReactNode;
  icon?: React.ReactNode;
  actionLabel?: string;
  onAction?: () => void;
}

const EmptyState: React.FC<EmptyStateProps> = ({
  title = 'No records found',
  message = 'Try adjusting your filters or create a new record.',
  icon = <InboxOutlined />,
  actionLabel,
  onAction,
}) => (
  <Paper sx={{ p: { xs: 3, md: 5 }, textAlign: 'center', borderStyle: 'dashed', bgcolor: 'background.paper' }}>
    <Box
      sx={{
        width: 72,
        height: 72,
        mx: 'auto',
        borderRadius: 2,
        display: 'grid',
        placeItems: 'center',
        color: 'primary.main',
        bgcolor: 'action.hover',
        '& svg': { fontSize: 42 },
      }}
    >
      {icon}
    </Box>
    <Typography variant="h6" sx={{ mt: 1.5 }}>
      {title}
    </Typography>
    <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 460, mx: 'auto' }}>
      {message}
    </Typography>
    {actionLabel && onAction ? (
      <Button variant="contained" onClick={onAction} sx={{ mt: 2.5 }}>
        {actionLabel}
      </Button>
    ) : null}
  </Paper>
);

export default EmptyState;
