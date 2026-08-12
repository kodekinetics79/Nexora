import React from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Divider, FormControlLabel, Grid,
  InputAdornment, Paper, Radio, RadioGroup, Stack, Switch, TextField, Typography,
} from '@mui/material';
import { AccountBalance as PolicyIcon, Save as SaveIcon } from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import commercialPolicyService, { type CommercialPolicyDTO } from '../../../api/services/commercialPolicyService';
import supplierScoringWeightsService, { type SupplierScoringWeightsDTO } from '../../../api/services/supplierScoringWeightsService';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import {
  WEIGHT_CRITERIA, WEIGHT_PRESETS, WEIGHT_TOTAL, matchingPreset, parseWeight, sameWeights,
  weightFieldError, weightTotal, weightTotalError, type SupplierWeightsForm,
} from '../../../utils/supplierScoringWeights';
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

const toWeightsForm = (weights: SupplierScoringWeightsDTO): SupplierWeightsForm => ({
  priceWeight: String(weights.priceWeight),
  leadTimeWeight: String(weights.leadTimeWeight),
  warrantyWeight: String(weights.warrantyWeight),
  paymentTermsWeight: String(weights.paymentTermsWeight),
});

/**
 * The tolerance half of the save committed and the weights half did not. Two governed rows behind
 * one button means this outcome is real, and a message that only named the failure would leave the
 * user reapplying a change that is already saved.
 */
class PartialPolicySaveError extends Error {
  readonly inner: unknown;

