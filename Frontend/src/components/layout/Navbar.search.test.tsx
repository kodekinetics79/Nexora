import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { GlobalSearchResponse, SearchHit } from '../../api/services/searchService';
import { rememberSearchHit } from './globalSearchHistory';

const mocks = vi.hoisted(() => ({ search: vi.fn(), logout: vi.fn() }));

vi.mock('../../api/services/searchService', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../api/services/searchService')>();
  return { ...actual, default: { search: (...args: unknown[]) => mocks.search(...args) } };
});

vi.mock('../../context/ThemeContext', () => ({
  useAppTheme: () => ({
    mode: 'light',
    setMode: vi.fn(),
    primaryColor: '#4682b4',
    setPrimaryColor: vi.fn(),
  }),
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({
    userData: { id: 7, businessUnitId: 42, userName: 'Aisha Noor', roleName: 'Sales Rep' },
    logout: mocks.logout,
  }),
}));

import Navbar from './Navbar';

const hit = (entity: SearchHit['entity'], id: number, title: string): SearchHit => ({
  entity,
  id,
  title,
  subtitle: `${title} reference`,
  status: 'ACTIVE',
  occurredOn: '2026-08-25T00:00:00Z',
  dateField: 'createdOn',
  matchedOn: 'name',
});

const response = (hits: SearchHit[]): GlobalSearchResponse => ({
  query: 'ac',
  hits,
  searchedEntities: ['customer', 'product'],
  deniedEntities: [],
  truncated: [],
  notes: [],
});

const setDesktop = (desktop: boolean) => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation(() => ({
      matches: desktop,
      media: '',
      onchange: null,
      addListener: vi.fn(),
      removeListener: vi.fn(),
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
};

const Location = () => <output aria-label="current route">{useLocation().pathname}</output>;

const renderNavbar = () => render(
  <MemoryRouter initialEntries={['/inbox']}>
    <Routes>
      <Route path="*" element={<><Navbar onToggleSidebar={vi.fn()} drawerWidth={280} /><Location /></>} />
    </Routes>
  </MemoryRouter>,
);

const searchInput = () => screen.getByRole('combobox', {
  name: 'Search customers, suppliers, products, enquiries, quotes, orders and shipments',
});

beforeEach(() => {
  sessionStorage.clear();
  mocks.search.mockReset();
  mocks.logout.mockReset();
  setDesktop(true);
});

describe('Navbar global search', () => {
  it('surfaces an ordinary Error message instead of rendering a blank or false no-match state', async () => {
    mocks.search.mockRejectedValue(new Error('Search gateway timed out.'));
    renderNavbar();

    fireEvent.change(searchInput(), { target: { value: 'bolt' } });

    expect(await screen.findByRole('alert')).toHaveTextContent('Search gateway timed out.');
    expect(screen.queryByText(/nothing matches/i)).not.toBeInTheDocument();
  });

  it('groups results and supports full combobox keyboard navigation and a live result count', async () => {
    mocks.search.mockResolvedValue(response([
      hit('product', 2, 'Anchor bolt M12'),
      hit('customer', 1, 'Acme Industrial'),
      hit('product', 3, 'Acorn nut M12'),
    ]));
    renderNavbar();
    const input = searchInput();

    fireEvent.change(input, { target: { value: 'ac' } });
    expect(await screen.findByText('3 search results shown.')).toBeInTheDocument();

    const listbox = screen.getByRole('listbox', { name: 'Search results' });
    const groups = within(listbox).getAllByRole('group');
    expect(within(groups[0]).getByText('Customers')).toBeInTheDocument();
    expect(within(groups[1]).getByText('Products')).toBeInTheDocument();

    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(input).toHaveAttribute('aria-activedescendant', expect.stringContaining('customer-1'));
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(input).toHaveAttribute('aria-activedescendant', expect.stringContaining('product-2'));
    fireEvent.keyDown(input, { key: 'ArrowUp' });
    expect(input).toHaveAttribute('aria-activedescendant', expect.stringContaining('customer-1'));
    fireEvent.keyDown(input, { key: 'End' });
    expect(input).toHaveAttribute('aria-activedescendant', expect.stringContaining('product-3'));
    fireEvent.keyDown(input, { key: 'Home' });
    expect(input).toHaveAttribute('aria-activedescendant', expect.stringContaining('customer-1'));
    fireEvent.keyDown(input, { key: 'End' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(screen.getByRole('status', { name: 'current route' })).toHaveTextContent('/inventory/products/3');

    fireEvent.focus(searchInput());
    expect(screen.getByText('Acorn nut M12')).toBeInTheDocument();
    fireEvent.keyDown(searchInput(), { key: 'Escape' });
    expect(searchInput()).toHaveAttribute('aria-expanded', 'false');
  });

  it('shows and clears only this tenant/user recent opened records on empty focus', async () => {
    rememberSearchHit({ businessUnitId: 42, userId: 7 }, hit('rfq', 62, 'RFQ-0062'));
    rememberSearchHit({ businessUnitId: 99, userId: 7 }, hit('rfq', 99, 'Other tenant RFQ'));
    renderNavbar();

    fireEvent.focus(searchInput());

    expect(await screen.findByText('RFQ-0062')).toBeInTheDocument();
    expect(screen.queryByText('Other tenant RFQ')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Clear history' }));
    expect(screen.queryByText('RFQ-0062')).not.toBeInTheDocument();
    expect(screen.getByText('No recently opened records in this session.')).toBeInTheDocument();
  });

  it('clears the active recent-record scope on logout', async () => {
    rememberSearchHit({ businessUnitId: 42, userId: 7 }, hit('rfq', 62, 'RFQ-0062'));
    renderNavbar();

    fireEvent.click(screen.getByRole('button', { name: /account menu/i }));
    fireEvent.click(await screen.findByRole('menuitem', { name: 'Log Out Session' }));

    expect(mocks.logout).toHaveBeenCalledOnce();
    fireEvent.focus(searchInput());
    expect(screen.queryByText('RFQ-0062')).not.toBeInTheDocument();
    expect(screen.getByText('No recently opened records in this session.')).toBeInTheDocument();
  });

  it('offers a full-screen search entry when the viewport is below the desktop breakpoint', async () => {
    setDesktop(false);
    renderNavbar();

    fireEvent.click(screen.getByRole('button', { name: 'Open global search' }));

    expect(await screen.findByRole('dialog', { name: 'Search Nexora' })).toBeInTheDocument();
    expect(searchInput()).toHaveFocus();
    expect(screen.getByText('No recently opened records in this session.')).toBeInTheDocument();
  });

  it('uses the server detail ahead of the generic Error message when both are available', async () => {
    mocks.search.mockRejectedValue(Object.assign(new Error('Request failed'), {
      response: { data: 'Search access could not be evaluated.' },
    }));
    renderNavbar();

    fireEvent.change(searchInput(), { target: { value: 'bolt' } });

    await waitFor(() => expect(screen.getByRole('alert')).toHaveTextContent('Search access could not be evaluated.'));
  });
});
