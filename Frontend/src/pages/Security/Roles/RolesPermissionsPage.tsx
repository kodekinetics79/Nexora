import React, { useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box,
  Typography,
  Paper,
  CircularProgress,
  MenuItem,
  Checkbox,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Button,
} from '@mui/material';

import rolePermissionService, {
  type RolePermissionBulkEntry,
  type RolePermissionDTO,
} from '../../../api/services/rolePermissionService';
import userService from '../../../api/services/userService';
import moduleService, { type ModuleDTO } from '../../../api/services/moduleService';
import SearchField from '../../../components/common/SearchField';
import { useAuth } from '../../../context/AuthContext';
import { EmptyState, ErrorState } from '../../../platform/components/States';
import { handleApiError } from '../../../utils/errorHandler';
import { toPresentableError } from '../../../utils/apiErrors';
import { useSnackbar } from 'notistack';

/**
 * "What failed" + "why", as one sentence.
 *
 * `toPresentableError`'s `fallbackMessage` REPLACES the status copy whenever the server's own text
 * is not trusted for display — which is the case for 403. Passing a domain fallback there would
 * therefore throw away the only line that explains a permission denial, so the two are joined
 * instead of one overriding the other.
 */
const describeFailure = (error: unknown, context: string): string =>
  `${context} ${toPresentableError(error).message}`;

/**
 * Resolves a translation, falling back to English copy when the key is not defined.
 *
 * i18next returns the KEY ITSELF for a missing translation, so the `t('some_key') || 'Fallback'`
 * idiom used across this codebase renders the literal string "some_key" on screen instead of the
 * fallback — `t()` never returns something falsy. (`retry` and `users_load_failed` are two keys
 * that were already reaching users this way.) This picks the fallback whenever the lookup did not
 * actually resolve.
 */
const label = (t: (key: string) => string, key: string, fallback: string): string => {
  const translated = t(key);
  return !translated || translated === key ? fallback : translated;
};

/** The four grant flags, in the order they appear as matrix columns. */
type PermissionFlag = 'canView' | 'canCreate' | 'canEdit' | 'canDelete';

const FLAG_COLUMNS: ReadonlyArray<{ flag: PermissionFlag; i18nKey: string; label: string }> = [
  { flag: 'canView', i18nKey: 'can_view', label: 'Can View' },
  { flag: 'canCreate', i18nKey: 'can_create', label: 'Can Create' },
  { flag: 'canEdit', i18nKey: 'can_edit', label: 'Can Edit' },
  { flag: 'canDelete', i18nKey: 'can_delete', label: 'Can Delete' },
];

const TOTAL_COLUMNS = FLAG_COLUMNS.length + 1;

/** `undefined` (a row from a backend that predates the CanView column) must never read as granted. */
const flagOf = (permission: RolePermissionDTO | undefined, flag: PermissionFlag): boolean =>
  permission?.[flag] === true;

