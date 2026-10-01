import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * A filtered list under a heading that claims to show everything.
 *
 * The quote views are filtered addresses — /sales/quotes?state=sent and friends. The page once
 * headed itself "Quote Management / Manage sales quotations and customer offers" whichever door
 * the rep came through, with nothing saying a filter was on. A rep clicked "Sent Quotes" at 4pm to
 * chase offers, saw three rows, and reported to their manager that the pipeline was nearly empty.
 *
 * The page has to name the slice it is showing and offer the way back to all of it. The tab strip
 * does both now: the slice's tab is lit, and "All" sits beside it.
 *
 * Since 2026-09-29 the list also reads like the Leads list: "Quotes · N", one worded verb per row
 * instead of an eye + download + ⋮ (owner: "old school"), and the rest in the Spreadsheet view.
 */

const { getAll, downloadPdf, downloadUploadedFile } = vi.hoisted(() => ({
  getAll: vi.fn(),
  downloadPdf: vi.fn(),
  downloadUploadedFile: vi.fn(),
}));

vi.mock('../../../api/services/quoteService', () => ({
  default: {
    getAll,
    revise: vi.fn(),
    sendEmail: vi.fn(),
    confirmPriceAttestation: vi.fn(),
    downloadPdf,
    downloadUploadedFile,
  },
  describeQuoteSendOutcome: () => ({ delivered: true, message: 'Quote emailed to the customer' }),
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { businessUnitId: 1 },
    hasPermission: () => true,
  }),
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

// None of these is under test; they only add noise to the header assertions.
vi.mock('./PriceConfirmationDialog', () => ({ default: () => null }));
vi.mock('../../../components/common/EmailPromptDialog', () => ({ default: () => null }));
// The upload window stands in with one button that reports a saved quote, as the real one does.
vi.mock('./UploadQuoteDialog', () => ({
  default: ({ open, onSaved }: { open: boolean; onSaved: (q: { quoteId: number; quoteNo: string; replacedDraftNo?: string | null }) => void }) =>
    (open ? <button type="button" onClick={() => onSaved({ quoteId: 77, quoteNo: 'QT-0929-0077', replacedDraftNo: null })}>fake save</button> : null),
}));
// The status window is tested on its own; here only WHICH quote it opened for matters.
vi.mock('./UpdateQuoteStatusDialog', () => ({
  default: ({ open, quote }: { open: boolean; quote: { quoteNo: string } | null }) =>
    (open && quote ? <div role="dialog" aria-label={`Update ${quote.quoteNo}`} /> : null),
}));

import QuotesPage from './QuotesPage';

// The rep's row. Shaped from QuoteResponseDTO as the grid consumes it.
const sent = {
  id: 11, quoteNo: 'QT-2026-0011', nexoraSerial: 'NX-Q-0011', customerName: 'Aramco',
  statusCode: 'SENT', statusValue: 'Sent', totalAmount: 1840000, currencyCode: 'SAR', currencyId: 1,
  quoteDate: '2026-08-01', validUntil: '2026-09-01', daysSinceSent: 3, isStale: false,
  itemCount: 2, rfqNo: 'RFQ-2026-0041', ownerName: 'Sara Rep',
};
const sentQuotes = [sent];

function renderQuotes(url: string) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const Address = () => <div data-testid="address">{useLocation().pathname + useLocation().search}</div>;
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[url]}>
        <Address />
        <Routes>
          <Route path="/sales/quotes" element={<QuotesPage />} />
          <Route path="/sales/quotes/view/:id" element={<div>quote page</div>} />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

const rows = (...items: object[]) => getAll.mockResolvedValue({ items, totalItems: items.length });

beforeEach(() => {
  vi.clearAllMocks();
  // The Simple / Spreadsheet choice is remembered per browser; every test starts from Simple.
  try { window.localStorage?.clear(); } catch { /* no storage in this environment */ }
  getAll.mockResolvedValue({ items: sentQuotes, totalItems: sentQuotes.length });
});

const tab = (name: string) => screen.getByRole('tab', { name });
const address = () => screen.getByTestId('address').textContent;
const toSpreadsheet = () => fireEvent.click(screen.getByRole('button', { name: 'Spreadsheet view' }));

