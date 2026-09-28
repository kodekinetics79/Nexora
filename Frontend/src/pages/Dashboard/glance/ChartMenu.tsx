import { useState } from 'react';
import { ButtonBase, Menu, MenuItem } from '@mui/material';
import { ArrowDropDown as DropIcon } from '@mui/icons-material';
import { neuFocus, neuKey } from './neumorphic';

/**
 * A chart's own label, doubling as the control that changes what the chart counts.
 *
 * It sits where an axis title would, so the reader changes the measure where they are already
 * looking. The accessible name says both what it shows now and that it can be changed.
 */
export default function ChartMenu<T extends string>({
  value, options, onChange, label,
}: {
  value: T;
  options: readonly { value: T; label: string }[];
  onChange: (next: T) => void;
  /** What the menu changes, e.g. "Bars show". */
  label: string;
}) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const current = options.find(o => o.value === value) ?? options[0];
  return (
    <>
      <ButtonBase
        aria-label={`${label}: ${current.label}. Change`}
        aria-haspopup="menu"
        onClick={e => setAnchor(e.currentTarget)}
        sx={theme => ({
          ...neuKey(theme.palette.mode), ...neuFocus,
          px: 1, py: 0.25, borderRadius: '10px', fontSize: 12, fontWeight: 700, color: 'text.primary',
          minHeight: 26,
        })}
      >
        {current.label}
        <DropIcon sx={{ fontSize: 18, ml: 0.25, color: 'text.secondary' }} />
      </ButtonBase>
      <Menu anchorEl={anchor} open={!!anchor} onClose={() => setAnchor(null)}>
        {options.map(o => (
          <MenuItem key={o.value} dense selected={o.value === value} onClick={() => { onChange(o.value); setAnchor(null); }}>
            {o.label}
          </MenuItem>
        ))}
      </Menu>
    </>
  );
}
