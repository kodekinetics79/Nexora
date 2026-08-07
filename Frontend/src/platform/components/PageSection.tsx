import type { ReactNode } from 'react';
import { Box, Paper, Typography } from '@mui/material';
import type { SxProps, Theme } from '@mui/material';
import Stack from './Flex';

/**
 * A titled panel. Every operations surface is a stack of these, so the heading level,
 * the padding and the radius are decided once rather than per page — which is what stops
 * a new tab from looking like a different product.
 */
export default function PageSection({
  title,
  subtitle,
  actions,
  children,
  sx,
}: {
  title: string;
  subtitle?: string;
  actions?: ReactNode;
  children: ReactNode;
  sx?: SxProps<Theme>;
}) {
  return (
    <Paper sx={{ p: { xs: 2, md: 3 }, borderRadius: 3, ...((sx as object) ?? {}) }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        alignItems={{ sm: 'flex-start' }}
        justifyContent="space-between"
        spacing={1.5}
        sx={{ mb: subtitle ? 2 : 1.5 }}
      >
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>
            {title}
          </Typography>
          {subtitle && (
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25 }}>
              {subtitle}
            </Typography>
          )}
        </Box>
        {actions && <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap' }}>{actions}</Box>}
      </Stack>
      {children}
    </Paper>
  );
}
