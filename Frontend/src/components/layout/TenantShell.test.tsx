import type { ReactNode } from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { claimChunkRecovery } from '../../utils/chunkRecovery';
import { pageReloader, resetDeploymentUpdateForTests } from '../../utils/deploymentUpdate';
import { clearAllUnsavedWork, setUnsavedWork } from '../../hooks/unsavedWorkRegistry';
import lazyWithRetry from '../../utils/lazyWithRetry';
import DeploymentUpdateNotice, { DEPLOYMENT_NOTICE_TEXT } from '../common/DeploymentUpdateNotice';
import TenantShell from './TenantShell';

/**
 * A screen failure must never take the navigation away, and a deploy must never reload the page
 * by itself. Both used to happen: the only boundary wrapped the whole app, and a missing code file
 * after a deploy reloaded the page on the spot (Chrome) or left a full-screen crash card (Safari,
 * Firefox).
 */

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ token: 'signed-in', userData: {} }),
}));

let shellMounts = 0;
vi.mock('./MainLayout', async () => {
  const { useEffect } = await import('react');
  return {
    default: ({ children }: { children: ReactNode }) => {
      useEffect(() => { shellMounts += 1; }, []);
      return (
        <div>
          <nav aria-label="Main">
            <Link to="/inbox">Inbox</Link>
            <Link to="/broken">Broken screen</Link>
            <Link to="/quotes">Quotes</Link>
            <Link to="/fine">Fine screen</Link>
          </nav>
          <main>{children}</main>
        </div>
      );
    },
  };
});

const Crash = () => {
  throw new Error('render failure');
};

const staleChunk = () => new TypeError('Failed to fetch dynamically imported module: /assets/QuotesPage-1a2b.js');
const StaleQuotes = lazyWithRetry(() => Promise.reject(staleChunk()), 0);

const renderApp = (initial: string) => render(
  <MemoryRouter initialEntries={[initial]}>
    <Routes>
      <Route path="/inbox" element={<TenantShell><h1>Inbox</h1></TenantShell>} />
      <Route path="/broken" element={<TenantShell><Crash /></TenantShell>} />
      <Route path="/quotes" element={<TenantShell><StaleQuotes /></TenantShell>} />
      <Route path="/fine" element={<TenantShell><h1>Fine screen</h1></TenantShell>} />
    </Routes>
    <DeploymentUpdateNotice />
  </MemoryRouter>,
);

describe('TenantShell', () => {
  let reload: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(() => {
    shellMounts = 0;
    sessionStorage.clear();
    reload = vi.fn<() => void>();
    pageReloader.reload = reload;
    vi.spyOn(console, 'error').mockImplementation(() => {});
  });

  afterEach(() => {
    clearAllUnsavedWork();
    resetDeploymentUpdateForTests();
    vi.restoreAllMocks();
  });

  it('keeps the navigation when one screen crashes, and clears the failure on the next navigation', async () => {
    renderApp('/broken');

    expect(await screen.findByRole('heading', { name: 'This screen stopped working' })).toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Fine screen' }));

    expect(await screen.findByRole('heading', { name: 'Fine screen' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'This screen stopped working' })).not.toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();
  });

  it('keeps ONE shell instance between the Inbox and any other screen', async () => {
    renderApp('/inbox');
    expect(await screen.findByRole('heading', { name: 'Inbox' })).toBeInTheDocument();
    const mountsAtInbox = shellMounts;

    fireEvent.click(screen.getByRole('link', { name: 'Fine screen' }));
    expect(await screen.findByRole('heading', { name: 'Fine screen' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('link', { name: 'Inbox' }));
    expect(await screen.findByRole('heading', { name: 'Inbox' })).toBeInTheDocument();

    expect(shellMounts).toBe(mountsAtInbox);
  });

  it('opens a screen whose code belongs to an older deploy fresh, as part of the navigation that asked for it', async () => {
    renderApp('/fine');
    expect(await screen.findByRole('heading', { name: 'Fine screen' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Quotes' }));

    expect(await screen.findByText('Opening the latest version of Nexora…')).toBeInTheDocument();
    expect(reload).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('navigation', { name: 'Main' })).toBeInTheDocument();
  });

  it('never reloads over unsaved work: the screen explains, and the rest of the form is left alone', async () => {
    setUnsavedWork('nexora.quote.edit.9', 'Leave without saving?');
    renderApp('/fine');
    expect(await screen.findByRole('heading', { name: 'Fine screen' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Quotes' }));

    expect(await screen.findByRole('heading', { name: 'Nexora was updated' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Reload this screen' })).toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();
    // The card already offers the reload; the quiet notice does not repeat it on the same screen.
    expect(screen.queryByText(DEPLOYMENT_NOTICE_TEXT)).not.toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: /reload/i })).toHaveLength(1);
  });

  it('does not reload the same address twice inside the recovery window (loop guard)', async () => {
    claimChunkRecovery(sessionStorage, '/quotes');
    renderApp('/fine');
    expect(await screen.findByRole('heading', { name: 'Fine screen' })).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Quotes' }));

    expect(await screen.findByRole('heading', { name: 'Nexora was updated' })).toBeInTheDocument();
    expect(reload).not.toHaveBeenCalled();

    // Leaving the failed screen clears its card; the notice now offers the reload quietly.
    await act(async () => {
      fireEvent.click(screen.getByRole('link', { name: 'Inbox' }));
    });
    expect(await screen.findByRole('heading', { name: 'Inbox' })).toBeInTheDocument();
    expect(screen.getByText(DEPLOYMENT_NOTICE_TEXT)).toBeInTheDocument();
  });
});
