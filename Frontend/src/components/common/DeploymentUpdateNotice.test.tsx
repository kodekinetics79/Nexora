import { act, fireEvent, render, screen } from '@testing-library/react';
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  installDeploymentUpdateListener,
  markDeploymentUpdated,
  pageReloader,
  resetDeploymentUpdateForTests,
} from '../../utils/deploymentUpdate';
import { clearAllUnsavedWork, setUnsavedWork } from '../../hooks/unsavedWorkRegistry';
import DeploymentUpdateNotice, { DEPLOYMENT_NOTICE_TEXT } from './DeploymentUpdateNotice';

const renderWithNavigation = () => render(
  <MemoryRouter initialEntries={['/procurement/leads/7/workbench']}>
    <nav>
      <Link to="/procurement/leads/7/workbench?tab=lines">Lines tab</Link>
      <Link to="/sales/quotes">Quotes</Link>
      <Link to="/inbox">Inbox</Link>
    </nav>
    <Routes>
      <Route path="*" element={<p>screen</p>} />
    </Routes>
    <DeploymentUpdateNotice />
  </MemoryRouter>,
);

describe('DeploymentUpdateNotice', () => {
  let reload: ReturnType<typeof vi.fn<() => void>>;

  beforeEach(() => {
    sessionStorage.clear();
    reload = vi.fn<() => void>();
    pageReloader.reload = reload;
  });

  afterEach(() => {
    clearAllUnsavedWork();
    resetDeploymentUpdateForTests();
  });

  it('offers the reload quietly and never takes it while the person stays on the screen', async () => {
    renderWithNavigation();
    expect(screen.queryByText(DEPLOYMENT_NOTICE_TEXT)).not.toBeInTheDocument();

    act(() => { markDeploymentUpdated(); });

    expect(screen.getByRole('status')).toHaveTextContent(DEPLOYMENT_NOTICE_TEXT);
    expect(reload).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Reload' }));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('loads the next screen fresh when the person navigates, but not for a tab or filter change', () => {
    renderWithNavigation();
    act(() => { markDeploymentUpdated(); });

    fireEvent.click(screen.getByRole('link', { name: 'Lines tab' }));
    expect(reload).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('link', { name: 'Quotes' }));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('never reloads on navigation while a form holds unsaved work', () => {
    renderWithNavigation();
    act(() => { markDeploymentUpdated(); });
    setUnsavedWork('nexora.quote.edit.9', 'Leave without saving?');

    fireEvent.click(screen.getByRole('link', { name: 'Quotes' }));
    expect(reload).not.toHaveBeenCalled();

    clearAllUnsavedWork();
    fireEvent.click(screen.getByRole('link', { name: 'Inbox' }));
    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('is raised by a failed Vite preload that never reached a boundary', () => {
    const uninstall = installDeploymentUpdateListener(window);
    renderWithNavigation();

    act(() => {
      const event = new Event('vite:preloadError', { cancelable: true }) as Event & { payload?: unknown };
      event.payload = new Error('Unable to preload CSS for /assets/QuotesPage-1a2b.css');
      window.dispatchEvent(event);
      // Not cancelled: cancelling makes the import resolve to nothing and crash the screen worse.
      expect(event.defaultPrevented).toBe(false);
    });

    expect(screen.getByText(DEPLOYMENT_NOTICE_TEXT)).toBeInTheDocument();
    uninstall();
  });
});
