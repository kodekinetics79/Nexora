import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi, beforeEach } from 'vitest';

/**
 * Setup must not offer a door that is bolted shut.
 *
 * "Business Units" is the first card of the first group in the Setup catalogue, described as
 * "the trading entities that issue quotes and invoices" — so it is very often the first thing a
 * founding administrator opens after activating their account. It carried an always-enabled
 * "Create new" button that opened a five-field dialog whose Save called POST /api/BusinessUnit,
 * which is `return Forbid()` UNCONDITIONALLY: the governed platform control plane owns the tenant
 * lifecycle, and the permission check passes before the hardcoded refusal is even reached.
 *
 * The refusal is CORRECT and stays. The defect was silence. The dialog's own copy already carried
 * the right sentence — "managed by the platform control plane" — but bound to `selectedRecord`,
 * the EDIT path, so the one path that always failed said nothing at all. The admin's first thirty
 * seconds in the product were: click the most prominent card, click the only primary button, type
 * five fields, get a 403.
 */

const { getAll, updateTaxRegistration, create } = vi.hoisted(() => ({
  getAll: vi.fn(),
  updateTaxRegistration: vi.fn(),
  create: vi.fn(),
}));

vi.mock('../../../api/services/businessUnitService', () => ({
  default: { getAll, updateTaxRegistration, create },
}));

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (k: string, f?: string) => f ?? k }),
}));

import BusinessUnitPage from './BusinessUnitPage';

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <BusinessUnitPage />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  getAll.mockResolvedValue({
    items: [{
      id: 7, businessUnitCode: 'NAS', businessUnitName: 'Noor And Sons',
      description: 'Trading entity', taxRegistrationNumber: '300000000000003', isActive: true,
    }],
    totalItems: 1,
  });
});

describe('Setup › Business Units', () => {
  it('does not offer a create action the server refuses unconditionally', async () => {
    renderPage();
    await screen.findByText('Noor And Sons');

    expect(screen.queryByRole('button', { name: /create new/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /add new business unit/i })).not.toBeInTheDocument();
  });

  it('says who does provision a business unit, so the admin is not left guessing', async () => {
    renderPage();
    await screen.findByText('Noor And Sons');

    // The page must state the constraint itself — not leave it to a dialog the user can no
    // longer open, which is exactly where the sentence used to be stranded.
    expect(screen.getByText(/provisioned by the platform/i)).toBeInTheDocument();
  });

  it('still says what the admin CAN change here', async () => {
    renderPage();
    await screen.findByText('Noor And Sons');

    // Removing the create path must not turn the screen into a read-only wall with no purpose:
    // the VAT / tax registration number is genuinely tenant-editable and is why this screen exists.
    expect(screen.getByText(/tax\s*registration number/i)).toBeInTheDocument();
  });

  it('never calls the forbidden create endpoint', async () => {
    renderPage();
    await screen.findByText('Noor And Sons');
    await waitFor(() => expect(getAll).toHaveBeenCalled());

    expect(create).not.toHaveBeenCalled();
  });
});
