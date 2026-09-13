import type { ReactNode } from 'react';
import { ButtonBase, Box, Typography } from '@mui/material';

interface BatchMetricFilterCardProps {
  label: string;
  value: number | string;
  icon: ReactNode;
  selected: boolean;
  onSelect: () => void;
}

/**
 * A reconciliation count is also a filter. Eight of these used to be 104px-tall cards with a
 * centred number, which spent a whole band of the page on eight digits. They are now dense rows:
 * label left, number right, lining figures, one hairline, the brass accent only on the one that
 * is on. Still a native button, so the target, Enter/Space and the accessible name are unchanged.
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
      px: 1.5,
      py: 1,
      minHeight: 40,
      width: '100%',
      display: 'flex',
      alignItems: 'center',
      gap: 1,
      boxSizing: 'border-box',
      textAlign: 'left',
      cursor: 'pointer',
      color: 'text.primary',
      font: 'inherit',
      borderRadius: 1,
      border: '1px solid',
      borderColor: selected ? 'primary.main' : 'divider',
      bgcolor: selected ? 'action.selected' : 'transparent',
      transition: 'background-color 120ms, border-color 120ms',
      '&:hover': { bgcolor: 'action.hover' },
      '&:focus-visible': {
        outline: '2px solid',
        outlineColor: 'primary.main',
        outlineOffset: 1,
      },
    }}
  >
    <Box sx={{ display: 'flex', flexShrink: 0, '& svg': { fontSize: 16 } }}>{icon}</Box>
    <Typography
      component="span"
      sx={{ flex: 1, minWidth: 0, fontSize: '0.8rem', lineHeight: 1.3, color: 'text.secondary', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
    >
      {label}
    </Typography>
    <Typography
      component="span"
      sx={{ fontSize: '1rem', fontWeight: 700, fontVariantNumeric: 'tabular-nums', flexShrink: 0 }}
    >
      {value}
    </Typography>
  </ButtonBase>
);
