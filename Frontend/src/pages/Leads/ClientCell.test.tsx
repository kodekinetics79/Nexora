import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import ClientCell, {
  clientIdentityState, clientStatusLabel, matchExplanation, realSenderAddress,
} from './ClientCell';
import type { ClientIdentityLike } from './ClientCell';

const candidate = (over: Partial<{ rank: number; customerId: number; customerName: string; confidence: number; reasonCode: string }> = {}) => ({
  rank: 1,
  customerId: 42,
  customerName: 'Saudi Electricity Company',
  confidence: 0.95,
  reasonCode: 'SENDER_DOMAIN',
  ...over,
});

describe('clientIdentityState', () => {
  it('treats a linked customer as resolved', () => {
    expect(clientIdentityState({ customerId: 42, customerMatchStatus: 'AUTO_MATCHED' })).toBe('resolved');
    expect(clientIdentityState({ customerId: 42, customerMatchStatus: 'CONFIRMED' })).toBe('resolved');
    expect(clientIdentityState({ customerId: 42, customerMatchStatus: 'VERIFIED_EMAIL' })).toBe('resolved');
  });

  it('NEVER treats a suggestion as a link, even if a payload contradicts the invariant', () => {
    // The database CHECK constraint forbids this combination. If it ever
    // reaches the UI anyway, an unconfirmed guess must not render as a fact.
    expect(clientIdentityState({ customerId: 7, customerMatchStatus: 'SUGGESTED' })).toBe('suggested');
    expect(clientIdentityState({ customerId: 7, customerMatchStatus: 'AMBIGUOUS' })).toBe('suggested');
  });

  it('is suggested when candidates exist without a link', () => {
    expect(clientIdentityState({ customerMatchStatus: 'SUGGESTED', clientCandidates: [candidate()] })).toBe('suggested');
  });

  it('is unresolved with no link, no candidates and no status', () => {
    expect(clientIdentityState({})).toBe('unresolved');
    expect(clientIdentityState({ customerMatchStatus: 'UNRESOLVED' })).toBe('unresolved');
  });
});

describe('client identity vocabulary', () => {
  it('never leaks a raw status enum to a user', () => {
    for (const status of [
      'AUTO_MATCHED', 'AUTO_MATCHED_CONTACT_UNRESOLVED', 'CONFIRMED',
      'CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED', 'VERIFIED_EMAIL', 'SUGGESTED',
      'AMBIGUOUS', 'UNRESOLVED', 'SOMETHING_NEW', '',
    ]) {
      const label = clientStatusLabel(status);
      expect(label).not.toMatch(/_/);
      expect(label.length).toBeGreaterThan(0);
    }
  });

  it('explains a match in plain language', () => {
    expect(matchExplanation({ customerMatchReasonCode: 'SENDER_DOMAIN' }))
      .toBe("Matched because the sender's email domain belongs to this client.");
  });

  it('has no explanation for an unknown reason code rather than inventing one', () => {
    expect(matchExplanation({ customerMatchReasonCode: 'WHAT_IS_THIS' })).toBeNull();
    expect(matchExplanation({})).toBeNull();
  });

  it("rejects Nexora's own synthetic senders as evidence", () => {
    // These are intake labels the platform writes for itself, not customers.
    expect(realSenderAddress('extraction@pipeline.local')).toBeNull();
    expect(realSenderAddress('sec@system.com')).toBeNull();
    expect(realSenderAddress('manual@upload.com')).toBeNull();
    expect(realSenderAddress('system@excel.upload')).toBeNull();
    expect(realSenderAddress('')).toBeNull();
    // ...but the real Saudi Electricity Company domain is real evidence.
    expect(realSenderAddress('57322@se.com.sa')).toBe('57322@se.com.sa');
  });
});

describe('ClientCell', () => {
  const states: Array<[string, ClientIdentityLike]> = [
    ['resolved', { customerId: 42, customerName: 'Saudi Electricity Company', customerMatchStatus: 'AUTO_MATCHED', customerMatchReasonCode: 'SENDER_DOMAIN' }],
    ['suggested', { customerMatchStatus: 'SUGGESTED', clientCandidates: [candidate()] }],
    ['ambiguous', { customerMatchStatus: 'AMBIGUOUS', clientCandidates: [candidate(), candidate({ rank: 2, customerId: 43, customerName: 'SEC Distribution' })] }],
    ['unresolved', { customerMatchStatus: 'UNRESOLVED' }],
  ];

  it.each(states)('never renders an empty cell in the %s state', (_name, lead) => {
    const { container } = render(<ClientCell lead={lead} onResolve={() => {}} />);
    expect(container).not.toBeEmptyDOMElement();
    expect(container.textContent?.trim().length ?? 0).toBeGreaterThan(0);
  });

  it('shows the client name when resolved', () => {
    render(<ClientCell lead={states[0][1]} onResolve={() => {}} />);
    expect(screen.getByText('Saudi Electricity Company')).toBeInTheDocument();
    // A resolved client is a fact, not an action — no resolve affordance.
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('marks a suggestion as unconfirmed and opens the resolver on click', () => {
    const onResolve = vi.fn();
    render(<ClientCell lead={states[1][1]} onResolve={onResolve} />);

    expect(screen.getByText('Saudi Electricity Company')).toBeInTheDocument();
    expect(screen.getByText(/Suggested/)).toBeInTheDocument();

    const trigger = screen.getByRole('button', { name: /Suggested client Saudi Electricity Company/i });
    fireEvent.click(trigger);
    expect(onResolve).toHaveBeenCalledTimes(1);
  });

  it('counts the competing clients when the evidence is ambiguous', () => {
    render(<ClientCell lead={states[2][1]} onResolve={() => {}} />);
    expect(screen.getByText('2 possible clients')).toBeInTheDocument();
  });

  it('offers a way forward when unresolved', () => {
    const onResolve = vi.fn();
    render(<ClientCell lead={states[3][1]} onResolve={onResolve} />);

    expect(screen.getByText('Unknown client')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /Set the client company for this lead/i }));
    expect(onResolve).toHaveBeenCalledTimes(1);
  });

  it('still states the client (or its absence) when the user cannot edit', () => {
    const { container } = render(<ClientCell lead={states[3][1]} onResolve={() => {}} canEdit={false} />);
    expect(screen.getByText('Unknown client')).toBeInTheDocument();
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
    expect(container).not.toBeEmptyDOMElement();
  });

  it('falls back to the customer id rather than rendering a blank name', () => {
    render(<ClientCell lead={{ customerId: 99, customerMatchStatus: 'CONFIRMED' }} onResolve={() => {}} />);
    expect(screen.getByText('Customer #99')).toBeInTheDocument();
  });
});
