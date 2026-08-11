import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlatformMfaPolicy } from '../types';

const { getMfaPolicy, changeMfaPolicy, getEffectiveMfaPolicy } = vi.hoisted(() => ({
  getMfaPolicy: vi.fn(),
  changeMfaPolicy: vi.fn(),
  getEffectiveMfaPolicy: vi.fn(),
}));

vi.mock('../api/client', () => ({
  platformApi: { getMfaPolicy, changeMfaPolicy, getEffectiveMfaPolicy },
}));

// Role gating itself is covered by permissions.test.ts; this file is about what the screen
// SAYS, so it runs as an Owner throughout.
vi.mock('../auth/usePlatformPermissions', () => ({
  usePlatformPermissions: () => ({
    role: 'Owner',
    isOwner: true,
    canAdministerTenants: true,
    canAdministerBilling: true,
    canImpersonate: true,
    roleUnknown: false,
  }),
}));

import PlatformAuthenticationPage from './PlatformAuthenticationPage';
import PlatformMfaEnforcementBanner, {
  MFA_DISABLED_BANNER_TEXT,
  MFA_DISABLED_BANNER_TEST_ID,
} from '../components/PlatformMfaEnforcementBanner';

const LOCAL_POLICY: PlatformMfaPolicy = {
  mode: 'REQUIRED',
  declaredMode: 'REQUIRED',
  environmentClass: 'LocalOrTest',
  environmentName: 'Development',
  isolatedTestInfrastructure: false,
  effectiveFromUtc: '2026-08-10T09:00:00Z',
  expiresAtUtc: null,
  bypassExpired: false,
  enforcementDisabled: false,
  passwordOnlySessionsPermitted: false,
  changedBy: 'owner@nexora.test',
  changeReason: 'Restored after the cutover rehearsal.',
  version: 3,
  updatedAtUtc: '2026-08-10T09:00:00Z',
  enrolledOperatorCount: 4,
  activeOperatorCount: 5,
  activeMfaBoundSessionCount: 2,
  activeBrowserTrustCount: 1,
  availableModes: ['REQUIRED', 'OPTIONAL', 'DISABLED_TEST_ONLY'],
  confirmationPhrases: {
    REQUIRED: 'RESTORE PLATFORM MFA',
    OPTIONAL: 'MAKE PLATFORM MFA OPTIONAL',
    DISABLED_TEST_ONLY: 'DISABLE PLATFORM MFA IN TEST',
  },
  maxBypassHours: 24,
  minimumReasonLength: 20,
  browserTrustHours: 12,
};

const PRODUCTION_POLICY: PlatformMfaPolicy = {
  ...LOCAL_POLICY,
  environmentClass: 'Production',
  environmentName: 'Production',
  availableModes: ['REQUIRED'],
  confirmationPhrases: { REQUIRED: 'RESTORE PLATFORM MFA' },
};

/**
 * A value for a `datetime-local` input, built from LOCAL clock components.
 *
 * Deriving it from `toISOString()` would be a UTC instant pasted into a control the browser reads
 * as wall-clock time, so the test would silently mean something different in every timezone — and
 * would fail outright anywhere more than two hours ahead of UTC.
 */