describe('the quote list opened through a filtered address', () => {
  it('lights the slice it is showing, with All beside it', async () => {
    renderQuotes('/sales/quotes?state=sent');

    await screen.findByText('QT-2026-0011');
    expect(tab('Sent')).toHaveAttribute('aria-selected', 'true');
    expect(tab('All')).toHaveAttribute('aria-selected', 'false');
    expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ state: 'sent' }));
  });

  it('names the right slice for each state the server actually narrows on', async () => {
    renderQuotes('/sales/quotes?state=follow-up');

    await screen.findByText('QT-2026-0011');
    expect(tab('Follow-up due')).toHaveAttribute('aria-selected', 'true');
  });

  it('offers a way back to the unfiltered list', async () => {
    renderQuotes('/sales/quotes?state=sent');

    fireEvent.click(await screen.findByRole('tab', { name: 'All' }));

    await waitFor(() => expect(screen.getByTestId('address')).toHaveTextContent('/sales/quotes'));
    expect(address()).not.toContain('state=');
    expect(tab('All')).toHaveAttribute('aria-selected', 'true');
  });

  it('says so when the link asks for a filter the server does not apply', async () => {
    // QuoteRepository.GetAllAsync silently ignores an unknown state, so the grid really is
    // complete — lighting a slice here would be the same lie pointing the other way.
    renderQuotes('/sales/quotes?state=requires-sourcing');

    expect(await screen.findByText(/not a filter this list applies/i)).toBeInTheDocument();
  });

  it('lights All on the unfiltered page', async () => {
    renderQuotes('/sales/quotes');

    await screen.findByText('QT-2026-0011');
    expect(tab('All')).toHaveAttribute('aria-selected', 'true');
    expect(screen.queryByText(/not a filter this list applies/i)).not.toBeInTheDocument();
  });

  it('asks for 25 quotes a page', async () => {
    renderQuotes('/sales/quotes');

    await screen.findByText('QT-2026-0011');
    expect(getAll).toHaveBeenCalledWith(expect.objectContaining({ pageNumber: 1, pageSize: 25 }));
  });
});

describe('the header, the way Leads has it', () => {
  it('titles the list with the total the server counted', async () => {
    getAll.mockResolvedValue({ items: sentQuotes, totalItems: 42 });
    renderQuotes('/sales/quotes');

    await screen.findByText('QT-2026-0011');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Quotes · 42');
  });

  it('has no filled button: Upload a quote and Create quote are both outlined', async () => {
    renderQuotes('/sales/quotes');

    await screen.findByText('QT-2026-0011');
    expect(screen.getByRole('button', { name: 'Upload a quote' })).toHaveClass('MuiButton-outlined');
    expect(screen.getByRole('button', { name: 'Create quote' })).toHaveClass('MuiButton-outlined');
    expect(document.querySelectorAll('.MuiButton-contained')).toHaveLength(0);
  });

  it('offers to open a just-uploaded quote from the message that confirms it', async () => {
    renderQuotes('/sales/quotes?state=draft');

    await screen.findByText('QT-2026-0011');
    fireEvent.click(screen.getByRole('button', { name: 'Upload a quote' }));
    fireEvent.click(screen.getByRole('button', { name: 'fake save' }));

    expect(await screen.findByText('QT-0929-0077 saved as sent.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Open' }));
    await waitFor(() => expect(address()).toBe('/sales/quotes/view/77'));
  });
});

