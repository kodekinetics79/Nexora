import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { QuoteStatusCatalog } from '../../../api/services/quoteService';

/**
 * Setup › Quote statuses. Three plain lists over Nexora's fixed quote lifecycle: the customer's
 * steps, how a quote ended (grouped under the locked Won / Lost / Expired), and the reasons.
 * Every change is sent and the catalog read again; a refusal shows the server's sentence.
 */

const mocks = vi.hoisted(() => ({
  getStatusCatalog: vi.fn(),
  addStatusOption: vi.fn(),
  updateStatusOption: vi.fn(),
  canEdit: true,
  isManager: true,
}));

vi.mock('../../../api/services/quoteService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/quoteService')>();
  return {
    ...actual,
    default: {
      getStatusCatalog: mocks.getStatusCatalog,
      addStatusOption: mocks.addStatusOption,
      updateStatusOption: mocks.updateStatusOption,
    },
  };
});

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    // Status changes are for managers and administrators (the server's [RequireManagerRole]).
    userData: { id: 3, businessUnitId: 7, userName: 'Admin', isManager: mocks.isManager },
    hasPermission: (moduleName: string, action?: string) =>
      moduleName === 'Quotations' && (action !== 'edit' || mocks.canEdit),
  }),
}));

import QuoteStatusesPage from './QuoteStatusesPage';

const catalog = (): QuoteStatusCatalog => ({
  steps: [
    { id: 1, name: 'Technical evaluation', sortOrder: 10, isActive: true, isSystem: false },
    { id: 2, name: 'Clarification asked', sortOrder: 20, isActive: true, isSystem: false },
    { id: 3, name: 'Negotiation / BAFO', sortOrder: 30, isActive: false, isSystem: false },
  ],
  endings: [
    { id: 11, name: 'Partly won', sortOrder: 10, isActive: true, isSystem: false, countsAs: 'WON' },
    { id: 12, name: 'Tender cancelled', sortOrder: 10, isActive: true, isSystem: false, countsAs: 'LOST' },
  ],
  reasons: [
    { id: 21, name: 'Best price', code: 'BEST_PRICE', sortOrder: 10, isActive: true, isSystem: false, for: 'WON' },
    { id: 22, name: 'Price too high', code: 'PRICE', sortOrder: 20, isActive: true, isSystem: false, for: null },
    { id: 23, name: 'Expired automatically', code: 'AUTO_EXPIRED', sortOrder: 0, isActive: true, isSystem: true, for: 'EXPIRED' },
  ],
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <QuoteStatusesPage />
    </QueryClientProvider>,
  );
}

const card = (title: string) => screen.getByRole('region', { name: title });
const refusal = (message: string) => ({ isAxiosError: true, response: { status: 400, data: { message } } });

beforeEach(() => {
  vi.clearAllMocks();
  mocks.canEdit = true;
  mocks.isManager = true;
  mocks.getStatusCatalog.mockResolvedValue(catalog());
  mocks.addStatusOption.mockResolvedValue({ id: 99, name: 'x', sortOrder: 0, isActive: true, isSystem: false });
  mocks.updateStatusOption.mockResolvedValue({ id: 1, name: 'x', sortOrder: 0, isActive: true, isSystem: false });
});

