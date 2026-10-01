import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, ButtonBase, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, FormControlLabel, FormLabel, IconButton, MenuItem, Paper, Radio, RadioGroup, Stack,
  Switch, TextField, Typography,
} from '@mui/material';
import {
  Add as AddIcon,
  ArrowDownward as DownIcon,
  ArrowUpward as UpIcon,
  Lock as LockIcon,
} from '@mui/icons-material';
import quoteService, {
  type QuoteCountsAs,
  type QuoteEndingOption,
  type QuoteReasonOption,
  type QuoteStatusCatalog,
  type QuoteStatusKind,
  type QuoteStatusOption,
} from '../../../api/services/quoteService';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';

/**
 * Setup › Quote statuses. The client's own words layered on Nexora's fixed quote lifecycle:
 * the customer's steps while a quote is SENT, the client's endings (each counting as Won, Lost or
 * Expired) and the reasons offered for an outcome. Won / Lost / Expired themselves and the system
 * reason "Expired automatically" are Nexora's and shown locked.
 *
 * No optimistic updates: every change is sent, then the catalog is read again, so the screen only
 * ever shows what the server holds. A refusal is shown in the server's own sentence.
 */

const CATALOG_KEY = ['quote-status-catalog', 'setup'] as const;

const OUTCOMES: { countsAs: QuoteCountsAs; label: string; color: 'success' | 'error' | 'default' }[] = [
  { countsAs: 'WON', label: 'Won', color: 'success' },
  { countsAs: 'LOST', label: 'Lost', color: 'error' },
  { countsAs: 'EXPIRED', label: 'Expired', color: 'default' },
];

/** The reason's "For" choices. Empty (null) is Lost and Expired: the reasons that existed before. */
const REASON_FOR: { value: string; label: string }[] = [
  { value: 'WON', label: 'Won' },
  { value: 'LOST', label: 'Lost' },
  { value: 'EXPIRED', label: 'Expired' },
  { value: 'ANY', label: 'Lost and Expired' },
];
const forValue = (value: QuoteCountsAs | null) => value ?? 'ANY';
const forLabel = (value: QuoteCountsAs | null) => REASON_FOR.find((o) => o.value === forValue(value))!.label;

const bySortOrder = <T extends QuoteStatusOption>(rows: T[]) =>
  [...rows].sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));

type UpdateBody = Parameters<typeof quoteService.updateStatusOption>[1];

/** The locked marker: Nexora decides these rows, not the client. */
const NexoraMark: React.FC = () => (
  <Chip size="small" icon={<LockIcon sx={{ fontSize: '14px !important' }} />} label="Nexora" variant="outlined" />
);

// ---------------------------------------------------------------------------------------------
// Rename in place: click the name, Enter saves, Escape puts it back.
// ---------------------------------------------------------------------------------------------

interface NameCellProps {
  row: QuoteStatusOption;
  canEdit: boolean;
  onRename: (name: string) => void;
  busy: boolean;
}

const NameCell: React.FC<NameCellProps> = ({ row, canEdit, onRename, busy }) => {
  const [editing, setEditing] = React.useState(false);
  const [draft, setDraft] = React.useState(row.name);
  const done = React.useRef(false);
  const inputRef = React.useRef<HTMLInputElement>(null);
  React.useEffect(() => {
    if (editing) inputRef.current?.focus();
  }, [editing]);

  const start = () => {
    done.current = false;
    setDraft(row.name);
    setEditing(true);
  };
  const finish = (save: boolean) => {
    if (done.current) return;
    done.current = true;
    setEditing(false);
    const name = draft.trim();
    if (save && name && name !== row.name) onRename(name);
  };

  if (!canEdit) {
    return <Typography sx={{ flex: 1, minWidth: 0, color: row.isActive ? 'text.primary' : 'text.disabled' }}>{row.name}</Typography>;
  }
  if (editing) {
    return (
      <TextField
        inputRef={inputRef}
        size="small"
        value={draft}
        onChange={(event) => setDraft(event.target.value.slice(0, 100))}
        onKeyDown={(event) => {
          if (event.key === 'Enter') { event.preventDefault(); finish(true); }
          if (event.key === 'Escape') { event.preventDefault(); finish(false); }
        }}
        onBlur={() => finish(true)}
        slotProps={{ htmlInput: { 'aria-label': `New name for ${row.name}` } }}
        sx={{ flex: 1, minWidth: 0 }}
      />
    );
  }
  return (
    <ButtonBase
      onClick={start}
      disabled={busy}
      aria-label={`Rename ${row.name}`}
      sx={{
        flex: 1, minWidth: 0, justifyContent: 'flex-start', textAlign: 'left', borderRadius: 1, px: 0.5, py: 0.5,
        '&:hover': { bgcolor: 'action.hover' },
      }}
    >
      <Typography component="span" sx={{ color: row.isActive ? 'text.primary' : 'text.disabled' }}>{row.name}</Typography>
    </ButtonBase>
  );
};

