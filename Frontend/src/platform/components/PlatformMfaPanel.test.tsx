import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import * as mfa from '../auth/platformMfa';

const { platformLogout } = vi.hoisted(() => ({ platformLogout: vi.fn() }));
vi.mock('../auth/usePlatformAuth', () => ({
  usePlatformAuth: () => ({ platformLogout }),
}));

import PlatformMfaPanel from './PlatformMfaPanel';

const renderPanel = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <SnackbarProvider><PlatformMfaPanel /></SnackbarProvider>
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.restoreAllMocks();
  platformLogout.mockReset().mockResolvedValue(undefined);
});

describe('PlatformMfaPanel enrollment', () => {
  it('shows recovery codes once and requires acknowledgement before sign-out', async () => {
    vi.spyOn(mfa, 'getPlatformMfaStatus').mockResolvedValue({ enabled: false, enabledAtUtc: null, recoveryCodesRemaining: 0 });
    vi.spyOn(mfa, 'beginPlatformMfaEnrollment').mockResolvedValue({ secret: 'BASE32SECRET', otpAuthUri: 'otpauth://totp/Nexora' });
    vi.spyOn(mfa, 'confirmPlatformMfaEnrollment').mockResolvedValue({
      enabledAtUtc: '2026-08-08T12:00:00Z',
      recoveryCodes: ['AAAAAA-BBBBBB-CCCCCC-DDDDDD', 'EEEEEE-FFFFFF-GGGGGG-HHHHHH'],
    });
    renderPanel();

    fireEvent.click(await screen.findByRole('button', { name: 'Set up authenticator' }));
    expect(await screen.findByText('BASE32SECRET')).toBeVisible();
    fireEvent.change(screen.getByLabelText(/6-digit authenticator code/), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enable MFA' }));

    expect(await screen.findByText(/AAAAAA-BBBBBB/)).toBeVisible();
    const continueButton = screen.getByRole('button', { name: 'Sign out and continue' });
    expect(continueButton).toBeDisabled();
    fireEvent.click(screen.getByLabelText(/I saved these recovery codes/i));
    fireEvent.click(continueButton);
    await waitFor(() => expect(platformLogout).toHaveBeenCalledOnce());
  });
});
