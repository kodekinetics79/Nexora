import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { platformLogin, platformCompleteMfa } = vi.hoisted(() => ({
  platformLogin: vi.fn(),
  platformCompleteMfa: vi.fn(),
}));

vi.mock('../auth/usePlatformAuth', () => ({
  usePlatformAuth: () => ({
    isPlatformAuthed: false,
    platformUser: null,
    platformLogin,
    platformCompleteMfa,
    platformLogout: vi.fn(),
  }),
}));

import PlatformLoginScreen from './PlatformLoginScreen';

beforeEach(() => {
  platformLogin.mockReset();
  platformCompleteMfa.mockReset().mockResolvedValue(undefined);
});

describe('PlatformLoginScreen MFA challenge', () => {
  it('does not treat a password-accepted MFA challenge as a broken login', async () => {
    platformLogin.mockResolvedValue({
      mfaRequired: true,
      challenge: { challengeId: 'challenge-1', expiresAtUtc: '2099-01-01T00:00:00Z', email: 'owner@nexora.local' },
    });
    render(<PlatformLoginScreen />);

    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: 'owner@nexora.local' } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enter Control Plane' }));

    expect(await screen.findByLabelText(/6-digit authenticator code/)).toBeVisible();
    expect(screen.getByText(/Password accepted/i)).toBeVisible();
    expect(screen.queryByText(/Unable to reach/i)).not.toBeInTheDocument();
  });

  it('submits exactly one TOTP or recovery code against the pending challenge', async () => {
    const challenge = { challengeId: 'challenge-2', expiresAtUtc: '2099-01-01T00:00:00Z', email: 'owner@nexora.local' };
    platformLogin.mockResolvedValue({ mfaRequired: true, challenge });
    render(<PlatformLoginScreen />);
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: challenge.email } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enter Control Plane' }));

    fireEvent.change(await screen.findByLabelText(/6-digit authenticator code/), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Verify and enter' }));
    // rememberBrowser travels with every verification, false unless the operator ticked the box.
    // Asserting it explicitly is what stops a future refactor from sending `true` by default —
    // which would silently extend a second factor to twelve hours for people who never asked.
    await waitFor(() => expect(platformCompleteMfa).toHaveBeenCalledWith(
      challenge,
      { totpCode: '123456', rememberBrowser: false },
    ));
  });

  it('can complete the same challenge with a single-use recovery code', async () => {
    const challenge = { challengeId: 'challenge-3', expiresAtUtc: '2099-01-01T00:00:00Z', email: 'owner@nexora.local' };
    platformLogin.mockResolvedValue({ mfaRequired: true, challenge });
    render(<PlatformLoginScreen />);
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: challenge.email } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enter Control Plane' }));

    fireEvent.click(await screen.findByRole('button', { name: 'Use a recovery code' }));
    fireEvent.change(screen.getByLabelText(/Recovery code/), { target: { value: 'AAAAAA-BBBBBB-CCCCCC-DDDDDD' } });
    fireEvent.click(screen.getByRole('button', { name: 'Verify and enter' }));
    await waitFor(() => expect(platformCompleteMfa).toHaveBeenCalledWith(
      challenge,
      { recoveryCode: 'AAAAAA-BBBBBB-CCCCCC-DDDDDD', rememberBrowser: false },
    ));
  });

  it('asks the server to remember this browser only when the operator ticks the box', async () => {
    const challenge = { challengeId: 'challenge-4', expiresAtUtc: '2099-01-01T00:00:00Z', email: 'owner@nexora.local' };
    platformLogin.mockResolvedValue({ mfaRequired: true, challenge });
    render(<PlatformLoginScreen />);
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: challenge.email } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enter Control Plane' }));

    fireEvent.change(await screen.findByLabelText(/6-digit authenticator code/), { target: { value: '123456' } });
    fireEvent.click(screen.getByLabelText(/Remember this browser/));
    fireEvent.click(screen.getByRole('button', { name: 'Verify and enter' }));

    await waitFor(() => expect(platformCompleteMfa).toHaveBeenCalledWith(
      challenge,
      { totpCode: '123456', rememberBrowser: true },
    ));
  });
});