describe('one worded verb per row (Simple view)', () => {
  const cases: [string, string, object][] = [
    ['a draft', 'Finish', { ...sent, id: 1, quoteNo: 'QT-D', statusCode: 'DRAFT', statusValue: 'Draft' }],
    ['a sent quote', 'Update status', { ...sent, id: 2, quoteNo: 'QT-S' }],
    ['an uploaded, sent quote', 'Update status', { ...sent, id: 3, quoteNo: 'QT-U', uploadedFileName: 'q.pdf' }],
    ['a won quote with no order yet', 'Enter PO', { ...sent, id: 4, quoteNo: 'QT-W', statusCode: 'ACCEPTED', statusValue: 'Accepted' }],
    ['a lost quote', 'View', { ...sent, id: 5, quoteNo: 'QT-L', statusCode: 'REJECTED', statusValue: 'Rejected' }],
    ['a quote a revision replaced', 'View', { ...sent, id: 6, quoteNo: 'QT-R', supersededByQuoteNo: 'QT-R-R2' }],
  ];

  it.each(cases)('%s reads "%s"', async (_name, verb, quote) => {
    rows(quote);
    renderQuotes('/sales/quotes');

    const quoteNo = (quote as { quoteNo: string }).quoteNo;
    const button = await screen.findByRole('button', { name: `${verb} ${quoteNo}` });
    expect(button).toHaveTextContent(verb);
    // One verb, never two.
    const others = ['Finish', 'Update status', 'Enter PO', 'View'].filter((v) => v !== verb);
    others.forEach((other) => expect(screen.queryByRole('button', { name: `${other} ${quoteNo}` })).not.toBeInTheDocument());
  });

  it('shows no eye, no download icon and no ⋮ on a row', async () => {
    renderQuotes('/sales/quotes');

    // The row is on screen, so the absences below are real.
    await screen.findByRole('button', { name: 'Update status QT-2026-0011' });
    expect(screen.queryByRole('button', { name: 'Open' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^Download/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^More for/ })).not.toBeInTheDocument();
    expect(document.querySelector('[data-testid="VisibilityIcon"]')).toBeNull();
  });

  it('"Update status" opens the status window on the list, for that quote', async () => {
    renderQuotes('/sales/quotes');

    fireEvent.click(await screen.findByRole('button', { name: 'Update status QT-2026-0011' }));

    expect(screen.getByRole('dialog', { name: 'Update QT-2026-0011' })).toBeInTheDocument();
    expect(address()).toBe('/sales/quotes');
  });

  it('"Finish" opens the draft', async () => {
    rows({ ...sent, statusCode: 'DRAFT', statusValue: 'Draft' });
    renderQuotes('/sales/quotes');

    fireEvent.click(await screen.findByRole('button', { name: 'Finish QT-2026-0011' }));
    await waitFor(() => expect(address()).toBe('/sales/quotes/view/11'));
  });

  it('"Enter PO" opens the quote with the PO window asked for', async () => {
    rows({ ...sent, statusCode: 'ACCEPTED', statusValue: 'Accepted' });
    renderQuotes('/sales/quotes');

    fireEvent.click(await screen.findByRole('button', { name: 'Enter PO QT-2026-0011' }));
    await waitFor(() => expect(address()).toBe('/sales/quotes/view/11?action=po'));
  });

  it('a click on the row opens the quote', async () => {
    renderQuotes('/sales/quotes');

    fireEvent.click(await screen.findByText('Aramco'));
    await waitFor(() => expect(address()).toBe('/sales/quotes/view/11'));
  });

  it('the quote number is a link that opens the quote', async () => {
    renderQuotes('/sales/quotes');

    fireEvent.click(await screen.findByRole('button', { name: 'Open quote QT-2026-0011' }));
    await waitFor(() => expect(address()).toBe('/sales/quotes/view/11'));
  });

  it('shows the total incl. VAT, the validity and the owner', async () => {
    renderQuotes('/sales/quotes');

    await screen.findByText('QT-2026-0011');
    expect(screen.getByRole('columnheader', { name: 'Total incl. VAT' })).toBeInTheDocument();
    expect(screen.getByText('SAR 1,840,000.00')).toBeInTheDocument();
    expect(screen.getByText('Valid until 1 Sep')).toBeInTheDocument();
    expect(screen.getByText('Sara Rep')).toBeInTheDocument();
  });

  it('says "No currency" rather than a blank code, and "Not set" for no owner', async () => {
    rows({ ...sent, currencyCode: undefined, ownerName: null });
    renderQuotes('/sales/quotes');

    expect(await screen.findByText('No currency')).toBeInTheDocument();
    expect(screen.getByText('Not set')).toBeInTheDocument();
  });

  it('a closed quote shows when it closed, quietly', async () => {
    rows({ ...sent, statusCode: 'REJECTED', statusValue: 'Rejected', outcomeOn: '2026-09-17T10:00:00Z' });
    renderQuotes('/sales/quotes');

    expect(await screen.findByText('Closed 17 Sep')).toBeInTheDocument();
  });

  it('the server does not sort, so the columns do not pretend to', async () => {
    renderQuotes('/sales/quotes');

    await screen.findByText('QT-2026-0011');
    const headers = screen.getAllByRole('columnheader');
    expect(headers.length).toBeGreaterThan(3);
    headers.forEach((header) => expect(header).not.toHaveClass('MuiDataGrid-columnHeader--sortable'));
  });
});

describe('the empty list says what is true', () => {
  it('a search in a tab clears the search only, and stays on the tab', async () => {
    getAll.mockResolvedValue({ items: [], totalItems: 0 });
    renderQuotes('/sales/quotes?state=sent');

    await screen.findByText('Nothing in "Sent"');
    fireEvent.change(screen.getByPlaceholderText('Search by quote, RFQ, customer or serial'), { target: { value: 'zzz' } });
    fireEvent.click(await screen.findByRole('button', { name: 'Clear the search' }));

    await screen.findByText('Nothing in "Sent"');
    expect(screen.getByPlaceholderText('Search by quote, RFQ, customer or serial')).toHaveValue('');
    expect(address()).toBe('/sales/quotes?state=sent');
  });

  it('shows loading, not the previous tab\'s rows, while a new tab loads', async () => {
    renderQuotes('/sales/quotes');

    await screen.findByText('QT-2026-0011');
    getAll.mockReturnValue(new Promise(() => {}));
    fireEvent.click(tab('Drafts'));

    await waitFor(() => expect(document.querySelector('.MuiDataGrid-overlay, [role="progressbar"]')).not.toBeNull());
  });
});

