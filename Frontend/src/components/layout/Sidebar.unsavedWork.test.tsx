import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * A rail click must not throw away a half-finished form.
 *
 * `useUnsavedWorkGuard` covered refresh and tab-close, and the quote pages' own Cancel buttons
 * asked before leaving, but the sidebar navigated with no question: a click on "Inbox" while a
 * 40-line quote was half priced lost the pricing silently. `BrowserRouter` has no `useBlocker`,
 * so the rail consults a small dirty flag before it navigates.
 */

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { isManager: false, businessUnitId: 1 },
    hasPermission: () => true,
    hasEntitlement: () => false,
  }),
}));
vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key }),
}));

import Sidebar from './Sidebar';
import { clearAllUnsavedWork, setUnsavedWork } from '../../hooks/unsavedWorkRegistry';

const WhereAmI = () => <output aria-label="Location">{useLocation().pathname}</output>;

const renderRail = () => render(
  <MemoryRouter initialEntries={['/sales/quotes/create']}>
    <Sidebar collapsed={false} />
    <WhereAmI />
  </MemoryRouter>,
);

beforeEach(() => {
  clearAllUnsavedWork();
  vi.spyOn(window, 'confirm');
});
afterEach(() => vi.restoreAllMocks());

describe('the rail and unsaved work', () => {
  it('asks before leaving a dirty form, and stays put when the answer is no', () => {
    setUnsavedWork('nexora.quote.create', 'Leave without saving? The lines you have entered on this quote will be lost.');
    vi.mocked(window.confirm).mockReturnValue(false);
    renderRail();

    fireEvent.click(screen.getByRole('button', { name: /^inbox/i }));

    expect(window.confirm).toHaveBeenCalledWith('Leave without saving? The lines you have entered on this quote will be lost.');
    expect(screen.getByLabelText('Location')).toHaveTextContent('/sales/quotes/create');
  });

  it('leaves when the answer is yes', () => {
    setUnsavedWork('nexora.quote.create', 'Leave without saving?');
    vi.mocked(window.confirm).mockReturnValue(true);
    renderRail();

    fireEvent.click(screen.getByRole('button', { name: /^inbox/i }));
    expect(screen.getByLabelText('Location')).toHaveTextContent('/inbox');
  });

  it('asks nothing when nothing is dirty (the control)', () => {
    renderRail();
    fireEvent.click(screen.getByRole('button', { name: /^inbox/i }));

    expect(window.confirm).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Location')).toHaveTextContent('/inbox');
  });
});
