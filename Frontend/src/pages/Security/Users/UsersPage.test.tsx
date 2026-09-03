import { beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import UsersPage from './UsersPage';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: () => '' }),
}));

/** The signed-in user. Row id 2 is "self"; row id 5 is somebody else. */
const CURRENT_USER_ID = 2;
const AUTH_BUSINESS_UNIT_ID = 1;

const hasPermission = vi.fn();

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    token: 'test-token',
    userData: { id: CURRENT_USER_ID, roleId: 84, businessUnitId: AUTH_BUSINESS_UNIT_ID },
    hasPermission: (moduleName: string, action?: string) => hasPermission(moduleName, action),
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: vi.fn(),
    setToken: vi.fn(),
    setUserData: vi.fn(),
    logout: vi.fn(),
  }),
}));

const getAll = vi.fn();
const getRoles = vi.fn();
const getTeams = vi.fn();
const getUserGroups = vi.fn();
const createUser = vi.fn();
const updateUser = vi.fn();

vi.mock('../../../api/services/userService', () => ({
  default: {
    getAll: (params: unknown) => getAll(params),
    getRoles: () => getRoles(),
    getTeams: (bu: unknown) => getTeams(bu),
    getUserGroups: (bu: unknown) => getUserGroups(bu),
    create: (data: FormData) => createUser(data),
    update: (id: number, data: FormData) => updateUser(id, data),
    changePassword: vi.fn(),
  },
}));

const ROLES = [
  { setupId: 1, setupName: 'Super_Administrator' },
  { setupId: 84, setupName: 'Super Admin' },
];

const USERS = {
  items: [
    { id: CURRENT_USER_ID, firstName: 'John', lastName: 'Michael', email: 'john@nexora.test', roleId: 84, roleName: 'Super Admin', buid: 1, isActive: true },
    { id: 5, firstName: 'Robert', lastName: 'Henry', email: 'robert@nexora.test', roleId: 84, roleName: 'Super Admin', buid: 1, isActive: true },
  ],
  totalCount: 2,
  pageNumber: 1,
  pageSize: 10,
};

