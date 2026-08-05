import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import RolesPermissionsPage from './RolesPermissionsPage';

// `t('key') || 'Fallback'` is the pattern throughout these pages, so an empty translation makes
// the component render its English fallback — which is what the assertions below read.
vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: () => '' }),
}));

/** The authenticated tenant. Deliberately NOT 1 — the page used to hardcode 1. */
const AUTH_BUSINESS_UNIT_ID = 7;

vi.mock('../../../context/AuthContext', () => ({
  useAuth: () => ({
    token: 'test-token',
    userData: { id: 2, roleId: 84, businessUnitId: AUTH_BUSINESS_UNIT_ID, isSuperAdmin: false },
    hasPermission: () => true,
    permissionsError: null,
    permissionsLoading: false,
    refreshPermissions: vi.fn(),
    setToken: vi.fn(),
    setUserData: vi.fn(),
    logout: vi.fn(),
  }),
}));

const getRoles = vi.fn();
const getModules = vi.fn();
const getPermissions = vi.fn();
const createPermission = vi.fn();
const updatePermission = vi.fn();
const bulkApply = vi.fn();

vi.mock('../../../api/services/userService', () => ({
  default: { getRoles: () => getRoles() },
}));

vi.mock('../../../api/services/moduleService', () => ({
  default: { getAll: (params: unknown) => getModules(params) },
}));

vi.mock('../../../api/services/rolePermissionService', () => ({
  default: {
    getAll: (params: unknown) => getPermissions(params),
    create: (data: unknown) => createPermission(data),
    update: (id: number, data: unknown) => updatePermission(id, data),
    bulkApply: (data: unknown) => bulkApply(data),
  },
}));

const ROLES = [
  { setupId: 1, setupName: 'Super_Administrator' },
  { setupId: 4, setupName: 'Sales Manager' },
];

const MODULES = {
  items: [
    { id: 1, moduleName: 'Dashboard', isActive: true },
    { id: 3, moduleName: 'Users', isActive: true },
    { id: 9, moduleName: 'Leads', isActive: true },
  ],
  totalCount: 3,
  pageNumber: 1,
  pageSize: 100,
};

const forbidden = (detail: string) => ({
  isAxiosError: true,
  message: 'Request failed with status code 403',
  config: { url: '/api/RolePermission', method: 'post' },
  response: { status: 403, data: { message: detail } },
});

const renderPage = () => {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <SnackbarProvider>
        <RolesPermissionsPage />
      </SnackbarProvider>
    </QueryClientProvider>,
  );
};

/** Picks a role from the MUI select, which is what unlocks the matrix body. */
const selectRole = async (name: string) => {
  // MUI's Select opens on mousedown, not click.
  fireEvent.mouseDown(await screen.findByRole('combobox', { name: /select role/i }));
  fireEvent.click(await screen.findByRole('option', { name }));
};

