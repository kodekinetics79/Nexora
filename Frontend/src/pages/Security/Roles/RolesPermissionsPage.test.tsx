import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SnackbarProvider } from 'notistack';
import { MemoryRouter } from 'react-router-dom';
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

const getSetups = vi.fn();
const getModules = vi.fn();
const getPermissions = vi.fn();
const createPermission = vi.fn();
const updatePermission = vi.fn();
const bulkApply = vi.fn();

// Roles are read from Setup_Master, not /api/User/Roles, because this screen has to know each
// role's AUTHORITY TIER before it can decide whether a permission matrix means anything for it.
vi.mock('../../../api/services/setupService', () => ({
  default: { getAll: (params: unknown) => getSetups(params) },
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

const role = (setupId: number, setupName: string, roleRank: number) => ({
  setupId, setupType: 'Role', setupCode: null, setupName,
  description: null, parentSetupId: null, roleRank, isActive: true,
});

const SETUPS = {
  items: [
    role(1, 'Super_Administrator', 30),
    role(4, 'Sales Manager', 10),
    role(5, 'Sales Representative', 0),
    role(6, 'Bespoke Desk', 0),
    // A non-role row, to prove the picker narrows Setup_Master to roles rather than listing
    // every lookup value in the tenant.
    {
      setupId: 9, setupType: 'DiscountType', setupCode: 'PERCENTAGE', setupName: 'Percentage',
      description: null, parentSetupId: null, roleRank: 0, isActive: true,
    },
  ],
  totalCount: 5,
  pageNumber: 1,
  pageSize: 5000,
};

const MODULES = {
  items: [
    { id: 1, moduleName: 'Dashboard', isActive: true },
    { id: 3, moduleName: 'Users', isActive: true },
    { id: 9, moduleName: 'Leads', isActive: true },
    // Ungoverned: a live Module row that no [RequireModulePermission] anywhere in the backend
    // consults. It must never become a checkbox.
    { id: 42, moduleName: 'UOM', isActive: true },
  ],
  totalCount: 4,
  pageNumber: 1,
  pageSize: 100,
};

/** The fixture modules that actually grant something — the only ones the matrix may render. */
const ENFORCED_COUNT = 3;

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
    // The empty state links to the screen that creates roles, so the page now needs a router the
    // way it already needs a query client. Without one, the <Link> throws and the whole page
    // renders as an empty <div> — which is what this helper produced before the wrapper was added.
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <SnackbarProvider>
          <RolesPermissionsPage />
        </SnackbarProvider>
      </QueryClientProvider>
    </MemoryRouter>,
  );
};

/** Picks a role from the MUI select. */
const selectRole = async (name: string) => {
  // MUI's Select opens on mousedown, not click.
  fireEvent.mouseDown(await screen.findByRole('combobox', { name: /select role/i }));
  fireEvent.click(await screen.findByRole('option', { name }));
};

/**
 * Opens the Advanced drawer, which is where the matrix now lives.
 *
 * Every matrix assertion goes through here, and that is the point: the checkboxes are reachable
 * but they are no longer the first thing anybody meets.
 */
const openAdvanced = async () => {
  fireEvent.click(await screen.findByRole('button', { name: /advanced .* customise this role/i }));
};

describe('RolesPermissionsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getSetups.mockResolvedValue(SETUPS);
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

  // ── the default path ───────────────────────────────────────────────────────

  it('doesNotShowTheMatrix_untilAdvancedIsOpened', async () => {
    // THE change this screen exists for. Somebody setting up a sales rep should never meet 212
    // checkboxes, or the word "module", on the default path.
    renderPage();
    await selectRole('Sales Representative');

    expect(await screen.findByText(/standard setups/i)).toBeInTheDocument();
    expect(screen.queryByLabelText('Can View on Leads')).not.toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();

    await openAdvanced();
    expect(await screen.findByLabelText('Can View on Leads')).toBeInTheDocument();
  });

  it('offersExactlyThreeLevelsOfAuthority_andNeverTheOwner', async () => {
    renderPage();
    await selectRole('Sales Representative');

    expect(await screen.findByRole('button', { name: /apply the standard sales representative setup/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /apply the standard sales manager setup/i })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /apply the standard system administrator setup/i })).toBeInTheDocument();
    // Owner is provisioned when the organization is created, never offered here.
    expect(screen.queryByRole('button', { name: /owner/i })).not.toBeInTheDocument();
  });

  it('appliesAWholeRoleSetup_inASingleClick', async () => {
    // The measured win: the standard rep is 23 ticked flags across 14 modules, and every one of
    // them used to be a separate click. It is one click now — and it writes EVERY enforced module,
    // including the ones the preset leaves empty, so the role genuinely matches the standard
    // rather than merely gaining its grants on top of whatever was there before.
    renderPage();
    await selectRole('Sales Representative');

    fireEvent.click(await screen.findByRole('button', { name: /apply the standard sales representative setup/i }));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    const request = bulkApply.mock.calls[0][0];
    expect(request.roleId).toBe(5);
    expect(request.entries).toHaveLength(ENFORCED_COUNT);

    // Dashboard is read-only for a representative...
    expect(request.entries).toContainEqual(
      { moduleId: 1, canView: true, canCreate: false, canEdit: false, canDelete: false },
    );
    // ...Leads is theirs to work but never to delete...
    expect(request.entries).toContainEqual(
      { moduleId: 9, canView: true, canCreate: true, canEdit: true, canDelete: false },
    );
    // ...and Users, which the preset does not name, is written as an explicit revoke.
    expect(request.entries).toContainEqual(
      { moduleId: 3, canView: false, canCreate: false, canEdit: false, canDelete: false },
    );
  });

  it('explainsWhyAPresetCannotBeApplied_ratherThanFailingOnClick', async () => {
    // A control that cannot work says why, in words. This screen writes permission lines; it does
    // not change a role's level of authority.
    renderPage();
    await selectRole('Sales Representative');

    const managerCard = await screen.findByRole('button', { name: /apply the standard sales manager setup/i });
    expect(managerCard).toBeDisabled();
    expect(managerCard).toHaveTextContent(/different level of authority/i);
  });

  it('refusesToApplyAPreset_whenTheModuleListCouldNotLoad', async () => {
    // Refuse rather than guess. Applying a preset against an empty module list would post an empty
    // change set: the server applies nothing, and the snackbar reports that the standard setup was
    // applied. A silent wrong answer about who can do what is the one failure this screen must
    // never produce, so the presets are not offered at all until the list is there.
    getModules.mockRejectedValue(forbidden('You may not read modules.'));
    renderPage();
    await selectRole('Sales Representative');

    await waitFor(() => {
      const alerts = screen.getAllByRole('alert');
      expect(alerts.some(a => /could not be loaded/i.test(a.textContent ?? ''))).toBe(true);
    });
    expect(screen.queryByRole('button', { name: /apply the standard sales representative setup/i }))
      .not.toBeInTheDocument();
    expect(bulkApply).not.toHaveBeenCalled();
  });

  // ── the administrator case ─────────────────────────────────────────────────

  it('rendersOneSentenceAndNoMatrix_forARoleThatAdministersEverything', async () => {
    // A role at Administrator or above satisfies every module check by RANK before the server
    // reads a permission row. A curated set of ticks for it would state the opposite of what is
    // enforced, so there are none — not even behind Advanced.
    renderPage();
    await selectRole('Super_Administrator');

    expect(await screen.findByText(/administers everything/i)).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /advanced/i })).not.toBeInTheDocument();
    expect(screen.queryByText(/standard setups/i)).not.toBeInTheDocument();
  });

  // ── permissions that grant nothing ─────────────────────────────────────────

  it('neverRendersACheckboxForAModuleNothingEnforces', async () => {
    // "UOM" is a live Module row with no [RequireModulePermission] anywhere in the backend.
    // Ticking it grants nothing and clearing it revokes nothing, while the row states in four
    // columns that it did both.
    renderPage();
    await selectRole('Sales Manager');
    await openAdvanced();

    expect(await screen.findByLabelText('Can View on Users')).toBeInTheDocument();
    expect(screen.queryByLabelText('Can View on UOM')).not.toBeInTheDocument();
    expect(screen.queryByText('UOM')).not.toBeInTheDocument();
  });

  it('excludesAnUnenforcedModuleFromEveryBulkWrite', async () => {
    // Not merely hidden: a write that included it would create a RolePermissions row for a module
    // no endpoint consults, which is how the orphan rows keep looking legitimate.
    renderPage();
    await selectRole('Sales Manager');
    await openAdvanced();

    fireEvent.click(await screen.findByRole('button', { name: /revoke all access/i }));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    const request = bulkApply.mock.calls[0][0];
    expect(request.entries).toHaveLength(ENFORCED_COUNT);
    expect(request.entries.map((entry: { moduleId: number }) => entry.moduleId)).not.toContain(42);
  });

  // ── drift and the way back ─────────────────────────────────────────────────

  it('saysHowFarARoleHasDriftedFromItsPreset_andOffersOneClickBack', async () => {
    // Getting out must be as easy as getting in. Somebody who wandered into Advanced and changed
    // things needs a way back that does not require remembering what the standard was.
    renderPage();
    await selectRole('Sales Representative');

    const drift = await screen.findByText(/differs from the standard sales representative setup/i);
    expect(drift).toBeInTheDocument();
    // Plain words, never a count of checkboxes.
    expect(drift.textContent).not.toMatch(/checkbox/i);

    fireEvent.click(screen.getByRole('button', { name: /reset to standard/i }));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    expect(bulkApply.mock.calls[0][0].entries).toContainEqual(
      { moduleId: 9, canView: true, canCreate: true, canEdit: true, canDelete: false },
    );
  });

  it('showsNoDriftLine_forARoleThatMatchesNoPreset', async () => {
    // A bespoke role has nothing to be measured against, and inventing a comparison would invite
    // a "reset" that silently rewrites a role somebody built on purpose.
    renderPage();
    await selectRole('Bespoke Desk');

    await screen.findByText(/standard setups/i);
    expect(screen.queryByText(/differs from the standard/i)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /reset to standard/i })).not.toBeInTheDocument();
  });

  // ── the role list ──────────────────────────────────────────────────────────

  it('listsOnlyRoleRows_notEverySetupMasterValue', async () => {
    renderPage();
    fireEvent.mouseDown(await screen.findByRole('combobox', { name: /select role/i }));

    expect(await screen.findByRole('option', { name: 'Sales Manager' })).toBeInTheDocument();
    expect(screen.queryByRole('option', { name: 'Percentage' })).not.toBeInTheDocument();
  });

  it('rendersEmptyState_whenRolesListIsEmpty', async () => {
    // This is the state production was actually in: the roles endpoint answered 200 with [], so
    // nothing errored and the matrix sat on its "please select a role" placeholder forever.
    getSetups.mockResolvedValue({ items: [], totalCount: 0, pageNumber: 1, pageSize: 5000 });
    renderPage();

    expect(
      await screen.findByText(/no roles are configured for this business unit/i),
    ).toBeInTheDocument();
    // The unusable role picker is gone rather than rendered empty.
    expect(screen.queryByRole('combobox', { name: /select role/i })).not.toBeInTheDocument();
  });

  it('rendersAnExplainedErrorState_whenRolesFailToLoad', async () => {
    getSetups.mockRejectedValue(forbidden('You may not read roles.'));
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

  // ── the matrix, which still works ──────────────────────────────────────────

  it('rendersACanViewColumnBoundToTheCanViewFlag', async () => {
    renderPage();
    await selectRole('Sales Manager');
    await openAdvanced();

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
    await openAdvanced();

    fireEvent.click(await screen.findByLabelText('Can Create on Users'));

    // Before this fix the mutation had `onSuccess` only: the rejection was swallowed entirely and
    // nothing anywhere on screen changed.
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent(/role does not permit|administrator/i);
  });

  it('bulkApply_issuesSingleBulkCall_notPerModule', async () => {
    renderPage();
    await selectRole('Sales Manager');
    await openAdvanced();

    fireEvent.click(await screen.findByLabelText('Toggle Can Create on every listed module'));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    // One transactional request replaces the ~51 sequential writes the loop used to issue.
    expect(createPermission).not.toHaveBeenCalled();
    expect(updatePermission).not.toHaveBeenCalled();

    const request = bulkApply.mock.calls[0][0];
    expect(request.roleId).toBe(4);
    expect(request.entries).toHaveLength(ENFORCED_COUNT);
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
    await openAdvanced();

    fireEvent.click(await screen.findByRole('button', { name: /revoke all access/i }));

    await waitFor(() => expect(bulkApply).toHaveBeenCalledTimes(1));
    const request = bulkApply.mock.calls[0][0];
    expect(request.entries).toHaveLength(ENFORCED_COUNT);
    // An all-false row is only truthfully "no access" once canView is part of it. Sending
    // canView:false is the whole point of the button.
    for (const entry of request.entries) {
      expect(entry).toMatchObject({ canView: false, canCreate: false, canEdit: false, canDelete: false });
    }
  });

  it('grantFullAccess_sendsEveryFlagTrue', async () => {
    renderPage();
    await selectRole('Sales Manager');
    await openAdvanced();

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
    await openAdvanced();

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
    await openAdvanced();

    fireEvent.click(await screen.findByRole('button', { name: /revoke all access/i }));

    await waitFor(() => {
      const alerts = screen.getAllByRole('alert');
      expect(alerts.some(a => /role does not permit|administrator/i.test(a.textContent ?? ''))).toBe(true);
    });
  });

  it('singleToggle_sendsTheAuthenticatedBusinessUnitAndAllFourFlags', async () => {
    renderPage();
    await selectRole('Sales Manager');
    await openAdvanced();

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
    await openAdvanced();

    await screen.findByLabelText('Can View on Users');
    const table = screen.getByRole('table', { name: /module permissions for sales manager/i });
    for (const box of within(table).getAllByRole('checkbox')) {
      expect(box).toHaveAccessibleName();
    }
  });
});
