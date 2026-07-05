import React from 'react';
import { Box, Paper, Stack, Typography, type SxProps, type Theme } from '@mui/material';

interface FormSectionProps {
  title: React.ReactNode;
  description?: React.ReactNode;
  actions?: React.ReactNode;
  children: React.ReactNode;
  sx?: SxProps<Theme>;
}

const FormSection: React.FC<FormSectionProps> = ({ title, description, actions, children, sx }) => (
  <Paper sx={{ p: { xs: 2, md: 3 }, ...sx }}>
    <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ justifyContent: 'space-between', gap: 2, mb: 2.5 }}>
      <Box>
        <Typography variant="h6">{title}</Typography>
        {description ? (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            {description}
          </Typography>
        ) : null}
      </Box>
      {actions}
    </Stack>
    {children}
  </Paper>
);

export default FormSection;
