import { render, screen, within } from '@testing-library/react';
import { createTheme, ThemeProvider } from '@mui/material/styles';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ setToken: vi.fn(), setUserData: vi.fn() }),
}));

vi.mock('../../context/ThemeContext', () => ({
  useAppTheme: () => ({ mode: 'light', setMode: vi.fn(), primaryColor: '#2563eb' }),
}));

import LoginPage from './LoginPage';

const renderLogin = () => render(
  <ThemeProvider theme={createTheme()}>
    <MemoryRouter>
      <LoginPage />
    </MemoryRouter>
  </ThemeProvider>,
);

describe('LoginPage navigation and value statement', () => {
  it('keeps the product identity and governed evidence narrative in the document', () => {
    renderLogin();

    const workflow = screen.getByRole('region', { name: 'Nexora evidence-to-cash workflow' });
    expect(workflow).not.toHaveAttribute('aria-hidden');
    expect(within(workflow).getByText('NEXORA')).toBeVisible();
    expect(within(workflow).getByText('From source evidence to collected cash.')).toBeVisible();

    const stages = within(workflow).getByRole('list', { name: 'Governed commercial stages' });
    expect(within(stages).getAllByRole('listitem')).toHaveLength(3);
    for (const stage of [
      'Capture & reconcile',
      'Approve & source',
      'Fulfil & collect',
    ]) {
      expect(within(stages).getByText(stage)).toBeVisible();
    }
  });

  it('exposes recovery and platform destinations as links', () => {
    renderLogin();

    expect(screen.getByRole('link', { name: 'Forgot password?' })).toHaveAttribute(
      'href',
      '/forgot-password',
    );
    expect(screen.getByRole('link', { name: 'Platform administrator sign-in' })).toHaveAttribute(
      'href',
      '/platform/tenants',
    );
  });

  it('keeps mobile-safe landmarks and the complete authentication task', () => {
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 390 });
    window.dispatchEvent(new Event('resize'));
    renderLogin();

    const viewport = screen.getByTestId('login-viewport');
    const workflow = screen.getByRole('region', { name: 'Nexora evidence-to-cash workflow' });
    const main = screen.getByRole('main');

    expect(viewport).toContainElement(workflow);
    expect(viewport).toContainElement(main);
    expect(main).toHaveAttribute('id', 'main-content');
    expect(main).toHaveAttribute('tabindex', '-1');
    expect(workflow.compareDocumentPosition(main) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.getByRole('heading', { level: 1, name: 'Sign in' })).toBeVisible();
    expect(screen.getByText(/work account to access your Nexora workspace/i)).toBeVisible();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});