describe('Quote statuses', () => {
  it('reads every row, active or not, and renders the three cards', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    expect(mocks.getStatusCatalog).toHaveBeenCalledWith(true);

    const steps = card("Customer's steps");
    expect(within(steps).getByText('Technical evaluation')).toBeInTheDocument();
    expect(within(steps).getByText('Negotiation / BAFO')).toBeInTheDocument();
    expect(within(steps).getByRole('checkbox', { name: 'Negotiation / BAFO active' })).not.toBeChecked();
    expect(within(steps).getByRole('checkbox', { name: 'Technical evaluation active' })).toBeChecked();

    const ended = card('How it ended');
    const won = within(ended).getByRole('group', { name: 'Won' });
    const lost = within(ended).getByRole('group', { name: 'Lost' });
    const expired = within(ended).getByRole('group', { name: 'Expired' });
    expect(within(won).getByText('Partly won')).toBeInTheDocument();
    expect(within(lost).getByText('Tender cancelled')).toBeInTheDocument();
    expect(within(won).queryByText('Tender cancelled')).not.toBeInTheDocument();
    expect(within(expired).getAllByText('Nexora')).toHaveLength(1);

    const reasons = card('Reasons');
    expect(within(reasons).getByText('Best price')).toBeInTheDocument();
    expect(within(reasons).getByText('Expired automatically')).toBeInTheDocument();
  });

  it('adds a step and reads the catalog again', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(within(card("Customer's steps")).getByRole('button', { name: 'Add step' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add step' });
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Site visit' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(mocks.addStatusOption).toHaveBeenCalledWith('step', { name: 'Site visit' }));
    await waitFor(() => expect(mocks.getStatusCatalog).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('adds an ending only once "Counts as" is chosen, and sends it', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(within(card('How it ended')).getByRole('button', { name: 'Add ending' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add ending' });
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Awarded to another bidder' } });
    expect(within(dialog).getByRole('button', { name: 'Add' })).toBeDisabled();

    const countsAs = within(dialog).getByRole('radiogroup', { name: 'Counts as' });
    fireEvent.click(within(countsAs).getByRole('radio', { name: 'Lost' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));

    await waitFor(() =>
      expect(mocks.addStatusOption).toHaveBeenCalledWith('ending', { name: 'Awarded to another bidder', countsAs: 'LOST' }),
    );
  });

  it('adds a reason for Lost and Expired as an empty "For"', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(within(card('Reasons')).getByRole('button', { name: 'Add reason' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add reason' });
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'No budget' } });
    fireEvent.mouseDown(within(dialog).getByRole('combobox', { name: 'For' }));
    fireEvent.click(await screen.findByRole('option', { name: 'Lost and Expired' }));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(mocks.addStatusOption).toHaveBeenCalledWith('reason', { name: 'No budget', for: null }));
  });

  it('renames in place: click the name, Enter saves', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(screen.getByRole('button', { name: 'Rename Clarification asked' }));
    const input = screen.getByRole('textbox', { name: 'New name for Clarification asked' });
    fireEvent.change(input, { target: { value: 'Clarification requested' } });
    fireEvent.keyDown(input, { key: 'Enter' });

    await waitFor(() => expect(mocks.updateStatusOption).toHaveBeenCalledWith(2, { name: 'Clarification requested' }));
    expect(mocks.updateStatusOption).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(mocks.getStatusCatalog).toHaveBeenCalledTimes(2));
  });

  it('Escape puts the name back without saving', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(screen.getByRole('button', { name: 'Rename Partly won' }));
    const input = screen.getByRole('textbox', { name: 'New name for Partly won' });
    fireEvent.change(input, { target: { value: 'Something else' } });
    fireEvent.keyDown(input, { key: 'Escape' });

    expect(screen.getByRole('button', { name: 'Rename Partly won' })).toBeInTheDocument();
    expect(mocks.updateStatusOption).not.toHaveBeenCalled();
  });

  it('switches a row off instead of deleting it', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Tender cancelled active' }));
    await waitFor(() => expect(mocks.updateStatusOption).toHaveBeenCalledWith(12, { isActive: false }));
    expect(screen.queryByRole('button', { name: /delete/i })).not.toBeInTheDocument();
  });

  it('moves a step down by swapping its place with the next one', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    expect(screen.getByRole('button', { name: 'Move Technical evaluation up' })).toBeDisabled();
    fireEvent.click(screen.getByRole('button', { name: 'Move Technical evaluation down' }));
    await waitFor(() => expect(mocks.updateStatusOption).toHaveBeenCalledTimes(2));
    expect(mocks.updateStatusOption).toHaveBeenCalledWith(2, { sortOrder: 10 });
    expect(mocks.updateStatusOption).toHaveBeenCalledWith(1, { sortOrder: 20 });
  });

  it('changes what a reason is for', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.mouseDown(within(screen.getByTestId('status-row-22')).getByRole('combobox', { name: /: for$/ }));
    fireEvent.click(await screen.findByRole('option', { name: 'Lost' }));
    await waitFor(() => expect(mocks.updateStatusOption).toHaveBeenCalledWith(22, { for: 'LOST' }));
  });

  it('shows Won / Lost / Expired and the system reason locked, with nothing to change', async () => {
    renderPage();
    await screen.findByText('Technical evaluation');
    for (const code of ['WON', 'LOST', 'EXPIRED']) {
      const row = screen.getByTestId(`locked-outcome-${code}`);
      expect(within(row).getByText('Nexora')).toBeInTheDocument();
      expect(within(row).queryAllByRole('button')).toHaveLength(0);
      expect(within(row).queryAllByRole('checkbox')).toHaveLength(0);
    }
    const system = screen.getByTestId('status-row-23');
    expect(within(system).getByText('Expired automatically')).toBeInTheDocument();
    expect(within(system).getByText('Nexora')).toBeInTheDocument();
    expect(within(system).getByText('For Expired')).toBeInTheDocument();
    expect(within(system).queryAllByRole('button')).toHaveLength(0);
    expect(within(system).queryAllByRole('checkbox')).toHaveLength(0);
    expect(within(system).queryAllByRole('combobox')).toHaveLength(0);
    expect(screen.queryByRole('button', { name: 'Rename Expired automatically' })).not.toBeInTheDocument();
  });

  it("shows the server's refusal sentence and reads the catalog again", async () => {
    mocks.updateStatusOption.mockRejectedValueOnce(refusal('A step with this name already exists.'));
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(screen.getByRole('checkbox', { name: 'Technical evaluation active' }));

    expect(await screen.findByText('A step with this name already exists.')).toBeInTheDocument();
    await waitFor(() => expect(mocks.getStatusCatalog).toHaveBeenCalledTimes(2));
  });

  it("keeps the add window open with the server's refusal in it", async () => {
    mocks.addStatusOption.mockRejectedValueOnce(refusal('An ending needs to count as Won, Lost or Expired.'));
    renderPage();
    await screen.findByText('Technical evaluation');
    fireEvent.click(screen.getByRole('button', { name: 'Add step' }));
    const dialog = await screen.findByRole('dialog', { name: 'Add step' });
    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'Site visit' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Add' }));

    expect(await within(dialog).findByText('An ending needs to count as Won, Lost or Expired.')).toBeInTheDocument();
    expect(screen.getByRole('dialog', { name: 'Add step' })).toBeInTheDocument();
  });

  it('is read-only without edit rights', async () => {
    mocks.canEdit = false;
    renderPage();
    await screen.findByText('Technical evaluation');
    // The page is mounted and populated; the controls are what is missing.
    expect(card("Customer's steps")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add step' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rename Technical evaluation' })).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Technical evaluation active' })).toBeDisabled();
  });
  it('is read-only for a rep who is not a manager', async () => {
    mocks.isManager = false;
    renderPage();
    await screen.findByText('Technical evaluation');
    // The page is mounted and populated; the controls are what is missing.
    expect(card("Customer's steps")).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add step' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Rename Technical evaluation' })).not.toBeInTheDocument();
    expect(screen.getByRole('checkbox', { name: 'Technical evaluation active' })).toBeDisabled();
  });
});
