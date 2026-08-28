import { render, screen } from '@testing-library/react';
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
  it('lets short and mobile viewports scroll instead of clipping the form', () => {
    renderLogin();

    expect(screen.getByTestId('login-viewport')).toHaveStyle({
      width: '100%',
      overflowY: 'auto',
    });
    expect(screen.getByTestId('login-viewport')).not.toHaveStyle({
      fontFamily: "'Poppins', sans-serif",
    });
    expect(screen.getByTestId('login-card')).not.toHaveStyle({ height: '850px' });
  });

  it('exposes recovery and platform destinations as links', () => {
    renderLogin();

    expect(screen.getByRole('link', { name: 'Forgot password?' })).toHaveAttribute(
      'href',
      '/forgot-password',
    );
    expect(screen.getByRole('link', { name: 'Platform Owner? Manage or delete tenants' })).toHaveAttribute(
      'href',
      '/platform/tenants',
    );
  });

  it('keeps the inquiry-to-RFQ value statement visible when decoration is unavailable', () => {
    renderLogin();

    const workflow = screen.getByRole('complementary', { name: 'Nexora inquiry-to-RFQ workflow' });
    expect(workflow).toHaveTextContent('Email evidence to governed RFQ');
    expect(workflow).toHaveTextContent('Every approved line stays traceable to its source.');
  });
});
