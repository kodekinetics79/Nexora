import { beforeEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import {
  AuthProvider,
  PERMISSION_SCHEMA_VERSION,
  useAuth,
  type Permission,
} from './AuthContext';

const getMyPermissions = vi.fn();

vi.mock('../api/services/userService', () => ({
  default: {
    getMyPermissions: (...args: unknown[]) => getMyPermissions(...args),
  },
}));

/** Any non-JWT string: `jwtDecode` throws, the expiry check yields null and the session stands. */
const TOKEN = 'test-token';

const permission = (overrides: Partial<Permission> & { moduleName: string }): Permission => ({
  moduleId: 1,
  canView: false,
  canCreate: false,
  canEdit: false,
  canDelete: false,
  ...overrides,
});

const mePayload = (overrides: Record<string, unknown> = {}) => ({
  userId: 2,
  roleId: 84,
  roleName: 'Super Admin',
  businessUnitId: 1,
  isSuperAdmin: false,
  isManager: false,
  permissions: [],
  ...overrides,
});

/** Surfaces the pieces of the context under assertion as plain text. */
const Probe = () => {
  const { hasPermission, permissionsError, userData } = useAuth();
  return (
    <div>
      <span data-testid="view">{String(hasPermission('Users', 'view'))}</span>
      <span data-testid="create">{String(hasPermission('Users', 'create'))}</span>
      <span data-testid="edit">{String(hasPermission('Users', 'edit'))}</span>
      <span data-testid="delete">{String(hasPermission('Users', 'delete'))}</span>
      <span data-testid="unknown-module">{String(hasPermission('Leads', 'view'))}</span>
      <span data-testid="role">{userData.roleName ?? 'none'}</span>
      <span data-testid="error">{permissionsError ?? 'none'}</span>
    </div>
  );
};

const renderAuth = () => render(<AuthProvider><Probe /></AuthProvider>);

describe('AuthContext', () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    getMyPermissions.mockReset();
  });

  it('viewPermission_requiresCanViewTrue', async () => {
    // A row that exists but grants nothing. Under the old rule ("in the list ⇒ can view") this
    // returned true, which is what made "Revoke All Access" hand out read on every module.
    localStorage.setItem('token', TOKEN);
    getMyPermissions.mockResolvedValue(mePayload({
      permissions: [permission({ moduleName: 'Users', canView: false, canEdit: true })],
    }));

    renderAuth();

    await waitFor(() => expect(screen.getByTestId('role')).toHaveTextContent('Super Admin'));
    expect(screen.getByTestId('view')).toHaveTextContent('false');
    expect(screen.getByTestId('edit')).toHaveTextContent('true');
    expect(screen.getByTestId('create')).toHaveTextContent('false');
    expect(screen.getByTestId('delete')).toHaveTextContent('false');
  });

  it('viewPermission_isGrantedWhenCanViewTrue', async () => {
    localStorage.setItem('token', TOKEN);
    getMyPermissions.mockResolvedValue(mePayload({
      permissions: [permission({ moduleName: 'Users', canView: true })],
    }));

    renderAuth();

    await waitFor(() => expect(screen.getByTestId('view')).toHaveTextContent('true'));
    // A module with no row at all stays denied — absence is never a grant.
    expect(screen.getByTestId('unknown-module')).toHaveTextContent('false');
  });

  it('viewPermission_deniesWhenCanViewIsAbsent_preCanViewPayload', async () => {
    // A backend that predates the CanView column omits the field entirely. `undefined` must fail
    // closed rather than inherit the old row-exists semantics.
    localStorage.setItem('token', TOKEN);
    getMyPermissions.mockResolvedValue(mePayload({
      permissions: [{ moduleId: 1, moduleName: 'Users', canCreate: true }],
    }));

    renderAuth();

    await waitFor(() => expect(screen.getByTestId('create')).toHaveTextContent('true'));
    expect(screen.getByTestId('view')).toHaveTextContent('false');
  });

  it('superAdmin_shortCircuitsAllChecks', async () => {
    localStorage.setItem('token', TOKEN);
    getMyPermissions.mockResolvedValue(mePayload({ isSuperAdmin: true, permissions: [] }));

    renderAuth();

    // No rows at all, yet every action is permitted: the server bypasses module checks for super
    // admins, so the client must render the same authority or the UI contradicts the API.
    await waitFor(() => expect(screen.getByTestId('view')).toHaveTextContent('true'));
    expect(screen.getByTestId('create')).toHaveTextContent('true');
    expect(screen.getByTestId('edit')).toHaveTextContent('true');
    expect(screen.getByTestId('delete')).toHaveTextContent('true');
    expect(screen.getByTestId('unknown-module')).toHaveTextContent('true');
  });

  it('discardsCachedSnapshot_whenSchemaVersionMismatches', async () => {
    // A snapshot written before `canView` existed: under v1 rules this row meant "can view".
    localStorage.setItem('token', TOKEN);
    localStorage.setItem('userData', JSON.stringify({
      id: 2,
      roleName: 'Stale Role',
      isSuperAdmin: false,
      permissions: [{ id: 1, roleId: 84, moduleId: 1, moduleName: 'Users', canCreate: true }],
    }));

    let resolveLoad: (value: unknown) => void = () => {};
    getMyPermissions.mockReturnValue(new Promise((resolve) => { resolveLoad = resolve; }));

    renderAuth();

    // Before the server answers, nothing from the stale snapshot is trusted.
    await waitFor(() => expect(screen.getByTestId('role')).toHaveTextContent('none'));
    expect(screen.getByTestId('view')).toHaveTextContent('false');
    expect(screen.getByTestId('create')).toHaveTextContent('false');
    expect(localStorage.getItem('userData')).toBeNull();

    resolveLoad(mePayload({
      roleName: 'Fresh Role',
      permissions: [permission({ moduleName: 'Users', canView: true })],
    }));

    await waitFor(() => expect(screen.getByTestId('role')).toHaveTextContent('Fresh Role'));
    expect(screen.getByTestId('view')).toHaveTextContent('true');
  });

  it('keepsCachedSnapshot_whenSchemaVersionMatches', async () => {
    localStorage.setItem('token', TOKEN);
    localStorage.setItem('userData', JSON.stringify({
      id: 2,
      roleName: 'Cached Role',
      schemaVersion: PERMISSION_SCHEMA_VERSION,
      permissions: [permission({ moduleName: 'Users', canView: true })],
    }));
    getMyPermissions.mockReturnValue(new Promise(() => {}));

    renderAuth();

    expect(screen.getByTestId('role')).toHaveTextContent('Cached Role');
    expect(screen.getByTestId('view')).toHaveTextContent('true');
  });

  it('surfacesError_whenMePermissionsFails', async () => {
    localStorage.setItem('token', TOKEN);
    getMyPermissions.mockRejectedValue({
      isAxiosError: true,
      message: 'Request failed with status code 403',
      config: { url: '/api/User/me/permissions', method: 'get' },
      response: { status: 403, data: { message: 'Permission denied' } },
    });

    renderAuth();

    // The old bootstrap swallowed this into `console.error` and continued with an empty permission
    // set, which is indistinguishable from a user who legitimately has no access.
    await waitFor(() => expect(screen.getByTestId('error')).not.toHaveTextContent('none'));
    expect(screen.getByTestId('error').textContent).toMatch(/role does not permit|administrator/i);
    expect(screen.getByTestId('view')).toHaveTextContent('false');
  });

  it('doesNotLoadPermissions_whenThereIsNoSession', () => {
    renderAuth();
    expect(getMyPermissions).not.toHaveBeenCalled();
    expect(screen.getByTestId('view')).toHaveTextContent('false');
  });

  it('stampsTheSchemaVersionOnEverySnapshotItWrites', async () => {
    localStorage.setItem('token', TOKEN);
    getMyPermissions.mockResolvedValue(mePayload({ roleName: 'Sales Manager' }));

    renderAuth();

    await waitFor(() => expect(screen.getByTestId('role')).toHaveTextContent('Sales Manager'));
    const cached = JSON.parse(localStorage.getItem('userData') ?? '{}');
    expect(cached.schemaVersion).toBe(PERMISSION_SCHEMA_VERSION);
  });
});