// ---------------------------------------------------------------------------------------------
// One editable row: order arrows, name, optional middle control, Active switch.
// ---------------------------------------------------------------------------------------------

interface EditableRowProps {
  row: QuoteStatusOption;
  canEdit: boolean;
  busy: boolean;
  isFirst: boolean;
  isLast: boolean;
  onMove: (direction: -1 | 1) => void;
  onUpdate: (body: UpdateBody) => void;
  middle?: React.ReactNode;
  indent?: boolean;
}

const EditableRow: React.FC<EditableRowProps> = ({ row, canEdit, busy, isFirst, isLast, onMove, onUpdate, middle, indent }) => (
  <Box
    data-testid={`status-row-${row.id}`}
    sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5, pl: indent ? 4 : 0, minHeight: 48 }}
  >
    {canEdit && (
      <Box sx={{ display: 'flex' }}>
        <IconButton size="small" aria-label={`Move ${row.name} up`} disabled={busy || isFirst} onClick={() => onMove(-1)}>
          <UpIcon fontSize="small" />
        </IconButton>
        <IconButton size="small" aria-label={`Move ${row.name} down`} disabled={busy || isLast} onClick={() => onMove(1)}>
          <DownIcon fontSize="small" />
        </IconButton>
      </Box>
    )}
    <NameCell row={row} canEdit={canEdit} busy={busy} onRename={(name) => onUpdate({ name })} />
    {middle}
    <FormControlLabel
      label="Active"
      labelPlacement="start"
      sx={{ ml: 1, mr: 0 }}
      control={(
        <Switch
          checked={row.isActive}
          disabled={!canEdit || busy}
          onChange={(event) => onUpdate({ isActive: event.target.checked })}
          slotProps={{ input: { 'aria-label': `${row.name} active` } }}
        />
      )}
    />
  </Box>
);

/** A row the client cannot change: Won / Lost / Expired, or a Nexora system row. */
const LockedRow: React.FC<{ name: string; testId: string; middle?: React.ReactNode }> = ({ name, testId, middle }) => (
  <Box data-testid={testId} sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5, minHeight: 48 }}>
    <LockIcon fontSize="small" sx={{ color: 'text.secondary', mx: 1 }} />
    <Typography sx={{ flex: 1, minWidth: 0, fontWeight: 700 }}>{name}</Typography>
    {middle}
    <NexoraMark />
  </Box>
);

// ---------------------------------------------------------------------------------------------
// Card frame
// ---------------------------------------------------------------------------------------------

const StatusCard: React.FC<{ title: string; addLabel: string; onAdd?: () => void; children: React.ReactNode }> = ({ title, addLabel, onAdd, children }) => (
  <Paper component="section" aria-label={title} sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 2, mb: 1 }}>
      <Typography variant="h6" component="h2" sx={{ fontWeight: 700 }}>{title}</Typography>
      {onAdd && <Button variant="outlined" startIcon={<AddIcon />} onClick={onAdd}>{addLabel}</Button>}
    </Box>
    {children}
  </Paper>
);

// ---------------------------------------------------------------------------------------------
// Add window: name, plus Counts as (ending) or For (reason).
// ---------------------------------------------------------------------------------------------

const ADD_TITLE: Record<QuoteStatusKind, string> = { step: 'Add step', ending: 'Add ending', reason: 'Add reason' };

interface AddDialogProps {
  kind: QuoteStatusKind | null;
  onClose: () => void;
  onAdded: () => void;
}