  constructor(inner: unknown) {
    super('The commercial policy saved and the supplier comparison weights did not.');
    this.name = 'PartialPolicySaveError';
    this.inner = inner;
  }
}

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
  const [weights, setWeights] = React.useState<SupplierWeightsForm | null>(null);
  const [reason, setReason] = React.useState('');

  // The API mirrors SlaController: reading is open to the tenant, writing is manager/admin.
  const canConfigure =
    (userData.isManager === true || userData.isSuperAdmin === true) && hasPermission('UOM', 'edit');

  const { data: policy, isLoading } = useQuery({
    queryKey: ['commercial-policy'],
    queryFn: () => commercialPolicyService.getPolicy(),
  });

  const { data: scoringWeights, isLoading: weightsLoading } = useQuery({
    queryKey: ['supplier-scoring-weights'],
    queryFn: () => supplierScoringWeightsService.getWeights(),
  });

  React.useEffect(() => {
    if (policy) setForm(toForm(policy));
  }, [policy]);

  React.useEffect(() => {
    if (scoringWeights) setWeights(toWeightsForm(scoringWeights));
  }, [scoringWeights]);

  // A tenant still on the server defaults has no row and no audit trail yet, so the first save is
  // always a real change even when the operator retyped nothing: it is what makes the values theirs.
  const policyDirty = !policy || !form || policy.isDefault
    || JSON.stringify(toForm(policy)) !== JSON.stringify(form);
  const weightsDirty = !scoringWeights || !weights || scoringWeights.isDefault
    || !sameWeights(toWeightsForm(scoringWeights), weights);

  const saveMutation = useMutation({
    mutationFn: async (next: { policy: FormState; weights: SupplierWeightsForm }) => {
      // Two governed rows, one reason. Each is written only when it actually differs from what was
      // loaded, so editing a tolerance does not stamp a weights audit entry that changed nothing —
      // an audit trail full of no-op rows is harder to read than one with fewer, truer rows.
      let policyCommitted = false;
      if (policyDirty) {
        await commercialPolicyService.updatePolicy({
          supplierInputTaxRecoverablePercent: numberOrNull(next.policy.supplierInputTaxRecoverablePercent),
          outputTaxRatePercent: next.policy.outputTaxRateStated ? numberOrNull(next.policy.outputTaxRatePercent) : null,
          clearOutputTaxRate: !next.policy.outputTaxRateStated,
          priceTolerancePercent: numberOrNull(next.policy.priceTolerancePercent),
          priceToleranceMinimumAmount: numberOrNull(next.policy.priceToleranceMinimumAmount),
          quantityTolerancePercent: numberOrNull(next.policy.quantityTolerancePercent),
          reason: reason.trim(),
        });
        policyCommitted = true;
      }
      if (weightsDirty) {
        try {
          await supplierScoringWeightsService.updateWeights({
            priceWeight: numberOrNull(next.weights.priceWeight) ?? 0,
            leadTimeWeight: numberOrNull(next.weights.leadTimeWeight) ?? 0,
            warrantyWeight: numberOrNull(next.weights.warrantyWeight) ?? 0,
            paymentTermsWeight: numberOrNull(next.weights.paymentTermsWeight) ?? 0,
            reason: reason.trim(),
          });
        } catch (error) {
          throw policyCommitted ? new PartialPolicySaveError(error) : error;
        }
      }
    },
    onSuccess: () => {
      setReason('');
      toast.success('Commercial policy saved');
    },
    onError: (error: unknown) => toast.error(
      error instanceof PartialPolicySaveError
        ? `Tax and tolerances were saved. The supplier comparison weights were not: ${presentableErrorMessage(error.inner)}`
        : presentableErrorMessage(error),
    ),
    // Runs after success and after a partial failure alike, so the version chips and the fields
    // always show what the server actually holds rather than what was attempted.
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['commercial-policy'] });
      queryClient.invalidateQueries({ queryKey: ['supplier-scoring-weights'] });
      // Prices and landed costs everywhere are computed from this row.
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
      // The weights decide which supplier offer the comparison recommends.
      queryClient.invalidateQueries({ queryKey: ['procurement-quote-comparisons'] });
    },
  });

  if (isLoading || weightsLoading || !form || !weights) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}>
        <CircularProgress />
      </Box>
    );
  }

  const setField = (key: keyof FormState, value: string | boolean) =>
    setForm({ ...form, [key]: value } as FormState);

  const setWeight = (key: keyof SupplierWeightsForm, value: string) =>
    setWeights({ ...weights, [key]: value });

  const total = weightTotal(weights);
  const weightErrors = WEIGHT_CRITERIA.map(({ key }) => weightFieldError(weights[key]));
  const totalError = weightTotalError(weights);
  const selectedPreset = matchingPreset(weights);
  const warrantyWeighted = (parseWeight(weights.warrantyWeight) ?? 0) > 0;
  // Same trap as warranty, and the reason both default to zero: Credit days ships with this release,
  // so it is empty on every supplier that already exists. Weighting payment terms before anyone has
  // filled it in does not tilt the ranking — it stops the ranking happening at all.
  const paymentTermsWeighted = (parseWeight(weights.paymentTermsWeight) ?? 0) > 0;

  const recoverableError = rangeError(form.supplierInputTaxRecoverablePercent, 0, 100, '%');
  const outputRateError = form.outputTaxRateStated
    ? rangeError(form.outputTaxRatePercent, 0, 100, '%')
    : null;
  const priceToleranceError = rangeError(form.priceTolerancePercent, 0, 25, '%');
  const quantityToleranceError = rangeError(form.quantityTolerancePercent, 0, 25, '%');
  const minimumAmountError = (numberOrNull(form.priceToleranceMinimumAmount) ?? -1) < 0
    ? 'Cannot be negative.' : null;

  const hasError = [recoverableError, outputRateError, priceToleranceError, quantityToleranceError,
    minimumAmountError, totalError, ...weightErrors].some((error) => error !== null);
  const canSave = canConfigure && !hasError && reason.trim().length > 0 && !saveMutation.isPending
    && (policyDirty || weightsDirty);

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
        The tax and tolerance numbers behind every cost and every price in this business unit, and
        the weights behind every supplier recommendation. Changing them re-bases every landed cost
        and every tax figure calculated afterwards, and changes which supplier offer the comparison
        puts first, so each change is recorded against your name with the reason you give.
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
        <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 2 }}>
          Supplier comparison
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          How supplier offers for the same RFQ line are ranked against each other. The four weights
          total 100, so every offer carries a score out of 100 that a buyer can check against the
          quote in front of them. The score orders the list and explains itself — it never awards
          anything, and it never blocks an offer from being awarded.
        </Typography>

        {scoringWeights?.isDefault && (
          <Alert severity="info" sx={{ mb: 2, borderRadius: 2 }}>
            No weights have been set for this business unit yet, so supplier offers are ranked on the
            starting values below. Saving once makes them yours and starts the audit trail.
          </Alert>
        )}

        <RadioGroup
          value={selectedPreset?.id ?? 'CUSTOM'}
          onChange={(e) => {
            const preset = WEIGHT_PRESETS.find((candidate) => candidate.id === e.target.value);
            // "Custom" is where the numbers already are; choosing it changes nothing but the label.
            if (preset) setWeights(preset.weights);
          }}
          sx={{ mb: 2 }}
        >
          {WEIGHT_PRESETS.map((preset) => (
            <FormControlLabel
              key={preset.id}
              value={preset.id}
              disabled={!canConfigure}
              control={<Radio size="small" />}
              sx={{ alignItems: 'flex-start', mb: 0.5, '& .MuiRadio-root': { pt: 0.25 } }}
              label={(
                <Box>
                  <Typography sx={{ fontWeight: 800, fontSize: '0.9rem' }}>{preset.label}</Typography>
                  <Typography variant="caption" color="text.secondary">{preset.caption}</Typography>
                </Box>
              )}
            />
          ))}
          <FormControlLabel
            value="CUSTOM"
            disabled={!canConfigure}
            control={<Radio size="small" />}
            sx={{ alignItems: 'flex-start', '& .MuiRadio-root': { pt: 0.25 } }}
            label={(
              <Box>
                <Typography sx={{ fontWeight: 800, fontSize: '0.9rem' }}>Custom</Typography>
                <Typography variant="caption" color="text.secondary">
                  Type the four numbers yourself. Selected automatically when they match no preset.
                </Typography>
              </Box>
            )}
          />
        </RadioGroup>

        <Grid container spacing={2.5}>
          {WEIGHT_CRITERIA.map(({ key, label, helper }, index) => (
            <Grid key={key} size={{ xs: 12, sm: 6, md: 3 }}>
              <TextField
                fullWidth type="number" size="small"
                label={label}
                value={weights[key]}
                disabled={!canConfigure}
                onChange={(e) => setWeight(key, e.target.value)}
                error={weightErrors[index] !== null}
                helperText={weightErrors[index] ?? helper}
                slotProps={{
                  input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                  htmlInput: { min: 0, max: 100, step: 1 },
                }}
              />
            </Grid>
          ))}
        </Grid>

        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mt: 2 }}>
          <Chip
            size="small"
            color={total === WEIGHT_TOTAL ? 'success' : 'error'}
            variant={total === WEIGHT_TOTAL ? 'filled' : 'outlined'}
            label={`Total ${total} of 100`}
          />
          {totalError && (
            <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
              {totalError}
            </Typography>
          )}
        </Stack>

        {warrantyWeighted && (
          <Alert severity="warning" sx={{ mt: 2, borderRadius: 2 }}>
            Warranty is scored from <strong>Warranty (months)</strong> on each supplier quote line —
            typed when a supplier response is entered in the Sourcing workbench or a quote is
            captured in the Supplier Quote inbox — and a longer warranty scores higher. That field is
            new in this release, so it is blank on every line recorded before it, and a criterion
            with no value is never scored as zero. While warranty carries weight, an offer whose line
            has no warranty months will show "Cannot score" instead of a score — it stays awardable,
            but it will not be ranked. Record the months on the lines you compare before giving this
            weight.
          </Alert>
        )}

        {paymentTermsWeighted && (
          <Alert severity="warning" sx={{ mt: 2, borderRadius: 2 }}>
            Payment terms are scored from each supplier's <strong>Credit days</strong>, which is new in
            this release and is empty until someone fills it in. While payment terms carry weight, an
            offer from a supplier with no Credit days will show "Cannot score" instead of a score — it
            stays awardable, but it will not be ranked. Set Credit days on the suppliers you compare
            most often before giving this weight.
          </Alert>
        )}

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
            {scoringWeights && !scoringWeights.isDefault && (
              <Chip size="small" label={`weights version ${scoringWeights.version}`} />
            )}
          </Stack>
          <Button
            variant="contained"
            startIcon={saveMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <SaveIcon />}
            disabled={!canSave}
            onClick={() => form && weights && saveMutation.mutate({ policy: form, weights })}
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