describe('RolesPermissionsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getRoles.mockResolvedValue(ROLES);
    getModules.mockResolvedValue(MODULES);
    getPermissions.mockResolvedValue({
      items: [{ id: 11, roleId: 4, moduleId: 3, businessUnitId: AUTH_BUSINESS_UNIT_ID, canView: true, canCreate: false, canEdit: true, canDelete: false }],
      totalCount: 1,
      pageNumber: 1,
      pageSize: 1000,
    });
    createPermission.mockResolvedValue({});
    updatePermission.mockResolvedValue({});
    bulkApply.mockResolvedValue({ applied: 3, created: 2, updated: 1 });
  });

  it('rendersEmptyState_whenRolesListIsEmpty', async () => {
    // This is the state production was actually in: the roles endpoint answered 200 with [], so
    // nothing errored and the matrix sat on its "please select a role" placeholder forever.
    getRoles.mockResolvedValue([]);
    renderPage();

    expect(
      await screen.findByText(/no roles are configured for this business unit/i),
    ).toBeInTheDocument();
    // The unusable role picker is gone rather than rendered empty.
    expect(screen.queryByRole('combobox', { name: /select role/i })).not.toBeInTheDocument();
  });

  it('rendersAnExplainedErrorState_whenRolesFailToLoad', async () => {
    getRoles.mockRejectedValue(forbidden('You may not read roles.'));
    renderPage();

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/role does not permit|administrator/i);
    expect(await screen.findByRole('button', { name: /retry/i })).toBeInTheDocument();
  });

  it('sendsAuthenticatedBusinessUnitId_notHardcodedOne', async () => {
    renderPage();
    await selectRole('Sales Manager');

    await waitFor(() => expect(getPermissions).toHaveBeenCalled());
    expect(getPermissions).toHaveBeenCalledWith(
      expect.objectContaining({ businessUnitId: AUTH_BUSINESS_UNIT_ID, roleId: 4 }),
    );
    expect(getPermissions).not.toHaveBeenCalledWith(expect.objectContaining({ businessUnitId: 1 }));
  });

  it('rendersACanViewColumnBoundToTheCanViewFlag', async () => {
    renderPage();
    await selectRole('Sales Manager');

    expect(await screen.findByLabelText('Can View on Users')).toBeChecked();
    expect(screen.getByLabelText('Can Edit on Users')).toBeChecked();
    expect(screen.getByLabelText('Can Create on Users')).not.toBeChecked();
    // A module with no row at all reads as no access on every flag.
    expect(screen.getByLabelText('Can View on Leads')).not.toBeChecked();
  });

  it('showsError_whenPermissionUpdateReturns403', async () => {
    updatePermission.mockRejectedValue(forbidden('You cannot grant permissions to your own role.'));
    renderPage();
    await selectRole('Sales Manager');

    fireEvent.click(await screen.findByLabelText('Can Create on Users'));

    // Before this fix the mutation had `onSuccess` only: the rejection was swallowed entirely and
    // nothing anywhere on screen changed.
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/role does not permit|administrator/i);
  });

  it('bulkApply_issuesSingleBulkCall_notPerModule', async () => {
    renderPage();
    await selectRole('Sales Manager');

    fireEvent.click(await screen.findByLabelText('Toggle Can Create on every listed module'));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    // One transactional request replaces the ~51 sequential writes the loop used to issue.
    expect(createPermission).not.toHaveBeenCalled();
    expect(updatePermission).not.toHaveBeenCalled();

    const request = bulkApply.mock.calls[0][0];
    expect(request.roleId).toBe(4);
    expect(request.entries).toHaveLength(MODULES.items.length);
    // Flags outside the toggled column are carried through unchanged, not reset to false.
    expect(request.entries).toContainEqual(
      { moduleId: 3, canView: true, canCreate: true, canEdit: true, canDelete: false },
    );
    // `businessUnitId` is never in the body — the server takes it from the caller's claim.
    expect(request).not.toHaveProperty('businessUnitId');
  });

  it('revokeAll_sendsCanViewFalse', async () => {
    renderPage();
    await selectRole('Sales Manager');

    fireEvent.click(await screen.findByRole('button', { name: /revoke all access/i }));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    const request = bulkApply.mock.calls[0][0];
    expect(request.entries).toHaveLength(MODULES.items.length);
    // An all-false row is only truthfully "no access" once canView is part of it. Sending
    // canView:false is the whole point of the button.
    for (const entry of request.entries) {
      expect(entry).toMatchObject({ canView: false, canCreate: false, canEdit: false, canDelete: false });
    }
  });

  it('grantFullAccess_sendsEveryFlagTrue', async () => {
    renderPage();
    await selectRole('Sales Manager');

    fireEvent.click(await screen.findByRole('button', { name: /grant full access/i }));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    for (const entry of bulkApply.mock.calls[0][0].entries) {
      expect(entry).toMatchObject({ canView: true, canCreate: true, canEdit: true, canDelete: true });
    }
  });

  it('doesNotShowSuccessToast_whenBulkFails', async () => {
    bulkApply.mockRejectedValue(forbidden('You cannot grant a permission you do not hold.'));
    renderPage();
    await selectRole('Sales Manager');

    fireEvent.click(await screen.findByRole('button', { name: /revoke all access/i }));

    // The success snackbar used to fire unconditionally after the loop, so a denied bulk change
    // reported "All modules updated" while nothing had been written.
    const alerts = await screen.findAllByRole('alert');
    expect(alerts.length).toBeGreaterThan(0);
    for (const alert of alerts) {
      expect(alert).not.toHaveTextContent(/updated|granted|revoked/i);
    }
    expect(screen.queryByText(/all access revoked/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/column permissions updated/i)).not.toBeInTheDocument();
  });

  it('showsTheReasonOnThePage_whenBulkFails_notOnlyInAToast', async () => {
    bulkApply.mockRejectedValue(forbidden('You cannot grant a permission you do not hold.'));
    renderPage();
    await selectRole('Sales Manager');

    fireEvent.click(await screen.findByRole('button', { name: /revoke all access/i }));

    await waitFor(() => {
      const alerts = screen.getAllByRole('alert');
      expect(alerts.some(a => /role does not permit|administrator/i.test(a.textContent ?? ''))).toBe(true);
    });
  });

  it('singleToggle_sendsTheAuthenticatedBusinessUnitAndAllFourFlags', async () => {
    renderPage();
    await selectRole('Sales Manager');

    // "Leads" has no existing row, so this is a create.
    fireEvent.click(await screen.findByLabelText('Can View on Leads'));

    await waitFor(() => expect(createPermission).toHaveBeenCalledTimes(1));
    expect(createPermission).toHaveBeenCalledWith(expect.objectContaining({
      roleId: 4,
      moduleId: 9,
      businessUnitId: AUTH_BUSINESS_UNIT_ID,
      canView: true,
      canCreate: false,
      canEdit: false,
      canDelete: false,
    }));
  });

  it('everyMatrixCheckboxHasAnAccessibleName', async () => {
    renderPage();
    await selectRole('Sales Manager');

    await screen.findByLabelText('Can View on Users');
    const table = screen.getByRole('table', { name: /module permissions for sales manager/i });
    for (const box of within(table).getAllByRole('checkbox')) {
      expect(box).toHaveAccessibleName();
    }
  });
});
