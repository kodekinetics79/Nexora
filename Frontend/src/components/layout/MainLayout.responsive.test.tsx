import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

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

beforeEach(() => {
  viewport.hasPersistentNavigation = true;
});

describe('MainLayout responsive navigation', () => {
  it('collapses the persistent rail on desktop', () => {
    render(<MainLayout><h1>Workspace</h1></MainLayout>);

    expect(screen.getByRole('button', { name: 'Toggle test navigation' })).toHaveTextContent('expanded');
    expect(screen.getAllByTestId('test-sidebar')).toHaveLength(1);
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'false');

    fireEvent.click(screen.getByRole('button', { name: 'Toggle test navigation' }));

    expect(screen.getByRole('button', { name: 'Toggle test navigation' })).toHaveTextContent('collapsed');
    expect(screen.getByTestId('test-sidebar')).toHaveAttribute('data-collapsed', 'true');
  });

  it('uses an expanded overlay drawer below the desktop breakpoint', () => {
    viewport.hasPersistentNavigation = false;
    render(<MainLayout><h1>Workspace</h1></MainLayout>);

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
