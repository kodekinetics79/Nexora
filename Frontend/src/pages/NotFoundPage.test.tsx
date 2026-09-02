import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The 404 page told the reader to "use the navigation menu" while rendering outside the shell
 * that holds the menu, with no link anywhere. A signed-in reader now gets the shell and a button
 * to the Inbox; an anonymous one gets the sign-in door.
 */

const auth = { token: null as string | null };
vi.mock('../context/AuthContext', () => ({
  useAuth: () => ({
    token: auth.token,
    userData: { isManager: false, businessUnitId: 1, firstName: 'Sara', lastName: 'Bin Ali' },
    hasPermission: () => true,
    hasEntitlement: () => false,
    logout: vi.fn(),
  }),
}));
vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, fallback?: string) => fallback ?? key, i18n: { language: 'en', changeLanguage: vi.fn() } }),
}));
// The shell's chrome is not under test; keep it light and free of network reads.
vi.mock('../components/layout/Navbar', () => ({ default: () => <header>navbar</header> }));

const navigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>();
  return { ...actual, useNavigate: () => navigate };
});

import NotFoundPage from './NotFoundPage';

const renderPage = () => render(
  <MemoryRouter initialEntries={['/no/such/page']}><NotFoundPage /></MemoryRouter>,
);

beforeEach(() => vi.clearAllMocks());

describe('NotFoundPage', () => {
  it('renders inside the shell with a Go to Inbox button when signed in', () => {
    auth.token = 'synthetic';
    renderPage();

    expect(screen.getByRole('heading', { level: 1, name: /page not found/i })).toBeInTheDocument();
    // The navigation the copy refers to is actually on screen.
    expect(screen.getByRole('navigation', { name: /main/i })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /go to inbox/i }));
    expect(navigate).toHaveBeenCalledWith('/inbox');
  });

  it('offers sign-in, and no shell, when nobody is signed in', () => {
    auth.token = null;
    renderPage();

    expect(screen.queryByRole('navigation', { name: /main/i })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: /sign in/i }));
    expect(navigate).toHaveBeenCalledWith('/login');
  });
});
