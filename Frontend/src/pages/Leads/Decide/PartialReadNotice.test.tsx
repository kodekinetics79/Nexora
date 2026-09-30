import { fireEvent, render, screen, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const getReadCompleteness = vi.fn();
vi.mock('../../../api/services/leadReadCompletenessService', () => ({
  default: { get: (...args: unknown[]) => getReadCompleteness(...args) },
}));

import PartialReadNotice, { partialReadSentence } from './PartialReadNotice';
import CreateRfqConfirmDialog from './CreateRfqConfirmDialog';

const client = () => new QueryClient({ defaultOptions: { queries: { retry: false } } });

/**
 * P0 #6 / X1. A 42-line Aramco print came back as 3 clean-looking lines and Decide said nothing:
 * a rep would have quoted 3 of 42. When part of the document was not read, Decide says so in one
 * line, and Create RFQ asks once before it goes ahead — inform, never block.
 */
describe('PartialReadNotice', () => {
  beforeEach(() => getReadCompleteness.mockReset());

  it('says how much of the document was read, in one short line', async () => {
    getReadCompleteness.mockResolvedValue({ partial: true, linesRead: 3, linesExpected: 42, partsUnread: 28 });
    render(<QueryClientProvider client={client()}><PartialReadNotice leadId={14} /></QueryClientProvider>);

    const notice = await screen.findByTestId('partial-read-notice');
    expect(notice).toHaveTextContent('Read 3 of ~42 lines — check the document');
    expect(getReadCompleteness).toHaveBeenCalledWith(14);
  });

  it('says nothing when the whole document was read', async () => {
    getReadCompleteness.mockResolvedValue({ partial: false, linesRead: null, linesExpected: null, partsUnread: 0 });
    render(<QueryClientProvider client={client()}><PartialReadNotice leadId={15} /></QueryClientProvider>);

    await vi.waitFor(() => expect(getReadCompleteness).toHaveBeenCalledWith(15));
    expect(screen.queryByTestId('partial-read-notice')).toBeNull();
  });

  it('still warns when the document does not show how many lines it holds', () => {
    expect(partialReadSentence({ linesRead: 2, linesExpected: null })).toBe('Part of this document was not read — check the document');
    expect(partialReadSentence({ linesRead: 0, linesExpected: 1 })).toBe('Read 0 of ~1 line — check the document');
  });
});

describe('Create RFQ on a partly read document', () => {
  beforeEach(() => getReadCompleteness.mockReset());

  const renderDialog = (onConfirm = vi.fn()) => {
    render(
      <QueryClientProvider client={client()}>
        <MemoryRouter initialEntries={['/procurement/leads/14/decide']}>
          <Routes>
            <Route
              path="/procurement/leads/:id/decide"
              element={(
                <CreateRfqConfirmDialog
                  open
                  customer="Saudi Aramco"
                  bidCount={3}
                  lineCount={3}
                  qualification="transition"
                  onCancel={vi.fn()}
                  onConfirm={onConfirm}
                />
              )}
            />
          </Routes>
        </MemoryRouter>
      </QueryClientProvider>,
    );
    return { onConfirm, dialog: screen.getByRole('dialog', { name: 'Create an RFQ for Saudi Aramco?' }) };
  };

  it('asks once, naming the partial read, and one click goes ahead', async () => {
    getReadCompleteness.mockResolvedValue({ partial: true, linesRead: 3, linesExpected: 42, partsUnread: 28 });
    const { dialog, onConfirm } = renderDialog();

    expect(await within(dialog).findByTestId('create-rfq-partial-read'))
      .toHaveTextContent('Read 3 of ~42 lines — check the document.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Yes, create the RFQ anyway' }));
    expect(onConfirm).toHaveBeenCalledOnce();
    expect(getReadCompleteness).toHaveBeenCalledWith(14);
  });

  it('asks the ordinary question when the whole document was read', async () => {
    getReadCompleteness.mockResolvedValue({ partial: false, linesRead: null, linesExpected: null, partsUnread: 0 });
    const { dialog } = renderDialog();

    await vi.waitFor(() => expect(getReadCompleteness).toHaveBeenCalledWith(14));
    expect(within(dialog).queryByTestId('create-rfq-partial-read')).toBeNull();
    expect(within(dialog).getByRole('button', { name: 'Yes, create the RFQ' })).toBeInTheDocument();
  });
});
