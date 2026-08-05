import { useState } from 'react';
import Stack from '../components/Flex';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Button,
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
import { PlanChip } from '../components/StatusChip';
import { ErrorState, LoadingState } from '../components/States';
import { fmtCurrency, fmtNumber } from '../components/format';

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
  features: string;
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
  features: '{}',
  isActive: true,
};

/**
 * The GET response exposes the plan's ENABLED feature keys; rebuild the
 * features JSON object from them for editing.
 */
const formFromPlan = (plan: Plan): PlanForm => ({
  code: plan.code,
  name: plan.name,
  monthlyPriceUsd: plan.priceMonthlyUsd == null ? '' : String(plan.priceMonthlyUsd),
  weight: String(plan.weight),
  maxConcurrentExtractionJobs: String(plan.concurrencyCap),
  maxDocsPerMonth: plan.monthlyDocQuota == null ? '0' : String(plan.monthlyDocQuota),
  maxSeats: plan.seatQuota == null ? '0' : String(plan.seatQuota),
  features: JSON.stringify(Object.fromEntries(plan.entitlements.map((key) => [key, true])), null, 2),
  isActive: plan.isActive,
});

const featuresError = (raw: string): string | null => {
  try {
    const parsed: unknown = JSON.parse(raw);
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return 'Features must be a JSON object.';
    }
    return null;
  } catch {
    return 'Features must be valid JSON.';
  }
};

export default function PlansFlagsPage() {
  const queryClient = useQueryClient();
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
        features: form.features.trim() || '{}',
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

  const featuresProblem = featuresError(form.features.trim() || '{}');
  const numbersValid =
    [form.weight, form.maxConcurrentExtractionJobs].every((v) => Number.isInteger(Number(v)) && Number(v) >= 1) &&
    [form.maxDocsPerMonth, form.maxSeats].every((v) => Number.isInteger(Number(v)) && Number(v) >= 0) &&
    (form.monthlyPriceUsd.trim() === '' || (Number.isFinite(Number(form.monthlyPriceUsd)) && Number(form.monthlyPriceUsd) >= 0));
  const formValid = form.code.trim().length > 0 && form.name.trim().length > 0 && !featuresProblem && numbersValid;

  return (
    <Box>
      <PageHeader
        title="Plans"
        subtitle="Persisted scheduling, quota, pricing, and entitlement configuration."
        actions={
          <Button variant="contained" startIcon={<AddIcon />} onClick={openCreate} sx={{ fontWeight: 700 }}>
            New Plan
          </Button>
        }
      />

      <Grid container spacing={2.5} sx={{ mb: 3 }}>
        {plans.map((plan) => {
          const accent = accentFor(plan.code);
          return (
            <Grid size={{ xs: 12, md: 4 }} key={plan.id}>
              <Paper sx={{ p: 3, borderRadius: 2, height: '100%', borderTop: `4px solid ${accent}` }}>
                <Stack direction="row" spacing={1} alignItems="center">
                  <WorkspacePremium sx={{ color: accent }} />
                  <Typography variant="h6" sx={{ fontWeight: 800, flex: 1 }}>{plan.name}</Typography>
                  <Button size="small" startIcon={<EditIcon fontSize="small" />} onClick={() => openEdit(plan)} sx={{ fontWeight: 700 }}>
                    Edit
                  </Button>
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

      <Paper sx={{ p: 3, borderRadius: 2, overflowX: 'auto' }}>
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
      </Paper>

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
                helperText="1–1000"
                fullWidth
              />
            </Stack>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="Max concurrent extraction jobs"
                value={form.maxConcurrentExtractionJobs}
                onChange={(e) => setForm({ ...form, maxConcurrentExtractionJobs: e.target.value })}
                fullWidth
              />
              <TextField
                label="Max docs / month"
                value={form.maxDocsPerMonth}
                onChange={(e) => setForm({ ...form, maxDocsPerMonth: e.target.value })}
                fullWidth
              />
              <TextField
                label="Max seats"
                value={form.maxSeats}
                onChange={(e) => setForm({ ...form, maxSeats: e.target.value })}
                fullWidth
              />
            </Stack>
            <TextField
              label="Features (JSON object)"
              value={form.features}
              onChange={(e) => setForm({ ...form, features: e.target.value })}
              error={!!featuresProblem}
              helperText={featuresProblem ?? 'Feature entitlement keys with boolean values, e.g. {"copilot": true}'}
              multiline
              minRows={3}
              fullWidth
              slotProps={{ input: { sx: { fontFamily: 'monospace', fontSize: 13 } } }}
            />
            <FormControlLabel
              control={<Switch checked={form.isActive} onChange={(e) => setForm({ ...form, isActive: e.target.checked })} />}
              label="Active (inactive plans are hidden from the catalog and cannot be assigned)"
            />
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
