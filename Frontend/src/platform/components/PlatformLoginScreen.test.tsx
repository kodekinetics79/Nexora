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
  it('keeps the separate tenant and platform sign-in surfaces visibly connected', () => {
    render(<PlatformLoginScreen />);

    expect(screen.getByRole('link', { name: 'Back to tenant sign-in' })).toHaveAttribute('href', '/login');
  });

  it('does not treat a password-accepted MFA challenge as a broken login', async () => {
    platformLogin.mockResolvedValue({
      mfaRequired: true,
      challenge: {
        challengeId: 'challenge-1',
        expiresAtUtc: '2099-01-01T00:00:00Z',
        email: 'owner@nexora.local',
        browserTrustOffered: true,
        browserTrustHours: 720,
      },
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
    const challenge = {
      challengeId: 'challenge-2',
      expiresAtUtc: '2099-01-01T00:00:00Z',
      email: 'owner@nexora.local',
      browserTrustOffered: true,
      browserTrustHours: 720,
    };
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
    const challenge = {
      challengeId: 'challenge-3',
      expiresAtUtc: '2099-01-01T00:00:00Z',
      email: 'owner@nexora.local',
      browserTrustOffered: true,
      browserTrustHours: 720,
    };
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

  it('names the real window on the checkbox instead of an open-ended promise', async () => {
    // "Remember this browser" said nothing about duration, which was tolerable while the window was
    // fixed at 12 hours and is not now that a platform Owner can set anything from 8 hours to 30
    // days. The number comes from the SERVER, on the challenge response, because at this point the
    // operator holds no token and cannot read the policy endpoint at all.
    const challenge = {
      challengeId: 'challenge-5',
      expiresAtUtc: '2099-01-01T00:00:00Z',
      email: 'owner@nexora.local',
      browserTrustOffered: true,
      browserTrustHours: 720,
    };
    platformLogin.mockResolvedValue({ mfaRequired: true, challenge });
    render(<PlatformLoginScreen />);
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: challenge.email } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enter Control Plane' }));

    expect(await screen.findByLabelText("Don't ask again on this browser for 30 days")).toBeInTheDocument();
  });

  it('does not offer to remember the browser at all when the platform has switched it off', async () => {
    // Not "offered and then quietly ignored". An operator who ticks a box the platform will not
    // honour is challenged again tomorrow with nothing on any screen explaining why.
    const challenge = {
      challengeId: 'challenge-6',
      expiresAtUtc: '2099-01-01T00:00:00Z',
      email: 'owner@nexora.local',
      browserTrustOffered: false,
      browserTrustHours: 0,
    };
    platformLogin.mockResolvedValue({ mfaRequired: true, challenge });
    render(<PlatformLoginScreen />);
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: challenge.email } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enter Control Plane' }));

    await screen.findByLabelText(/6-digit authenticator code/);
    expect(screen.queryByLabelText(/Don't ask again on this browser/i)).not.toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/6-digit authenticator code/), { target: { value: '123456' } });
    fireEvent.click(screen.getByRole('button', { name: 'Verify and enter' }));
    await waitFor(() => expect(platformCompleteMfa).toHaveBeenCalledWith(
      challenge,
      { totpCode: '123456', rememberBrowser: false },
    ));
  });

  it('asks the server to remember this browser only when the operator ticks the box', async () => {
    const challenge = {
      challengeId: 'challenge-4',
      expiresAtUtc: '2099-01-01T00:00:00Z',
      email: 'owner@nexora.local',
      browserTrustOffered: true,
      browserTrustHours: 720,
    };
    platformLogin.mockResolvedValue({ mfaRequired: true, challenge });
    render(<PlatformLoginScreen />);
    fireEvent.change(screen.getByLabelText(/Email/), { target: { value: challenge.email } });
    fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'secret' } });
    fireEvent.click(screen.getByRole('button', { name: 'Enter Control Plane' }));

    fireEvent.change(await screen.findByLabelText(/6-digit authenticator code/), { target: { value: '123456' } });
    fireEvent.click(screen.getByLabelText(/Don't ask again on this browser/));
    fireEvent.click(screen.getByRole('button', { name: 'Verify and enter' }));

    await waitFor(() => expect(platformCompleteMfa).toHaveBeenCalledWith(
      challenge,
      { totpCode: '123456', rememberBrowser: true },
    ));
  });
});
