import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Divider, FormControlLabel, Grid,
  InputAdornment, Paper, Stack, Switch, TextField, Typography,
} from '@mui/material';
import { AccountBalance as PolicyIcon, Save as SaveIcon } from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import commercialPolicyService, { type CommercialPolicyDTO } from '../../../api/services/commercialPolicyService';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { useAuth } from '../../../context/AuthContext';

const MAX_REASON = 1000;

/** Kept as strings so a half-typed "1." does not become NaN mid-keystroke. */
interface FormState {
  supplierInputTaxRecoverablePercent: string;
  outputTaxRatePercent: string;
  outputTaxRateStated: boolean;
  priceTolerancePercent: string;
  priceToleranceMinimumAmount: string;
  quantityTolerancePercent: string;
}

const toForm = (policy: CommercialPolicyDTO): FormState => ({
  supplierInputTaxRecoverablePercent: String(policy.supplierInputTaxRecoverablePercent),
  outputTaxRatePercent: policy.outputTaxRatePercent === null ? '' : String(policy.outputTaxRatePercent),
  outputTaxRateStated: policy.outputTaxRatePercent !== null,
  priceTolerancePercent: String(policy.priceTolerancePercent),
  priceToleranceMinimumAmount: String(policy.priceToleranceMinimumAmount),
  quantityTolerancePercent: String(policy.quantityTolerancePercent),
});

const numberOrNull = (raw: string): number | null => {
  const trimmed = raw.trim();
  if (trimmed === '') return null;
  const value = Number(trimmed);
  return Number.isFinite(value) ? value : null;
};

const rangeError = (raw: string, min: number, max: number, unit: string): string | null => {
  const value = numberOrNull(raw);
  if (value === null) return `Enter a number between ${min} and ${max}${unit}.`;
  if (value < min || value > max) return `Must be between ${min} and ${max}${unit}.`;
  return null;
};

/**
 * Commercial Policy — the tenant-settable numbers behind every landed cost and every customer
 * price.
 *
 * These settings existed in the database and nothing could change them: no endpoint, no screen.
 * Every tenant silently ran the KSA defaults, while the product direction was explicitly to "keep
 * it custom and let the customer set this". A change here re-bases every landed cost and every
 * derived tax computed afterwards, which is why the reason box is mandatory and why the page shows
 * who last changed it and when.
 */
const CommercialPolicyPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { userData, hasPermission } = useAuth();
  const [form, setForm] = React.useState<FormState | null>(null);
  const [reason, setReason] = React.useState('');

  // The API mirrors SlaController: reading is open to the tenant, writing is manager/admin.
  const canConfigure =
    (userData.isManager === true || userData.isSuperAdmin === true) && hasPermission('UOM', 'edit');

  const { data: policy, isLoading } = useQuery({
    queryKey: ['commercial-policy'],
    queryFn: () => commercialPolicyService.getPolicy(),
  });

  React.useEffect(() => {
    if (policy) setForm(toForm(policy));
  }, [policy]);

  const saveMutation = useMutation({
    mutationFn: (next: FormState) => commercialPolicyService.updatePolicy({
      supplierInputTaxRecoverablePercent: numberOrNull(next.supplierInputTaxRecoverablePercent),
      outputTaxRatePercent: next.outputTaxRateStated ? numberOrNull(next.outputTaxRatePercent) : null,
      clearOutputTaxRate: !next.outputTaxRateStated,
      priceTolerancePercent: numberOrNull(next.priceTolerancePercent),
      priceToleranceMinimumAmount: numberOrNull(next.priceToleranceMinimumAmount),
      quantityTolerancePercent: numberOrNull(next.quantityTolerancePercent),
      reason: reason.trim(),
    }),
    onSuccess: () => {
      setReason('');
      toast.success('Commercial policy saved');
      queryClient.invalidateQueries({ queryKey: ['commercial-policy'] });
      // Prices and landed costs everywhere are computed from this row.
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(error)),
  });

  if (isLoading || !form) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}>
        <CircularProgress />
      </Box>
    );
  }

  const setField = (key: keyof FormState, value: string | boolean) =>
    setForm({ ...form, [key]: value } as FormState);

  const recoverableError = rangeError(form.supplierInputTaxRecoverablePercent, 0, 100, '%');
  const outputRateError = form.outputTaxRateStated
    ? rangeError(form.outputTaxRatePercent, 0, 100, '%')
    : null;
  const priceToleranceError = rangeError(form.priceTolerancePercent, 0, 25, '%');
  const quantityToleranceError = rangeError(form.quantityTolerancePercent, 0, 25, '%');
  const minimumAmountError = (numberOrNull(form.priceToleranceMinimumAmount) ?? -1) < 0
    ? 'Cannot be negative.' : null;

  const hasError = [recoverableError, outputRateError, priceToleranceError, quantityToleranceError,
    minimumAmountError].some((error) => error !== null);
  const canSave = canConfigure && !hasError && reason.trim().length > 0 && !saveMutation.isPending;

  const recoverablePercent = numberOrNull(form.supplierInputTaxRecoverablePercent) ?? 0;

  return (
    <Box sx={{ p: 3, maxWidth: 900, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
        <PolicyIcon color="primary" />
        <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
          Commercial Policy
        </Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3 }}>
        The tax and tolerance numbers behind every cost and every price in this business unit.
        Changing them re-bases every landed cost and every tax figure calculated afterwards, so each
        change is recorded against your name with the reason you give.
      </Typography>

      {policy?.isDefault && (
        <Alert severity="info" sx={{ mb: 2, borderRadius: 2 }}>
          These are the starting values for Saudi Arabia. Nothing has been set for this business unit
          yet — saving once makes them yours and starts the audit trail.
        </Alert>
      )}
      {!canConfigure && (
        <Alert severity="warning" sx={{ mb: 2, borderRadius: 2 }}>
          You can see this policy but not change it. Ask a manager or an administrator.
        </Alert>
      )}

      <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
        <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 2 }}>
          Tax
        </Typography>
        <Grid container spacing={2.5}>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth type="number" size="small"
              label="Supplier tax you can reclaim"
              value={form.supplierInputTaxRecoverablePercent}
              disabled={!canConfigure}
              onChange={(e) => setField('supplierInputTaxRecoverablePercent', e.target.value)}
              error={recoverableError !== null}
              helperText={recoverableError ?? (
                recoverablePercent >= 100
                  ? 'You reclaim all of the tax suppliers charge you, so none of it is a cost of the goods.'
                  : recoverablePercent <= 0
                    ? 'You reclaim none of it, so all of the tax suppliers charge you is a cost of the goods.'
                    : `You reclaim ${recoverablePercent}%, so the remaining ${(100 - recoverablePercent).toFixed(2).replace(/\.?0+$/, '')}% is a cost of the goods.`
              )}
              slotProps={{
                input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                htmlInput: { min: 0, max: 100, step: 0.01 },
              }}
            />
          </Grid>
          <Grid size={{ xs: 12, md: 6 }}>
            <TextField
              fullWidth type="number" size="small"
              label="Tax you charge customers"
              value={form.outputTaxRatePercent}
              disabled={!canConfigure || !form.outputTaxRateStated}
              onChange={(e) => setField('outputTaxRatePercent', e.target.value)}
              error={outputRateError !== null}
              helperText={outputRateError
                ?? 'Applied to every standard-rated quote line. Lines marked as an export, exempt or reverse charge are always taxed at zero.'}
              slotProps={{
                input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                htmlInput: { min: 0, max: 100, step: 0.01 },
              }}
            />
            <FormControlLabel
              sx={{ mt: 0.5 }}
              control={(
                <Switch
                  size="small"
                  checked={form.outputTaxRateStated}
                  disabled={!canConfigure}
                  onChange={(e) => setField('outputTaxRateStated', e.target.checked)}
                />
              )}
              label={(
                <Typography variant="caption" color="text.secondary">
                  We have a rate to state
                </Typography>
              )}
            />
            {!form.outputTaxRateStated && (
              <Alert severity="warning" sx={{ mt: 1, borderRadius: 2 }}>
                With no rate stated, no quote can be sent and no PDF can be produced until one is set.
                A price sent with no tax shown on it is treated as tax-inclusive, and the difference
                comes out of your margin.
              </Alert>
            )}
          </Grid>
        </Grid>

        <Divider sx={{ my: 3 }} />
        <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 2 }}>
          Customer PO tolerances
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          How far a customer purchase order may differ from what you quoted before Nexora reports it
          as a discrepancy.
        </Typography>
        <Grid container spacing={2.5}>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              fullWidth type="number" size="small"
              label="Price tolerance"
              value={form.priceTolerancePercent}
              disabled={!canConfigure}
              onChange={(e) => setField('priceTolerancePercent', e.target.value)}
              error={priceToleranceError !== null}
              helperText={priceToleranceError ?? 'Percentage difference in unit price treated as rounding.'}
              slotProps={{
                input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                htmlInput: { min: 0, max: 25, step: 0.1 },
              }}
            />
          </Grid>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              fullWidth type="number" size="small"
              label="Minimum price difference"
              value={form.priceToleranceMinimumAmount}
              disabled={!canConfigure}
              onChange={(e) => setField('priceToleranceMinimumAmount', e.target.value)}
              error={minimumAmountError !== null}
              helperText={minimumAmountError ?? 'A difference smaller than this is ignored whatever the percentage says.'}
              slotProps={{ htmlInput: { min: 0, step: 0.01 } }}
            />
          </Grid>
          <Grid size={{ xs: 12, md: 4 }}>
            <TextField
              fullWidth type="number" size="small"
              label="Quantity tolerance"
              value={form.quantityTolerancePercent}
              disabled={!canConfigure}
              onChange={(e) => setField('quantityTolerancePercent', e.target.value)}
              error={quantityToleranceError !== null}
              helperText={quantityToleranceError ?? 'Zero means any quantity change is a real award decision, not noise.'}
              slotProps={{
                input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                htmlInput: { min: 0, max: 25, step: 0.1 },
              }}
            />
          </Grid>
        </Grid>

        <Divider sx={{ my: 3 }} />
        <TextField
          fullWidth multiline minRows={2} required
          label="Reason for this change"
          value={reason}
          disabled={!canConfigure}
          onChange={(e) => setReason(e.target.value.slice(0, MAX_REASON))}
          helperText={`Recorded in your audit trail with who changed it and when. ${reason.length}/${MAX_REASON}`}
        />

        <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mt: 3 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            {policy && !policy.isDefault && policy.modifiedOn && (
              <Typography variant="caption" color="text.secondary">
                Last changed {new Date(policy.modifiedOn).toLocaleString()} by {policy.modifiedBy ?? 'unknown'}
              </Typography>
            )}
            {policy && !policy.isDefault && <Chip size="small" label={`version ${policy.version}`} />}
          </Stack>
          <Button
            variant="contained"
            startIcon={saveMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <SaveIcon />}
            disabled={!canSave}
            onClick={() => form && saveMutation.mutate(form)}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            Save policy
          </Button>
        </Stack>
      </Paper>
    </Box>
  );
};

export default CommercialPolicyPage;
