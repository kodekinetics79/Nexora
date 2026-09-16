import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * Setup › Supplier Email. The company standard for the words around a supplier RFQ email; the part
 * lines stay Nexora's. A field equal to the Nexora default is saved as null so it keeps following
 * the default.
 */

const mocks = vi.hoisted(() => ({
  getCompany: vi.fn(),
  saveCompany: vi.fn(),
  getBusinessUnit: vi.fn(),
  canEdit: true,
}));

vi.mock('../../../api/services/supplierEmailSettingsService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../api/services/supplierEmailSettingsService')>();
  return {
    ...actual,
    default: { getCompany: mocks.getCompany, saveCompany: mocks.saveCompany },
  };
});

vi.mock('../../../api/services/businessUnitService', () => ({
  default: { getById: mocks.getBusinessUnit },
}));

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { id: 3, businessUnitId: 7, userName: 'Admin' },
    hasPermission: (moduleName: string, action?: string) =>
      moduleName === 'Quote Configuration' && (action !== 'edit' || mocks.canEdit),
  }),
}));

import SupplierEmailPage from './SupplierEmailPage';

const DEFAULTS = {
  subject: 'Request for Quotation [RFQ number] from [Company name]',
  greeting: 'Dear [Supplier name],',
  opening: '[Company name] invites you to submit a quotation for the following request.',
  defaultMessage: 'Please submit your best pricing and lead times.',
  signOff: 'Kind regards,\n[Company name]',
};

const company = (overrides: Record<string, string | null> = {}) => ({
  subject: null,
  greeting: null,
  opening: null,
  defaultMessage: null,
  signOff: null,
  defaults: DEFAULTS,
  updatedBy: null,
  updatedOn: null,
  ...overrides,
});

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <SupplierEmailPage />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

const field = (label: string) => screen.getByLabelText(label) as HTMLInputElement | HTMLTextAreaElement;
const preview = () => screen.getByTestId('supplier-email-preview');

beforeEach(() => {
  vi.clearAllMocks();
  mocks.canEdit = true;
  mocks.getBusinessUnit.mockResolvedValue({ id: 7, businessUnitName: 'Noor And Sons' });
  mocks.saveCompany.mockImplementation(async (body) => company(body));
});

describe('Setup › Supplier Email', () => {
  it('shows the saved wording, and the Nexora default where nothing is saved', async () => {
    mocks.getCompany.mockResolvedValue(company({ greeting: 'Hello [Supplier name],', signOff: 'Thanks,\nAisha' }));
    renderPage();

    expect(await screen.findByLabelText('Greeting')).toHaveValue('Hello [Supplier name],');
    expect(field('Sign-off and signature')).toHaveValue('Thanks,\nAisha');
    expect(field('Subject line')).toHaveValue(DEFAULTS.subject);
    expect(field('Opening sentence')).toHaveValue(DEFAULTS.opening);
    expect(field('Default message')).toHaveValue(DEFAULTS.defaultMessage);
    expect(screen.getByRole('note', { name: 'The part details' })).toHaveTextContent(/cannot be changed/);
  });

  it('fills the placeholders in the preview as the admin types', async () => {
    mocks.getCompany.mockResolvedValue(company());
    renderPage();
    await screen.findByLabelText('Greeting');
    await waitFor(() => expect(within(preview()).getByTestId('preview-subject'))
      .toHaveTextContent('Request for Quotation SRFQ-0001-00000012 from Noor And Sons'));

    fireEvent.change(field('Greeting'), { target: { value: 'Salaam [Supplier name] team,' } });
    fireEvent.change(field('Default message'), { target: { value: 'Quote [RFQ number] by Thursday please.' } });

    expect(preview()).toHaveTextContent('Salaam Gulf Switchgear Trading Co. team,');
    expect(preview()).toHaveTextContent('Quote SRFQ-0001-00000012 by Thursday please.');
    expect(preview()).toHaveTextContent('Part no. LC1D95M7');
    expect(preview()).toHaveTextContent('Please reply to this email with your price, availability');
  });

  it('cannot save until something changed, and says why', async () => {
    mocks.getCompany.mockResolvedValue(company());
    renderPage();
    await screen.findByLabelText('Greeting');

    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.getByText(/Nothing to save yet/)).toBeInTheDocument();
  });

  it('saves typed wording, and null for any field still equal to its default', async () => {
    mocks.getCompany.mockResolvedValue(company({ opening: 'We would like a price for:' }));
    renderPage();
    await screen.findByLabelText('Greeting');

    fireEvent.change(field('Sign-off and signature'), { target: { value: 'Best,\nAisha Noor\nSales' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mocks.saveCompany).toHaveBeenCalled());
    expect(mocks.saveCompany.mock.calls[0][0]).toEqual({
      subject: null,
      greeting: null,
      opening: 'We would like a price for:',
      defaultMessage: null,
      signOff: 'Best,\nAisha Noor\nSales',
    });
  });

  it('resets to the Nexora defaults and saves them as nulls', async () => {
    mocks.getCompany.mockResolvedValue(company({ greeting: 'Hi,', signOff: 'Cheers' }));
    renderPage();
    await screen.findByLabelText('Greeting');

    fireEvent.click(screen.getByRole('button', { name: 'Reset to Nexora defaults' }));
    expect(field('Greeting')).toHaveValue(DEFAULTS.greeting);
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(mocks.saveCompany).toHaveBeenCalledWith({
      subject: null, greeting: null, opening: null, defaultMessage: null, signOff: null,
    }));
  });

  it('is read-only for someone who cannot change the quote format', async () => {
    mocks.canEdit = false;
    mocks.getCompany.mockResolvedValue(company());
    renderPage();
    await screen.findByLabelText('Greeting');

    expect(field('Greeting')).toBeDisabled();
    expect(field('Sign-off and signature')).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Save' })).not.toBeInTheDocument();
    expect(screen.getByText(/Only people allowed to change the quote format/)).toBeInTheDocument();
  });
});
