import type { ReactNode } from 'react';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useNavigate } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

/**
 * The landing Inbox and every other tenant screen must share ONE shell instance.
 *
 * `/inbox` and `/advanced` started their route element with `<RequireAuth>` while every other screen
 * started with `<MainLayout>`. React Router renders route elements at the same position with no key,
 * so a different root type threw the whole frame away — sidebar state reset, top bar remounted — on
 * every move between the Inbox and anything else. That reads as "the page refreshed".
 */

vi.mock('./context/AuthContext', () => ({
  useAuth: () => ({
    token: 'signed-in',
    userData: {},
    hasPermission: () => true,
    permissionsError: null,
    permissionsLoading: false,
    permissionsStale: false,
    refreshPermissions: vi.fn(),
  }),
}));

const shell = vi.hoisted(() => ({ mounts: 0 }));
vi.mock('./components/layout/MainLayout', async () => {
  const { useEffect } = await import('react');
  return {
    default: ({ children }: { children: ReactNode }) => {
      useEffect(() => { shell.mounts += 1; }, []);
      return <div data-testid="tenant-shell">{children}</div>;
    },
  };
});

vi.mock('./components/layout/RouteAnnouncer', () => ({ default: () => null }));
vi.mock('./pages/Inbox/InboxPage', () => ({ default: () => <h1>Inbox</h1> }));
vi.mock('./pages/Leads/AssignedLeadsPage', () => ({ default: () => <h1>Assigned Leads queue</h1> }));

import App from './App';

const Go = () => {
  const navigate = useNavigate();
  return (
    <>
      <button type="button" onClick={() => navigate('/inbox')}>Go to Inbox</button>
      <button type="button" onClick={() => navigate('/procurement/leads/assigned')}>Go to assigned</button>
    </>
  );
};

describe('tenant shell continuity', () => {
  it('keeps the same shell when moving between the Inbox and another screen', async () => {
    render(
      <MemoryRouter initialEntries={['/procurement/leads/assigned']}>
        <Go />
        <App />
      </MemoryRouter>,
    );

    // Warm both screens' code first: a first visit may suspend, which is a separate concern.
    expect(await screen.findByRole('heading', { name: 'Assigned Leads queue' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Go to Inbox' }));
    expect(await screen.findByRole('heading', { name: 'Inbox' })).toBeInTheDocument();

    const mountsBefore = shell.mounts;

    fireEvent.click(screen.getByRole('button', { name: 'Go to assigned' }));
    expect(await screen.findByRole('heading', { name: 'Assigned Leads queue' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Go to Inbox' }));
    expect(await screen.findByRole('heading', { name: 'Inbox' })).toBeInTheDocument();

    expect(shell.mounts).toBe(mountsBefore);
  });
});
