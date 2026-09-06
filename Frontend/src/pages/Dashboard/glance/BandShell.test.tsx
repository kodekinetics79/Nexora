import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import BandShell from './BandShell';
import type { BandSeal } from './BandShell';

const seal = (over: Partial<BandSeal> = {}): BandSeal => ({
  scope: 'Company-wide',
  window: '1 Jan – 30 Jan',
  generatedAt: '2026-01-30T14:32:00Z',
  governed: true,
  ...over,
});

describe('BandShell seal', () => {
  it('states scope, window and freshness in one line', () => {
    render(<BandShell title="Did we win" seal={seal()}><p>content</p></BandShell>);

    expect(screen.getByTestId('band-seal')).toHaveTextContent(/^Company-wide · 1 Jan – 30 Jan · as of \d{2}:\d{2}$/);
  });

  // Filled vs outlined is the whole reason the screen has no global date picker, so it has to be
  // an attribute a reader (and a test) can tell apart, not just a shade.
  it('is filled when the period control governs the band and outlined when it does not', () => {
    const { rerender } = render(<BandShell title="Did we win" seal={seal({ governed: true })}><p>content</p></BandShell>);
    const governed = screen.getByTestId('band-seal');
    expect(governed).toHaveAttribute('data-governed', 'true');
    expect(governed).toHaveAccessibleName(/follows the period you choose above/i);
    expect(getComputedStyle(governed).backgroundColor).toBe('var(--nx-glance-seal-ground)');

    rerender(<BandShell title="What is closing on us" seal={seal({ governed: false, window: 'Next 14 days' })}><p>content</p></BandShell>);
    const fixed = screen.getByTestId('band-seal');
    expect(fixed).toHaveAttribute('data-governed', 'false');
    expect(fixed).toHaveAccessibleName(/own fixed window/i);
    expect(getComputedStyle(fixed).backgroundColor).toBe('rgba(0, 0, 0, 0)');
  });

  it('says the scope could not be resolved rather than printing a wire word', () => {
    render(<BandShell title="Did we win" seal={seal({ scope: null })}><p>content</p></BandShell>);

    expect(screen.getByTestId('band-seal')).toHaveTextContent(/^Scope not stated ·/);
  });

  it('never guesses a clock when the server states no generatedAt', () => {
    render(<BandShell title="Did we win" seal={seal({ generatedAt: null })}><p>content</p></BandShell>);

    expect(screen.getByTestId('band-seal')).toHaveTextContent('freshness not stated');
  });
});

describe('BandShell states', () => {
  it('renders the band content when the server answered', () => {
    render(<BandShell title="Did we win" seal={seal()}><p>18 of 24 decided</p></BandShell>);

    expect(screen.getByText('18 of 24 decided')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows the server reason and a Retry when the band failed', () => {
    const onRetry = vi.fn();
    render(
      <BandShell title="Did we win" seal={seal()} error="Performance aggregate timed out." onRetry={onRetry}>
        <p>content</p>
      </BandShell>,
    );

    const alert = screen.getByRole('alert');
    expect(alert).toHaveTextContent('Performance aggregate timed out.');
    // The band's own content must not be drawn behind a failure — there is nothing to draw.
    expect(screen.queryByText('content')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('renders the server sentence, and no alert or retry, when the reader may not see the band', () => {
    render(
      <BandShell
        title="Why we lost"
        seal={seal()}
        forbidden="Loss reasons are visible to managers of the account team."
      >
        <p>content</p>
      </BandShell>,
    );

    expect(screen.getByText('Loss reasons are visible to managers of the account team.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument();
    expect(screen.queryByText('content')).not.toBeInTheDocument();
  });

  // Forbidden outranks error: when the server has told us these numbers are not this reader's,
  // offering a Retry invites them to keep knocking on a door that is not going to open.
  it('prefers the forbidden sentence over an error', () => {
    render(
      <BandShell title="Why we lost" seal={seal()} forbidden="Not yours to see." error="Boom" onRetry={vi.fn()}>
        <p>content</p>
      </BandShell>,
    );

    expect(screen.getByText('Not yours to see.')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('keeps the seal and the title while loading', () => {
    render(<BandShell title="Did we win" seal={seal()} loading><p>content</p></BandShell>);

    expect(screen.getByRole('heading', { level: 2, name: 'Did we win' })).toBeInTheDocument();
    expect(screen.getByTestId('band-seal')).toBeInTheDocument();
    expect(screen.getByRole('status')).toHaveTextContent(/loading did we win/i);
  });
});
