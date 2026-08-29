import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useRef, useState } from 'react';
import {
  Alert, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel,
  Stack, Switch, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material';
import { Tune as TuneIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { useNavigate } from 'react-router-dom';
import commercialIntelligenceService, { type RepRoutingProfileDTO } from '../../api/services/commercialIntelligenceService';
import { useAuth } from '../../context/AuthContext';
import { PageShell, PipelineGroups, QueryState, ResponsiveTable } from './CommercialPagePrimitives';

/**
 * Rep directory, and the only place a governed Sales Rep profile can be created or changed.
 *
 * The profile table is fail-closed: `LoadUserAvailabilityAsync` treats a missing profile row as
 * "not available", so a tenant with an empty `sales_rep_profiles` table has nobody the routing
 * engine will accept and every lead lands Unassigned. The write endpoint for that table already
 * existed and was reachable from nothing; this screen is the caller.
 *
 * Two numbers are shown per rep and they are not the same number. "Capacity" is what a manager
 * configured. The routing verdict beside it is what the engine will do today, which also folds in
 * measured workload and whether the profile's effective window is still open.
 */

const asKeyList = (value: string): string[] =>
  value.split(',').map(part => part.trim()).filter(part => part.length > 0);

type DraftState = {
  isRoutingEligible: boolean;
  capacityPercent: string;
  distributionWeight: string;
  territoryKeys: string;
  productCategoryKeys: string;
};

function RoutingCell({ row }: { row: RepRoutingProfileDTO }) {
  const label = !row.hasProfile ? 'No profile'
    : !row.profileEffectiveNow ? 'Not effective'
    : row.isAvailable ? 'Eligible' : 'Blocked';
  const color = label === 'Eligible' ? 'success' : label === 'No profile' ? 'default' : 'warning';
  return (
    <Stack spacing={0.25}>
      <Tooltip title={row.eligibilityReason}><Chip size="small" color={color} label={label} sx={{ alignSelf: 'flex-start' }} /></Tooltip>
      <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'normal' }}>{row.eligibilityReason}</Typography>
      {row.hasProfile && (
        <Typography variant="caption" color="text.secondary">
          Set to {row.capacityPercent}% capacity, weight {row.distributionWeight}
          {row.workloadPoints != null ? ` | ${row.workloadPoints} workload points` : ''}
        </Typography>
      )}
    </Stack>
  );
}