const AddDialog: React.FC<AddDialogProps> = ({ kind, onClose, onAdded }) => {
  const [name, setName] = React.useState('');
  const [countsAs, setCountsAs] = React.useState<QuoteCountsAs | ''>('');
  const [reasonFor, setReasonFor] = React.useState('LOST');
  const nameRef = React.useRef<HTMLInputElement>(null);

  const add = useMutation({
    mutationFn: () => {
      const trimmed = name.trim();
      if (kind === 'ending') return quoteService.addStatusOption('ending', { name: trimmed, countsAs: countsAs as QuoteCountsAs });
      if (kind === 'reason') {
        return quoteService.addStatusOption('reason', { name: trimmed, for: reasonFor === 'ANY' ? null : (reasonFor as QuoteCountsAs) });
      }
      return quoteService.addStatusOption('step', { name: trimmed });
    },
    onSuccess: () => {
      onAdded();
      onClose();
    },
  });

  // A fresh window each time it opens.
  const { reset } = add;
  React.useEffect(() => {
    if (!kind) return;
    setName('');
    setCountsAs('');
    setReasonFor('LOST');
    reset();
  }, [kind, reset]);

  const ready = name.trim() !== '' && (kind !== 'ending' || countsAs !== '');

  return (
    <Dialog
      open={kind !== null}
      onClose={add.isPending ? undefined : onClose}
      fullWidth
      maxWidth="xs"
      slotProps={{ transition: { onEntered: () => nameRef.current?.focus() } }}
    >
      <DialogTitle sx={{ fontWeight: 800 }}>{kind ? ADD_TITLE[kind] : ''}</DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <TextField
            inputRef={nameRef}
            fullWidth
            size="small"
            label="Name"
            value={name}
            onChange={(event) => setName(event.target.value.slice(0, 100))}
            onKeyDown={(event) => { if (event.key === 'Enter' && ready && !add.isPending) add.mutate(); }}
          />
          {kind === 'ending' && (
            <Box>
              <FormLabel id="ending-counts-as" sx={{ fontWeight: 700, color: 'text.primary' }}>Counts as</FormLabel>
              <RadioGroup
                row
                aria-labelledby="ending-counts-as"
                value={countsAs}
                onChange={(event) => setCountsAs(event.target.value as QuoteCountsAs)}
              >
                {OUTCOMES.map((o) => (
                  <FormControlLabel key={o.countsAs} value={o.countsAs} control={<Radio color={o.color} />} label={o.label} />
                ))}
              </RadioGroup>
            </Box>
          )}
          {kind === 'reason' && (
            <TextField select fullWidth size="small" label="For" value={reasonFor} onChange={(event) => setReasonFor(event.target.value)}>
              {REASON_FOR.map((o) => <MenuItem key={o.value} value={o.value}>{o.label}</MenuItem>)}
            </TextField>
          )}
          {add.isError && <Alert severity="error">{presentableErrorMessage(add.error, 'It could not be added.')}</Alert>}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} color="inherit" disabled={add.isPending}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!ready || add.isPending}
          onClick={() => add.mutate()}
          startIcon={add.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
          sx={{ fontWeight: 800 }}
        >
          Add
        </Button>
      </DialogActions>
    </Dialog>
  );
};

// ---------------------------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------------------------

const EMPTY: QuoteStatusCatalog = { steps: [], endings: [], reasons: [] };

