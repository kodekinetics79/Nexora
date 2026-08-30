import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createTheme, ThemeProvider } from '@mui/material/styles';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  post: vi.fn(),
  getMyPermissions: vi.fn(),
  setToken: vi.fn(),
  setUserData: vi.fn(),
  setMode: vi.fn(),
}));

vi.mock('../../api/axiosInstance', () => ({
  default: { post: mocks.post },
}));

vi.mock('../../api/services/userService', () => ({
  default: { getMyPermissions: mocks.getMyPermissions },
}));

vi.mock('../../context/AuthContext', () => ({
  useAuth: () => ({ setToken: mocks.setToken, setUserData: mocks.setUserData }),
}));

vi.mock('../../context/ThemeContext', () => ({
  useAppTheme: () => ({ mode: 'light', setMode: mocks.setMode, primaryColor: '#2563eb' }),
}));

import LoginPage from './LoginPage';

const renderLogin = () => render(
  <ThemeProvider theme={createTheme()}>
    <MemoryRouter initialEntries={['/login']}>
      <LoginPage />
    </MemoryRouter>
  </ThemeProvider>,
);

const enterCredentials = (email = 'buyer@example.test', password = 'Correct-Horse-42!') => {
  fireEvent.change(screen.getByLabelText(/^Email address/, { selector: 'input' }), {
    target: { value: email },
  });
  fireEvent.change(screen.getByLabelText(/^Password/, { selector: 'input' }), {
    target: { value: password },
  });
};

const organizationResponse = {
  data: {
    requiresBusinessUnitSelection: true,
    businessUnits: [
      { id: 22, name: 'North America Operations' },
      { id: 31, name: 'Middle East Operations' },
    ],
  },
};

const authenticatedResponse = {
  data: {
    id: 7,
    email: 'buyer@example.test',
    userName: 'Pilot Buyer',
    roleId: 4,
    roleName: 'Procurement Officer',
    isSuperAdmin: false,
    isManager: false,
    businessUnitId: 22,
    businessUnitName: 'North America Operations',
    token: 'test-token',
  },
};

beforeEach(() => {
  for (const mock of Object.values(mocks)) mock.mockReset();
  mocks.getMyPermissions.mockResolvedValue({
    userId: 7,
    roleId: 4,
    roleName: 'Procurement Officer',
    businessUnitId: 22,
    isSuperAdmin: false,
    isManager: false,
    hasModuleAuthorityByRank: false,
    permissions: [],
    entitlements: [],
  });
});

describe('LoginPage accessible interaction contract', () => {
  it('names the credential fields, controls, recovery paths, and password reveal state', () => {
    renderLogin();

    const email = screen.getByLabelText(/^Email address/, { selector: 'input' });
    expect(email).toHaveAttribute('type', 'email');
    expect(email).toHaveAttribute('autocomplete', 'username');
    expect(email).toBeRequired();

    const password = screen.getByLabelText(/^Password/, { selector: 'input' });
    expect(password).toHaveAttribute('type', 'password');
    expect(password).toHaveAttribute('autocomplete', 'current-password');
    expect(password).toBeRequired();

    const reveal = screen.getByRole('button', { name: 'Show password' });
    expect(reveal).toHaveAttribute('aria-pressed', 'false');
    fireEvent.click(reveal);
    expect(screen.getByRole('button', { name: 'Hide password' })).toHaveAttribute('aria-pressed', 'true');
    expect(password).toHaveAttribute('type', 'text');

    expect(screen.getByRole('button', { name: 'Switch to dark theme' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Sign in' })).toHaveAttribute('aria-busy', 'false');
    expect(screen.getByRole('link', { name: 'Forgot password?' })).toHaveAttribute('href', '/forgot-password');
    expect(screen.getByRole('link', { name: 'Platform administrator sign-in' })).toHaveAttribute('href', '/platform/tenants');
  });

  it('retains an accessible name, exposes busy state, and prevents duplicate submission while loading', async () => {
    mocks.post.mockImplementationOnce(() => new Promise(() => undefined));
    renderLogin();
    enterCredentials();

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    const submitting = await screen.findByRole('button', { name: 'Signing in…' });
    expect(submitting).toHaveAttribute('aria-busy', 'true');
    expect(submitting).toBeDisabled();
    expect(mocks.post).toHaveBeenCalledTimes(1);
    expect(mocks.post).toHaveBeenCalledWith('/api/Auth/Login', {
      email: 'buyer@example.test',
      password: 'Correct-Horse-42!',
    });

    fireEvent.click(submitting);
    expect(mocks.post).toHaveBeenCalledTimes(1);
  });

  it('disambiguates the organization, keeps Continue disabled until selection, and submits the selected tenant', async () => {
    mocks.post
      .mockResolvedValueOnce(organizationResponse)
      .mockResolvedValueOnce(authenticatedResponse);
    renderLogin();
    enterCredentials();

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByText(/belongs to more than one organization/i)).toBeVisible();
    const continueButton = screen.getByRole('button', { name: 'Continue' });
    expect(continueButton).toBeDisabled();
    expect(screen.queryByRole('link', { name: 'Forgot password?' })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Platform administrator sign-in' })).not.toBeInTheDocument();

    fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Which organization?' }));
    fireEvent.click(await screen.findByRole('option', { name: 'North America Operations' }));
    expect(continueButton).toBeEnabled();
    fireEvent.click(continueButton);

    await waitFor(() => expect(mocks.post).toHaveBeenCalledTimes(2));
    expect(mocks.post).toHaveBeenNthCalledWith(2, '/api/Auth/Login', {
      email: 'buyer@example.test',
      password: 'Correct-Horse-42!',
      businessUnitId: 22,
    });
    await waitFor(() => expect(mocks.setToken).toHaveBeenCalledWith('test-token'));
    expect(mocks.getMyPermissions).toHaveBeenCalledTimes(1);
    expect(mocks.setUserData).toHaveBeenCalledWith(expect.objectContaining({
      businessUnitId: 22,
      roleName: 'Procurement Officer',
    }));
  });

  it('returns from organization selection without losing the supplied credentials', async () => {
    mocks.post.mockResolvedValueOnce(organizationResponse);
    renderLogin();
    enterCredentials('shared-buyer@example.test', 'Workspace-Pass-77!');

    fireEvent.click(screen.getByRole('button', { name: 'Sign in' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Back to sign in' }));

    expect(screen.getByLabelText(/^Email address/, { selector: 'input' })).toHaveValue('shared-buyer@example.test');
    expect(screen.getByLabelText(/^Password/, { selector: 'input' })).toHaveValue('Workspace-Pass-77!');
    expect(screen.getByRole('link', { name: 'Forgot password?' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Platform administrator sign-in' })).toBeVisible();
  });
});
