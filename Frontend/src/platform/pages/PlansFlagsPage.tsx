import { useState } from 'react';
import Stack from '../components/Flex';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Alert,
  AlertTitle,
  Button,
  Checkbox,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControlLabel,
  Grid,
  Paper,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { Add as AddIcon, EditOutlined as EditIcon, WorkspacePremium } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import type { Plan, UpsertPlanInput } from '../types';
import PageHeader from '../components/PageHeader';
import RoleGate from '../components/RoleGate';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { PlanChip } from '../components/StatusChip';
import { ErrorState, LoadingState } from '../components/States';
import { fmtCurrency, fmtNumber } from '../components/format';
import {
  ENTITLEMENT_CATALOG,
  serializeEntitlements,
  splitPlanEntitlements,
  type EntitlementKey,
} from '../entitlements';

const PLAN_ACCENT: Record<string, string> = {
  free: '#64748b',
  pro: '#2563eb',
  enterprise: '#059669',
};
const accentFor = (code: string) => PLAN_ACCENT[code] ?? '#7c3aed';

interface PlanForm {
  code: string;
  name: string;
  monthlyPriceUsd: string;
  weight: string;
  maxConcurrentExtractionJobs: string;
  maxDocsPerMonth: string;
  maxSeats: string;
  entitlements: EntitlementKey[];
  unknownEntitlements: string[];
  isActive: boolean;
}

const emptyForm: PlanForm = {
  code: '',
  name: '',
  monthlyPriceUsd: '',
  weight: '1',
  maxConcurrentExtractionJobs: '2',
  maxDocsPerMonth: '1000',
  maxSeats: '5',
  entitlements: [],
  unknownEntitlements: [],
  isActive: true,
};

/**
 * The GET response exposes the plan's ENABLED feature keys; rebuild the
 * features JSON object from them for editing.
 */
export const formFromPlan = (plan: Plan): PlanForm => ({
  code: plan.code,
  name: plan.name,
  monthlyPriceUsd: plan.priceMonthlyUsd == null ? '' : String(plan.priceMonthlyUsd),
  weight: String(plan.weight),
  maxConcurrentExtractionJobs: String(plan.concurrencyCap),
  maxDocsPerMonth: plan.monthlyDocQuota == null ? '0' : String(plan.monthlyDocQuota),
  maxSeats: plan.seatQuota == null ? '0' : String(plan.seatQuota),
  entitlements: splitPlanEntitlements(plan.entitlements).selected,
  unknownEntitlements: splitPlanEntitlements(plan.entitlements).unknown,
  isActive: plan.isActive,
});

