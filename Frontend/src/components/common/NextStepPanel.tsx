import React from 'react';
import { Alert, AlertTitle, Box, Stack, Typography } from '@mui/material';
import { alpha } from '@mui/material/styles';

/**
 * The one panel every journey screen shows near the top: what state the record is in and what
 * the rep does next, in one sentence, with the control that does it beside the sentence.
 *
 * Derived from facts the page already holds; it never fetches and never decides anything. The
 * screen drives, so the rep does not have to work out the next move from a set of tiles.
 */
export interface NextStepPanelProps {
  /** Blockers → 'error' or 'warning'; a clear road → 'info'; nothing left to do → 'success'. */
  tone: 'info' | 'warning' | 'error' | 'success';
  title: string;
  /** The sentence. Plain words, present tense, names the thing to do. */
  sentence: React.ReactNode;
  /** The control that does it, rendered beside the sentence. */
  action?: React.ReactNode;
  /** Detail under the sentence: the list of blockers, each with its own link. */
  children?: React.ReactNode;
  testId?: string;
}

const NextStepPanel: React.FC<NextStepPanelProps> = ({ tone, title, sentence, action, children, testId }) => (
  <Alert
    severity={tone}
    role="status"
    data-testid={testId}
    sx={{
      mb: 2,
      px: 2,
      py: 1.5,
      borderRadius: 3,
      border: '1px solid',
      borderColor: (t) => alpha(t.palette[tone].main, t.palette.mode === 'dark' ? 0.4 : 0.3),
      borderLeft: '4px solid',
      borderLeftColor: `${tone}.main`,
      alignItems: 'flex-start',
      '& .MuiAlert-message': { width: '100%', minWidth: 0 },
      '& .MuiAlert-icon': { mt: 0.5 },
    }}
  >
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={{ xs: 1.5, md: 3 }} sx={{ justifyContent: 'space-between', alignItems: { md: 'center' } }}>
      <Box sx={{ minWidth: 0 }}>
        <AlertTitle sx={{ fontWeight: 700, mb: 0.25 }}>{title}</AlertTitle>
        <Typography variant="body1" sx={{ fontWeight: 500, maxWidth: '72ch' }}>{sentence}</Typography>
      </Box>
      {action && <Box sx={{ flexShrink: 0 }}>{action}</Box>}
    </Stack>
    {children && <Box sx={{ mt: 1.5, pt: 1.5, borderTop: '1px solid', borderColor: 'divider' }}>{children}</Box>}
  </Alert>
);

export default NextStepPanel;
