import { describe, expect, it } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import ClientCell, { confirmedExplanation, matchExplanation } from './ClientCell';

/**
 * A lead whose customer a colleague CONFIRMED still carried the resolver's pre-confirmation
 * sentence — "Client evidence was found but matched no customer in this tenant." — as the tooltip
 * and accessible description of a cell that showed the confirmed customer's name. Two facts on one
 * cell, contradicting each other; the rep believed the wrong one.
 */
const STALE = 'Client evidence was found but matched no customer in this tenant.';

describe('a confirmed customer explains itself from its status, not from the machine’s old sentence', () => {
  it('says the customer is confirmed and the contact still unknown', () => {
    expect(matchExplanation({
      customerId: 30,
      customerName: 'Saudi Aramco',
      customerMatchStatus: 'CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED',
      customerMatchReasonCode: 'NO_MATCH',
      customerMatchExplanation: STALE,
    })).toBe('Customer confirmed by a colleague; contact still unknown.');
  });

  it('says both are confirmed when the contact is known too', () => {
    expect(matchExplanation({
      customerId: 30,
      customerMatchStatus: 'CONFIRMED',
      customerMatchExplanation: STALE,
    })).toBe('Customer and contact confirmed by a colleague.');
    expect(confirmedExplanation('confirmed')).toBe('Customer and contact confirmed by a colleague.');
  });

  it('still shows the engine’s own quoted sentence for a machine match', () => {
    const authored = '"Saudi Electricity Company" appears in the delivery address: "Saudi Electricity Company-DAMMAM".';
    expect(matchExplanation({ customerId: 42, customerMatchStatus: 'AUTO_MATCHED', customerMatchExplanation: authored })).toBe(authored);
    expect(confirmedExplanation('AUTO_MATCHED')).toBeNull();
    expect(confirmedExplanation(undefined)).toBeNull();
  });

  it('puts the confirmed sentence, not the stale one, on the cell the rep hovers', async () => {
    render(
      <ClientCell
        lead={{ customerId: 30, customerName: 'Saudi Aramco', customerMatchStatus: 'CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED', customerMatchExplanation: STALE }}
        onResolve={() => {}}
      />,
    );
    fireEvent.mouseOver(screen.getByText('Saudi Aramco'));
    const tooltip = await screen.findByRole('tooltip');
    expect(tooltip).toHaveTextContent('Customer confirmed by a colleague; contact still unknown.');
    expect(tooltip).not.toHaveTextContent(STALE);
  });
});
