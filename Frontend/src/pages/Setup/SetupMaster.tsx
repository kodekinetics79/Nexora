import React, { useState, useMemo, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Typography,
  Paper,
  Button,
  TextField,
  Chip,
  IconButton,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Grid,
  FormControlLabel,
  Switch,
  CircularProgress,
  Tooltip,
  Divider,
  MenuItem,
  Card,
  CardContent,
  Avatar,
  Alert,
  Link as MuiLink,
} from '@mui/material';
import {
  Add as AddIcon,
  Edit as EditIcon,
  Settings as SettingsIcon,
  Layers as LayersIcon,
  CheckCircle as ActiveIcon,
  Cancel as InactiveIcon,
  Security as ShieldIcon,
  LockOutlined as LockIcon,
} from '@mui/icons-material';
import { useTranslation } from 'react-i18next';
import { useLocation, useNavigate, Link as RouterLink } from 'react-router-dom';
import setupService from '../../api/services/setupService';
import type { SetupMasterDTO, SetupMasterCreateDTO, SetupMasterUpdateDTO } from '../../api/services/setupService';
import { useAuth } from '../../context/AuthContext';
import SearchField from '../../components/common/SearchField';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import {
  DEFAULT_ROLE_RANK,
  ROLE_RANK_DENIED_MESSAGE,
  ROLE_RANK_MEMBER,
  ROLE_RANK_MANAGER,
  ROLE_RANK_ADMIN,
  ROLE_RANK_OWNER,
  asRoleRank,
  isRoleSetupType,
  roleRankChoices,
  roleRankTier,
} from './roleRankTiers';
import { SETUP_TYPES_OWNED_ELSEWHERE, entryForLocation, normaliseSetupType, setupEntryLabel } from './setupCatalog';
import lodash from 'lodash';

/** Authority reads at a glance: the higher the tier, the louder the chip. */
const ROLE_RANK_CHIP_COLOR: Record<number, 'default' | 'info' | 'warning' | 'error'> = {
  [ROLE_RANK_MEMBER]: 'default',
  [ROLE_RANK_MANAGER]: 'info',
  [ROLE_RANK_ADMIN]: 'warning',
  [ROLE_RANK_OWNER]: 'error',
};

