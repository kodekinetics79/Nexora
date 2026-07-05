import React from 'react';
import { Chip, alpha, useTheme } from '@mui/material';

type StatusTone = 'success' | 'warning' | 'error' | 'info' | 'neutral' | 'primary';

const inferTone = (label: string): StatusTone => {
  const value = label.toLowerCase();
  if (['active', 'accepted', 'approved', 'completed', 'sent', 'success', 'ok', 'won'].some((x) => value.includes(x))) return 'success';
  if (['draft', 'pending', 'progress', 'open', 'new'].some((x) => value.includes(x))) return 'warning';
  if (['reject', 'cancel', 'failed', 'inactive', 'lost', 'delete'].some((x) => value.includes(x))) return 'error';
  if (['email', 'manual', 'rfq', 'quote'].some((x) => value.includes(x))) return 'info';
  return 'neutral';
};

interface StatusBadgeProps {
  label: React.ReactNode;
  tone?: StatusTone;
  size?: 'small' | 'medium';
}

const StatusBadge: React.FC<StatusBadgeProps> = ({ label, tone, size = 'small' }) => {
  const theme = useTheme();
  const resolvedTone = tone || inferTone(String(label || ''));
  const color = resolvedTone === 'neutral' ? theme.palette.text.secondary : theme.palette[resolvedTone].main;

  return (
    <Chip
      label={label}
      size={size}
      sx={{
        bgcolor: alpha(color, 0.12),
        color,
        border: `1px solid ${alpha(color, 0.22)}`,
        fontWeight: 850,
        letterSpacing: 0,
      }}
    />
  );
};

export default StatusBadge;
