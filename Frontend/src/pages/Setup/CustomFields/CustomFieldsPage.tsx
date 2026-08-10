import React, { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Checkbox, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, FormControlLabel, IconButton, MenuItem, Paper, Stack, Table, TableBody,
  TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material';
import {
  Add as AddIcon, Edit as EditIcon, KeyboardArrowDown as DownIcon,
  KeyboardArrowUp as UpIcon, Archive as RetireIcon, Undo as ReactivateIcon,
  Lock as LockIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import customFieldService, {
  activeVersion, CUSTOM_FIELD_ENTITY_TYPES, CUSTOM_FIELD_TYPE_CHOICES,
  type CustomFieldDataType, type CustomFieldDefinition, type CustomFieldOption,
} from '../../../api/services/customFieldService';
import { presentableErrorMessage } from '../../../utils/apiErrors';

/**
 * AA-01 · custom-field administration.
 *
 * The product owner's ask was that a customer can add the fields they need without a
 * developer. This is that screen. Three rules it has to teach, not just enforce:
 *
 *   1. the key is permanent — values are stored under it,
 *   2. retiring is not deleting — the data stays,
 *   3. changing a type once values exist is refused, not silently coerced.
 *
 * Each is stated in the interface at the moment it matters, because a rule a user only meets
 * as a server error is a rule the product failed to explain.
 */

const OPTION_TYPES: CustomFieldDataType[] = ['Option', 'MultiOption'];

/** Suggests a stable key from a label. The user can still override it before saving. */
const suggestKey = (label: string): string =>
  label.toLowerCase().trim()
    .replace(/[^a-z0-9]+/g, '_')
    .replace(/^_+|_+$/g, '')
    .replace(/^([0-9])/, 'f$1')
    .slice(0, 63);

interface DraftState {
  stableKey: string;
  label: string;
  dataType: CustomFieldDataType;
  isRequired: boolean;
  helpText: string;
  options: CustomFieldOption[];
}

const emptyDraft: DraftState = {
  stableKey: '', label: '', dataType: 'Text', isRequired: false, helpText: '', options: [],
};

const typeLabel = (t: CustomFieldDataType): string =>
  CUSTOM_FIELD_TYPE_CHOICES.find((c) => c.value === t)?.label ?? t;

const CustomFieldsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [entityType, setEntityType] = useState<string>('Customer');
  const [editing, setEditing] = useState<CustomFieldDefinition | null>(null);
  const [creating, setCreating] = useState(false);
  const [draft, setDraft] = useState<DraftState>(emptyDraft);
  const [keyTouched, setKeyTouched] = useState(false);
  const [retiring, setRetiring] = useState<CustomFieldDefinition | null>(null);
  const [retireReason, setRetireReason] = useState('');

  const queryKey = useMemo(() => ['custom-field-definitions', entityType], [entityType]);
  const { data: definitions = [], isPending, isError, refetch } = useQuery({
    queryKey,
    queryFn: () => customFieldService.listDefinitions(entityType),
    retry: false,
  });

  const ordered = useMemo(
    () => [...definitions].sort((a, b) => a.displayOrder - b.displayOrder
      || a.stableKey.localeCompare(b.stableKey)),
    [definitions],
  );

  const invalidate = () => queryClient.invalidateQueries({ queryKey });
  const fail = (fallback: string) => (error: unknown) =>
    enqueueSnackbar(presentableErrorMessage(error, fallback), { variant: 'error' });

  const createMutation = useMutation({
    mutationFn: () => customFieldService.createDefinition({
      entityType,
      stableKey: draft.stableKey,
      version: {
        label: draft.label,
        dataType: draft.dataType,
        isRequired: draft.isRequired,
        helpText: draft.helpText.trim() || null,
      },
      options: OPTION_TYPES.includes(draft.dataType) ? draft.options : undefined,
      activate: true,
      displayOrder: definitions.length,
    }),
    onSuccess: () => {
      enqueueSnackbar('Field added. It is now available on the record and in the column picker.', { variant: 'success' });
      closeEditor();
      void invalidate();
    },
    onError: fail('The field could not be created.'),
  });

  const updateMutation = useMutation({
    mutationFn: () => customFieldService.addVersion(editing!.id, {
      version: {
        label: draft.label,
        dataType: draft.dataType,
        isRequired: draft.isRequired,
        helpText: draft.helpText.trim() || null,
      },
      options: OPTION_TYPES.includes(draft.dataType) ? draft.options : undefined,
      activate: true,
    }),
    onSuccess: () => {
      enqueueSnackbar('Field updated.', { variant: 'success' });
      closeEditor();
      void invalidate();
    },
    onError: fail('The field could not be updated.'),
  });

  const reorderMutation = useMutation({
    mutationFn: (next: CustomFieldDefinition[]) => customFieldService.reorder(
      entityType,
      next.map((d, index) => ({ definitionId: d.id, displayOrder: index })),
    ),
    onSuccess: () => void invalidate(),
    onError: fail('The new order could not be saved.'),
  });

  const retireMutation = useMutation({
    mutationFn: () => customFieldService.retire(retiring!.id, retireReason),
    onSuccess: () => {
      enqueueSnackbar('Field retired. Values already captured are unchanged and still readable.', { variant: 'success' });
      setRetiring(null);
      setRetireReason('');
      void invalidate();
    },
    onError: fail('The field could not be retired.'),
  });

  const reactivateMutation = useMutation({
    mutationFn: (definition: CustomFieldDefinition) => customFieldService.reactivate(definition.id),
    onSuccess: () => {
      enqueueSnackbar('Field is in use again.', { variant: 'success' });
      void invalidate();
    },
    onError: fail('The field could not be reactivated.'),
  });

  const busy = createMutation.isPending || updateMutation.isPending;

  const openCreate = () => {
    setDraft(emptyDraft);
    setKeyTouched(false);
    setEditing(null);
    setCreating(true);
  };

  const openEdit = (definition: CustomFieldDefinition) => {
    const version = activeVersion(definition);
    setDraft({
      stableKey: definition.stableKey,
      label: version?.label ?? '',
      dataType: version?.dataType ?? 'Text',
      isRequired: version?.isRequired ?? false,
      helpText: version?.helpText ?? '',
      options: version?.options ?? [],
    });
    setKeyTouched(true);
    setCreating(false);
    setEditing(definition);
  };

  const closeEditor = () => {
    setEditing(null);
    setCreating(false);
    setDraft(emptyDraft);
  };

  const move = (index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= ordered.length) return;
    const next = [...ordered];
    [next[index], next[target]] = [next[target], next[index]];
    reorderMutation.mutate(next);
  };

  const originalType = editing ? activeVersion(editing)?.dataType : undefined;
  const typeIsChanging = Boolean(editing) && originalType !== undefined && originalType !== draft.dataType;
  const keyValid = /^[a-z][a-z0-9_]{2,62}$/.test(draft.stableKey);
  const canSave = draft.label.trim().length > 0
    && (editing ? true : keyValid)
    && (!OPTION_TYPES.includes(draft.dataType) || draft.options.length > 0);

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 2, flexWrap: 'wrap' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>
            Custom fields
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Add the fields your business needs to the records you use most. They appear on the
            record and become columns you can switch on in any list.
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
          <TextField
            select size="small" label="Applies to" value={entityType}
            onChange={(e) => setEntityType(e.target.value)}
            sx={{ minWidth: 200 }}
          >
            {CUSTOM_FIELD_ENTITY_TYPES.map((option) => (
              <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>
            ))}
          </TextField>
          <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate} sx={{ px: 3 }}>
            Add field
          </Button>
        </Stack>
      </Box>

      {isError && (
        <Alert
          severity="error"
          sx={{ mb: 1.5 }}
          action={<Button color="inherit" onClick={() => void refetch()}>Retry</Button>}
        >
          Custom fields could not be loaded. No empty result has been assumed.
        </Alert>
      )}

      <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell sx={{ width: 90 }}>Order</TableCell>
              <TableCell>Field</TableCell>
              <TableCell sx={{ width: 150 }}>Type</TableCell>
              <TableCell sx={{ width: 100 }}>Required</TableCell>
              <TableCell sx={{ width: 130 }}>Status</TableCell>
              <TableCell sx={{ width: 130 }} align="right">Actions</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {isPending && (
              <TableRow>
                <TableCell colSpan={6} align="center" sx={{ py: 4 }}>
                  <CircularProgress size={22} />
                </TableCell>
              </TableRow>
            )}

            {!isPending && ordered.length === 0 && (
              <TableRow>
                <TableCell colSpan={6} sx={{ py: 5 }}>
                  <Typography variant="body2" color="text.secondary" align="center">
                    No custom fields yet. Add one and it becomes available on every record of
                    this type, and as a column you can switch on in the list.
                  </Typography>
                </TableCell>
              </TableRow>
            )}

            {ordered.map((definition, index) => {
              const version = activeVersion(definition);
              const retired = definition.status === 'Retired';
              return (
                <TableRow key={definition.id} sx={{ opacity: retired ? 0.6 : 1 }}>
                  <TableCell>
                    <Stack direction="row">
                      <IconButton
                        size="small" aria-label={`Move ${version?.label ?? definition.stableKey} up`}
                        disabled={index === 0 || retired || reorderMutation.isPending}
                        onClick={() => move(index, -1)}
                      >
                        <UpIcon fontSize="inherit" />
                      </IconButton>
                      <IconButton
                        size="small" aria-label={`Move ${version?.label ?? definition.stableKey} down`}
                        disabled={index === ordered.length - 1 || retired || reorderMutation.isPending}
                        onClick={() => move(index, 1)}
                      >
                        <DownIcon fontSize="inherit" />
                      </IconButton>
                    </Stack>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2" sx={{ fontWeight: 600 }}>
                      {version?.label ?? definition.stableKey}
                    </Typography>
                    <Typography variant="caption" sx={{ color: 'text.disabled', fontFamily: 'monospace' }}>
                      {definition.stableKey}
                    </Typography>
                    {retired && definition.retirementReason && (
                      <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary' }}>
                        Retired: {definition.retirementReason}
                      </Typography>
                    )}
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">{version ? typeLabel(version.dataType) : '—'}</Typography>
                  </TableCell>
                  <TableCell>
                    {version?.isRequired
                      ? <Chip label="Required" size="small" color="warning" variant="outlined" />
                      : <Typography variant="body2" color="text.disabled">Optional</Typography>}
                  </TableCell>
                  <TableCell>
                    <Chip
                      label={retired ? 'Retired' : definition.status === 'Active' ? 'In use' : 'Draft'}
                      size="small"
                      color={retired ? 'default' : definition.status === 'Active' ? 'success' : 'info'}
                      variant="outlined"
                    />
                  </TableCell>
                  <TableCell align="right">
                    {retired ? (
                      <Tooltip title="Put this field back into use">
                        <span>
                          <IconButton
                            size="small" color="primary" aria-label={`Reactivate ${definition.stableKey}`}
                            disabled={reactivateMutation.isPending}
                            onClick={() => reactivateMutation.mutate(definition)}
                          >
                            <ReactivateIcon fontSize="small" />
                          </IconButton>
                        </span>
                      </Tooltip>
                    ) : (
                      <>
                        <Tooltip title="Edit">
                          <IconButton
                            size="small" color="info" aria-label={`Edit ${definition.stableKey}`}
                            onClick={() => openEdit(definition)}
                          >
                            <EditIcon fontSize="small" />
                          </IconButton>
                        </Tooltip>
                        <Tooltip title="Stop offering this field (values are kept)">
                          <IconButton
                            size="small" aria-label={`Retire ${definition.stableKey}`}
                            onClick={() => { setRetiring(definition); setRetireReason(''); }}
                          >
                            <RetireIcon fontSize="small" />
                          </IconButton>
                        </Tooltip>
                      </>
                    )}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </Paper>

      {/* ── Add / edit ─────────────────────────────────────────────────────── */}
      <Dialog open={creating || editing !== null} onClose={closeEditor} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 700 }}>
          {editing ? 'Edit field' : 'Add a field'}
        </DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2} sx={{ pt: 0.5 }}>
            <TextField
              size="small" fullWidth required label="Name shown to users"
              value={draft.label}
              onChange={(e) => {
                const label = e.target.value;
                setDraft((p) => ({
                  ...p,
                  label,
                  stableKey: !editing && !keyTouched ? suggestKey(label) : p.stableKey,
                }));
              }}
              helperText="For example: Our vendor code, Framework agreement expiry."
            />

            <TextField
              size="small" fullWidth required label="Reference key"
              value={draft.stableKey}
              disabled={Boolean(editing)}
              onChange={(e) => { setKeyTouched(true); setDraft((p) => ({ ...p, stableKey: e.target.value })); }}
              error={!editing && draft.stableKey.length > 0 && !keyValid}
              slotProps={editing
                ? { input: { endAdornment: <LockIcon fontSize="small" sx={{ color: 'text.disabled' }} /> } }
                : undefined}
              helperText={editing
                ? 'The reference key can never change. Every value already saved is stored under it, so renaming it would lose them. Retire this field and create a new one if you need a different key.'
                : '3–63 characters, lowercase letters, numbers and underscores. This is permanent once saved.'}
            />

            <TextField
              select size="small" fullWidth label="Type of information"
              value={draft.dataType}
              onChange={(e) => setDraft((p) => ({ ...p, dataType: e.target.value as CustomFieldDataType }))}
              helperText={CUSTOM_FIELD_TYPE_CHOICES.find((c) => c.value === draft.dataType)?.hint}
            >
              {CUSTOM_FIELD_TYPE_CHOICES.map((choice) => (
                <MenuItem key={choice.value} value={choice.value}>{choice.label}</MenuItem>
              ))}
            </TextField>

            {typeIsChanging && (
              <Alert severity="warning">
                You are changing this field from <strong>{typeLabel(originalType!)}</strong> to{' '}
                <strong>{typeLabel(draft.dataType)}</strong>. If any record already holds a value
                for it, Nexora will refuse this change rather than convert the values — a number
                field cannot silently swallow text somebody typed. Retire this field and create a
                replacement instead; retiring keeps everything already captured readable.
              </Alert>
            )}

            {OPTION_TYPES.includes(draft.dataType) && (
              <Box>
                <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 1 }}>Choices</Typography>
                <Stack spacing={1}>
                  {draft.options.map((option, index) => (
                    <Stack key={index} direction="row" spacing={1}>
                      <TextField
                        size="small" label="Shown to users" value={option.label}
                        onChange={(e) => setDraft((p) => {
                          const options = [...p.options];
                          const label = e.target.value;
                          options[index] = {
                            ...options[index],
                            label,
                            stableKey: suggestKey(label) || options[index].stableKey,
                          };
                          return { ...p, options };
                        })}
                        sx={{ flexGrow: 1 }}
                      />
                      <Button
                        size="small" color="inherit"
                        onClick={() => setDraft((p) => ({
                          ...p,
                          options: p.options.filter((_, i) => i !== index),
                        }))}
                      >
                        Remove
                      </Button>
                    </Stack>
                  ))}
                  <Button
                    size="small" startIcon={<AddIcon />}
                    onClick={() => setDraft((p) => ({
                      ...p,
                      options: [...p.options, { stableKey: '', label: '', displayOrder: p.options.length + 1 }],
                    }))}
                  >
                    Add a choice
                  </Button>
                </Stack>
              </Box>
            )}

            <FormControlLabel
              control={
                <Checkbox
                  size="small" checked={draft.isRequired}
                  onChange={(e) => setDraft((p) => ({ ...p, isRequired: e.target.checked }))}
                />
              }
              label={
                <Box>
                  <Typography variant="body2">Must be filled in</Typography>
                  <Typography variant="caption" color="text.secondary">
                    Nexora will not save the record until this field has a value.
                  </Typography>
                </Box>
              }
            />

            <TextField
              size="small" fullWidth label="Hint for the person filling it in (optional)"
              value={draft.helpText}
              onChange={(e) => setDraft((p) => ({ ...p, helpText: e.target.value }))}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 2 }}>
          <Button onClick={closeEditor} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            disabled={!canSave || busy}
            startIcon={busy ? <CircularProgress size={14} color="inherit" /> : undefined}
            onClick={() => (editing ? updateMutation.mutate() : createMutation.mutate())}
          >
            {editing ? 'Save changes' : 'Add field'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* ── Retire ─────────────────────────────────────────────────────────── */}
      <Dialog open={retiring !== null} onClose={() => setRetiring(null)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 700 }}>Retire this field?</DialogTitle>
        <DialogContent dividers>
          <Alert severity="info" sx={{ mb: 2 }}>
            Retiring is not deleting. Every value already captured stays on its record and stays
            readable — the field simply stops being offered for new entries and stops appearing
            in the column picker. You can put it back into use later.
          </Alert>
          <Typography variant="body2" sx={{ mb: 2 }}>
            <strong>{(retiring ? activeVersion(retiring)?.label : null) ?? retiring?.stableKey}</strong>
          </Typography>
          <TextField
            size="small" fullWidth required multiline minRows={2}
            label="Why is it being retired?"
            value={retireReason}
            onChange={(e) => setRetireReason(e.target.value)}
            helperText="Recorded against the field so the decision is explainable later."
          />
        </DialogContent>
        <DialogActions sx={{ px: 3, py: 2 }}>
          <Button onClick={() => setRetiring(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained" color="warning"
            disabled={retireReason.trim().length === 0 || retireMutation.isPending}
            onClick={() => retireMutation.mutate()}
          >
            Retire field
          </Button>
        </DialogActions>
      </Dialog>

      <Divider sx={{ mt: 3 }} />
      <Typography variant="caption" sx={{ color: 'text.disabled', display: 'block', mt: 1.5 }}>
        Fields you add here appear on the record itself and in the “Columns” picker on the
        matching list, where each person chooses which ones they want to see.
      </Typography>
    </Box>
  );
};

export default CustomFieldsPage;