export default function RepDirectoryPage() {
  const navigate = useNavigate();
  const client = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canEdit = hasPermission('Users', 'edit');
  const [target, setTarget] = useState<RepRoutingProfileDTO | null>(null);
  const [draft, setDraft] = useState<DraftState | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const mutationIntent = useRef<{ fingerprint: string; key: string } | null>(null);

  const summaries = useQuery({ queryKey: ['commercial-intelligence', 'reps'], queryFn: commercialIntelligenceService.getRepDirectory });
  const profiles = useQuery({ queryKey: ['commercial-intelligence', 'rep-routing-profiles'], queryFn: commercialIntelligenceService.getRepRoutingProfiles });

  // The directory itself is driven by `reps`, exactly as before. Routing is an OVERLAY on top of
  // it: if the routing read is slow or fails, the directory still lists everyone and still opens
  // profiles — it just cannot say anything about routing yet. Making the roster depend on the new
  // query would have turned a routing outage into an empty rep directory.
  const rows = useMemo(() => {
    const byUser = new Map((profiles.data ?? []).map(profile => [profile.userId, profile]));
    return (summaries.data ?? []).map(summary => ({ summary, profile: byUser.get(summary.userId) }));
  }, [summaries.data, profiles.data]);
  const nobodyEligible = !!profiles.data?.length && !profiles.data.some(profile => profile.isAvailable);

  const mutation = useMutation({
    mutationFn: () => {
      const capacity = Number(draft!.capacityPercent);
      const weight = Number(draft!.distributionWeight);
      const body = {
        isRoutingEligible: draft!.isRoutingEligible,
        capacityPercent: capacity,
        distributionWeight: weight,
        territoryKeys: asKeyList(draft!.territoryKeys),
        productCategoryKeys: asKeyList(draft!.productCategoryKeys),
        // Omitted on create so the server applies its own midnight-UTC default; round-tripped on
        // update so adjusting capacity does not silently restart the effective period.
        effectiveFromUtc: target!.hasProfile ? target!.effectiveFromUtc : undefined,
        effectiveToUtc: target!.hasProfile ? target!.effectiveToUtc : undefined,
        expectedVersion: target!.version,
      };
      const fingerprint = JSON.stringify([target!.userId, body]);
      if (mutationIntent.current?.fingerprint !== fingerprint) mutationIntent.current = { fingerprint, key: crypto.randomUUID() };
      return commercialIntelligenceService.upsertRepRoutingProfile(target!.userId, body, mutationIntent.current.key);
    },
    onSuccess: async () => {
      mutationIntent.current = null;
      // A successful POST is not enough for this directory: the table renders the server's live
      // eligibility verdict, not just the submitted fields. Keep the named editor intact while the
      // authoritative overlay is re-read, then close it. Fire-and-forget invalidation previously
      // cleared `target` during the exit transition (blank title) and left the row stale until a
      // manual reload when the background refresh lost its race with navigation/rendering.
      const refreshed = await profiles.refetch();
      setEditorOpen(false);
      enqueueSnackbar(
        refreshed.isError
          ? 'Routing profile saved, but the directory could not refresh. Use Retry to load the current routing status.'
          : 'Routing profile saved',
        { variant: refreshed.isError ? 'warning' : 'success' },
      );
    },
    onError: (error: any) => {
      const conflict = error?.response?.status === 409;
      enqueueSnackbar(
        conflict
          ? 'This profile was changed by someone else. It has been reloaded — reapply your change.'
          : (error?.response?.data?.error || 'The routing profile could not be saved.'),
        { variant: conflict ? 'warning' : 'error' },
      );
      if (conflict) {
        // Never retry blind against a stale version: reload and make the manager look again.
        mutationIntent.current = null;
        setTarget(null);
        void client.invalidateQueries({ queryKey: ['commercial-intelligence', 'rep-routing-profiles'] });
      }
    },
  });

  const openEditor = (profile: RepRoutingProfileDTO) => {
    mutationIntent.current = null;
    setTarget(profile);
    setEditorOpen(true);
    setDraft({
      isRoutingEligible: profile.isRoutingEligible ?? true,
      capacityPercent: String(profile.capacityPercent ?? 100),
      distributionWeight: String(profile.distributionWeight ?? 1),
      territoryKeys: profile.territoryKeys.join(', '),
      productCategoryKeys: profile.productCategoryKeys.join(', '),
    });
  };
  const closeEditor = () => { mutationIntent.current = null; setEditorOpen(false); };
  const clearClosedEditor = () => { setTarget(null); setDraft(null); };

  const capacity = Number(draft?.capacityPercent);
  const weight = Number(draft?.distributionWeight);
  const capacityValid = Number.isInteger(capacity) && capacity >= 0 && capacity <= 100;
  const weightValid = Number.isFinite(weight) && weight > 0 && weight <= 1000;

  return (
    <PageShell title="Rep directory" subtitle="Sales ownership, workload, and who the routing engine is allowed to assign work to.">
      {nobodyEligible && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          No representative in this business unit is currently eligible for governed routing, so every
          incoming lead will land in the routing queue unassigned and no manual assignment will be
          accepted either. Give at least one person an eligible routing profile below.
        </Alert>
      )}
      <QueryState
        loading={summaries.isLoading}
        error={summaries.isError}
        empty={!rows.length}
        onRetry={() => { void summaries.refetch(); void profiles.refetch(); }}
        emptyText="No sales representatives were returned."
      >
        <ResponsiveTable label="Sales representatives">
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Representative</TableCell>
                <TableCell>Role</TableCell>
                <TableCell>Routing</TableCell>
                <TableCell align="right">Active leads</TableCell>
                <TableCell align="right">Follow-ups due</TableCell>
                <TableCell align="right">Pipeline</TableCell>
                <TableCell>Profile</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map(({ profile, summary }) => (
                <TableRow hover key={summary.userId}>
                  <TableCell>
                    <Typography sx={{ fontWeight: 700 }}>{summary.name}</Typography>
                    <Typography variant="caption" color="text.secondary">{summary.email}</Typography>
                  </TableCell>
                  <TableCell>{summary.roleName || 'Sales representative'}</TableCell>
                  <TableCell>{profile
                    ? <RoutingCell row={profile} />
                    : <Typography variant="caption" color="text.secondary">{profiles.isError ? 'Routing status unavailable' : 'Loading'}</Typography>}
                  </TableCell>
                  <TableCell align="right">{summary.activeLeads}</TableCell>
                  <TableCell align="right">{summary.followUpsDue}</TableCell>
                  <TableCell align="right"><PipelineGroups groups={summary.pipelineGroups} /></TableCell>
                  <TableCell>
                    <Stack direction="row" spacing={1}>
                      {canEdit && profile && (
                        <Button size="small" startIcon={<TuneIcon />} onClick={() => openEditor(profile)}>
                          {profile.hasProfile ? 'Routing' : 'Enable routing'}
                        </Button>
                      )}
                      <Button size="small" onClick={() => navigate(`/sales/reps/${summary.userId}`)}>Open</Button>
                    </Stack>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </ResponsiveTable>
      </QueryState>

      <Dialog
        open={editorOpen}
        onClose={() => !mutation.isPending && closeEditor()}
        fullWidth
        maxWidth="sm"
        slotProps={{ transition: { onExited: clearClosedEditor } }}
      >
        <DialogTitle>Routing profile &mdash; {target?.name}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            {target && !target.hasProfile && (
              <Alert severity="info">
                {target.name} has no governed profile, so the routing engine cannot assign them anything
                and a manager cannot either. Saving this creates one.
              </Alert>
            )}
            {target?.hasProfile && !target.profileEffectiveNow && (
              <Alert severity="warning">
                This profile exists but is outside its effective dates, which makes it invisible to routing.
              </Alert>
            )}
            <FormControlLabel
              control={<Switch checked={draft?.isRoutingEligible ?? true} onChange={event => setDraft(current => current && { ...current, isRoutingEligible: event.target.checked })} />}
              label="Eligible for governed routing"
            />
            <TextField
              label="Capacity percent" type="number" value={draft?.capacityPercent ?? ''}
              onChange={event => setDraft(current => current && { ...current, capacityPercent: event.target.value })}
              error={!capacityValid} helperText="0-100. Caps how much of the measured workload ceiling this rep may carry."
              slotProps={{ htmlInput: { min: 0, max: 100, step: 1 } }}
            />
            <TextField
              label="Distribution weight" type="number" value={draft?.distributionWeight ?? ''}
              onChange={event => setDraft(current => current && { ...current, distributionWeight: event.target.value })}
              error={!weightValid} helperText="Greater than 0, up to 1000. Relative share when several eligible reps tie."
              slotProps={{ htmlInput: { min: 0, max: 1000, step: 0.1 } }}
            />
            <TextField
              label="Territory keys" value={draft?.territoryKeys ?? ''}
              onChange={event => setDraft(current => current && { ...current, territoryKeys: event.target.value })}
              helperText="Comma separated. Leave blank for no territory restriction."
            />
            <TextField
              label="Product category keys" value={draft?.productCategoryKeys ?? ''}
              onChange={event => setDraft(current => current && { ...current, productCategoryKeys: event.target.value })}
              helperText="Comma separated. Leave blank for no category restriction."
            />
            {target?.hasProfile && (
              <Typography variant="caption" color="text.secondary">
                Version {target.version}, last changed by {target.updatedBy || 'unknown'}.
              </Typography>
            )}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={closeEditor} disabled={mutation.isPending}>Cancel</Button>
          <Button variant="contained" disabled={!capacityValid || !weightValid || mutation.isPending} onClick={() => mutation.mutate()}>
            Save profile
          </Button>
        </DialogActions>
      </Dialog>
    </PageShell>
  );
}
