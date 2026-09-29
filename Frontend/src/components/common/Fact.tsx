import React from 'react';
import { Box, Typography } from '@mui/material';

/**
 * One label + value pair in a record fold or facts row: the same look as the quote page's
 * "Quote record" and the Decide page's facts row (small caps label, one value line).
 */
const Fact: React.FC<{ label: string; value: React.ReactNode; labelColor?: string; testId?: string }> = ({ label, value, labelColor = 'text.secondary', testId }) => (
  <Box sx={{ minWidth: 0 }} data-testid={testId}>
    <Typography variant="caption" sx={{ display: 'block', color: labelColor, letterSpacing: '.07em', textTransform: 'uppercase', fontWeight: 700, fontSize: '0.68rem', lineHeight: 1.5, mb: 0.25, whiteSpace: 'nowrap' }}>
      {label}
    </Typography>
    <Typography
      variant="body2"
      component="div"
      sx={{ fontWeight: 600, fontSize: '0.9rem', lineHeight: 1.45, minHeight: 30, display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: 0.75, fontVariantNumeric: 'tabular-nums', overflowWrap: 'anywhere', color: 'text.primary' }}
    >
      {value}
    </Typography>
  </Box>
);

export default Fact;
