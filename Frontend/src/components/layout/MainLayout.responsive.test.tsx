import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const viewport = vi.hoisted(() => ({ hasPersistentNavigation: true }));

vi.mock('@mui/material', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@mui/material')>();
  return {
    ...actual,
    useMediaQuery: () => viewport.hasPersistentNavigation,
  };
});

vi.mock('./Navbar', () => ({
  default: ({
    onToggleSidebar,
    sidebarExpanded,
  }: {
    onToggleSidebar: () => void;
    sidebarExpanded: boolean;
  }) => (
    <button type="button" onClick={onToggleSidebar} aria-label="Toggle test navigation">
      {sidebarExpanded ? 'expanded' : 'collapsed'}
    </button>
  ),
}));

vi.mock('./Sidebar', () => ({
  default: ({
    collapsed,
    onNavigate,
  }: {
    collapsed: boolean;
    onNavigate?: () => void;
  }) => (
    <div data-testid="test-sidebar" data-collapsed={collapsed ? 'true' : 'false'}>
      <button type="button" onClick={onNavigate}>Navigate</button>
    </div>
  ),
}));

vi.mock('../common/Branding', () => ({ default: () => <span>Nexora</span> }));
vi.mock('./SkipLink', () => ({
  MAIN_CONTENT_ID: 'main-content',
  default: () => <a href="#main-content">Skip to main content</a>,
}));
vi.mock('./ImpersonationBanner', () => ({ default: () => null }));

import MainLayout from './MainLayout';

const renderLayout = () => render(
  <MemoryRouter>
    <MainLayout><h1>Workspace</h1></MainLayout>
  </MemoryRouter>,
);

beforeEach(() => {
  viewport.hasPersistentNavigation = true;
  window.localStorage.clear();
});
afterEach(() => vi.useRealTimers());

describe('MainLayout responsive navigation', () => {
  it('starts closed on desktop; the toggle pins it open and the choice is remembered', () => {
    const { unmount } = renderLayout();

    expect(screen.getByRole('heading', { name: 'Workspace' }).parentElement).toHaveClass('nx-route-enter');

    const toggle = screen.getByRole('button', { name: 'Toggle test navigation' });
    expect(toggle).toHaveTextContent('collapsed');
    expect(screen.getAllByTestId('test-sidebar')).toHaveLength(1);
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'true');

    fireEvent.click(toggle);
    expect(toggle).toHaveTextContent('expanded');
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'false');
    unmount();

    renderLayout();
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'false');
  });

  it('opens while the pointer rests on the closed menu and closes when it leaves', () => {
    vi.useFakeTimers();
    renderLayout();
    const nav = screen.getByRole('navigation', { name: 'Main' });

    fireEvent.mouseEnter(nav);
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'true');
    act(() => { vi.advanceTimersByTime(300); });
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'false');

    fireEvent.mouseLeave(nav);
    act(() => { vi.advanceTimersByTime(400); });
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'true');
  });

  it('opens on keyboard focus and Escape closes it', () => {
    renderLayout();
    fireEvent.focus(screen.getByRole('button', { name: 'Navigate' }));
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'false');
    fireEvent.keyDown(screen.getByRole('button', { name: 'Navigate' }), { key: 'Escape' });
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'true');
  });

  it('uses an expanded overlay drawer below the desktop breakpoint', () => {
    viewport.hasPersistentNavigation = false;
    renderLayout();

    const toggle = screen.getByRole('button', { name: 'Toggle test navigation' });
    expect(toggle).toHaveTextContent('collapsed');
    expect(screen.getAllByTestId('test-sidebar')).toHaveLength(1);
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'false');

    fireEvent.click(toggle);
    expect(toggle).toHaveTextContent('expanded');

    fireEvent.click(screen.getByRole('button', { name: 'Navigate' }));
    expect(toggle).toHaveTextContent('collapsed');
  });
});
