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
 *
 * An empty state that only says "nothing here" is still a dead end: the reader now knows the
 * queue is clear and has no idea what to do about it. So every caller passes `action` — the one
 * button that either creates the first row or moves the reader to the next real piece of work —
 * and `filteredAction`, which is nearly always "clear the search", because the two nothings need
 * two different buttons.
 */
export function gridEmptyOverlay(options: {
  /** Shown when the grid is empty and no filter or search is active. */
  title: string;
  /** Why the queue exists, or what will put something in it. */
  message?: string;
  icon?: ReactNode;
  /**
   * The next action for a genuinely empty queue — a `<Button>`. Without one the reader is told a
   * fact and left on it.
   */
  action?: ReactNode;
  /** True when a search or filter is narrowing the result — changes the copy, not the layout. */
  filtered?: boolean;
  /** Overrides the filtered headline; defaults to a generic "nothing matches" line. */
  filteredTitle?: string;
  filteredMessage?: string;
  /** The next action when a filter emptied the list — usually a "Clear search" button. */
  filteredAction?: ReactNode;
}) {
  const {
    title, message, icon, action, filtered = false, filteredAction,
    filteredTitle = 'Nothing matches your filter',
    filteredMessage = 'Clear the search or filter to see everything in this list.',
  } = options;

  return function NoRowsOverlay() {
    const shownAction = filtered ? (filteredAction ?? action) : action;
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
        {shownAction && <Box sx={{ mt: 0.5 }}>{shownAction}</Box>}
      </Box>
    );
  };
}

export default gridEmptyOverlay;
