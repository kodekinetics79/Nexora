import React, { useMemo, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Alert,
  Box,
  Button,
  Card,
  CardActionArea,
  Checkbox,
  Chip,
  CircularProgress,
  Collapse,
  Divider,
  MenuItem,
  Paper,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import {
  AdminPanelSettings as AdministratorIcon,
  CheckCircle as SelectedIcon,
  ExpandLess as CollapseIcon,
  ExpandMore as ExpandIcon,
  Restore as ResetIcon,
} from '@mui/icons-material';

import { Link as RouterLink } from 'react-router-dom';
import rolePermissionService, {
  type RolePermissionBulkEntry,
  type RolePermissionDTO,
} from '../../../api/services/rolePermissionService';
import setupService from '../../../api/services/setupService';
import moduleService, { type ModuleDTO } from '../../../api/services/moduleService';
import SearchField from '../../../components/common/SearchField';
import { useAuth } from '../../../context/AuthContext';
import { EmptyState, ErrorState } from '../../../platform/components/States';
import { handleApiError } from '../../../utils/errorHandler';
import { toPresentableError } from '../../../utils/apiErrors';
import { isEnforcedModule } from '../../Setup/permissionModules';
import { isRoleSetupType } from '../../Setup/roleRankTiers';
import {
  DESK_PRESETS,
  ROLE_LADDER,
  administersEverything,
  describeDrift,
  driftFromPreset,
  presetForRoleName,
  presetGrantFor,
  type RolePreset,
} from '../../Setup/rolePresets';
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
 * fallback — `t()` never returns something falsy. This picks the fallback whenever the lookup did
 * not actually resolve.
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

/**
 * Roles &amp; Permissions — a preset first, the matrix only if you ask for it.
 *
 * <b>What this screen used to be.</b> Pick a role, then meet 212 checkboxes across 44 modules with
 * nothing on screen indicating which combination adds up to a working sales representative. It was
 * a screen for somebody who already knew the answer, and the people who use this product are
 * salespeople. Configuring the standard rep meant 23 deliberate ticks across 14 modules, and a
 * sales manager 37 across 19 — each tick an opportunity to produce a role that half-works in a way
 * nobody notices until a quote cannot be sent.
 *
 * <b>What it is now.</b> The default path is one choice: which of three roles is this. The matrix
 * still exists — 629 gated endpoints resolve through it and some customers genuinely need a
 * bespoke role — but it is folded into "Advanced", closed, and when it is open the screen says in
 * plain words how far the role has drifted from its preset and offers one click back. Getting out
 * is as easy as getting in, which is the property that makes it safe to let somebody wander in.
 *
 * <b>The administrator case.</b> A role at Administrator or above satisfies every module check by
 * RANK, before the server reads a single permission row. Rendering a matrix for it — even a
 * carefully curated one — would be a screen stating the opposite of what is enforced: every box
 * could be cleared and the role would still hold the entire tenant. So it renders one sentence and
 * no checkboxes at all.
 */
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
  /** Advanced is CLOSED by default. That default is the whole point of this screen. */
  const [advancedOpen, setAdvancedOpen] = useState(false);
  /** Set when a bulk apply fails, so the table itself explains the failure and not just a toast. */
  const [bulkError, setBulkError] = useState<string | null>(null);

  /**
   * Roles come from Setup_Master rather than `/api/User/Roles`, because that endpoint returns only
   * an id and a name — and this screen has to know each role's AUTHORITY TIER to decide whether a
   * permission matrix means anything for it at all.
   *
   * Fetched unfiltered and narrowed here, exactly as the Setup Master screen does: `SetupType` is
   * stored inconsistently cased and padded in live data ('Role', ' Role '), so a server-side
   * `setupType=Role` filter silently drops real rows.
   */
  const rolesQuery = useQuery({
    queryKey: ['setup-roles', businessUnitId],
    queryFn: () => setupService.getAll({ pageSize: 5000 }),
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

  const roles = useMemo(
    () => (rolesQuery.data?.items ?? []).filter((row) => isRoleSetupType(row.setupType)),
    [rolesQuery.data],
  );

  const permissions = permissionsQuery.data;
  const loadingPermissions = permissionsQuery.isLoading;

  const selectedRole = useMemo(
    () => roles.find((role) => role.setupId === selectedRoleId),
    [roles, selectedRoleId],
  );
  const selectedRoleName = selectedRole?.setupName;
  const selectedRoleRank = selectedRole?.roleRank ?? 0;
  const roleAdministersEverything = administersEverything({ rank: selectedRoleRank });

  /**
   * Only modules the server actually enforces.
   *
   * `GET /api/Module` returns every row ever inserted, and nine of them are gated by nothing
   * anywhere in the backend. A checkbox for those grants nothing when ticked and revokes nothing
   * when cleared, while the row states in four columns that it did both — which is worse than
   * having no checkbox at all. The rows stay in the database (629 endpoints resolve through the
   * table); they are simply not presented as decisions.
   */
  const enforcedModules: ModuleDTO[] = useMemo(
    () => (modulesQuery.data?.items ?? []).filter((module) => isEnforcedModule(module.moduleName)),
    [modulesQuery.data],
  );

  const filteredModules: ModuleDTO[] = useMemo(
    () => enforcedModules.filter((module) =>
      module.moduleName.toLowerCase().includes(search.toLowerCase()),
    ),
    [enforcedModules, search],
  );

  const permissionByModule = useMemo(() => {
    const map = new Map<number, RolePermissionDTO>();
    for (const item of permissions?.items ?? []) map.set(item.moduleId, item);
    return map;
  }, [permissions]);

  /** The role's live grants keyed by module NAME, which is what a preset is expressed in. */
  const grantsByModuleName = useMemo(() => {
    const map = new Map<string, { canView: boolean; canCreate: boolean; canEdit: boolean; canDelete: boolean }>();
    for (const module of enforcedModules) {
      const permission = permissionByModule.get(module.id);
      map.set(module.moduleName.trim().toLowerCase(), {
        canView: flagOf(permission, 'canView'),
        canCreate: flagOf(permission, 'canCreate'),
        canEdit: flagOf(permission, 'canEdit'),
        canDelete: flagOf(permission, 'canDelete'),
      });
    }
    return map;
  }, [enforcedModules, permissionByModule]);

  /** The preset this role was built from, matched on its name. */
  const matchedPreset = useMemo(() => presetForRoleName(selectedRoleName), [selectedRoleName]);

  const drift = useMemo(
    () => (matchedPreset ? driftFromPreset(matchedPreset, grantsByModuleName) : []),
    [matchedPreset, grantsByModuleName],
  );

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

  // The module list is part of "busy" on purpose. Every write on this screen is expressed in
  // module ids, so until that list has arrived there is nothing correct to send — and a preset
  // applied against an empty list would write nothing while reporting that it had.
  const modulesUnavailable = modulesQuery.isLoading || modulesQuery.isError || enforcedModules.length === 0;
  const isBusy = loadingPermissions || bulkMutation.isPending || modulesUnavailable;

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

  /**
   * Writes a preset's grants across EVERY enforced module — not only the ones the preset names.
   *
   * A module the preset leaves out is written as an explicit all-false row rather than skipped,
   * because "apply the standard Sales Representative setup" has to mean the role ends up matching
   * it. Skipping the unnamed modules would leave whatever was there before and produce a role that
   * claims to be a standard rep while holding more than one.
   */
  const applyPreset = (preset: RolePreset) => {
    // Refuse rather than guess. If the module list has not arrived — or failed — this would post
    // an empty change set, the server would apply nothing, and the snackbar would report that the
    // preset had been applied. A silent wrong answer about who can do what is the one failure
    // this screen must never produce.
    if (enforcedModules.length === 0) {
      setBulkError(
        'The list of areas this role can be given has not loaded yet, so nothing was changed. '
        + 'Try again in a moment.',
      );
      return Promise.resolve();
    }

    const entries = enforcedModules.map((module) => ({
      moduleId: module.id,
      ...presetGrantFor(preset, module.moduleName),
    }));
    return applyBulk(
      entries,
      `Apply the standard ${preset.name} permissions`,
      `${preset.name} permissions applied.`,
    );
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

  /** The role picker — the first and, for most people, only decision on this screen. */
  const renderRolePicker = () => (
    <Paper sx={{ p: 2, mb: 2, borderRadius: 2 }}>
      <TextField
        select
        label={label(t, 'select_role', 'Select Role')}
        value={selectedRoleId}
        onChange={(e) => {
          setSelectedRoleId(e.target.value === '' ? '' : Number(e.target.value));
          // Advanced does not stay open across roles. Somebody who opened it to inspect one
          // bespoke role should not be dropped into a matrix for the next one.
          setAdvancedOpen(false);
          setBulkError(null);
        }}
        sx={{ minWidth: 280 }}
        size="small"
      >
        {roles.map(r => <MenuItem key={r.setupId} value={r.setupId}>{r.setupName}</MenuItem>)}
      </TextField>
    </Paper>
  );

  /**
   * A role whose authority comes from its tier. One sentence, no checkboxes.
   *
   * This is the case the old screen got most wrong: it drew 212 controls for a role that holds
   * everything regardless of any of them.
   */
  const renderAdministratorNotice = () => (
    <Paper sx={{ p: 3, borderRadius: 2 }} data-testid="administers-everything">
      <Box sx={{ display: 'flex', gap: 2, alignItems: 'flex-start' }}>
        <AdministratorIcon color="primary" sx={{ fontSize: 36 }} />
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 700, mb: 0.5 }}>
            {selectedRoleName} administers everything
          </Typography>
          <Typography variant="body1" color="text.secondary">
            This role administers every part of {label(t, 'the_organization', 'the organization')}.
            Individual permission lines do not apply to it, so there is nothing to tick here.
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1.5 }}>
            To give someone narrower access, give them a different role instead of changing this one.
          </Typography>
          <Button
            component={RouterLink}
            to="/setup/master?type=role"
            size="small"
            sx={{ mt: 1.5, px: 0 }}
          >
            {label(t, 'manage_roles', 'Manage roles')}
          </Button>
        </Box>
      </Box>
    </Paper>
  );

  /** One preset card. Selected state is derived from the role's NAME, not from local state. */
  const renderPresetCard = (preset: RolePreset) => {
    const isCurrent = matchedPreset?.code === preset.code;
    const tierMatches = preset.rank === selectedRoleRank;

    return (
      <Card
        key={preset.code}
        variant="outlined"
        sx={{
          flex: '1 1 260px',
          minWidth: 260,
          borderColor: isCurrent ? 'primary.main' : 'divider',
          borderWidth: isCurrent ? 2 : 1,
        }}
      >
        <CardActionArea
          disabled={isBusy || !tierMatches}
          onClick={() => { void applyPreset(preset); }}
          sx={{ p: 2, height: '100%', alignItems: 'flex-start', justifyContent: 'flex-start' }}
          aria-label={`Apply the standard ${preset.name} setup`}
        >
          <Box sx={{ display: 'flex', gap: 1, alignItems: 'center', mb: 0.5 }}>
            <Typography variant="subtitle1" sx={{ fontWeight: 700 }}>{preset.name}</Typography>
            {isCurrent && (
              <Chip
                size="small"
                color="primary"
                icon={<SelectedIcon />}
                label={label(t, 'current', 'Current')}
              />
            )}
          </Box>
          <Typography variant="body2" color="text.secondary">{preset.summary}</Typography>
          {!tierMatches && (
            // Rule: a control that cannot work says why, in words. This screen writes permission
            // lines; it does not change a role's authority tier, which is set where roles are
            // created. Offering the click and then failing would be worse than explaining.
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              This setup belongs to a different level of authority than{' '}
              {selectedRoleName} has. Change the role’s level under Manage roles first.
            </Typography>
          )}
        </CardActionArea>
      </Card>
    );
  };

  /** The default path: choose a role's setup in one click. */
  const renderPresets = () => (
    <Paper sx={{ p: 2, mb: 2, borderRadius: 2 }}>
      <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 0.5 }}>
        {label(t, 'standard_setups', 'Standard setups')}
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Pick the one that matches what this person does. You can change it at any time.
      </Typography>

      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2 }}>
        {ROLE_LADDER.map(renderPresetCard)}
      </Box>

      <Divider sx={{ my: 2 }} />
      <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 0.5 }}>
        {label(t, 'other_desks', 'Other desks')}
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        Same level of authority as a sales representative — a different part of the business.
      </Typography>
      <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 2 }}>
        {DESK_PRESETS.map(renderPresetCard)}
      </Box>
    </Paper>
  );

  /** The drift line plus the way back. Only meaningful once we know which preset to compare to. */
  const renderDrift = () => {
    if (!matchedPreset) return null;
    if (drift.length === 0) return null;

    return (
      <Alert
        severity="info"
        // `role="status"`, not MUI's default `role="alert"`. This line reports a state, not a
        // failure: an assertive live region would interrupt a screen-reader user mid-sentence
        // every time a role is selected, and it would also mask the genuine error alerts that
        // this screen raises when a write is refused.
        role="status"
        sx={{ mb: 2, borderRadius: 2 }}
        action={
          <Button
            size="small"
            startIcon={<ResetIcon />}
            disabled={isBusy}
            onClick={() => { void applyPreset(matchedPreset); }}
          >
            {label(t, 'reset_to_standard', 'Reset to standard')}
          </Button>
        }
      >
        {describeDrift(matchedPreset, drift)}
      </Alert>
    );
  };

  /** Everything the matrix needs above it, kept inside Advanced rather than on the default path. */
  const renderMatrixControls = () => (
    <Box sx={{ p: 2, display: 'flex', gap: 3, alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' }}>
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
      <SearchField value={search} onChange={setSearch} placeholder={label(t, 'search_modules', 'Search modules...')} width={300} />
    </Box>
  );

  const renderMatrix = () => (
    <TableContainer>
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
                <Checkbox
                  size="small"
                  disabled={isBusy}
                  slotProps={{ input: { 'aria-label': 'Toggle every permission on every listed module' } }}
                  onChange={(e) => { void handleAllRowsToggle(e.target.checked); }}
                />
              </Box>
            </TableCell>
            {FLAG_COLUMNS.map(({ flag, i18nKey, label: englishLabel }) => (
              <TableCell key={flag} align="center" sx={{ fontWeight: 700 }}>
                <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
                  {label(t, i18nKey, englishLabel)}
                  <Checkbox
                    size="small"
                    disabled={isBusy}
                    slotProps={{ input: { 'aria-label': `Toggle ${englishLabel} on every listed module` } }}
                    onChange={(e) => { void handleBulkToggleColumn(flag, e.target.checked); }}
                  />
                </Box>
              </TableCell>
            ))}
          </TableRow>
        </TableHead>
        <TableBody>
          {permissionsQuery.isError ? (
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
  );

  /** Progressive disclosure: the matrix exists, it is just not the first thing anybody meets. */
  const renderAdvanced = () => (
    <Paper sx={{ borderRadius: 2, overflow: 'hidden' }}>
      <Button
        fullWidth
        onClick={() => setAdvancedOpen((open) => !open)}
        endIcon={advancedOpen ? <CollapseIcon /> : <ExpandIcon />}
        aria-expanded={advancedOpen}
        sx={{ justifyContent: 'space-between', p: 2, color: 'text.primary', textTransform: 'none' }}
      >
        <Typography variant="subtitle2" sx={{ fontWeight: 700 }}>
          {label(t, 'advanced_customise_role', 'Advanced — customise this role')}
        </Typography>
      </Button>
      <Collapse in={advancedOpen} unmountOnExit>
        <Divider />
        <Box sx={{ px: 2, pt: 2 }}>
          <Typography variant="body2" color="text.secondary">
            Set access one area at a time. Most roles never need this — the standard setups above
            already cover the usual jobs.
          </Typography>
        </Box>
        {renderMatrixControls()}
        {renderMatrix()}
      </Collapse>
    </Paper>
  );

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      <Box sx={{ mb: 2 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800 }}>{label(t, 'roles_and_permissions', 'Roles & Permissions')}</Typography>
        <Typography variant="body2" color="text.secondary">
          {label(t, 'choose_what_a_role_can_do', 'Choose what each role can do')}
        </Typography>
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
      ) : roles.length === 0 ? (
        <Paper sx={{ borderRadius: 2, p: 2 }}>
          <EmptyState
            title={label(t, 'no_roles_configured', 'No roles are configured for this business unit')}
            // `t(key) || fallback` never reached the fallback: i18next answers a missing key with
            // the key itself, which is truthy, so this screen printed "no_roles_configured_help"
            // at the reader. `label` is the helper this file already has for exactly that.
            message={label(
              t,
              'no_roles_configured_help',
              'Roles are created under Manage roles, where each one is also given its level of authority. Until at least one role exists there is nothing to configure here.',
            )}
            action={
              <Box sx={{ display: 'flex', gap: 1, flexWrap: 'wrap', justifyContent: 'center' }}>
                {/* The screen that creates roles, one click away — this empty state used to name it
                    in prose and then offer only a Retry that could not change the answer. */}
                <Button variant="contained" component={RouterLink} to="/setup/master?type=role">
                  {label(t, 'create_a_role', 'Create a role')}
                </Button>
                <Button variant="outlined" onClick={() => { void rolesQuery.refetch(); }}>
                  {label(t, 'retry', 'Retry')}
                </Button>
              </Box>
            }
          />
        </Paper>
      ) : (
        <>
          {renderRolePicker()}

          {selectedRoleId === '' ? (
            <Paper sx={{ borderRadius: 2, p: 2 }}>
              <EmptyState
                minHeight={200}
                title={label(t, 'please_select_role', 'Choose a role to get started')}
                message="Pick a role above and you can give it one of the standard setups in a single click."
              />
            </Paper>
          ) : roleAdministersEverything ? (
            renderAdministratorNotice()
          ) : (
            <>
              {bulkError && (
                <Paper sx={{ borderRadius: 2, mb: 2, p: 1 }}>
                  <ErrorState
                    minHeight={140}
                    message={bulkError}
                    onRetry={() => { void permissionsQuery.refetch(); }}
                  />
                </Paper>
              )}

              {/* Loaded-but-empty is handled here alongside a failed request, because both leave
                  every control on this screen unable to do anything. Rendering disabled preset
                  cards with no explanation is the state this branch exists to avoid. */}
              {modulesQuery.isError || (!modulesQuery.isLoading && enforcedModules.length === 0) ? (
                <Paper sx={{ borderRadius: 2, p: 2 }}>
                  <ErrorState
                    message={modulesQuery.isError
                      ? describeFailure(
                        modulesQuery.error,
                        'The areas this role can be given could not be loaded, so nothing can be changed here yet.',
                      )
                      : 'No part of the product is set up to have access granted to it yet, so there is nothing to configure for this role.'}
                    onRetry={() => { void modulesQuery.refetch(); }}
                  />
                </Paper>
              ) : (
                <>
                  {renderPresets()}
                  {renderDrift()}
                  {renderAdvanced()}
                </>
              )}
            </>
          )}
        </>
      )}
    </Box>
  );
};

export default RolesPermissionsPage;
