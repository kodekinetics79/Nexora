import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { AccountTeamGap, NO_ACCOUNT_TEAM_EXPLANATION, healthBandWord, healthWord, metricWithStatus } from './customerWords';

/**
 * The Customers list showed "No account team" in red with no reason, and the customer page printed
 * "Account health: insufficient-evidence", "RFQ trend: … (insufficient-evidence)" and "unavailable:
 * No immutable quote-time landed-cost evidence is linked to accepted customer quote lines." A rep
 * should never meet a wire token or an engineer's sentence.
 */
describe('AccountTeamGap', () => {
  it('explains the red mark on hover in the list', async () => {
    render(<AccountTeamGap />);
    fireEvent.mouseOver(screen.getByText('No account team'));
    expect(await screen.findByRole('tooltip')).toHaveTextContent(NO_ACCOUNT_TEAM_EXPLANATION);
  });

  it('prints the reason beside the mark on the detail page', () => {
    render(<AccountTeamGap variant="detail" />);
    expect(screen.getByText('No account team')).toBeInTheDocument();
    expect(screen.getByText(/route to nobody by default/)).toBeInTheDocument();
  });
});

describe('health words', () => {
  it('turns the evidence tokens into "Not enough history yet"', () => {
    for (const token of ['insufficient-evidence', 'insufficient_evidence', 'unavailable', '', null, undefined]) {
      expect(healthWord(token)).toBe('Not enough history yet');
      expect(healthBandWord(token)).toBe('Not enough history yet');
    }
  });

  it('says nothing for "available" — the number beside it is the answer', () => {
    expect(healthWord('available')).toBe('');
    expect(metricWithStatus(12.5, '%', 'available')).toBe('12.5%');
  });

  it('gives the status in words when there is no figure', () => {
    expect(metricWithStatus(null, '%', 'insufficient-evidence')).toBe('Not enough history yet');
    expect(metricWithStatus(undefined, '%', 'unavailable')).toBe('Not enough history yet');
  });

  it('never leaks a hyphenated or underscored token', () => {
    for (const token of ['healthy', 'at-risk', 'at_risk', 'watch', 'some-new-band']) {
      expect(healthWord(token)).not.toMatch(/[-_]/);
      expect(healthWord(token).length).toBeGreaterThan(0);
    }
  });
});
