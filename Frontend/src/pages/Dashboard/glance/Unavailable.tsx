import type { ReactNode } from 'react';
import { Box, Stack, Typography } from '@mui/material';
import { InfoOutlined as ReasonIcon } from '@mui/icons-material';

/**
 * A figure the server will not state, shown as defocus rather than as absence.
 *
 * The band keeps its frame — axis, labels, full height — so nothing reflows when the figure
 * arrives, but the frame is pushed out of focus and the server's sentence sits crisp on top of it.
 * The eye lands on the words, not on a ghost chart it might read numbers off. That is the point of
 * the treatment: a faded chart at full sharpness invites reading; a blurred one cannot be read at
 * all, and the only readable thing left is why.
 *
 * This is NOT the band's empty state ("nothing happened yet", a calm outline) and NOT the band's
 * error state (an Alert with a Retry, owned by BandShell). It is the third case: the band loaded,
 * and this particular figure has a stated reason for not existing.
 */
export interface UnavailableProps {
  /** The server's own words. Never paraphrased, never replaced with a house sentence. */
  reason: string;
  /** The band's chart frame, drawn as usual and then defocused. */
  children: ReactNode;
  /** Optional follow-on, e.g. InsufficiencyPips when the reason is a sample threshold. */
  action?: ReactNode;
}

export default function Unavailable({ reason, children, action }: UnavailableProps) {
  return (
    <Box sx={{ position: 'relative', minWidth: 0 }}>
      {/* The frame still occupies its real height, which is what keeps the band from reflowing.
          It is inert to the reader and to assistive tech: its numbers are not being stated. */}
      <Box aria-hidden sx={{ opacity: 0.4, filter: 'blur(2px)', pointerEvents: 'none', userSelect: 'none' }}>
        {children}
      </Box>
      <Stack
        direction="row"
        spacing={1}
        sx={{
          position: 'absolute',
          inset: 0,
          alignItems: 'center',
          justifyContent: 'center',
          p: 2,
          textAlign: 'left',
        }}
      >
        <ReasonIcon fontSize="small" sx={{ color: 'text.secondary', flexShrink: 0, mt: '2px' }} />
        <Stack spacing={1} sx={{ minWidth: 0, maxWidth: 420 }}>
          <Typography variant="subtitle2" sx={{ fontWeight: 800, color: 'text.primary' }}>
            Not available
          </Typography>
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.45 }}>
            {reason}
          </Typography>
          {action}
        </Stack>
      </Stack>
    </Box>
  );
}
