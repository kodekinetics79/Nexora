import { Box, Typography } from '@mui/material';
import type { ReactNode } from 'react';
import { Inbox as InboxIcon } from '@mui/icons-material';

/**
 * The "no rows" panel a DataGrid shows when it has nothing to draw.
 *
 * Twenty-two grids across the product fell through to MUI's bare "No rows", which reads the
 * same whether the queue is genuinely clear, the filter excluded everything, or the request
 * failed and the page substituted an empty array. Those are three different situations and a
 * salesperson acts differently on each. Error handling belongs to the page (see
 * `ApiErrorNotice`); this covers the two the grid itself can distinguish.
 *
 * Modelled on the one grid that already did this properly —
 * `pages/ExtractionReview/ExtractionReviewPage.tsx` — and on `platform/components/States.tsx`.
 */
export function gridEmptyOverlay(options: {
  /** Shown when the grid is empty and no filter or search is active. */
  title: string;
  /** Why the queue exists, or what will put something in it. */
  message?: string;
  icon?: ReactNode;
  /** True when a search or filter is narrowing the result — changes the copy, not the layout. */
  filtered?: boolean;
  /** Overrides the filtered headline; defaults to a generic "nothing matches" line. */
  filteredTitle?: string;
  filteredMessage?: string;
}) {
  const {
    title, message, icon, filtered = false,
    filteredTitle = 'Nothing matches your filter',
    filteredMessage = 'Clear the search or filter to see everything in this list.',
  } = options;

  return function NoRowsOverlay() {
    return (
      <Box
        sx={{
          height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center',
          justifyContent: 'center', gap: 1.25, p: 3, textAlign: 'center',
        }}
      >
        <Box sx={{ color: 'text.secondary', opacity: 0.6, display: 'flex' }}>
          {icon ?? <InboxIcon sx={{ fontSize: 48 }} />}
        </Box>
        <Typography sx={{ fontWeight: 800 }}>{filtered ? filteredTitle : title}</Typography>
        {(filtered ? filteredMessage : message) && (
          <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 420 }}>
            {filtered ? filteredMessage : message}
          </Typography>
        )}
      </Box>
    );
  };
}

export default gridEmptyOverlay;
