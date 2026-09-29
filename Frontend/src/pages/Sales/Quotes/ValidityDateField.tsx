import React from 'react';
import { Box, Chip, Stack, TextField } from '@mui/material';
import dayjs from 'dayjs';

/** The validity periods a rep picks with one click (owner 2026-09-28). The date field stays for anything else. */
export const VALIDITY_PRESET_DAYS = [30, 60, 90] as const;

export interface ValidityDateFieldProps {
  /** YYYY-MM-DD, or '' when not set. */
  value: string;
  onChange: (value: string) => void;
  /** The day the presets count from (YYYY-MM-DD): the quote date, the date sent, or today when omitted. */
  from?: string | null;
  label?: string;
  /** Chip text for a preset. Extend validity reads "+30 days"; everywhere else "30 days". */
  presetLabel?: (days: number) => string;
  /** Extra one-click dates shown before the presets, e.g. the buyer's own minimum. */
  extraChips?: { label: string; value: string }[];
  min?: string;
  required?: boolean;
  error?: boolean;
  helperText?: React.ReactNode;
  size?: 'small' | 'medium';
  fullWidth?: boolean;
  sx?: object;
}

/** A validity date: 30 / 60 / 90 days in one click, or any date typed or picked by hand. */
export default function ValidityDateField({
  value, onChange, from, label = 'Valid until', presetLabel = (days) => `${days} days`, extraChips = [],
  min, required, error, helperText, size, fullWidth, sx,
}: ValidityDateFieldProps) {
  const base = from && dayjs(from).isValid() ? dayjs(from) : dayjs();
  const presets = VALIDITY_PRESET_DAYS.map((days) => ({ label: presetLabel(days), value: base.add(days, 'day').format('YYYY-MM-DD') }));
  const chips = [...extraChips, ...presets];

  return (
    <Box sx={sx}>
      <TextField
        type="date"
        label={label}
        value={value}
        required={required}
        fullWidth={fullWidth}
        size={size}
        error={error}
        helperText={helperText}
        onChange={(event) => onChange(event.target.value)}
        slotProps={{ inputLabel: { shrink: true }, htmlInput: { min } }}
      />
      <Stack direction="row" spacing={0.5} useFlexGap sx={{ mt: 0.75, flexWrap: 'wrap' }}>
        {chips.map((chip) => {
          const picked = value === chip.value;
          return (
            <Chip
              key={`${chip.label}-${chip.value}`}
              size="small"
              label={chip.label}
              variant={picked ? 'filled' : 'outlined'}
              color={picked ? 'primary' : 'default'}
              disabled={Boolean(min && chip.value < min)}
              onClick={() => onChange(chip.value)}
            />
          );
        })}
      </Stack>
    </Box>
  );
}
