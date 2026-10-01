import React from 'react';
import { Box, Chip, Tooltip, Typography, type ChipProps } from '@mui/material';
import type { QuoteDTO } from '../../../api/services/quoteService';
import { quoteStatusWords, type QuoteTone } from './quoteState';

const CHIP: Record<QuoteTone, Pick<ChipProps, 'color' | 'variant'>> = {
  neutral: { color: 'default', variant: 'outlined' },
  info: { color: 'info', variant: 'outlined' },
  success: { color: 'success', variant: 'filled' },
  warning: { color: 'warning', variant: 'filled' },
  error: { color: 'error', variant: 'filled' },
};

/**
 * A quote's status the way the Leads list shows a lead's: one chip, one short line under it.
 * The same component on the Quotes list and the quote page, so a quote never has two names
 * (the quote page used to show "Accepted" and "Won" side by side).
 */
const QuoteStatusChip: React.FC<{ quote: QuoteDTO; hideDetail?: boolean }> = ({ quote, hideDetail }) => {
  const words = quoteStatusWords(quote);
  const chip = (
    <Chip
      label={words.label}
      size="small"
      {...CHIP[words.tone]}
      sx={{ fontWeight: 700, fontSize: '0.7rem', maxWidth: '100%' }}
    />
  );
  return (
    <Box sx={{ lineHeight: 1.3, py: 0.25, minWidth: 0 }}>
      {words.hint ? <Tooltip title={words.hint}>{chip}</Tooltip> : chip}
      {!hideDetail && words.detail && (
        <Typography variant="caption" color="text.secondary" noWrap component="div" title={words.detail}>
          {words.detail}
        </Typography>
      )}
    </Box>
  );
};

export default QuoteStatusChip;
