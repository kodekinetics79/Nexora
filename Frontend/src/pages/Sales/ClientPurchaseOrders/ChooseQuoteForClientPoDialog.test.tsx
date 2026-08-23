import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ChooseQuoteForClientPoDialog from './ChooseQuoteForClientPoDialog';

const getAll = vi.fn();
const getById = vi.fn();

vi.mock('../../../api/services/quoteService', () => ({
  __esModule: true,
  default: {
    getAll: (...args: unknown[]) => getAll(...args),
    getById: (...args: unknown[]) => getById(...args),
  },
}));

/**
 * A quotation as the LIST endpoint returns it. Only the fields the picker decides on are set;
 * anything cast in is deliberately absent so a rule that starts depending on a field the list does
 * not carry fails here rather than in front of a user.
 */
const listedQuote = (over: Record<string, unknown>) => ({
  id: 1,
  quoteNo: 'QT-1',
  quoteDate: '2026-08-01',
  validUntil: '2026-09-01',
  version: 1,
  statusId: 90,
  statusCode: 'SENT',
  statusValue: 'Sent',
  commercialCaseId: 7,
  customerId: 9,
  currencyId: 3,
  customerName: 'Noor & Sons LLC',
  nexoraSerial: 'NOOR-SONS-LLC-2026-000099',
  ...over,
} as any);

const renderPicker = (onChosen = vi.fn()) => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={client}>
      <ChooseQuoteForClientPoDialog open onClose={vi.fn()} onChosen={onChosen} />
    </QueryClientProvider>,
  );
  return onChosen;
};

/**
 * The door this dialog opens is "upload the client PO and hook it to the quote", started from the
 * Client PO Inbox rather than from inside one quotation.
 *
 * The defect it pins
 * ------------------
 * The picker decides which quotations are offered, and the SERVER decides which it will accept —
 * `CustomerAwardApplicationService.LoadEligibleQuoteAsync` (sent, accepted or ordered; not
 * superseded) plus `ValidatePurchaseOrderCommand` (a commercial case, a customer and a currency are
 * all required). Two independent statements of one rule drift. When they drift towards permissive
 * the user gets an unexplained 400 or 409 after transcribing a whole purchase order; when they
 * drift towards strict, the quotation they are holding a PO for simply is not in the list and there
 * is nothing on screen to explain its absence.
 *
 * These tests assert the OFFER — what the list is allowed to contain, and that what is handed on
 * carries the identity the create-PO command requires. They do not re-test the workspace: what
 * happens after a quotation is chosen is `CustomerAwardWorkspace`, which has its own suite.
 */
describe('ChooseQuoteForClientPoDialog', () => {
  beforeEach(() => {
    getAll.mockReset();
    getById.mockReset();
  });

  it('offers only quotations the server would accept a purchase order against', async () => {
    getAll.mockResolvedValue({
      totalItems: 4,
      items: [
        listedQuote({ id: 1, quoteNo: 'QT-SENT' }),
        listedQuote({ id: 2, quoteNo: 'QT-ACCEPTED', statusCode: 'ACCEPTED', statusValue: 'Accepted' }),
        // Refused by LoadEligibleQuoteAsync: nothing has been sent to the customer, so there is no
        // document for a purchase order to be answering.
        listedQuote({ id: 3, quoteNo: 'QT-DRAFT', statusCode: 'DRAFT', statusValue: 'Draft' }),
        // Refused by ValidatePurchaseOrderCommand: the PO takes the quotation's currency, and a
        // quotation with none cannot lend one.
        listedQuote({ id: 4, quoteNo: 'QT-NO-CURRENCY', currencyId: undefined }),
      ],
    });

    renderPicker();

    await screen.findByText('QT-SENT');
    expect(screen.getByText('QT-ACCEPTED')).toBeInTheDocument();
    expect(screen.queryByText('QT-DRAFT')).not.toBeInTheDocument();
    expect(screen.queryByText('QT-NO-CURRENCY')).not.toBeInTheDocument();
  });

  it('hands on the commercial identity the purchase order will be filed under', async () => {
    getAll.mockResolvedValue({ totalItems: 1, items: [listedQuote({ id: 42, quoteNo: 'QT-42' })] });
    getById.mockResolvedValue(listedQuote({
      id: 42,
      quoteNo: 'QT-42',
      currencyCode: 'SAR',
      quoteItems: [{ id: 900, productId: 5, productName: 'Ball valve 2in', itemDescription: 'Ball valve 2in', quantity: 10, unitPrice: 100 }],
    }));
    const onChosen = renderPicker();

    fireEvent.click(await screen.findByRole('button', { name: 'Upload PO' }));

    await waitFor(() => expect(onChosen).toHaveBeenCalledTimes(1));
    // The triple ValidateQuoteIdentity checks the customer PO against. Handing the workspace a
    // quote missing any of it produces a purchase order the spine cannot accept, and the failure
    // surfaces only after the operator has keyed the buyer's whole document.
    expect(onChosen.mock.calls[0][0]).toMatchObject({
      id: 42,
      commercialCaseId: 7,
      customerId: 9,
      currencyId: 3,
    });
    // The quoted lines, because the workspace compares the buyer's figures against them. A quote
    // handed over with no lines would compare the purchase order against nothing and report an
    // exact match on every line.
    expect(onChosen.mock.calls[0][0].lines).toHaveLength(1);
  });

  it('refuses to open a quotation that has no priced lines instead of handing over an empty one', async () => {
    getAll.mockResolvedValue({ totalItems: 1, items: [listedQuote({ id: 43, quoteNo: 'QT-43' })] });
    getById.mockResolvedValue(listedQuote({ id: 43, quoteNo: 'QT-43', quoteItems: [] }));
    const onChosen = renderPicker();

    fireEvent.click(await screen.findByRole('button', { name: 'Upload PO' }));

    await screen.findByText('This quotation has no priced lines to match a purchase order against.');
    expect(onChosen).not.toHaveBeenCalled();
  });

  it('says the quotations could not be loaded rather than showing an empty list', async () => {
    getAll.mockRejectedValue(new Error('network'));

    renderPicker();

    await screen.findByText(/could not be loaded/i);
    expect(screen.queryByText(/No quotation here can take a Client PO/i)).not.toBeInTheDocument();
  });
});
