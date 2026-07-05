import React from 'react';
import { Box } from '@mui/material';
import LoadingSkeleton from './LoadingSkeleton';

interface LoadingPageProps {
  variant?: 'dashboard' | 'table' | 'form';
}

const LoadingPage: React.FC<LoadingPageProps> = ({ variant = 'table' }) => (
  <Box sx={{ p: { xs: 1, md: 2 } }}>
    <LoadingSkeleton variant={variant} />
  </Box>
);

export default LoadingPage;
