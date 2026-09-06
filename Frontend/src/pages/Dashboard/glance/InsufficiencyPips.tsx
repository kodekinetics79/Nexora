import { Box, Stack, Typography } from '@mui/material';
import { seriesVar } from './tokens';

/**
 * A figure that is not published yet, and exactly how far off it is.
 *
 * This is the screen's honesty made visible. A win rate on three decided quotes is noise, so the
 * server withholds it below its minimum sample — and the old habit of drawing a grey dash in that
 * slot reads as zero, or as a bug, and tells the reader nothing about what would make the figure
 * appear. Five pips do: the reader sees the threshold, sees how much of it they have, and reads one
 * sentence saying what publishes it. Progress, not absence.
 *
 * Used anywhere a sample threshold bites, so the pips and the sentence are the same object every
 * time rather than a per-band improvisation.
 */
export interface InsufficiencyPipsProps {
  /** How many the reader has, from the server. */
  have: number;
  /** The server's minimum sample — never hardcode 5 at the call site; performance sends it. */
  need: number;
  /** The subject of the sentence, e.g. "A win rate". */
  label: string;
  /** What is being counted, e.g. "quotes have been decided". */
  unitPhrase?: string;
}

export default function InsufficiencyPips({ have, need, label, unitPhrase = 'quotes have been decided' }: InsufficiencyPipsProps) {
  const total = Math.max(1, Math.round(need));
  const counted = Math.max(0, Math.round(have));
  const filled = Math.min(counted, total);
  const sentence = `${label} is published once ${total} ${unitPhrase}. You have ${counted}.`;

  return (
    <Stack spacing={1} sx={{ minWidth: 0 }}>
      <Stack
        direction="row"
        spacing={0.75}
        role="img"
        aria-label={`${counted} of ${total}`}
        sx={{ alignItems: 'center' }}
      >
        {Array.from({ length: total }, (_, i) => (
          <Box
            key={i}
            data-testid={i < filled ? 'pip-filled' : 'pip-empty'}
            sx={{
              width: 10,
              height: 10,
              borderRadius: '50%',
              boxSizing: 'border-box',
              // Filled pips are the brass mark — the thing the reader is accumulating. Empty pips
              // are the same circle in outline, so the gap between having and needing is the only
              // difference the eye has to resolve.
              backgroundColor: i < filled ? seriesVar('brassMark') : 'transparent',
              border: '1px solid',
              borderColor: seriesVar('brassMark'),
              opacity: i < filled ? 1 : 0.45,
            }}
          />
        ))}
      </Stack>
      <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
        {sentence}
      </Typography>
    </Stack>
  );
}
