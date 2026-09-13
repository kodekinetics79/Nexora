import { useState } from 'react';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AuthProvider, PERMISSIONS_STALE_MS, type Permission } from '../../context/AuthContext';
import PermissionGuard from './PermissionGuard';

/**
 * The owner's "refresh is irritating" on production, pinned at the seam where it happened.
 *
 * Every 60 s the permission re-check made create/edit answers false while it was in flight, and a
 * route-level create/edit guard rendered `null` for that second. Upload documents and the quote,
 * order and shipment editors unmounted and came back empty. These tests mount the REAL provider and
 * the REAL guard — a mocked `useAuth` cannot observe the flip at all, which is how it shipped.
 */

const getMyPermissions = vi.fn();

vi.mock('../../api/services/userService', () => ({
  default: {
    getMyPermissions: (...args: unknown[]) => getMyPermissions(...args),
  },
}));

const leads = (overrides: Partial<Permission>): Permission => ({
  moduleId: 1,
  moduleName: 'Leads',
  canView: true,
  canCreate: false,
  canEdit: false,
  canDelete: false,
  ...overrides,
});

const me = (permissions: Permission[]) => ({
  userId: 2,
  roleId: 84,
  roleName: 'Sales Rep',
  businessUnitId: 1,
  isSuperAdmin: false,
  isManager: false,
  hasModuleAuthorityByRank: false,
  permissions,
  entitlements: [],
});

/** A stand-in for Upload documents: local state only, exactly what a remount throws away. */
const UploadScreen = () => {
  const [note, setNote] = useState('');
  return (
    <label>
      Upload note
      <input value={note} onChange={(event) => setNote(event.target.value)} />
    </label>
  );
};

const renderScreen = () => render(
  <MemoryRouter>
    <AuthProvider>
      <PermissionGuard moduleName="Leads" action="create" page>
        <UploadScreen />
      </PermissionGuard>
    </AuthProvider>
  </MemoryRouter>,
);

describe('PermissionGuard around a whole create/edit screen', () => {
  beforeEach(() => {
    vi.useRealTimers();
    localStorage.clear();
    sessionStorage.clear();
    getMyPermissions.mockReset();
    localStorage.setItem('token', 'test-token');
  });

  it('keeps the screen mounted, with what was typed, across the scheduled 60 s permission check', async () => {
    vi.useFakeTimers();
    let answerRecheck: (value: unknown) => void = () => {};
    getMyPermissions
      .mockResolvedValueOnce(me([leads({ canCreate: true })]))
      .mockReturnValueOnce(new Promise((resolve) => { answerRecheck = resolve; }));

    renderScreen();
    await act(async () => { await Promise.resolve(); });

    const input = screen.getByLabelText('Upload note');
    fireEvent.change(input, { target: { value: '40 nos cable tray 300mm' } });

    await act(async () => { await vi.advanceTimersByTimeAsync(PERMISSIONS_STALE_MS); });
    expect(getMyPermissions).toHaveBeenCalledTimes(2);

    // Same DOM node, same value, while the re-check is in flight...
    expect(screen.getByLabelText('Upload note')).toBe(input);
    expect(input).toHaveValue('40 nos cable tray 300mm');
    expect(screen.queryByRole('status', { name: /checking your access/i })).not.toBeInTheDocument();

    // ...and after it answers.
    await act(async () => {
      answerRecheck(me([leads({ canCreate: true })]));
      await Promise.resolve();
    });
    expect(screen.getByLabelText('Upload note')).toBe(input);
    expect(input).toHaveValue('40 nos cable tray 300mm');
    vi.useRealTimers();
  });

  it('explains a permissions read that failed, with a retry, instead of rendering nothing', async () => {
    getMyPermissions.mockRejectedValue({
      isAxiosError: true,
      message: 'Request failed with status code 503',
      config: { url: '/api/User/me/permissions', method: 'get' },
      response: { status: 503, data: {} },
    });

    renderScreen();

    expect(await screen.findByRole('heading', { name: 'We could not check your access' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument();
    expect(screen.queryByLabelText('Upload note')).not.toBeInTheDocument();
  });

  it('says it is checking while the first read of the session is on its way, not Access Denied', async () => {
    getMyPermissions.mockReturnValue(new Promise(() => {}));

    renderScreen();

    expect(await screen.findByRole('progressbar', { name: 'Checking your access' })).toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Access Denied' })).not.toBeInTheDocument();
  });

  it('names the missing grant when the answer really is no', async () => {
    getMyPermissions.mockResolvedValue(me([leads({ canCreate: false })]));

    renderScreen();

    expect(await screen.findByRole('heading', { name: 'Access Denied' })).toBeInTheDocument();
    expect(screen.getByText('Can Create')).toBeInTheDocument();
    expect(screen.queryByLabelText('Upload note')).not.toBeInTheDocument();
  });

  it('still simply hides a guarded CONTROL (not a screen) when the grant is missing', async () => {
    getMyPermissions.mockResolvedValue(me([leads({ canCreate: false })]));

    render(
      <MemoryRouter>
        <AuthProvider>
          <p>Leads list</p>
          <PermissionGuard moduleName="Leads" action="create">
            <button type="button">New lead</button>
          </PermissionGuard>
        </AuthProvider>
      </MemoryRouter>,
    );

    await act(async () => { await Promise.resolve(); });
    expect(screen.getByText('Leads list')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'New lead' })).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByRole('status')).not.toBeInTheDocument();
  });
});
