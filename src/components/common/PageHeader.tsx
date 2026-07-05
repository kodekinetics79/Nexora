import React from 'react';
import { Box, Breadcrumbs, Stack, Typography, type SxProps, type Theme } from '@mui/material';

interface PageHeaderProps {
  title: React.ReactNode;
  subtitle?: React.ReactNode;
  eyebrow?: React.ReactNode;
  breadcrumbs?: React.ReactNode[];
  actions?: React.ReactNode;
  sx?: SxProps<Theme>;
}

const PageHeader: React.FC<PageHeaderProps> = ({ title, subtitle, eyebrow, breadcrumbs, actions, sx }) => {
  return (
    <Box
      sx={{
        display: 'flex',
        flexDirection: { xs: 'column', md: 'row' },
        justifyContent: 'space-between',
        alignItems: { xs: 'stretch', md: 'flex-end' },
        gap: 2,
        mb: 3,
        ...sx,
      }}
    >
      <Box sx={{ minWidth: 0 }}>
        {breadcrumbs?.length ? (
          <Breadcrumbs sx={{ mb: 1, '& .MuiTypography-root': { fontSize: 12, fontWeight: 700 } }}>
            {breadcrumbs.map((item, index) => (
              <Typography key={index} color={index === breadcrumbs.length - 1 ? 'text.primary' : 'text.secondary'}>
                {item}
              </Typography>
            ))}
          </Breadcrumbs>
        ) : null}
        {eyebrow ? (
          <Typography variant="overline" color="primary.main" sx={{ display: 'block', mb: 0.5 }}>
            {eyebrow}
          </Typography>
        ) : null}
        <Typography
          variant="h4"
          sx={{
            fontWeight: 850,
            color: 'text.primary',
            fontSize: { xs: '1.75rem', md: '2.1rem' },
            overflowWrap: 'anywhere',
          }}
        >
          {title}
        </Typography>
        {subtitle ? (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75, maxWidth: 760 }}>
            {subtitle}
          </Typography>
        ) : null}
      </Box>
      {actions ? (
        <Stack direction="row" spacing={1.25} sx={{ flexWrap: 'wrap', justifyContent: { xs: 'flex-start', md: 'flex-end' } }}>
          {actions}
        </Stack>
      ) : null}
    </Box>
  );
};

export default PageHeader;