describe('a quote made outside Nexora and uploaded', { timeout: 30000 }, () => {
  const uploaded = {
    ...sent, id: 31, quoteNo: 'QT-0926-0007', externalQuoteReference: 'QT-EXCEL-77',
    uploadedFileName: 'Quote QT-EXCEL-77.pdf',
  };

  beforeEach(() => {
    rows(uploaded);
  });

  it('says it was uploaded, with the number on the file', async () => {
    renderQuotes('/sales/quotes');

    expect(await screen.findByText('Uploaded · QT-EXCEL-77')).toBeInTheDocument();
  });

  it('downloads the file the customer got, not a Nexora PDF, and is not emailed or revised', async () => {
    renderQuotes('/sales/quotes');
    await screen.findByText('QT-0926-0007');
    toSpreadsheet();

    fireEvent.click(await screen.findByRole('button', { name: 'More for QT-0926-0007' }));
    expect(screen.queryByRole('menuitem', { name: 'Send again' })).not.toBeInTheDocument();
    expect(screen.queryByRole('menuitem', { name: 'Make a revision' })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Download file' }));

    await waitFor(() => expect(downloadUploadedFile).toHaveBeenCalledWith(31, 'Quote QT-EXCEL-77.pdf'));
    expect(downloadPdf).not.toHaveBeenCalled();
  });
});

// The Spreadsheet view draws every field (about 40 columns); jsdom needs longer than the default.
describe('the Spreadsheet view ⋮ offers only what the server allows', { timeout: 30000 }, () => {
  const menuFor = async (quoteNo: string) => {
    await screen.findByText(quoteNo);
    toSpreadsheet();
    fireEvent.click(await screen.findByRole('button', { name: `More for ${quoteNo}` }));
    return within(await screen.findByRole('menu'));
  };

  it('a sent quote Nexora made: Download, Send again, Make a revision', async () => {
    renderQuotes('/sales/quotes');
    const menu = await menuFor('QT-2026-0011');

    expect(menu.getByRole('menuitem', { name: 'Download PDF' })).toBeInTheDocument();
    expect(menu.getByRole('menuitem', { name: 'Send again' })).toBeInTheDocument();
    expect(menu.getByRole('menuitem', { name: 'Make a revision' })).toBeInTheDocument();
  });

  it('a replaced quote: Download only', async () => {
    rows({ ...sent, supersededByQuoteNo: 'QT-2026-0011-R2' });
    renderQuotes('/sales/quotes');
    const menu = await menuFor('QT-2026-0011');

    expect(menu.getByRole('menuitem', { name: 'Download PDF' })).toBeInTheDocument();
    expect(menu.queryByRole('menuitem', { name: 'Send again' })).not.toBeInTheDocument();
    expect(menu.queryByRole('menuitem', { name: 'Make a revision' })).not.toBeInTheDocument();
  });

  it('a won or lost quote: Download only (its chain is closed)', async () => {
    rows({ ...sent, statusCode: 'ACCEPTED', statusValue: 'Accepted' });
    renderQuotes('/sales/quotes');
    const menu = await menuFor('QT-2026-0011');

    expect(menu.getByRole('menuitem', { name: 'Download PDF' })).toBeInTheDocument();
    expect(menu.queryByRole('menuitem', { name: 'Send again' })).not.toBeInTheDocument();
    expect(menu.queryByRole('menuitem', { name: 'Make a revision' })).not.toBeInTheDocument();
  });

  it('keeps the row verb beside the ⋮, and every export field as a column', async () => {
    renderQuotes('/sales/quotes');
    await screen.findByText('QT-2026-0011');
    toSpreadsheet();

    expect(await screen.findByRole('button', { name: 'Update status QT-2026-0011' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Nexora Serial' })).toBeInTheDocument();
  });
});

describe('a quote a sent revision replaced', () => {
  it('reads "Replaced" by its revision, never Sent or No reply', async () => {
    rows(
      { ...sent, id: 21, quoteNo: 'QT-0926-0003', isStale: true, daysSinceSent: 12, supersededByQuoteNo: 'QT-0926-0003-R2' },
      { ...sent, id: 22, quoteNo: 'QT-0926-0003-R2', daysSinceSent: 1 },
    );
    renderQuotes('/sales/quotes');

    expect(await screen.findByText('By QT-0926-0003-R2')).toBeInTheDocument();
    expect(screen.getByText('Replaced')).toBeInTheDocument();
    expect(screen.queryByText('No reply')).not.toBeInTheDocument();
    expect(screen.getByText('1 day ago')).toBeInTheDocument();
  });
});
