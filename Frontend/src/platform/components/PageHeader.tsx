import type { ReactNode } from 'react';
import { Box, Typography } from '@mui/material';

export default function PageHeader({
  title,
  subtitle,
  actions,
}: {
  title: string;
  subtitle?: string;
  actions?: ReactNode;
}) {
  return (
    <Box
      sx={{
        display: 'flex',
        flexWrap: 'wrap',
        gap: 2,
        alignItems: 'flex-end',
        justifyContent: 'space-between',
        mb: 3,
      }}
    >
      <Box>
        {/*
          Cambay at 700, which is the display voice DESIGN.md specifies and the tenant side
          already uses. This was Source Sans 3 at 800 — a black weight standing in for a
          typeface, which is what made every console screen read as MUI defaults with brand
          colours applied rather than as the same product as the customer-facing app.
        */}
        <Typography
          variant="h4"
          component="h1"
          sx={{
            fontFamily: '"Cambay", "Source Sans 3", sans-serif',
            fontWeight: 700,
            fontSize: 30,
            lineHeight: 1.15,
            letterSpacing: '-0.02em',
          }}
        >
          {title}
        </Typography>
        {subtitle && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, fontWeight: 500 }}>
            {subtitle}
          </Typography>
        )}
      </Box>
      {actions && <Box sx={{ display: 'flex', gap: 1.5, flexWrap: 'wrap' }}>{actions}</Box>}
    </Box>
  );
}
