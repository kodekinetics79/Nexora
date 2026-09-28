import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { LeadDecisionEvidenceDTO } from '../../../api/services/leadDecisionService';

const getBuyerTerms = vi.fn();
vi.mock('../../../api/services/leadDecisionService', () => ({
  default: { getBuyerTerms: (...args: unknown[]) => getBuyerTerms(...args) },
}));

import BuyerTermsPanel from './BuyerTermsPanel';

const DOCX = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
const evidence = (overrides: Partial<LeadDecisionEvidenceDTO> = {}): LeadDecisionEvidenceDTO => ({
  occurrenceId: 1, sourceDocumentId: 18, kind: 'DOCUMENT', name: 'RFP - 6000000028 - 1 of 3.docx', mediaType: DOCX,
  status: 'CLEARED', sourceAvailable: true, downloadUrl: '/api/File/source-document/18', ...overrides,
});

const renderPanel = (items: LeadDecisionEvidenceDTO[]) => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <BuyerTermsPanel evidence={items} />
  </QueryClientProvider>,
);

describe('BuyerTermsPanel', () => {
  beforeEach(() => getBuyerTerms.mockReset());

  it("lists what the buyer requires, in the buyer's words on hover", async () => {
    getBuyerTerms.mockResolvedValue([
      { key: 'closes', label: 'Closes', value: '8 Oct 2026, 3:00 PM, no extension', quote: 'Due date: 10/8/2026 3:00 PM; Allow bidding overtime: No' },
      { key: 'delivery_terms', label: 'Delivery terms', value: 'AMC/SAC; also price VDD/VTC as an alternative', quote: 'The preferred Incoterms for this bidding process are AMC/SAC.' },
      { key: 'quote_currency', label: 'Quote in', value: 'USD or SAR', quote: 'Quotations shall be submitted in either USD or SAR.' },
    ]);
    renderPanel([evidence()]);

    const section = await screen.findByRole('region', { name: 'Buyer requires' });
    expect(getBuyerTerms).toHaveBeenCalledWith(18);
    // Folded to one line of what decides the bid, so the lines keep the screen.
    expect(within(section).getByTestId('buyer-terms-headline')).toHaveTextContent('Closes 8 Oct 2026, 3:00 PM, no extension');
    expect(within(section).queryByRole('term')).toBeNull();
    fireEvent.click(within(section).getByRole('button', { name: 'All 3' }));
    expect(within(section).getByRole('button', { name: 'Fewer' })).toHaveAttribute('aria-expanded', 'true');
    expect(within(section).getByText('Delivery terms')).toBeInTheDocument();
    expect(within(section).getByText('AMC/SAC; also price VDD/VTC as an alternative')).toBeInTheDocument();
    expect(within(section).getByText('8 Oct 2026, 3:00 PM, no extension')).toBeInTheDocument();
    expect(within(section).getByText('From RFP - 6000000028 - 1 of 3.docx')).toBeInTheDocument();
    expect(within(section).getByLabelText('“Quotations shall be submitted in either USD or SAR.”')).toHaveTextContent('USD or SAR');
  });

  it('shows nothing when the document states no terms', async () => {
    getBuyerTerms.mockResolvedValue([]);
    const { container } = renderPanel([evidence()]);
    await vi.waitFor(() => expect(getBuyerTerms).toHaveBeenCalled());
    expect(container).toBeEmptyDOMElement();
  });

  it('does not ask for terms from a file that is not a Word document', () => {
    const { container } = renderPanel([evidence({ name: 'bid-list.xlsx', mediaType: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet' })]);
    expect(getBuyerTerms).not.toHaveBeenCalled();
    expect(container).toBeEmptyDOMElement();
  });
});
