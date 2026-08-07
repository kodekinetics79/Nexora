import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { describe, expect, it, vi } from 'vitest';

// The wizard reads plans, rate cards, saved drafts and slug availability on open. None of
// that is under test here — the question is whether an invalid step can be left behind —
// so the client is reduced to empty answers.
vi.mock('../api/client', () => ({
  platformApi: {
    listPlans: vi.fn().mockResolvedValue([]),
    listRateCards: vi.fn().mockResolvedValue([]),
    listProvisioningDrafts: vi.fn().mockResolvedValue([]),
    checkSlug: vi.fn().mockResolvedValue({ slug: null, isAvailable: true, reason: 'None', message: null }),
    submitProvisioning: vi.fn(),
  },
  toProvisionRequestBody: vi.fn(() => ({})),
}));

import ProvisionTenantWizard from './ProvisionTenantWizard';

function renderWizard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <SnackbarProvider>
        <ProvisionTenantWizard open onClose={vi.fn()} onSubmitted={vi.fn()} />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
}

describe('provisioning wizard', () => {
  it('opens on company identity and asks for far more than a name and a slug', async () => {
    renderWizard();

    expect(await screen.findByRole('heading', { name: /who is this company\?/i })).toBeInTheDocument();
    expect(screen.getByLabelText(/registered legal name/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/company contact email/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/country of registration/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/address line 1/i)).toBeInTheDocument();
  });

  it('refuses to advance past an incomplete step and says how many fields are wrong', async () => {
    renderWizard();

    fireEvent.click(await screen.findByRole('button', { name: /next/i }));

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent(/need attention/i));
    // Still on step one: the commercial step's heading never appears.
    expect(screen.queryByRole('heading', { name: /how does this tenant pay\?/i })).not.toBeInTheDocument();
  });

  it('mirrors the organization name into the slug and legal name until they are edited', async () => {
    renderWizard();

    const name = await screen.findByLabelText(/organization name/i);
    fireEvent.change(name, { target: { value: 'Acme Trading' } });

    expect(screen.getByLabelText(/workspace slug/i)).toHaveValue('acme-trading');
    expect(screen.getByLabelText(/registered legal name/i)).toHaveValue('Acme Trading');

    fireEvent.change(screen.getByLabelText(/workspace slug/i), { target: { value: 'acme-uae' } });
    fireEvent.change(name, { target: { value: 'Acme Trading LLC' } });

    expect(screen.getByLabelText(/workspace slug/i)).toHaveValue('acme-uae');
  });

  it('exposes every step as a focusable control, so the wizard is navigable without a mouse', async () => {
    renderWizard();

    // A non-linear MUI Stepper publishes itself as a tablist of real <button>
    // elements — keyboard-reachable, and each one announcing its own position.
    await screen.findByLabelText(/organization name/i);
    const steps = screen.getAllByRole('tab');
    expect(steps.map((step) => step.textContent)).toEqual([
      '1Company identity',
      '2Commercial terms',
      '3Founding administrator',
      '4Review & provision',
    ]);
    expect(steps.every((step) => step.tagName === 'BUTTON')).toBe(true);
    expect(steps[0]).toHaveAttribute('aria-selected', 'true');
  });
});
