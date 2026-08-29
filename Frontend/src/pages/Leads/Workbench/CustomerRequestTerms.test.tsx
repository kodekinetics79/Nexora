import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import CustomerRequestTerms from './CustomerRequestTerms';

describe('CustomerRequestTerms', () => {
  it('shows the frozen customer terms for participation review', () => {
    render(
      <CustomerRequestTerms
        requiredDeliveryDate="2026-10-01T00:00:00Z"
        deliveryLocation="North Logistics Hub, Gate 4"
        agreementReference="FRAME-2026-118"
      />,
    );

    expect(screen.getByText('Customer request terms')).toBeInTheDocument();
    expect(screen.getByText('01 Oct 2026')).toBeInTheDocument();
    expect(screen.getByText('North Logistics Hub, Gate 4')).toBeInTheDocument();
    expect(screen.getByText('FRAME-2026-118')).toBeInTheDocument();
    expect(screen.queryByText(/not captured/i)).not.toBeInTheDocument();
  });

  it('warns clearly when extraction did not capture a customer term', () => {
    render(<CustomerRequestTerms requiredDeliveryDate={null} deliveryLocation="Dammam" agreementReference={null} />);

    expect(screen.getAllByText('Not captured')).toHaveLength(2);
    expect(screen.getByText(/check the source evidence before committing participation/i)).toBeInTheDocument();
  });
});
