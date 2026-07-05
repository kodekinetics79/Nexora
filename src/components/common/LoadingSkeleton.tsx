import React from 'react';
import { Box, Grid, Paper, Skeleton, Stack } from '@mui/material';

interface LoadingSkeletonProps {
  variant?: 'dashboard' | 'table' | 'form';
}

const LoadingSkeleton: React.FC<LoadingSkeletonProps> = ({ variant = 'table' }) => {
  if (variant === 'dashboard') {
    return (
      <Box>
        <Skeleton width={260} height={42} />
        <Skeleton width={420} height={24} sx={{ mb: 3 }} />
        <Grid container spacing={2.5}>
          {[0, 1, 2, 3].map((item) => (
            <Grid size={{ xs: 12, sm: 6, md: 3 }} key={item}>
              <Paper sx={{ p: 2.5 }}>
                <Skeleton width="45%" />
                <Skeleton width="70%" height={50} />
                <Skeleton width="55%" />
              </Paper>
            </Grid>
          ))}
        </Grid>
      </Box>
    );
  }

  if (variant === 'form') {
    return (
      <Paper sx={{ p: 3 }}>
        <Skeleton width={220} height={32} />
        <Stack spacing={2} sx={{ mt: 2 }}>
          <Skeleton height={48} />
          <Skeleton height={48} />
          <Skeleton height={100} />
        </Stack>
      </Paper>
    );
  }

  return (
    <Paper sx={{ p: 2 }}>
      <Stack spacing={1.25}>
        <Skeleton width="35%" height={32} />
        {[0, 1, 2, 3, 4, 5].map((item) => (
          <Skeleton key={item} height={48} />
        ))}
      </Stack>
    </Paper>
  );
};

export default LoadingSkeleton;