const QuoteStatusesPage: React.FC = () => {
  const { hasPermission, userData } = useAuth();
  // The server takes status changes from managers and administrators only; offering the controls
  // to anyone else would only collect refusals.
  const canEdit = hasPermission('Quotations', 'edit')
    && (userData?.isManager === true || userData?.isSuperAdmin === true || userData?.hasModuleAuthorityByRank === true);
  const queryClient = useQueryClient();
  const [adding, setAdding] = React.useState<QuoteStatusKind | null>(null);

  const catalog = useQuery({ queryKey: CATALOG_KEY, queryFn: () => quoteService.getStatusCatalog(true) });

  const refetchAll = () => {
    // The rep's window reads ['quote-status-catalog'] and ['quote-outcome-reasons']; refresh them too.
    queryClient.invalidateQueries({ queryKey: ['quote-status-catalog'] });
    queryClient.invalidateQueries({ queryKey: ['quote-outcome-reasons'] });
  };

  /** Each call is one or more PUTs, sent in order; the catalog is read again whatever happens. */
  const change = useMutation({
    mutationFn: async (updates: { id: number; body: UpdateBody }[]) => {
      for (const update of updates) {
        await quoteService.updateStatusOption(update.id, update.body);
      }
    },
    onSettled: refetchAll,
  });

  const update = (id: number) => (body: UpdateBody) => change.mutate([{ id, body }]);

  /**
   * Moves a row one place within its list and numbers the list 10, 20, 30… — only rows whose
   * number actually changes are sent, so a tidy list costs two calls.
   */
  const move = <T extends QuoteStatusOption>(list: T[], index: number, direction: -1 | 1) => {
    const target = index + direction;
    if (target < 0 || target >= list.length) return;
    const reordered = [...list];
    [reordered[index], reordered[target]] = [reordered[target], reordered[index]];
    const updates = reordered
      .map((row, i) => ({ row, sortOrder: (i + 1) * 10 }))
      .filter(({ row, sortOrder }) => row.sortOrder !== sortOrder)
      .map(({ row, sortOrder }) => ({ id: row.id, body: { sortOrder } }));
    if (updates.length) change.mutate(updates);
  };

  const data = catalog.data ?? EMPTY;
  const steps = bySortOrder(data.steps.filter((s) => !s.isSystem));
  const systemSteps = data.steps.filter((s) => s.isSystem);
  const reasons = bySortOrder(data.reasons.filter((r) => !r.isSystem));
  const systemReasons = data.reasons.filter((r) => r.isSystem);
  const busy = change.isPending || catalog.isFetching;

  const renderEditable = <T extends QuoteStatusOption>(list: T[], middle?: (row: T) => React.ReactNode, indent?: boolean) =>
    list.map((row, index) => (
      <EditableRow
        key={row.id}
        row={row}
        canEdit={canEdit}
        busy={busy}
        isFirst={index === 0}
        isLast={index === list.length - 1}
        onMove={(direction) => move(list, index, direction)}
        onUpdate={update(row.id)}
        middle={middle?.(row)}
        indent={indent}
      />
    ));

  const endingsFor = (countsAs: QuoteCountsAs): QuoteEndingOption[] =>
    bySortOrder(data.endings.filter((e) => e.countsAs === countsAs && !e.isSystem));

  const reasonFor = (row: QuoteReasonOption) => (
    <TextField
      select
      size="small"
      label="For"
      value={forValue(row.for)}
      disabled={!canEdit || busy}
      slotProps={{ select: { SelectDisplayProps: { 'aria-label': `${row.name}: for` } as React.HTMLAttributes<HTMLDivElement> } }}
      onChange={(event) => update(row.id)({ for: event.target.value === 'ANY' ? null : (event.target.value as QuoteCountsAs) })}
      sx={{ width: 180 }}
    >
      {REASON_FOR.map((o) => <MenuItem key={o.value} value={o.value}>{o.label}</MenuItem>)}
    </TextField>
  );

  return (
    <Box sx={{ width: '100%', maxWidth: 880, px: 1, py: 1 }}>
      <Typography variant="h5" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 2 }}>Quote statuses</Typography>

      {change.isError && (
        <Alert severity="error" sx={{ mb: 2 }} onClose={() => change.reset()}>
          {presentableErrorMessage(change.error, 'The change was not saved.')}
        </Alert>
      )}

      {catalog.isLoading && <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>}

      {catalog.isError && (
        <Alert
          severity="error"
          action={<Button color="inherit" size="small" onClick={() => catalog.refetch()}>Try again</Button>}
        >
          {presentableErrorMessage(catalog.error, 'Quote statuses could not be loaded.')}
        </Alert>
      )}

      {catalog.isSuccess && (
        <Stack spacing={3}>
          <StatusCard title="Customer's steps" addLabel="Add step" onAdd={canEdit ? () => setAdding('step') : undefined}>
            {systemSteps.map((s) => <LockedRow key={s.id} name={s.name} testId={`status-row-${s.id}`} />)}
            {renderEditable(steps)}
          </StatusCard>

          <StatusCard title="How it ended" addLabel="Add ending" onAdd={canEdit ? () => setAdding('ending') : undefined}>
            {OUTCOMES.map((o) => (
              <Box key={o.countsAs} role="group" aria-label={o.label}>
                <LockedRow name={o.label} testId={`locked-outcome-${o.countsAs}`} />
                {data.endings.filter((e) => e.countsAs === o.countsAs && e.isSystem).map((e) => (
                  <Box key={e.id} sx={{ pl: 4 }}><LockedRow name={e.name} testId={`status-row-${e.id}`} /></Box>
                ))}
                {renderEditable(endingsFor(o.countsAs), undefined, true)}
              </Box>
            ))}
          </StatusCard>

          <StatusCard title="Reasons" addLabel="Add reason" onAdd={canEdit ? () => setAdding('reason') : undefined}>
            {renderEditable(reasons, reasonFor)}
            {systemReasons.map((r) => (
              <LockedRow
                key={r.id}
                name={r.name}
                testId={`status-row-${r.id}`}
                middle={<Typography color="text.secondary" sx={{ width: 180 }}>For {r.code === 'AUTO_EXPIRED' ? 'Expired' : forLabel(r.for)}</Typography>}
              />
            ))}
          </StatusCard>
        </Stack>
      )}

      <AddDialog kind={adding} onClose={() => setAdding(null)} onAdded={refetchAll} />
    </Box>
  );
};

export default QuoteStatusesPage;