const localDateTimeInput = (msFromNow: number): string => {
  const at = new Date(Date.now() + msFromNow);
  const pad = (value: number) => String(value).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}T${pad(at.getHours())}:${pad(at.getMinutes())}`;
};

const renderPage = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <SnackbarProvider>
          <PlatformAuthenticationPage />
        </SnackbarProvider>
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

const renderBanner = () => {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={client}>
        <PlatformMfaEnforcementBanner />
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

describe('PlatformAuthenticationPage', () => {
  beforeEach(() => {
    getMfaPolicy.mockReset().mockResolvedValue(LOCAL_POLICY);
    changeMfaPolicy.mockReset().mockResolvedValue({ ...LOCAL_POLICY, mode: 'DISABLED_TEST_ONLY' });
    getEffectiveMfaPolicy.mockReset().mockResolvedValue(LOCAL_POLICY);
  });

  it('shows the environment, the mode, who changed it and how many operators it affects', async () => {
    renderPage();

    expect(await screen.findByText(/MFA is required for every platform operator/i)).toBeInTheDocument();
    expect(screen.getByText('Environment: Development')).toBeInTheDocument();
    expect(screen.getByText('Class: LocalOrTest')).toBeInTheDocument();
    expect(screen.getByText('4 enrolled of 5 active')).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
    expect(screen.getAllByText('owner@nexora.test').length).toBeGreaterThan(0);
  });

  it('does not render the disable option in production, and says why instead of showing an empty control', async () => {
    getMfaPolicy.mockResolvedValue(PRODUCTION_POLICY);
    renderPage();

    expect(await screen.findByTestId('platform-mfa-not-relaxable')).toBeInTheDocument();
    // The control itself is absent — not present-and-disabled, which would still advertise that
    // disabling is a thing this deployment can do.
    expect(screen.queryByLabelText(/MFA enforcement mode/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /apply mfa policy/i })).not.toBeInTheDocument();
    expect(screen.getByText(/not reachable in production by any route/i)).toBeInTheDocument();
  });

  it('names every unmet precondition on screen instead of just disabling Apply', async () => {
    renderPage();
    await screen.findByLabelText(/MFA enforcement mode/i);

    fireEvent.change(screen.getByLabelText(/MFA enforcement mode/i), {
      target: { value: 'DISABLED_TEST_ONLY' },
    });

    const apply = screen.getByRole('button', { name: /apply mfa policy/i });
    expect(apply).toBeDisabled();

    // Asserted on the caption specifically, not on any text in the document: the field helpers
    // legitimately repeat some of this copy, and a loose query would pass on the helper while the
    // caption — the thing that appears next to the disabled button — was missing entirely.
    const blocked = () => screen.getByTestId('platform-mfa-apply-blocked');
    expect(blocked()).toHaveTextContent(/Give a reason of at least 20 characters/i);

    fireEvent.change(screen.getByLabelText(/^Reason/i), {
      target: { value: 'Rehearsing the ZATCA cutover on the isolated staging rig this afternoon.' },
    });
    await waitFor(() => expect(blocked()).toHaveTextContent(/An expiry is required/i));

    fireEvent.change(screen.getByLabelText(/Expires at/i), {
      target: { value: localDateTimeInput(2 * 3600_000) },
    });
    await waitFor(() => expect(blocked()).toHaveTextContent(/Type DISABLE PLATFORM MFA IN TEST exactly/i));

    fireEvent.change(screen.getByLabelText(/Confirmation phrase/i), {
      target: { value: 'DISABLE PLATFORM MFA IN TEST' },
    });
    await waitFor(() => expect(blocked()).toHaveTextContent(/Re-enter your current platform password/i));

    fireEvent.change(screen.getByLabelText(/Current password/i), { target: { value: 'hunter2' } });
    await waitFor(() => expect(screen.getByRole('button', { name: /apply mfa policy/i })).toBeEnabled());
    expect(screen.queryByTestId('platform-mfa-apply-blocked')).not.toBeInTheDocument();
  });

  it('sends the mode, reason, expiry, confirmation and password the server demands', async () => {
    renderPage();
    await screen.findByLabelText(/MFA enforcement mode/i);

    fireEvent.change(screen.getByLabelText(/MFA enforcement mode/i), {
      target: { value: 'DISABLED_TEST_ONLY' },
    });
    fireEvent.change(screen.getByLabelText(/^Reason/i), {
      target: { value: 'Rehearsing the ZATCA cutover on the isolated staging rig this afternoon.' },
    });
    fireEvent.change(screen.getByLabelText(/Expires at/i), {
      target: { value: localDateTimeInput(2 * 3600_000) },
    });
    fireEvent.change(screen.getByLabelText(/Confirmation phrase/i), {
      target: { value: 'DISABLE PLATFORM MFA IN TEST' },
    });
    fireEvent.change(screen.getByLabelText(/Current password/i), { target: { value: 'hunter2' } });

    fireEvent.click(screen.getByRole('button', { name: /apply mfa policy/i }));

    await waitFor(() => expect(changeMfaPolicy).toHaveBeenCalledTimes(1));
    const sent = changeMfaPolicy.mock.calls[0][0];
    expect(sent.mode).toBe('DISABLED_TEST_ONLY');
    expect(sent.confirmation).toBe('DISABLE PLATFORM MFA IN TEST');
    expect(sent.currentPassword).toBe('hunter2');
    expect(sent.expiresAtUtc).toBeTruthy();
    expect(sent.expectedVersion).toBe(3);
  });

  it('explains that an expired bypass restored REQUIRED on its own', async () => {
    getMfaPolicy.mockResolvedValue({
      ...LOCAL_POLICY,
      mode: 'REQUIRED',
      declaredMode: 'DISABLED_TEST_ONLY',
      bypassExpired: true,
      expiresAtUtc: '2026-08-10T13:00:00Z',
    });
    renderPage();

    expect(await screen.findByText(/expired at .* and enforcement\s+returned to REQUIRED on its own/is))
      .toBeInTheDocument();
  });
});

describe('PlatformMfaEnforcementBanner', () => {
  beforeEach(() => {
    getEffectiveMfaPolicy.mockReset();
  });

  it('renders the persistent warning verbatim while enforcement is disabled', async () => {
    getEffectiveMfaPolicy.mockResolvedValue({
      ...LOCAL_POLICY,
      mode: 'DISABLED_TEST_ONLY',
      declaredMode: 'DISABLED_TEST_ONLY',
      enforcementDisabled: true,
      passwordOnlySessionsPermitted: true,
      expiresAtUtc: '2026-08-10T21:00:00Z',
    });

    renderBanner();

    const banner = await screen.findByTestId(MFA_DISABLED_BANNER_TEST_ID);
    expect(banner).toHaveTextContent(MFA_DISABLED_BANNER_TEXT);
    expect(banner).toHaveAttribute('role', 'alert');
  });

  it('renders nothing at all while enforcement is on', async () => {
    getEffectiveMfaPolicy.mockResolvedValue(LOCAL_POLICY);

    renderBanner();

    await waitFor(() => expect(getEffectiveMfaPolicy).toHaveBeenCalled());
    expect(screen.queryByTestId(MFA_DISABLED_BANNER_TEST_ID)).not.toBeInTheDocument();
  });

  // The banner is driven by the SERVER's answer, so a failed or unavailable read must not
  // invent an "all clear" — but it also must not manufacture a warning it cannot support.
  it('stays silent when the policy cannot be read, rather than guessing', async () => {
    getEffectiveMfaPolicy.mockRejectedValue(new Error('offline'));

    renderBanner();

    await waitFor(() => expect(getEffectiveMfaPolicy).toHaveBeenCalled());
    expect(screen.queryByTestId(MFA_DISABLED_BANNER_TEST_ID)).not.toBeInTheDocument();
  });
});