export default function PlansFlagsPage() {
  const queryClient = useQueryClient();
  // Plan create/update is Owner-only server-side: a plan is what every tenant on it is
  // charged and quota'd by, so editing one reaches every customer at once.
  const permissions = usePlatformPermissions();
  const { enqueueSnackbar } = useSnackbar();
  const [dialog, setDialog] = useState<{ mode: 'create' } | { mode: 'edit'; plan: Plan } | null>(null);
  const [form, setForm] = useState<PlanForm>(emptyForm);

  const { data: plans, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.plans(),
    queryFn: () => platformApi.listPlans(),
  });

  const upsertMutation = useMutation({
    mutationFn: async () => {
      const input: UpsertPlanInput = {
        code: form.code.trim().toLowerCase(),
        name: form.name.trim(),
        weight: Number(form.weight),
        maxConcurrentExtractionJobs: Number(form.maxConcurrentExtractionJobs),
        maxDocsPerMonth: Number(form.maxDocsPerMonth),
        maxSeats: Number(form.maxSeats),
        monthlyPriceUsd: form.monthlyPriceUsd.trim() === '' ? null : Number(form.monthlyPriceUsd),
        features: serializeEntitlements(form.entitlements),
        isActive: form.isActive,
      };
      return dialog?.mode === 'edit'
        ? platformApi.updatePlan(dialog.plan.id, input)
        : platformApi.createPlan(input);
    },
    onSuccess: (plan) => {
      enqueueSnackbar(`Plan ${plan.code} saved`, { variant: 'success' });
      setDialog(null);
      queryClient.invalidateQueries({ queryKey: platformKeys.plans() });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Failed to save the plan'), { variant: 'error' }),
  });

  if (isLoading) return <LoadingState label="Loading plans…" minHeight="60vh" />;
  if (isError || !plans) return <ErrorState message="The persisted plan catalog could not be loaded." onRetry={() => refetch()} />;

  const openCreate = () => {
    setForm(emptyForm);
    setDialog({ mode: 'create' });
  };
  const openEdit = (plan: Plan) => {
    setForm(formFromPlan(plan));
    setDialog({ mode: 'edit', plan });
  };

  const formatQuota = (value: number | null, unit: string) =>
    value == null ? 'Unlimited' : `${fmtNumber(value)}${unit ? ` ${unit}` : ''}`;
  const formatPrice = (plan: Plan) =>
    plan.priceMonthlyUsd == null ? 'Not priced' : plan.priceMonthlyUsd === 0 ? 'Free' : `${fmtCurrency(plan.priceMonthlyUsd)}/mo`;

  // `Number('')` is 0, so a CLEARED quota field used to pass every one of these checks and be
  // submitted as a hard zero. The server does not read zero as "unlimited" — it reads the plan
  // as unquotaed and falls back to the 50-document unplanned-tenant allowance — so an operator
  // who emptied a box to mean "no limit" silently throttled every tenant on that plan.
  const numberProblem = (value: string, label: string, min: number): string | null => {
    if (value.trim() === '') return `${label} is required. It cannot be left blank.`;
    if (!/^\d+$/.test(value.trim())) return `${label} must be a whole number.`;
    if (Number(value) < min) return `${label} must be at least ${min}.`;
    return null;
  };
  const fieldProblems = {
    weight: numberProblem(form.weight, 'Dispatcher weight', 1),
    maxConcurrentExtractionJobs: numberProblem(form.maxConcurrentExtractionJobs, 'Max concurrent extraction jobs', 1),
    maxDocsPerMonth: numberProblem(form.maxDocsPerMonth, 'Max docs / month', 0),
    maxSeats: numberProblem(form.maxSeats, 'Max seats', 0),
    monthlyPriceUsd: form.monthlyPriceUsd.trim() === ''
      ? null
      : Number.isFinite(Number(form.monthlyPriceUsd)) && Number(form.monthlyPriceUsd) >= 0
        ? null
        : 'Monthly price must be a number of 0 or more, or blank for "not priced".',
    code: form.code.trim().length === 0 ? 'A plan code is required.' : null,
    name: form.name.trim().length === 0 ? 'A plan name is required.' : null,
  };
  const formValid = Object.values(fieldProblems).every((problem) => problem === null);

  return (
    <Box>
      <PageHeader
        title="Plans"
        subtitle="Persisted scheduling, quota, pricing, and entitlement configuration."
        actions={
          <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
            {(disabled) => (
              <Button
                variant="contained"
                startIcon={<AddIcon />}
                disabled={disabled}
                onClick={openCreate}
                sx={{ fontWeight: 700 }}
              >
                New Plan
              </Button>
            )}
          </RoleGate>
        }
      />

      <Grid container spacing={2.5} sx={{ mb: 3 }}>
        {plans.length === 0 && (
          <Grid size={{ xs: 12 }}>
            <Alert severity="warning">
              <AlertTitle>No plans configured</AlertTitle>
              Create the first plan before provisioning or assigning billable tenants. No entitlement defaults are inferred.
            </Alert>
          </Grid>
        )}
        {plans.map((plan) => {
          const accent = accentFor(plan.code);
          return (
            <Grid size={{ xs: 12, md: 4 }} key={plan.id}>
              <Paper sx={{ p: 3, borderRadius: 2, height: '100%', borderTop: `4px solid ${accent}` }}>
                <Stack direction="row" spacing={1} alignItems="center">
                  <WorkspacePremium sx={{ color: accent }} />
                  <Typography variant="h6" sx={{ fontWeight: 800, flex: 1 }}>{plan.name}</Typography>
                  <RoleGate allowed={permissions.isOwner} requirement={REQUIRED_ROLE_COPY.owner}>
                    {(disabled) => (
                      <Button
                        size="small"
                        startIcon={<EditIcon fontSize="small" />}
                        disabled={disabled}
                        onClick={() => openEdit(plan)}
                        sx={{ fontWeight: 700 }}
                      >
                        Edit
                      </Button>
                    )}
                  </RoleGate>
                </Stack>
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                  {formatPrice(plan)} · code <Box component="code">{plan.code}</Box>
                </Typography>
                <Divider sx={{ my: 2 }} />
                <Stack spacing={1.25}>
                  <PlanRow label="Dispatcher weight" value={`${plan.weight}x`} />
                  <PlanRow label="Concurrency cap" value={`${plan.concurrencyCap} jobs`} />
                  <PlanRow label="Monthly document quota" value={formatQuota(plan.monthlyDocQuota, 'docs')} />
                  <PlanRow label="Seat quota" value={formatQuota(plan.seatQuota, 'seats')} />
                </Stack>
                <Divider sx={{ my: 2 }} />
                <Typography variant="overline" color="text.secondary" sx={{ fontWeight: 700 }}>Enabled entitlements</Typography>
                <Stack direction="row" useFlexGap spacing={0.75} sx={{ mt: 1, flexWrap: 'wrap' }}>
                  {plan.entitlements.length === 0 ? (
                    <Typography variant="body2" color="text.secondary">None configured</Typography>
                  ) : plan.entitlements.map((entitlement) => (
                    <Chip key={entitlement} size="small" variant="outlined" label={entitlement} />
                  ))}
                </Stack>
              </Paper>
            </Grid>
          );
        })}
      </Grid>

      {plans.length > 0 && <Paper sx={{ p: 3, borderRadius: 2, overflowX: 'auto' }}>
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 2 }}>Quota Matrix</Typography>
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell sx={{ fontWeight: 700 }}>Attribute</TableCell>
              {plans.map((plan) => <TableCell key={plan.id} align="right"><PlanChip tier={plan.code} /></TableCell>)}
            </TableRow>
          </TableHead>
          <TableBody>
            {[
              { label: 'Price / month', get: formatPrice },
              { label: 'Dispatcher weight', get: (plan: Plan) => `${plan.weight}x` },
              { label: 'Concurrency cap', get: (plan: Plan) => `${plan.concurrencyCap} jobs` },
              { label: 'Monthly documents', get: (plan: Plan) => formatQuota(plan.monthlyDocQuota, '') },
              { label: 'Seats', get: (plan: Plan) => formatQuota(plan.seatQuota, '') },
              { label: 'Entitlements', get: (plan: Plan) => `${plan.entitlements.length}` },
            ].map((row) => (
              <TableRow key={row.label} hover>
                <TableCell sx={{ fontWeight: 600 }}>{row.label}</TableCell>
                {plans.map((plan) => <TableCell key={plan.id} align="right">{row.get(plan)}</TableCell>)}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </Paper>}

      {/* Create / edit dialog */}
      <Dialog open={!!dialog} onClose={() => setDialog(null)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>
          {dialog?.mode === 'edit' ? `Edit plan — ${dialog.plan.name}` : 'Create plan'}
        </DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2.5} sx={{ mt: 0.5 }}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="Code"
                value={form.code}
                onChange={(e) => setForm({ ...form, code: e.target.value })}
                helperText="Lowercased unique identifier, e.g. pro"
                fullWidth
                required
              />
              <TextField
                label="Name"
                value={form.name}
                onChange={(e) => setForm({ ...form, name: e.target.value })}
                fullWidth
                required
              />
            </Stack>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="Monthly price (USD)"
                value={form.monthlyPriceUsd}
                onChange={(e) => setForm({ ...form, monthlyPriceUsd: e.target.value })}
                helperText="Blank = not priced"
                fullWidth
              />
              <TextField
                label="Dispatcher weight"
                value={form.weight}
                onChange={(e) => setForm({ ...form, weight: e.target.value })}
                error={Boolean(fieldProblems.weight)}
                helperText={fieldProblems.weight ?? '1–1000'}
                fullWidth
              />
            </Stack>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="Max concurrent extraction jobs"
                value={form.maxConcurrentExtractionJobs}
                onChange={(e) => setForm({ ...form, maxConcurrentExtractionJobs: e.target.value })}
                error={Boolean(fieldProblems.maxConcurrentExtractionJobs)}
                helperText={fieldProblems.maxConcurrentExtractionJobs ?? '1 or more. How many extraction jobs a tenant on this plan may run at once.'}
                fullWidth
              />
              <TextField
                label="Max docs / month"
                value={form.maxDocsPerMonth}
                onChange={(e) => setForm({ ...form, maxDocsPerMonth: e.target.value })}
                error={Boolean(fieldProblems.maxDocsPerMonth)}
                helperText={fieldProblems.maxDocsPerMonth ?? 'Zero is not "unlimited" — it drops the plan to the unplanned-tenant allowance.'}
                fullWidth
              />
              <TextField
                label="Max seats"
                value={form.maxSeats}
                onChange={(e) => setForm({ ...form, maxSeats: e.target.value })}
                error={Boolean(fieldProblems.maxSeats)}
                helperText={fieldProblems.maxSeats ?? 'The number of users a tenant on this plan may have.'}
                fullWidth
              />
            </Stack>
            <Box>
              <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 0.5 }}>Feature entitlements</Typography>
              <Typography variant="caption" color="text.secondary">
                Closed server-owned catalogue. Unselected features are explicitly written as false.
              </Typography>
              {(['Modules', 'Capabilities'] as const).map((group) => (
                <Box key={group} sx={{ mt: 1.5 }}>
                  <Typography variant="overline" color="text.secondary" sx={{ fontWeight: 700 }}>{group}</Typography>
                  <Grid container spacing={0.5}>
                    {ENTITLEMENT_CATALOG.filter((entry) => entry.group === group).map((entry) => (
                      <Grid key={entry.key} size={{ xs: 12, sm: 6 }}>
                        <FormControlLabel
                          control={<Checkbox checked={form.entitlements.includes(entry.key)} onChange={(_, checked) => setForm({
                            ...form,
                            entitlements: checked
                              ? [...form.entitlements, entry.key]
                              : form.entitlements.filter((key) => key !== entry.key),
                          })} />}
                          label={<Box><Typography variant="body2">{entry.label}</Typography><Typography variant="caption" color="text.secondary">{entry.key}</Typography></Box>}
                        />
                      </Grid>
                    ))}
                  </Grid>
                </Box>
              ))}
              {form.entitlements.length === 0 && (
                <Alert severity="warning" sx={{ mt: 1.5 }}>
                  <AlertTitle>No product features enabled</AlertTitle>
                  This plan will grant quotas but deny every closed-catalogue module and capability.
                </Alert>
              )}
              {form.unknownEntitlements.length > 0 && (
                <Alert severity="error" sx={{ mt: 1.5 }}>
                  <AlertTitle>Unsupported entitlement keys were returned</AlertTitle>
                  {form.unknownEntitlements.join(', ')} cannot be saved by the closed catalogue. Saving this plan removes them.
                </Alert>
              )}
            </Box>
            <FormControlLabel
              control={<Switch checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} />}
              label="Active (inactive plans are hidden from the catalog and cannot be assigned)"
            />
            {!form.isActive && (
              <Alert severity="warning">
                <AlertTitle>Plan inactive</AlertTitle>
                Existing assignments remain visible, but this plan cannot be assigned to another tenant.
              </Alert>
            )}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setDialog(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            onClick={() => upsertMutation.mutate()}
            disabled={!formValid || upsertMutation.isPending}
            sx={{ fontWeight: 700, px: 3 }}
          >
            {upsertMutation.isPending ? 'Saving…' : dialog?.mode === 'edit' ? 'Save changes' : 'Create plan'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

function PlanRow({ label, value }: { label: string; value: string }) {
  return (
    <Stack direction="row" justifyContent="space-between" spacing={2}>
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" sx={{ fontWeight: 700, textAlign: 'right' }}>{value}</Typography>
    </Stack>
  );
}
