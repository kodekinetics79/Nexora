import { render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * The price attestation is a deliberate control and stays.
 *
 * Nexora converts documents into leads, RFQs and quotes; it is not an auto quote sender. A person
 * takes responsibility before anything reaches a customer, and this dialog is where that happens.
 * The gate is correct — it is server-enforced, it is not bypassable, and that is the design.
 *
 * What was wrong was the wording. The two recorded sources are SALES_MANAGER and SUPPLIER_QUOTE
 * (a database check constraint permits only those), and SALES_MANAGER was labelled "My sales
 * manager gave me these prices" — the direct case only. A rep pricing a line from stock is
 * applying the price list or standard margin that sales management set, which is sales-management
 * authority, but no option said so. The same screen already derives four cost sources including
 * INTERNAL_INVENTORY, so the system knew the answer and offered nothing true.
 *
 * That rep had to state something false to send a quote, roughly fifteen times a day. An
 * attestation people must lie to satisfy is worse than none: it poisons the exact record the
 * control exists to create, and it teaches the rep that the product's controls are theatre.
 *
 * These tests pin the fix without pinning the prose: both sources remain, and the
 * sales-management option must cover the indirect case.
 */

const { getPriceAttestation } = vi.hoisted(() => ({ getPriceAttestation: vi.fn() }));

vi.mock('../../../api/services/quoteService', async () => {
  const actual = await vi.importActual<Record<string, unknown>>('../../../api/services/quoteService');
  return { ...actual, default: { ...(actual.default as object), getPriceAttestation } };
});

import PriceConfirmationDialog from './PriceConfirmationDialog';

function renderDialog() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <PriceConfirmationDialog
        open
        quoteId={66}
        quoteNo="QT-0826-0002"
        recipientEmail="buyer@aramco.com"
        onCancel={() => {}}
        onConfirm={() => {}}
      />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getPriceAttestation.mockResolvedValue({
    satisfied: false,
    lines: [{ lineNumber: 1, description: 'Cisco Catalyst 9200', quantity: 2, unitPrice: 920000, totalAmount: 1840000 }],
    totalAmount: 1840000,
    currencyCode: null,
  });
});

describe('the price attestation gate', () => {
  it('still demands a confirmation before a quote can be sent', async () => {
    renderDialog();
    // The control itself is the product's design, not a defect: no auto-send.
    expect(await screen.findByText(/Confirm the prices before sending/i)).toBeInTheDocument();
  });

  it('still records both permitted sources', async () => {
    renderDialog();
    await screen.findAllByRole('radio');
    expect(screen.getAllByRole('radio')).toHaveLength(2);
    expect(screen.getByText(/supplier quote/i)).toBeInTheDocument();
  });

  it('gives a rep pricing from stock an option that is true', async () => {
    renderDialog();
    await screen.findAllByRole('radio');

    // Read the option LABELS, not the selected-option helper: a rep choosing between the two
    // has selected nothing yet, so anything only rendered on selection is invisible to them.
    const page = document.body.textContent ?? '';
    // The narrow personal claim left a stock-priced line with nothing honest to select.
    expect(page).not.toMatch(/my sales manager gave me these prices/i);
    // Sales-management authority must reach the indirect case: the price list or standard margin.
    expect(page).toMatch(/price list|standard margin/i);
  });
});