const SetupMaster: React.FC = () => {
  const { t } = useTranslation();
  const { userData } = useAuth();
  const location = useLocation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const { data: allSetups, isLoading } = useQuery({
    queryKey: ['setups-global-all'],
    queryFn: () => setupService.getAll({ pageSize: 5000 }),
  });

  /**
   * The list can be scoped to one type by URL — `/setup/master?type=role` is how the catalogue's
   * "Roles" entry lands here, and clicking a type chip below writes the same URL, so a scoped list
   * can be linked and bookmarked.
   *
   * This replaces a path sniffer that mapped `/setup/currency`, `/setup/uom` and `/setup/warehouse`
   * onto setup types. Those paths have had their own screens and their own backing tables for some
   * time, so the sniffer could never fire — and the types it named are not what those screens edit.
   */
  const requestedType = new URLSearchParams(location.search).get('type') || undefined;

  /** Stored casing wins: `Setup_Master.SetupType` holds "Role", the URL may say "role". */
  const setupType = useMemo(() => {
    if (!requestedType) return undefined;
    const wanted = normaliseSetupType(requestedType);
    return (
      allSetups?.items?.find((item) => normaliseSetupType(item.setupType) === wanted)?.setupType ??
      requestedType
    );
  }, [requestedType, allSetups]);

  /** Every type in the tenant, with its row count — the chip rail and the type dropdown read this. */
  const typeCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const item of allSetups?.items ?? []) {
      counts.set(item.setupType, (counts.get(item.setupType) ?? 0) + 1);
    }
    return [...counts.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [allSetups]);

  const scopeToType = (type?: string) => {
    navigate(type ? `/setup/master?type=${encodeURIComponent(type)}` : '/setup/master');
  };

  // Filters
  const [search, setSearch] = useState('');
  const [filterActive, setFilterActive] = useState<boolean | 'all'>('all');

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<SetupMasterDTO | null>(null);

  const [formData, setFormData] = useState<Partial<SetupMasterDTO>>({
    setupType: setupType || '',
    setupCode: '',
    setupName: '',
    description: '',
    isActive: true,
    parentSetupId: null,
    // New roles start with no administrative authority: the server's default and the safe choice.
    roleRank: DEFAULT_ROLE_RANK,
  });

  // Sync form data when selection changes
  useEffect(() => {
    if (selectedRecord) {
      setFormData({
        setupType: selectedRecord.setupType,
        setupCode: selectedRecord.setupCode || '',
        setupName: selectedRecord.setupName,
        description: selectedRecord.description || '',
        isActive: selectedRecord.isActive,
        parentSetupId: selectedRecord.parentSetupId,
        roleRank: asRoleRank(selectedRecord.roleRank),
      });
    } else {
      setFormData({
        setupType: setupType || '',
        setupCode: '',
        setupName: '',
        description: '',
        isActive: true,
        parentSetupId: null,
        roleRank: DEFAULT_ROLE_RANK,
      });
    }
  }, [selectedRecord, setupType]);

  const groupedData = useMemo(() => {
    if (!allSetups?.items) return {};

    let filtered = allSetups.items;

    if (setupType) {
      const wanted = normaliseSetupType(setupType);
      filtered = filtered.filter(item => normaliseSetupType(item.setupType) === wanted);
    }

    if (search) {
      const lowerSearch = search.toLowerCase();
      filtered = filtered.filter(item =>
        item.setupName.toLowerCase().includes(lowerSearch) ||
        (item.setupCode && item.setupCode.toLowerCase().includes(lowerSearch)) ||
        item.setupType.toLowerCase().includes(lowerSearch)
      );
    }

    if (filterActive !== 'all') {
      const isActiveFilter = filterActive === true || (filterActive as any) === 'true';
      filtered = filtered.filter(item => item.isActive === isActiveFilter);
    }

    return lodash.groupBy(filtered, 'setupType');
  }, [allSetups, search, filterActive, setupType]);

  const distinctTypes = useMemo(() => Object.keys(groupedData).sort(), [groupedData]);

  /** Types a row may be created under here — anything a dedicated screen owns is not one of them. */
  const creatableTypes = useMemo(
    () => typeCounts
      .map(([type]) => type)
      .filter((type) => !SETUP_TYPES_OWNED_ELSEWHERE[normaliseSetupType(type)]),
    [typeCounts],
  );

  /** True while the list is scoped to a type this screen only displays. */
  const scopedTypeIsOwnedElsewhere = !!setupType && !!SETUP_TYPES_OWNED_ELSEWHERE[normaliseSetupType(setupType)];

  /**
   * A scoped list is a destination in its own right — `?type=role` is "Roles" in the sidebar's
   * catalogue and in the breadcrumb above. Take the heading from the same entry so the page does
   * not introduce a third name (the stored type, e.g. "Role") for what the reader just clicked.
   */
  const catalogEntry = entryForLocation(location.pathname, location.search);
  // Only an entry that names this scope may retitle the page. Without the query check, a type with
  // no catalogue entry of its own (PRICE_STRUCTURE, ShipmentStatus…) falls back to the unscoped
  // entry and the heading claims to be the whole list while showing one type of it.
  const scopedEntry = catalogEntry?.path.includes('?') ? catalogEntry : undefined;
  const heading = setupType
    ? (scopedEntry ? setupEntryLabel(scopedEntry, t) : setupType)
    : 'Lists & Picklists';
  const subheading = setupType
    ? scopedEntry?.description ??
      `Every ${setupType} value the platform offers. Rows here appear in the dropdowns that use this type.`
    : 'The values behind the dropdowns — statuses, reasons, categories and roles. Anything listed here is offered somewhere in the platform.';

  const createMutation = useMutation({
    mutationFn: (newRecord: SetupMasterCreateDTO) => setupService.create(newRecord),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['setups-global-all'] });
      setIsModalOpen(false);
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, updateData }: { id: number; updateData: SetupMasterUpdateDTO }) => setupService.update(id, updateData),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['setups-global-all'] });
      setIsModalOpen(false);
    },
  });

  // Only role rows carry authority. The server rejects a non-Member rank on a lookup row with a
  // 400, so the field is neither shown nor sent for anything else.
  const isRoleForm = isRoleSetupType(formData.setupType);
  const selectedRoleRank = asRoleRank(formData.roleRank);

  /**
   * The tiers this caller may grant. `isSuperAdmin`/`isManager` are the only authority signals the
   * session carries — see the DTO-gap note in `roleRankTiers.ts`.
   */
  const rankChoices = useMemo(
    () => roleRankChoices(
      { isSuperAdmin: userData.isSuperAdmin, isManager: userData.isManager },
      {
        currentRank: selectedRecord && isRoleSetupType(selectedRecord.setupType)
          ? asRoleRank(selectedRecord.roleRank)
          : null,
      },
    ),
    [userData.isSuperAdmin, userData.isManager, selectedRecord],
  );

  const blockedChoices = useMemo(() => rankChoices.filter((choice) => choice.disabled), [rankChoices]);

  /** True when the tier currently in the form is one the server will certainly refuse. */
  const selectedRankRefused =
    isRoleSetupType(formData.setupType) &&
    rankChoices.find((choice) => choice.rank === asRoleRank(formData.roleRank))?.disabled === true;

  const openDialog = (record: SetupMasterDTO | null) => {
    // A rejection from a previous attempt must not haunt the next one.
    createMutation.reset();
    updateMutation.reset();
    setSelectedRecord(record);
    setIsModalOpen(true);
  };

  const closeDialog = () => {
    setIsModalOpen(false);
    createMutation.reset();
    updateMutation.reset();
  };

  const handleSave = () => {
    if (selectedRecord) {
      updateMutation.mutate({
        id: selectedRecord.setupId,
        updateData: {
          setupType: formData.setupType || '',
          setupCode: formData.setupCode || '',
          setupName: formData.setupName || '',
          description: formData.description || '',
          parentSetupId: formData.parentSetupId,
          ...(isRoleForm ? { roleRank: selectedRoleRank } : {}),
          isActive: !!formData.isActive,
          modifiedBy: userData.userName || 'System'
        }
      });
    } else {
      createMutation.mutate({
        setupType: formData.setupType || '',
        setupCode: formData.setupCode || '',
        setupName: formData.setupName || '',
        description: formData.description || '',
        parentSetupId: formData.parentSetupId,
        ...(isRoleForm ? { roleRank: selectedRoleRank } : {}),
        isActive: !!formData.isActive,
        createdBy: userData.userName || 'System'
      });
    }
  };

  const saveError = createMutation.error ?? updateMutation.error;

  const [isNewType, setIsNewType] = useState(false);

  return (
    <Box sx={{ width: '100%', p: 3 }}>
      {/* Header Section */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 2, flexWrap: 'wrap' }}>
        <Box>
          <Typography variant="h4" component="h1" sx={{ fontWeight: 900, color: 'text.primary', display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <SettingsIcon sx={{ fontSize: 32, color: 'primary.main' }} />
            {heading}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 500, ml: 6, mt: 0.25, maxWidth: 640, lineHeight: 1.5 }}>
            {subheading}
          </Typography>
        </Box>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          onClick={() => openDialog(null)}
          disabled={scopedTypeIsOwnedElsewhere}
          sx={{
            borderRadius: 2.5,
            px: 3,
            py: 1,
            fontWeight: 800,
            textTransform: 'none',
            boxShadow: '0 4px 12px rgba(0,0,0,0.1)'
          }}
        >
          {t('create_new')}
        </Button>
      </Box>

      {/* Filter Bar */}
      <Paper sx={{ p: 1.5, mb: 2.5, borderRadius: 3, display: 'flex', gap: 2, alignItems: 'center', border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField
          width="350px"
          value={search}
          onChange={setSearch}
          placeholder="Search configuration..."
        />
        <Divider orientation="vertical" flexItem />
        <TextField
          select
          size="small"
          value={filterActive}
          onChange={(e) => setFilterActive(e.target.value as any)}
          sx={{ minWidth: 140, '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
        >
          <MenuItem value="all">All Status</MenuItem>
          <MenuItem value="true">Active Only</MenuItem>
          <MenuItem value="false">Inactive Only</MenuItem>
        </TextField>
      </Paper>

      {/* Type rail. A tenant accumulates dozens of types; scrolling past all of them to reach one
          was the whole complaint about this screen. Each chip writes ?type=, so a scoped list is a
          link you can send someone. */}
      {typeCounts.length > 0 && (
        <Box
          role="group"
          aria-label="Filter by type"
          sx={{ display: 'flex', flexWrap: 'wrap', gap: 1, mb: 4 }}
        >
          <Chip
            label="All types"
            size="small"
            color={setupType ? 'default' : 'primary'}
            variant={setupType ? 'outlined' : 'filled'}
            onClick={() => scopeToType(undefined)}
            sx={{ fontWeight: 700 }}
          />
          {typeCounts.map(([type, count]) => {
            const owner = SETUP_TYPES_OWNED_ELSEWHERE[normaliseSetupType(type)];
            const isSelected = !!setupType && normaliseSetupType(setupType) === normaliseSetupType(type);
            return (
              <Chip
                key={type}
                label={`${type} · ${count}`}
                size="small"
                color={isSelected ? 'primary' : 'default'}
                variant={isSelected ? 'filled' : 'outlined'}
                icon={owner ? <LockIcon sx={{ fontSize: '13px !important' }} /> : undefined}
                onClick={() => scopeToType(type)}
                sx={{ fontWeight: 600 }}
              />
            );
          })}
        </Box>
      )}

      {/* Content Grid */}
      {isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', p: 10 }}>
          <CircularProgress size={40} thickness={4} />
        </Box>
      ) : (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
          {distinctTypes.map((type) => {
            // A type with a screen of its own is shown here but never edited here — two editors over
            // one table is how the same value ends up with two different meanings.
            const owner = SETUP_TYPES_OWNED_ELSEWHERE[normaliseSetupType(type)];
            return (
            <Box key={type}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mb: owner ? 1.5 : 2.5, pl: 0.5, flexWrap: 'wrap' }}>
                <Typography variant="subtitle1" component="h2" sx={{ fontWeight: 900, letterSpacing: '0.05em', color: 'text.primary', textTransform: 'uppercase' }}>
                  {type}
                </Typography>
                <Chip
                  label={groupedData[type].length}
                  size="small"
                  sx={{ fontWeight: 800, backgroundColor: 'action.selected', fontSize: '0.7rem' }}
                />
                <Divider sx={{ flex: 1, opacity: 0.5 }} />
              </Box>
              {owner && (
                <Alert
                  severity="info"
                  icon={<LockIcon fontSize="small" />}
                  sx={{ mb: 2.5, borderRadius: 2 }}
                  action={
                    <Button
                      component={RouterLink}
                      to={owner.path}
                      size="small"
                      sx={{ fontWeight: 700, textTransform: 'none', whiteSpace: 'nowrap', flexShrink: 0 }}
                    >
                      Open {owner.label}
                    </Button>
                  }
                >
                  These rows belong to {owner.label}, which is where they are created and changed.
                  They are listed here so the list is complete, not so it can be edited twice.
                </Alert>
              )}
              <Grid container spacing={2}>
                {groupedData[type].map((item) => (
                  <Grid size={{ xs: 12, sm: 6, md: 4, lg: 3, xl: 2.4 }} key={item.setupId}>
                    <Card
                      sx={{
                        borderRadius: 3,
                        border: '1px solid',
                        borderColor: 'divider',
                        boxShadow: 'none',
                        transition: 'all 0.2s',
                        '&:hover': {
                          borderColor: 'primary.main',
                          transform: 'translateY(-2px)',
                          boxShadow: '0 8px 24px rgba(0,0,0,0.05)'
                        }
                      }}
                    >
                      <CardContent sx={{ p: 2 }}>
                        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 1.5 }}>
                          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                            <Avatar sx={{ width: 28, height: 28, bgcolor: item.isActive ? 'primary.light' : 'action.disabled', fontSize: '0.8rem', fontWeight: 800 }}>
                              {item.setupName.charAt(0).toUpperCase()}
                            </Avatar>
                            <Typography variant="body2" sx={{ fontWeight: 800, color: 'text.primary' }}>
                              {item.setupCode || 'NO-CODE'}
                            </Typography>
                          </Box>
                          <Tooltip title={owner ? `Edited in ${owner.label}` : 'Edit'}>
                            {/* A disabled button fires no events, so the tooltip needs a live
                                wrapper to hang off. */}
                            <Box component="span">
                              <IconButton
                                size="small"
                                onClick={() => openDialog(item)}
                                disabled={!!owner}
                                aria-label={owner ? `${item.setupName} is edited in ${owner.label}` : `Edit ${item.setupName}`}
                                sx={{ mt: -0.5, mr: -0.5, color: 'text.secondary' }}
                              >
                                {owner ? <LockIcon fontSize="small" /> : <EditIcon fontSize="small" />}
                              </IconButton>
                            </Box>
                          </Tooltip>
                        </Box>

                        <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 0.5, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {item.setupName}
                        </Typography>

                        {/* Clamped to two lines with the height it needs for them. It was a fixed
                            32px box the text overflowed, so a two-line description ran under the
                            chips below it. */}
                        <Typography
                          variant="caption"
                          sx={{
                            color: 'text.secondary',
                            mb: 1.75,
                            minHeight: 34,
                            lineHeight: 1.4,
                            overflow: 'hidden',
                            display: '-webkit-box',
                            WebkitLineClamp: 2,
                            WebkitBoxOrient: 'vertical',
                          }}
                        >
                          {item.description || 'No description available for this item.'}
                        </Typography>

                        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 0.75, flexWrap: 'wrap' }}>
                          <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, minWidth: 0, flexWrap: 'wrap' }}>
                            <Chip
                              icon={item.isActive ? <ActiveIcon sx={{ fontSize: '12px !important' }} /> : <InactiveIcon sx={{ fontSize: '12px !important' }} />}
                              label={item.isActive ? 'Active' : 'Disabled'}
                              size="small"
                              color={item.isActive ? 'success' : 'default'}
                              sx={{ height: 20, fontSize: '0.65rem', fontWeight: 800, borderRadius: 1.5 }}
                            />
                            {/* Authority is a property of ROLE rows only — a UOM or City row has none,
                                so no empty chip is rendered for them. */}
                            {isRoleSetupType(item.setupType) && (
                              <Tooltip title={roleRankTier(item.roleRank).description}>
                                <Chip
                                  icon={<ShieldIcon sx={{ fontSize: '12px !important' }} />}
                                  label={roleRankTier(item.roleRank).label}
                                  size="small"
                                  color={ROLE_RANK_CHIP_COLOR[asRoleRank(item.roleRank)]}
                                  variant={asRoleRank(item.roleRank) === ROLE_RANK_MEMBER ? 'outlined' : 'filled'}
                                  sx={{ height: 20, fontSize: '0.65rem', fontWeight: 800, borderRadius: 1.5 }}
                                />
                              </Tooltip>
                            )}
                          </Box>
                          <Typography variant="caption" sx={{ fontSize: '0.6rem', color: 'text.disabled', fontWeight: 600, flexShrink: 0, whiteSpace: 'nowrap' }}>
                            ID: #{item.setupId}
                          </Typography>
                        </Box>
                      </CardContent>
                    </Card>
                  </Grid>
                ))}
              </Grid>
            </Box>
            );
          })}
          {distinctTypes.length === 0 && (
            <Box sx={{ textAlign: 'center', py: 10 }}>
              <LayersIcon sx={{ fontSize: 64, color: 'action.disabled', mb: 2 }} />
              <Typography variant="h6" color="text.secondary" sx={{ fontWeight: 700 }}>
                {search || filterActive !== 'all' ? 'No items match your search.' : 'Nothing on this list yet.'}
              </Typography>
              {setupType && !search && filterActive === 'all' && (
                <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>
                  No {setupType} values exist yet. Create the first one, or{' '}
                  <MuiLink component="button" type="button" onClick={() => scopeToType(undefined)} sx={{ fontWeight: 600 }}>
                    show every type
                  </MuiLink>
                  .
                </Typography>
              )}
            </Box>
          )}
        </Box>
      )}

      {/* Standard Setup Dialog */}
      <Dialog
        open={isModalOpen}
        onClose={closeDialog}
        fullWidth
        maxWidth="sm"
      >
        <DialogTitle sx={{ fontWeight: 'bold' }}>
          {selectedRecord ? 'Update Setup Record' : 'Create New Setup Record'}
        </DialogTitle>
        <DialogContent dividers>
          {saveError != null && (
            <ApiErrorNotice
              error={saveError}
              // A 403 on a role row is the rank guard; `toPresentableError` never renders server
              // text on a 403, so the reason is named here and the server's own sentence stays in
              // the technical-detail disclosure.
              fallbackMessage={isRoleForm
                ? ROLE_RANK_DENIED_MESSAGE
                : 'This setup record could not be saved. Nothing was changed — try again.'}
              sx={{ mb: 2 }}
            />
          )}
          <Grid container spacing={2} sx={{ mt: 0.5 }}>
            <Grid size={{ xs: 12, md: 6 }}>
              {isNewType || !selectedRecord ? (
                isNewType ? (
                  <TextField
                    fullWidth
                    label="New Setup Type"
                    variant="outlined"
                    value={formData.setupType}
                    onChange={(e) => setFormData({ ...formData, setupType: e.target.value.toUpperCase() })}
                    required
                    placeholder="E.G. CURRENCY"
                  />
                ) : (
                  <TextField
                    select
                    fullWidth
                    label="Setup Type"
                    variant="outlined"
                    value={formData.setupType}
                    onChange={(e) => setFormData({ ...formData, setupType: e.target.value })}
                    required
                  >
                    {/* Every type in the tenant, not just the ones the current filter left on
                        screen — scoping the list to Role used to leave Role as the only type you
                        could create. Types another screen owns are absent by design. */}
                    {creatableTypes.map((type) => (
                      <MenuItem key={type} value={type}>
                        {type}
                      </MenuItem>
                    ))}
                    {creatableTypes.length === 0 && <MenuItem disabled>No types available</MenuItem>}
                  </TextField>
                )
              ) : (
                <TextField
                  fullWidth
                  label="Setup Type"
                  variant="outlined"
                  value={formData.setupType}
                  disabled
                />
              )}
            </Grid>
            {!selectedRecord && (
              <Grid size={{ xs: 12, md: 6 }} sx={{ display: 'flex', alignItems: 'center' }}>
                <FormControlLabel
                  control={<Switch checked={isNewType} onChange={(e) => setIsNewType(e.target.checked)} />}
                  label="Enter New Type?"
                />
              </Grid>
            )}
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                label="Setup Code"
                variant="outlined"
                value={formData.setupCode}
                onChange={(e) => setFormData({ ...formData, setupCode: e.target.value })}
              />
            </Grid>
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                label="Setup Name"
                variant="outlined"
                value={formData.setupName}
                onChange={(e) => setFormData({ ...formData, setupName: e.target.value })}
                required
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField
                fullWidth
                label="Description"
                variant="outlined"
                multiline
                rows={3}
                value={formData.description}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
              />
            </Grid>
            {/* Role rows only: a lookup value has no authority, and the server 400s if one is sent. */}
            {isRoleForm && (
              <Grid size={{ xs: 12 }}>
                <TextField
                  select
                  fullWidth
                  label="Role authority"
                  variant="outlined"
                  value={selectedRoleRank}
                  onChange={(e) => setFormData({ ...formData, roleRank: asRoleRank(Number(e.target.value)) })}
                  slotProps={{
                    select: {
                      // The menu rows are two lines each; the closed field stays a single line.
                      renderValue: (value) => `${roleRankTier(Number(value)).label} — ${roleRankTier(Number(value)).summary}`,
                    },
                  }}
                  helperText={roleRankTier(selectedRoleRank).description}
                >
                  {rankChoices.map((choice) => (
                    <MenuItem key={choice.rank} value={choice.rank} disabled={choice.disabled}>
                      <Box sx={{ py: 0.25 }}>
                        <Typography variant="body2" sx={{ fontWeight: 800 }}>
                          {choice.label} — {choice.summary}
                        </Typography>
                        <Typography
                          variant="caption"
                          sx={{ display: 'block', color: choice.availability === 'grantable' ? 'text.secondary' : 'warning.main', whiteSpace: 'normal' }}
                        >
                          {choice.note ?? choice.description}
                        </Typography>
                      </Box>
                    </MenuItem>
                  ))}
                </TextField>
                {blockedChoices.length > 0 && (
                  <Typography variant="caption" sx={{ display: 'block', mt: 0.5, color: 'text.secondary' }}>
                    {blockedChoices.map((choice) => choice.note).filter(Boolean)[0]}
                  </Typography>
                )}
              </Grid>
            )}
            <Grid size={{ xs: 12 }}>
              <FormControlLabel
                control={
                  <Switch
                    checked={formData.isActive}
                    onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })}
                    color="primary"
                  />
                }
                label="Is Active"
              />
            </Grid>
          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={closeDialog} color="inherit">
            {t('cancel') || 'Cancel'}
          </Button>
          <Button
            onClick={handleSave}
            variant="contained"
            color="primary"
            disabled={createMutation.isPending || updateMutation.isPending || selectedRankRefused}
          >
            {selectedRecord ? 'Update' : 'Create'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default SetupMaster;
