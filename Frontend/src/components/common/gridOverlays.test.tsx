import { render, screen } from '@testing-library/react';
import { Button } from '@mui/material';
import { describe, expect, it } from 'vitest';
import gridEmptyOverlay from './gridOverlays';

/**
 * An empty state has to do three things, and the product was doing one of them.
 *
 * Twenty-two grids shipped MUI's bare "No rows" — a string that cannot say whether the queue is
 * genuinely clear, a filter excluded everything, or the request failed and the page substituted an
 * empty array. `gridEmptyOverlay` already separated the first two. What it could not do was give
 * the reader anything to DO: a rep who learns their queue is clear and is offered no next step has
 * been informed and abandoned, which is a dead end with better typography.
 *
 * So the factory takes an action, the two nothings take DIFFERENT actions, and these tests assert
 * what a person sees rather than which prop was passed.
 */

const renderOverlay = (options: Parameters<typeof gridEmptyOverlay>[0]) => {
  const Overlay = gridEmptyOverlay(options);
  return render(<Overlay />);
};

describe('a genuinely empty queue', () => {
  it('says what it is, why it is empty, and offers the next action', () => {
    renderOverlay({
      title: 'No draft RFQs',
      message: 'A converted lead lands here as a draft until it is reviewed.',
      action: <Button>See all inquiries</Button>,
    });

    expect(screen.getByText('No draft RFQs')).toBeInTheDocument();
    expect(screen.getByText(/a converted lead lands here/i)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'See all inquiries' })).toBeInTheDocument();
  });
});

describe('a queue a filter emptied', () => {
  it('reads differently from a true zero', () => {
    renderOverlay({
      title: 'No draft RFQs',
      message: 'A converted lead lands here as a draft.',
      filtered: true,
      filteredTitle: 'No draft RFQ matches this search',
      filteredMessage: 'Clear the search to see every draft.',
      action: <Button>See all inquiries</Button>,
      filteredAction: <Button>Clear the search</Button>,
    });

    expect(screen.getByText('No draft RFQ matches this search')).toBeInTheDocument();
    // The true-zero copy must not leak through: it would tell the reader there are no drafts when
    // their own search is what hid them.
    expect(screen.queryByText('No draft RFQs')).toBeNull();
  });

  it('offers CLEARING the filter, not the create-the-first-one action', () => {
    renderOverlay({
      title: 'No draft RFQs',
      filtered: true,
      action: <Button>See all inquiries</Button>,
      filteredAction: <Button>Clear the search</Button>,
    });

    expect(screen.getByRole('button', { name: 'Clear the search' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'See all inquiries' })).toBeNull();
  });

  it('falls back to the main action when a caller gives no filtered one', () => {
    // Better one button than none — a filtered state with no way out is the dead end this exists
    // to remove.
    renderOverlay({
      title: 'No draft RFQs',
      filtered: true,
      action: <Button>See all inquiries</Button>,
    });

    expect(screen.getByRole('button', { name: 'See all inquiries' })).toBeInTheDocument();
  });
});

describe('backwards compatibility', () => {
  it('still renders for a caller that passes no action at all', () => {
    // Four call sites predate the action slot. None of them may crash.
    renderOverlay({ title: 'No outstanding RFQs' });

    expect(screen.getByText('No outstanding RFQs')).toBeInTheDocument();
    expect(screen.queryByRole('button')).toBeNull();
  });
});
