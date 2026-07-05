import React from 'react';
import { Box, Paper, Stack, Typography, alpha, useTheme } from '@mui/material';
import { CheckCircle } from '@mui/icons-material';

export interface TimelineItem {
  title: React.ReactNode;
  description?: React.ReactNode;
  meta?: React.ReactNode;
  icon?: React.ReactNode;
  color?: string;
}

interface TimelineCardProps {
  title: React.ReactNode;
  subtitle?: React.ReactNode;
  items: TimelineItem[];
}

const TimelineCard: React.FC<TimelineCardProps> = ({ title, subtitle, items }) => {
  const theme = useTheme();

  return (
    <Paper sx={{ p: 2.5 }}>
      <Typography variant="subtitle1" sx={{ fontWeight: 850 }}>
        {title}
      </Typography>
      {subtitle ? (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25, mb: 2 }}>
          {subtitle}
        </Typography>
      ) : null}
      <Stack spacing={0}>
        {items.map((item, index) => {
          const color = item.color || theme.palette.primary.main;
          return (
            <Box key={index} sx={{ display: 'grid', gridTemplateColumns: '32px 1fr', gap: 1.5 }}>
              <Box sx={{ position: 'relative', display: 'flex', justifyContent: 'center' }}>
                <Box
                  sx={{
                    width: 28,
                    height: 28,
                    borderRadius: '50%',
                    display: 'grid',
                    placeItems: 'center',
                    color,
                    bgcolor: alpha(color, 0.12),
                    border: `1px solid ${alpha(color, 0.22)}`,
                    zIndex: 1,
                  }}
                >
                  {item.icon || <CheckCircle sx={{ fontSize: 16 }} />}
                </Box>
                {index < items.length - 1 ? (
                  <Box sx={{ position: 'absolute', top: 28, bottom: 0, width: 1, bgcolor: 'divider' }} />
                ) : null}
              </Box>
              <Box sx={{ pb: index < items.length - 1 ? 2.25 : 0 }}>
                <Typography variant="body2" sx={{ fontWeight: 800 }}>
                  {item.title}
                </Typography>
                {item.description ? (
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.25 }}>
                    {item.description}
                  </Typography>
                ) : null}
                {item.meta ? (
                  <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 0.35 }}>
                    {item.meta}
                  </Typography>
                ) : null}
              </Box>
            </Box>
          );
        })}
      </Stack>
    </Paper>
  );
};

export default TimelineCard;
