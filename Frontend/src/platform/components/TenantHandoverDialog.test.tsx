import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import TenantHandoverDialog from './TenantHandoverDialog';
import type { ProvisionTenantResult } from '../types';

const writeText = vi.fn<(value: string) => Promise<void>>();

beforeEach(() => {
  writeText.mockReset();
  writeText.mockResolvedValue(undefined);
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
});

const result = (overrides: Partial<ProvisionTenantResult> = {}): ProvisionTenantResult => ({
  tenant: {
    id: '7', name: 'Acme Trading', slug: 'acme-trading', planId: '3', planCode: 'pro',
    status: 'active', statusReason: null, createdAt: null,
    legalName: null, registrationNumber: null, taxNumber: null, countryCode: 'AE', industry: null,
    website: null, addressLine1: null, addressLine2: null, city: null, stateProvince: null,
    postalCode: null, phone: null, contactEmail: null, logoUrl: null,
    billingMode: 'Billable', billingModeReason: null, rateCardId: null, billingStartsOn: null,
    trialEndsOn: null, contractStartOn: null, contractEndOn: null, paymentTermsDays: null,
    purchaseOrderReference: null, billingContactName: null, billingContactEmail: null,
    billingAddress: null, accountOwnerEmail: null, baseCurrencyCode: 'AED', timeZoneId: null,
    locale: null, dataRegion: null,
    deploymentProfile: 'PRODUCTION', deploymentProfileReason: null,
    deploymentProfileApprovedBy: null, deploymentProfileApprovedOn: null,
  },
  foundingAdmin: {
    userId: '11', email: 'sam@acme.example', roleName: 'Super Administrator',
    generatedPassword: null, invitation: null,
  },
  baseline: {
    quoteConfiguration: true, baseCurrency: 'AED', unitsOfMeasure: 12, roles: 5,
    permissionGrants: 84, leadReferencePrefix: 'ACME',
  },
  billing: { mode: 'Billable', planCode: 'pro', rateCardCode: null, billingStartsOn: null, warnings: [] },
  ...overrides,
});

describe('credential handover', () => {
  it('holds the operator on the screen until the one-time password is copied', async () => {
    const onClose = vi.fn();
    const payload = result();
    payload.foundingAdmin.generatedPassword = 'Zt7-quiet-harbour-42';

    render(<TenantHandoverDialog result={payload} onClose={onClose} />);

    // The credential exists in this response and nowhere else, so dismissing
    // before copying it would lose it permanently.
    const confirm = screen.getByRole('button', { name: /copy the password first/i });
    expect(confirm).toBeDisabled();

    fireEvent.click(screen.getByRole('button', { name: /^copy$/i }));

    expect(writeText).toHaveBeenCalledWith('Zt7-quiet-harbour-42');
    await waitFor(() => expect(screen.getByRole('button', { name: /done/i })).toBeEnabled());
    fireEvent.click(screen.getByRole('button', { name: /done/i }));
    expect(onClose).toHaveBeenCalled();
  });

  it('announces that the copy succeeded', async () => {
    const payload = result();
    payload.foundingAdmin.generatedPassword = 'Zt7-quiet-harbour-42';

    render(<TenantHandoverDialog result={payload} onClose={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: /^copy$/i }));

    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/password copied/i));
  });

  it('shows the activation link, its expiry and the absence of a password when the mail did NOT go out', () => {
    const payload = result();
    payload.foundingAdmin.invitation = {
      expiresAtUtc: '2026-08-13T09:00:00Z',
      // The server serves the link ONLY on this branch — a live activation link is a
      // bearer credential and is withheld once the invitation has been emailed.
      activationUrl: 'https://app.example/activate/abc123',
      emailSent: false,
    };

    render(<TenantHandoverDialog result={payload} onClose={vi.fn()} />);

    expect(screen.getByText('https://app.example/activate/abc123')).toBeInTheDocument();
    expect(screen.getByText(/no password exists yet/i)).toBeInTheDocument();
    expect(screen.getByText(/this link is the only copy/i)).toBeInTheDocument();
    // Nothing irrecoverable is on screen, so the operator is not held hostage.
    expect(screen.getByRole('button', { name: /done/i })).toBeEnabled();
  });

  it('does not render an empty link box when the invitation was emailed', () => {
    const payload = result();
    // The ordinary path: mail accepted, so the server withholds activationUrl. The
    // screen used to render an "Activation link" heading over an empty monospace box
    // and a Copy button that copied `undefined`, under a caption promising a link.
    payload.foundingAdmin.invitation = {
      expiresAtUtc: '2026-08-13T09:00:00Z',
      activationUrl: null,
      emailSent: true,
    };

    render(<TenantHandoverDialog result={payload} onClose={vi.fn()} />);

    expect(screen.queryByText(/^activation link$/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /^copy$/i })).not.toBeInTheDocument();
    expect(screen.getByText(/has been emailed to them/i)).toBeInTheDocument();
  });

  it('renders the server billing warnings rather than burying them', () => {
    const payload = result();
    payload.billing = {
      mode: 'Internal', planCode: null, rateCardCode: null, billingStartsOn: null,
      warnings: ['No rate card attached — metered usage will not be billed.'],
    };

    render(<TenantHandoverDialog result={payload} onClose={vi.fn()} />);

    expect(screen.getByText(/metered usage will not be billed/i)).toBeInTheDocument();
  });

  it('reports the seeded baseline as a readiness checklist', () => {
    render(<TenantHandoverDialog result={result()} onClose={vi.fn()} />);

    expect(screen.getByText('Quote template')).toBeInTheDocument();
    expect(screen.getByText('12 seeded')).toBeInTheDocument();
    expect(screen.getByText('84 applied')).toBeInTheDocument();
  });

  it('says so when the server reported no baseline, rather than claiming success', () => {
    render(<TenantHandoverDialog result={result({ baseline: null })} onClose={vi.fn()} />);

    expect(screen.getByText(/has not been confirmed usable/i)).toBeInTheDocument();
  });
});
