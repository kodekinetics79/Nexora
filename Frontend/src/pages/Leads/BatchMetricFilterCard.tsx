import type { ReactNode } from 'react';
import { ButtonBase, Stack, Typography } from '@mui/material';

interface BatchMetricFilterCardProps {
  label: string;
  value: number;
  icon: ReactNode;
  selected: boolean;
  onSelect: () => void;
}

/**
 * A reconciliation metric is also a filter. Keeping the entire card as a native
 * button preserves its large target while giving Enter/Space activation for free.
 */
export const BatchMetricFilterCard = ({
  label, value, icon, selected, onSelect,
}: BatchMetricFilterCardProps) => (
  <ButtonBase
    type="button"
    onClick={onSelect}
    aria-label={`Filter batch by ${label} (${value})`}
    aria-pressed={selected}
    sx={{
      p: 2,
      borderRadius: 2,
      minHeight: 104,
      width: '100%',
      display: 'block',
      boxSizing: 'border-box',
      textAlign: 'left',
      cursor: 'pointer',
      color: 'text.primary',
      font: 'inherit',
      border: '1px solid',
      borderColor: 'divider',
      bgcolor: selected ? 'action.selected' : 'background.paper',
      '&:focus-visible': {
        outline: '3px solid',
        outlineColor: 'primary.main',
        outlineOffset: 2,
      },
    }}
  >
    <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
      <Typography variant="h5" sx={{ fontWeight: 900 }}>{value}</Typography>
      {icon}
    </Stack>
    <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>{label}</Typography>
  </ButtonBase>
);