const RolesPermissionsPage: React.FC = () => {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { userData } = useAuth();

  // The signed-in tenant. The previous code hardcoded `1` behind comments claiming roles were
  // "global" — they are not: Setup_Master rows and RolePermissions rows are both scoped by
  // BusinessUnitID. The server overrides this from the caller's claim, so the hardcoded value was
  // not a live isolation hole, but it would have become one the moment anyone trusted the comment.
  const businessUnitId = userData.businessUnitId;

  const [selectedRoleId, setSelectedRoleId] = useState<number | ''>('');
  const [search, setSearch] = useState('');
  /** Set when a bulk apply fails, so the table itself explains the failure and not just a toast. */
  const [bulkError, setBulkError] = useState<string | null>(null);

  const rolesQuery = useQuery({
    queryKey: ['roles', businessUnitId],
    queryFn: () => userService.getRoles(),
  });

  const modulesQuery = useQuery({
    queryKey: ['modules'],
    queryFn: () => moduleService.getAll({ pageSize: 100 }),
  });

  const permissionsQuery = useQuery({
    queryKey: ['permissions', businessUnitId, selectedRoleId],
    queryFn: () => rolePermissionService.getAll({
      businessUnitId,
      roleId: selectedRoleId === '' ? undefined : selectedRoleId,
      pageSize: 1000,
    }),
    enabled: selectedRoleId !== '',
  });

  const roles = rolesQuery.data;
  const permissions = permissionsQuery.data;
  const loadingPermissions = permissionsQuery.isLoading;

  const filteredModules: ModuleDTO[] = useMemo(
    () => (modulesQuery.data?.items ?? []).filter(m =>
      m.moduleName.toLowerCase().includes(search.toLowerCase())
    ),
    [modulesQuery.data, search],
  );

  const permissionByModule = useMemo(() => {
    const map = new Map<number, RolePermissionDTO>();
    for (const item of permissions?.items ?? []) map.set(item.moduleId, item);
    return map;
  }, [permissions]);

  const invalidatePermissions = () => {
    queryClient.invalidateQueries({ queryKey: ['permissions'] });
  };

  const updatePermissionMutation = useMutation({
    mutationFn: (permission: Record<string, unknown> & { id?: number }) =>
      permission.id
        ? rolePermissionService.update(permission.id, permission)
        : rolePermissionService.create(permission),
    onSuccess: invalidatePermissions,
    // Without this, a 403 from the escalation guards was completely invisible: the mutation
    // rejected, nothing rendered, and (for bulk operations) a green success toast fired anyway.
    onError: (error: unknown) => handleApiError(error),
  });

  const bulkMutation = useMutation({
    mutationFn: (request: { roleId: number; reason: string; entries: RolePermissionBulkEntry[] }) =>
      rolePermissionService.bulkApply(request),
    onSuccess: invalidatePermissions,
    onError: (error: unknown) => handleApiError(error),
  });

  const isBusy = loadingPermissions || bulkMutation.isPending;

  /** Current state of a module's row, used as the base for a single-flag edit. */
  const entryFor = (moduleId: number, overrides?: Partial<RolePermissionBulkEntry>): RolePermissionBulkEntry => {
    const existing = permissionByModule.get(moduleId);
    return {
      moduleId,
      canView: flagOf(existing, 'canView'),
      canCreate: flagOf(existing, 'canCreate'),
      canEdit: flagOf(existing, 'canEdit'),
      canDelete: flagOf(existing, 'canDelete'),
      ...overrides,
    };
  };

  const handleToggle = (module: ModuleDTO, field: PermissionFlag) => {
    if (selectedRoleId === '') return;
    const existing = permissionByModule.get(module.id);
    const next = entryFor(module.id, { [field]: !flagOf(existing, field) } as Partial<RolePermissionBulkEntry>);

    updatePermissionMutation.mutate({
      ...(existing ? { id: existing.id } : {}),
      roleId: selectedRoleId,
      moduleId: module.id,
      businessUnitId,
      canView: next.canView,
      canCreate: next.canCreate,
      canEdit: next.canEdit,
      canDelete: next.canDelete,
    });
  };

  /**
   * Applies `entries` in ONE transactional request.
   *
   * The loop this replaces issued a write per module (~51 sequential calls). A denial half-way
   * through left the role partially configured while the UI still announced success, because the
   * snackbar fired unconditionally after the loop. Now the server applies all or nothing, and the
   * success message only runs when the call actually resolved.
   */
  const applyBulk = async (entries: RolePermissionBulkEntry[], reason: string, successMessage: string) => {
    if (selectedRoleId === '') return;
    setBulkError(null);
    try {
      await bulkMutation.mutateAsync({ roleId: selectedRoleId, reason, entries });
      enqueueSnackbar(successMessage, { variant: 'success' });
    } catch (error) {
      // `onError` already raised the toast; this records the reason on the page so the failure
      // survives the snackbar and the user can read what is actually required.
      setBulkError(describeFailure(error, 'The permission changes were not applied.'));
    }
  };

  const handleBulkToggleColumn = (field: PermissionFlag, checked: boolean) => {
    const column = FLAG_COLUMNS.find(c => c.flag === field);
    const columnLabel = column ? label(t, column.i18nKey, column.label) : field;
    return applyBulk(
      filteredModules.map(m => entryFor(m.id, { [field]: checked } as Partial<RolePermissionBulkEntry>)),
      `${checked ? 'Grant' : 'Revoke'} ${columnLabel} across ${filteredModules.length} module(s)`,
      label(t, 'column_permissions_updated', 'Column permissions updated'),
    );
  };

  const handleBulkToggleRow = (module: ModuleDTO, checked: boolean) => {
    if (selectedRoleId === '') return;
    const existing = permissionByModule.get(module.id);

    updatePermissionMutation.mutate({
      ...(existing ? { id: existing.id } : {}),
      roleId: selectedRoleId,
      moduleId: module.id,
      businessUnitId,
      canView: checked,
      canCreate: checked,
      canEdit: checked,
      canDelete: checked,
    });
  };

  const handleAllRowsToggle = (checked: boolean) =>
    applyBulk(
      // `canView: false` is the point of the Revoke button. An all-false row is now genuinely no
      // access; previously it was a read grant, so "Revoke All Access" silently granted read on
      // every module in the product.
      filteredModules.map(m => ({
        moduleId: m.id,
        canView: checked,
        canCreate: checked,
        canEdit: checked,
        canDelete: checked,
      })),
      checked
        ? `Grant full access on ${filteredModules.length} module(s)`
        : `Revoke all access on ${filteredModules.length} module(s)`,
      checked
        ? (label(t, 'all_modules_updated', 'Full access granted on all listed modules'))
        : (label(t, 'all_access_revoked', 'All access revoked on the listed modules')),
    );

  const selectedRoleName = roles?.find(r => r.setupId === selectedRoleId)?.setupName;

  /** Everything above the matrix: the role picker plus the two whole-matrix actions. */
  const renderControls = () => (
    <Paper sx={{ p: 2, mb: 2, display: 'flex', gap: 3, alignItems: 'center', borderRadius: 2, justifyContent: 'space-between', flexWrap: 'wrap' }}>
      <Box sx={{ display: 'flex', gap: 3, alignItems: 'center', flexWrap: 'wrap' }}>
        <TextField
          select
          label={label(t, 'select_role', 'Select Role')}
          value={selectedRoleId}
          onChange={(e) => setSelectedRoleId(e.target.value === '' ? '' : Number(e.target.value))}
          sx={{ minWidth: 250 }}
          size="small"
        >
          {(roles ?? []).map(r => <MenuItem key={r.setupId} value={r.setupId}>{r.setupName}</MenuItem>)}
        </TextField>
        {selectedRoleId !== '' && (
          <Box sx={{ display: 'flex', gap: 1 }}>
            <Button
              variant="outlined"
              size="small"
              color="success"
              onClick={() => { void handleAllRowsToggle(true); }}
              disabled={isBusy}
            >
              {label(t, 'grant_full_access', 'Grant Full Access')}
            </Button>
            <Button
              variant="outlined"
              size="small"
              color="error"
              onClick={() => { void handleAllRowsToggle(false); }}
              disabled={isBusy}
            >
              {label(t, 'revoke_all_access', 'Revoke All Access')}
            </Button>
          </Box>
        )}
      </Box>
      <SearchField value={search} onChange={setSearch} placeholder={label(t, 'search_modules', 'Search modules...')} width={300} />
    </Paper>
  );

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" component="h1" sx={{ fontWeight: 800 }}>{label(t, 'roles_and_permissions', 'Roles & Permissions')}</Typography>
          <Typography variant="body2" color="text.secondary">{label(t, 'configure_module_access', 'Configure module-level access for system roles')}</Typography>
        </Box>
      </Box>

      {/* A failed role list is why this screen was blank: the request answered 200 with [], so no
          error state existed anywhere and the placeholder row was indistinguishable from
          "you haven't picked a role yet". Both outcomes are now named explicitly. */}
      {rolesQuery.isError ? (
        <Paper sx={{ borderRadius: 2, p: 2 }}>
          <ErrorState
            message={describeFailure(rolesQuery.error, 'The list of roles could not be loaded.')}
            onRetry={() => { void rolesQuery.refetch(); }}
          />
        </Paper>
      ) : rolesQuery.isLoading ? (
        <Paper sx={{ borderRadius: 2, p: 2 }}>
          <Box role="status" aria-live="polite" sx={{ display: 'flex', justifyContent: 'center', py: 6 }}>
            <CircularProgress size={28} aria-label={label(t, 'loading_roles', 'Loading roles')} />
          </Box>
        </Paper>
      ) : (roles ?? []).length === 0 ? (
        <Paper sx={{ borderRadius: 2, p: 2 }}>
          <EmptyState
            title={label(t, 'no_roles_configured', 'No roles are configured for this business unit')}
            message={
              t('no_roles_configured_help') ||
              'Roles are created under Setup → Setup Master with a type of "Role". Until at least one role exists there is nothing to assign permissions to.'
            }
            action={
              <Button variant="outlined" onClick={() => { void rolesQuery.refetch(); }}>
                {label(t, 'retry', 'Retry')}
              </Button>
            }
          />
        </Paper>
      ) : (
        <>
          {renderControls()}

          {bulkError && (
            <Paper sx={{ borderRadius: 2, mb: 2, p: 1 }}>
              <ErrorState
                minHeight={140}
                message={bulkError}
                onRetry={() => { void permissionsQuery.refetch(); }}
              />
            </Paper>
          )}

          <TableContainer component={Paper} sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Table
              size="small"
              aria-label={
                selectedRoleName
                  ? `Module permissions for ${selectedRoleName}`
                  : (label(t, 'roles_and_permissions', 'Roles & Permissions'))
              }
            >
              <TableHead>
                <TableRow sx={{ backgroundColor: 'action.hover' }}>
                  <TableCell sx={{ fontWeight: 700, py: 1.5 }}>
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                      {label(t, 'module_name', 'Module Name')}
                      {selectedRoleId !== '' && (
                        <Checkbox
                          size="small"
                          disabled={isBusy}
                          slotProps={{ input: { 'aria-label': 'Toggle every permission on every listed module' } }}
                          onChange={(e) => { void handleAllRowsToggle(e.target.checked); }}
                        />
                      )}
                    </Box>
                  </TableCell>
                  {FLAG_COLUMNS.map(({ flag, i18nKey, label: englishLabel }) => (
                    <TableCell key={flag} align="center" sx={{ fontWeight: 700 }}>
                      <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
                        {label(t, i18nKey, englishLabel)}
                        {selectedRoleId !== '' && (
                          <Checkbox
                            size="small"
                            disabled={isBusy}
                            slotProps={{ input: { 'aria-label': `Toggle ${englishLabel} on every listed module` } }}
                            onChange={(e) => { void handleBulkToggleColumn(flag, e.target.checked); }}
                          />
                        )}
                      </Box>
                    </TableCell>
                  ))}
                </TableRow>
              </TableHead>
              <TableBody>
                {selectedRoleId === '' ? (
                  <TableRow>
                    <TableCell colSpan={TOTAL_COLUMNS} align="center" sx={{ py: 8, color: 'text.secondary' }}>
                      {label(t, 'please_select_role', 'Please select a role above to view and configure permissions')}
                    </TableCell>
                  </TableRow>
                ) : permissionsQuery.isError ? (
                  <TableRow>
                    <TableCell colSpan={TOTAL_COLUMNS}>
                      <ErrorState
                        minHeight={200}
                        message={describeFailure(permissionsQuery.error, 'The permissions for this role could not be loaded.')}
                        onRetry={() => { void permissionsQuery.refetch(); }}
                      />
                    </TableCell>
                  </TableRow>
                ) : loadingPermissions ? (
                  <TableRow>
                    <TableCell colSpan={TOTAL_COLUMNS} align="center" sx={{ py: 8 }}>
                      <CircularProgress size={24} aria-label={label(t, 'loading_permissions', 'Loading permissions')} />
                    </TableCell>
                  </TableRow>
                ) : filteredModules.length === 0 ? (
                  <TableRow>
                    <TableCell colSpan={TOTAL_COLUMNS}>
                      <EmptyState
                        minHeight={200}
                        title={label(t, 'no_modules_match', 'No modules match your search')}
                        message={search ? `Nothing matched “${search}”.` : undefined}
                      />
                    </TableCell>
                  </TableRow>
                ) : filteredModules.map(m => {
                  const p = permissionByModule.get(m.id);
                  const isAllRowChecked = FLAG_COLUMNS.every(({ flag }) => flagOf(p, flag));

                  return (
                    <TableRow key={m.id} hover>
                      <TableCell sx={{ fontWeight: 600 }}>
                        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                          <Checkbox
                            size="small"
                            checked={isAllRowChecked}
                            disabled={isBusy}
                            slotProps={{ input: { 'aria-label': `Toggle every permission on ${m.moduleName}` } }}
                            onChange={(e) => handleBulkToggleRow(m, e.target.checked)}
                          />
                          <Box>{m.moduleName}</Box>
                        </Box>
                      </TableCell>
                      {FLAG_COLUMNS.map(({ flag, label: englishLabel }) => (
                        <TableCell key={flag} align="center">
                          <Checkbox
                            size="small"
                            checked={flagOf(p, flag)}
                            disabled={isBusy}
                            slotProps={{ input: { 'aria-label': `${englishLabel} on ${m.moduleName}` } }}
                            onChange={() => handleToggle(m, flag)}
                          />
                        </TableCell>
                      ))}
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </TableContainer>
        </>
      )}
    </Box>
  );
};

export default RolesPermissionsPage;
