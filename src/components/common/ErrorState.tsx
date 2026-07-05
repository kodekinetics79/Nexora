import React from 'react';
import { Alert, AlertTitle, Button, Stack } from '@mui/material';

interface ErrorStateProps {
  title?: string;
  message?: React.ReactNode;
  onRetry?: () => void;
}

const ErrorState: React.FC<ErrorStateProps> = ({ title = 'Something went wrong', message, onRetry }) => (
  <Alert
    severity="error"
    variant="outlined"
    action={
      onRetry ? (
        <Button color="error" size="small" onClick={onRetry}>
          Retry
        </Button>
      ) : undefined
    }
    sx={{ borderRadius: 2 }}
  >
    <Stack spacing={0.5}>
      <AlertTitle>{title}</AlertTitle>
      {message || 'The request could not be completed.'}
    </Stack>
  </Alert>
);

export default ErrorState;