const renderPage = () => {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <SnackbarProvider>
          <UsersPage />
        </SnackbarProvider>
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

const openCreateDialog = async () => {
  fireEvent.click(await screen.findByRole('button', { name: /add user/i }));
  return screen.findByRole('dialog');
};

describe('UsersPage', () => {
  beforeAll(() => {
    // The DataGrid measures its viewport; jsdom ships neither observer.
    class NoopObserver {
      observe() {}
      unobserve() {}
      disconnect() {}
    }
    (globalThis as Record<string, unknown>).ResizeObserver ??= NoopObserver;
    (globalThis as Record<string, unknown>).IntersectionObserver ??= NoopObserver;
  });

  beforeEach(() => {
    vi.clearAllMocks();
    hasPermission.mockReturnValue(true);
    getAll.mockResolvedValue(USERS);
    getRoles.mockResolvedValue(ROLES);
    getTeams.mockResolvedValue([]);
    getUserGroups.mockResolvedValue([]);
    createUser.mockResolvedValue({});
    updateUser.mockResolvedValue({});
  });

  it('rendersRoleNameColumn', async () => {
    renderPage();
    // The column exists and the grid renders the RoleName the users endpoint supplies. This was
    // null for every row while the roles lookup was broken.
    expect(await screen.findByRole('columnheader', { name: /role/i })).toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByText('Super Admin').length).toBeGreaterThan(0));
  });

  it('rendersRoleEmptyStateGuard_whenNoRolesAreConfigured', async () => {
    getRoles.mockResolvedValue([]);
    renderPage();

    // Role is required to create a user, so an empty list must be stated, not left as a blank
    // dropdown the administrator cannot get past.
    expect(
      await screen.findByText(/no roles are configured for this business unit/i),
    ).toBeInTheDocument();
    expect(await screen.findByRole('button', { name: /add user/i })).toBeDisabled();
  });

  it('invitesByDefault_andSendsNoPassword', async () => {
    // The default path mints no credential: the person receives an activation link and chooses
    // their own password. The administrator never types one, so none can leak through them.
    renderPage();
    const dialog = await openCreateDialog();

    expect(within(dialog).queryByLabelText(/^password/i)).not.toBeInTheDocument();
    expect(dialog.textContent).toMatch(/send invitation/i);
    expect(within(dialog).getByRole('button', { name: /set a password instead/i })).toBeInTheDocument();

    fireEvent.change(within(dialog).getByLabelText(/first name/i), { target: { value: 'Bryan' } });
    fireEvent.change(within(dialog).getByLabelText(/last name/i), { target: { value: 'Evrest' } });
    fireEvent.change(within(dialog).getByLabelText(/email address/i), { target: { value: 'bryan@nexora.test' } });
    fireEvent.mouseDown(within(dialog).getByRole('combobox', { name: /role/i }));
    fireEvent.click(await screen.findByRole('option', { name: 'Super Admin' }));

    fireEvent.click(within(dialog).getByRole('button', { name: /create user/i }));

    await waitFor(() => expect(createUser).toHaveBeenCalledTimes(1));
    const sent = createUser.mock.calls[0][0] as FormData;
    expect(sent.get('Activation')).toBe('invite');
    expect(sent.get('Password')).toBeNull();
  });

  it('passwordFieldIsRequired_andHasNoDefaultValue', async () => {
    renderPage();
    const dialog = await openCreateDialog();
    fireEvent.click(within(dialog).getByRole('button', { name: /set a password instead/i }));

    const password = within(dialog).getByLabelText(/^password/i);
    // No pre-filled value and, critically, no helper text advertising a shared default credential.
    expect(password).toHaveValue('');
    expect(password).toBeRequired();
    expect(dialog.textContent).not.toMatch(/Welcome@123/);

    // The form cannot be submitted without one.
    expect(within(dialog).getByRole('button', { name: /create user/i })).toBeDisabled();
  });

  it('doesNotSendADefaultPassword_whenCreatingAUser', async () => {
    renderPage();
    const dialog = await openCreateDialog();
    fireEvent.click(within(dialog).getByRole('button', { name: /set a password instead/i }));

    fireEvent.change(within(dialog).getByLabelText(/first name/i), { target: { value: 'Bryan' } });
    fireEvent.change(within(dialog).getByLabelText(/last name/i), { target: { value: 'Evrest' } });
    fireEvent.change(within(dialog).getByLabelText(/email address/i), { target: { value: 'bryan@nexora.test' } });
    fireEvent.change(within(dialog).getByLabelText(/^password/i), { target: { value: 'a-real-password' } });

    fireEvent.mouseDown(within(dialog).getByRole('combobox', { name: /role/i }));
    fireEvent.click(await screen.findByRole('option', { name: 'Super Admin' }));

    fireEvent.click(within(dialog).getByRole('button', { name: /create user/i }));

    await waitFor(() => expect(createUser).toHaveBeenCalledTimes(1));
    const sent = createUser.mock.calls[0][0] as FormData;
    expect(sent.get('Activation')).toBe('password');
    expect(sent.get('Password')).toBe('a-real-password');
    expect(sent.get('Password')).not.toBe('Welcome@123');
    // Provenance is server-derived; the client must not claim it.
    expect(sent.get('CreatedBy')).toBeNull();
    expect(sent.get('ModifiedBy')).toBeNull();
  });

  it('explainsThatTheTeamFieldIsWhatAManagerSees', async () => {
    // Users.TeamID is the ONE authority on team membership — the account-team scope resolver reads
    // it and nothing else. Left as None, this person's pipeline is visible to them alone and their
    // manager sees an empty dashboard with nothing on screen explaining why. The field that causes
    // that has to say so where the decision is made.
    renderPage();
    const dialog = await openCreateDialog();

    const team = within(dialog).getByRole('combobox', { name: /team/i });
    expect(team).toBeInTheDocument();
    expect(dialog.textContent).toMatch(/manager of this team can see/i);
  });

  it('changePasswordButtonHidden_forOtherUsers', async () => {
    renderPage();
    await screen.findByRole('columnheader', { name: /role/i });
    await waitFor(() => expect(screen.getByText('robert@nexora.test')).toBeInTheDocument());

    // `POST /api/User/{id}/ChangePassword` rejects any caller that is not the subject, so the key
    // icon 403'd on every row but your own — including for the Super Admin.
    const keys = screen.getAllByRole('button', { name: /change my password/i });
    expect(keys).toHaveLength(1);

    const ownRow = screen.getByText('john@nexora.test').closest('[role="row"]');
    expect(ownRow).not.toBeNull();
    expect(within(ownRow as HTMLElement).getByRole('button', { name: /change my password/i })).toBeInTheDocument();

    const otherRow = screen.getByText('robert@nexora.test').closest('[role="row"]');
    expect(otherRow).not.toBeNull();
    expect(within(otherRow as HTMLElement).queryByRole('button', { name: /change my password/i })).toBeNull();
  });

  it('hidesAddAndEditAffordances_withoutTheMatchingUsersGrant', async () => {
    hasPermission.mockImplementation((_module: string, action?: string) => action !== 'create' && action !== 'edit');
    renderPage();

    await waitFor(() => expect(screen.getByText('john@nexora.test')).toBeInTheDocument());
    // Hidden rather than rendered-and-403ing.
    expect(screen.queryByRole('button', { name: /add user/i })).toBeNull();
    expect(screen.queryByRole('button', { name: /edit user/i })).toBeNull();
  });

  it('showsTheServerReason_whenTheUsersGridFails', async () => {
    getAll.mockRejectedValue({
      isAxiosError: true,
      message: 'Request failed with status code 403',
      config: { url: '/api/User', method: 'get' },
      response: { status: 403, data: { message: 'denied' } },
    });
    renderPage();

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/role does not permit|administrator/i);
    expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument();
  });
});
