import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import RouteAnnouncer from './RouteAnnouncer';
import {
  notifyPlatformSessionPresenceChanged,
  PLATFORM_SESSION_TOKEN_KEY,
} from '../../platform/auth/platformSessionPresence';

const NavigationHarness = () => {
  const navigate = useNavigate();
  return <button onClick={() => navigate('/platform/tenants')}>Open platform tenants</button>;
};

describe('RouteAnnouncer platform authentication boundary', () => {
  beforeEach(() => {
    sessionStorage.removeItem(PLATFORM_SESSION_TOKEN_KEY);
  });

  it('announces the sign-in boundary instead of protected platform content before authentication', async () => {
    render(
      <MemoryRouter initialEntries={['/login']}>
        <RouteAnnouncer />
        <NavigationHarness />
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Open platform tenants' }));

    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('Platform Console Sign In, page loaded.');
      expect(document.title).toBe('Platform Console Sign In | NEXORA');
    });
  });

  it('keeps the protected route title once a platform session exists', async () => {
    sessionStorage.setItem(PLATFORM_SESSION_TOKEN_KEY, 'test-platform-session');
    render(
      <MemoryRouter initialEntries={['/login']}>
        <RouteAnnouncer />
        <NavigationHarness />
      </MemoryRouter>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Open platform tenants' }));

    await waitFor(() => {
      expect(screen.getByRole('status')).toHaveTextContent('Tenants, page loaded.');
      expect(document.title).toBe('Tenants | NEXORA');
    });
  });

  it('updates the title and announcement when sign-in succeeds without changing the platform URL', async () => {
    render(
      <MemoryRouter initialEntries={['/platform/tenants']}>
        <RouteAnnouncer />
      </MemoryRouter>,
    );

    await waitFor(() => expect(document.title).toBe('Platform Console Sign In | NEXORA'));

    act(() => {
      sessionStorage.setItem(PLATFORM_SESSION_TOKEN_KEY, 'test-platform-session');
      notifyPlatformSessionPresenceChanged();
    });

    await waitFor(() => {
      expect(document.title).toBe('Tenants | NEXORA');
      expect(screen.getByRole('status')).toHaveTextContent('Tenants, page loaded.');
    });
  });
});
