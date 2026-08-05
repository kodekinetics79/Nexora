import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import LateIngestedBadge from './LateIngestedBadge';
import { formatDateSafe } from '../../utils/dates';

describe('LateIngestedBadge', () => {
  it('renders nothing when the lead is not flagged late-ingested', () => {
    const { container } = render(
      <LateIngestedBadge lateIngested={false} ingestedOn="2026-07-05T00:00:00Z" dueDate="2026-07-01T00:00:00Z" />,
    );
    expect(container).toBeEmptyDOMElement();
  });

  it('renders nothing when the flag is absent (older payloads)', () => {
    const { container } = render(<LateIngestedBadge ingestedOn="2026-07-05T00:00:00Z" />);
    expect(container).toBeEmptyDOMElement();
  });

  it('renders the badge with the audit explanation in its tooltip', async () => {
    render(
      <LateIngestedBadge lateIngested ingestedOn="2026-07-05T00:00:00Z" dueDate="2026-07-01T00:00:00Z" />,
    );

    const badge = screen.getByLabelText('Loaded after deadline');
    expect(badge).toBeInTheDocument();

    // The audit meaning travels with the badge (MUI tooltip on hover).
    fireEvent.mouseOver(badge);
    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip).toHaveTextContent(
      /excluded from Nexora's response-time and aging performance metrics/,
    );
    // Labels are asserted through the shared formatter so the test is
    // independent of the host timezone (dates render in local time app-wide).
    expect(tooltip).toHaveTextContent(formatDateSafe('2026-07-05T00:00:00Z'));
    expect(tooltip).toHaveTextContent(`deadline of ${formatDateSafe('2026-07-01T00:00:00Z')}`);
  });

  it('omits the due-date clause when no real deadline exists', async () => {
    render(<LateIngestedBadge lateIngested ingestedOn="2026-07-05T00:00:00Z" dueDate={null} />);

    fireEvent.mouseOver(screen.getByLabelText('Loaded after deadline'));
    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip).toHaveTextContent(/after its deadline\./);
  });
});
