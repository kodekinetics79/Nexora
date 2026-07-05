import React from 'react';
import { Box, Paper, Stack, Typography, alpha, useTheme, type SxProps, type Theme } from '@mui/material';

interface InfoCardProps {
  title: React.ReactNode;
  subtitle?: React.ReactNode;
  icon?: React.ReactNode;
  accent?: string;
  actions?: React.ReactNode;
  children: React.ReactNode;
  sx?: SxProps<Theme>;
}

const InfoCard: React.FC<InfoCardProps> = ({ title, subtitle, icon, accent, actions, children, sx }) => {
  const theme = useTheme();
  const color = accent || theme.palette.primary.main;

  return (
    <Paper
      sx={{
        position: 'relative',
        overflow: 'hidden',
        p: { xs: 2, md: 2.5 },
        '&:before': {
          content: '""',
          position: 'absolute',
          inset: '0 0 auto 0',
          height: 3,
          background: `linear-gradient(90deg, ${color}, ${alpha(color, 0.18)})`,
        },
        ...sx,
      }}
    >
      <Stack direction="row" sx={{ alignItems: 'flex-start', justifyContent: 'space-between', gap: 2, mb: 2 }}>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', minWidth: 0 }}>
          {icon ? (
            <Box
              sx={{
                width: 38,
                height: 38,
                borderRadius: 2,
                display: 'grid',
                placeItems: 'center',
                color,
                bgcolor: alpha(color, 0.1),
                border: `1px solid ${alpha(color, 0.16)}`,
                flexShrink: 0,
              }}
            >
              {icon}
            </Box>
          ) : null}
          <Box sx={{ minWidth: 0 }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 850 }}>
              {title}
            </Typography>
            {subtitle ? (
              <Typography variant="body2" color="text.secondary">
                {subtitle}
              </Typography>
            ) : null}
          </Box>
        </Stack>
        {actions}
      </Stack>
      {children}
    </Paper>
  );
};

export default InfoCard;
