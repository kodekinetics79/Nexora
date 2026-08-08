import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControl,
  FormLabel,
  Grid,
  MenuItem,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import {
  Calculate as ComputeIcon,
  EditOutlined as EditIcon,
  GppGoodOutlined as FinalizeIcon,
  PushPinOutlined as PinIcon,
  SwapHoriz as PlanIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import ReasonDialog from '../../components/ReasonDialog';
import RoleGate from '../../components/RoleGate';
import { EmptyState, ErrorState, LoadingState } from '../../components/States';
import { BillingModeChip, Dash, SoftChip } from '../../components/StatusChip';
import { fmtDate, fmtDateTime, fmtNumber } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../../auth/permissions';
import {
  leakReasonLabel,
  validateCommercialTerms,
  type CommercialTermsForm,
} from '../../components/commercialTermsValidation';
import { BILLING_MODES } from '../../types';
import type { BillingMode, Tenant, TenantBillingProfile } from '../../types';

const fmtMoney = (amount: number, currency: string) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency, maximumFractionDigits: 2 }).format(amount);

const currentPeriod = () => new Date().toISOString().slice(0, 7);

const toDateInput = (value: string | null): string => (value ? value.slice(0, 10) : '');

const formFromProfile = (profile: TenantBillingProfile): CommercialTermsForm => ({
  billingMode:
    (BILLING_MODES.find((mode) => mode.toLowerCase() === profile.billingMode.toLowerCase()) as BillingMode) ??
    'Billable',
  billingModeReason: profile.billingModeReason ?? '',
  trialEndsOn: toDateInput(profile.trialEndsOn),
  billingStartsOn: toDateInput(profile.billingStartsOn),
});

export default function CommercialTab({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();

  const [termsOpen, setTermsOpen] = useState(false);
  const [terms, setTerms] = useState<CommercialTermsForm>({
    billingMode: 'Billable',
    billingModeReason: '',
    trialEndsOn: '',
    billingStartsOn: '',
  });
  const [termsAttempted, setTermsAttempted] = useState(false);
  const [pinOpen, setPinOpen] = useState(false);
  const [pinCardId, setPinCardId] = useState('');
  const [planOpen, setPlanOpen] = useState(false);
  const [planId, setPlanId] = useState('');
  const [period, setPeriod] = useState(currentPeriod());
  const [finalizeId, setFinalizeId] = useState<string | null>(null);
  const [reviewId, setReviewId] = useState<string | null>(null);

  // The whole billing controller is Owner|BillingAdmin. A SupportAdmin or ReadOnlyOps
  // operator gets no request at all rather than a 403 on page load.
  const profileQuery = useQuery({
    queryKey: platformKeys.tenantBilling(tenant.id),
    queryFn: () => platformApi.getTenantBillingProfile(tenant.id),
    enabled: permissions.canAdministerBilling,
  });

  const rateCardsQuery = useQuery({
    queryKey: platformKeys.rateCards(),
    queryFn: () => platformApi.listRateCards(),
    enabled: permissions.canAdministerBilling,
  });

  const plansQuery = useQuery({
    queryKey: platformKeys.plans(),
    queryFn: () => platformApi.listPlans(),
    enabled: permissions.canAdministerBilling,
  });

  const reviewQuery = useQuery({
    queryKey: platformKeys.statement(reviewId ?? ''),
    queryFn: () => platformApi.getStatement(reviewId as string),
    enabled: Boolean(reviewId) && permissions.canAdministerBilling,
  });

  const profile = profileQuery.data;

  useEffect(() => {
    if (profile) setTerms(formFromProfile(profile));
  }, [profile]);

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantBilling(tenant.id) });
    queryClient.invalidateQueries({ queryKey: [...platformKeys.all, 'billing'] });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
  };

  const fail = (fallback: string) => (error: unknown) =>
    enqueueSnackbar(platformErrorMessage(error, fallback), { variant: 'error' });

  const termsMutation = useMutation({
    mutationFn: () =>
      platformApi.setTenantCommercialTerms(tenant.id, {
        billingMode: terms.billingMode,
        billingModeReason: terms.billingMode === 'Billable' ? null : terms.billingModeReason.trim(),
        // Only a trial carries an end date; sending one for another mode would hand the
        // server an expiry for a tenant that has none.
        trialEndsOn: terms.billingMode === 'Trial' && terms.trialEndsOn ? terms.trialEndsOn : null,
        billingStartsOn: terms.billingStartsOn || null,
      }),
    onSuccess: () => {
      enqueueSnackbar('Commercial terms updated', { variant: 'success' });
      setTermsOpen(false);
      invalidate();
    },
    onError: fail('The commercial terms were refused'),
  });

  const pinMutation = useMutation({
    mutationFn: (reason: string) =>
      platformApi.setTenantRateCard(tenant.id, { rateCardId: pinCardId || null, reason }),
    onSuccess: () => {
      enqueueSnackbar(pinCardId ? 'Rate card pinned' : 'Rate card pin cleared', { variant: 'success' });
      setPinOpen(false);
      invalidate();
    },
    onError: fail('The rate card change was refused'),
  });

  // Plan assignment lives on `PUT /api/platform/tenants/{id}/plan`, which carries the
  // Billing policy for the same reason everything else here does: the plan is what a
  // customer is charged, so it is a commercial decision and not a support one.
  const planMutation = useMutation({
    mutationFn: (reason: string) => platformApi.changeTenantPlan(tenant.id, planId, reason),
    onSuccess: (updated) => {
      enqueueSnackbar(`Plan set to ${updated.planCode ?? planId}`, { variant: 'success' });
      setPlanOpen(false);
      invalidate();
    },
    onError: fail('The plan change was refused'),
  });

  const computeMutation = useMutation({
    mutationFn: () => platformApi.computeStatement(tenant.id, period),
    onSuccess: (statement) => {
      enqueueSnackbar(`Statement computed — ${fmtMoney(statement.totalAmount, statement.currency)}`, {
        variant: 'success',
      });
      setReviewId(statement.id);
      invalidate();
    },
    onError: fail('The statement could not be computed'),
  });

  const finalizeMutation = useMutation({
    mutationFn: (id: string) => platformApi.finalizeStatement(id),
    onSuccess: () => {
      enqueueSnackbar('Statement finalized — it is now immutable', { variant: 'success' });
      setFinalizeId(null);
      invalidate();
    },
    onError: fail('Finalize was refused'),
  });

  if (!permissions.canAdministerBilling) {
    return (
      <Alert severity="info" sx={{ borderRadius: 2 }}>
        <AlertTitle sx={{ fontWeight: 800 }}>Commercial terms are not yours to see</AlertTitle>
        {REQUIRED_ROLE_COPY.billing} A support administrator can suspend, resume and archive a tenant, and must
        not be able to move it onto a cheaper rate card or extend a trial.
      </Alert>
    );
  }

  if (profileQuery.isLoading) return <LoadingState label="Reading the billing profile…" />;
  if (profileQuery.isError || !profile) {
    return (
      <ErrorState
        message={platformErrorMessage(profileQuery.error, 'The billing profile could not be read.')}
        onRetry={() => profileQuery.refetch()}
      />
    );
  }

  const risk = profile.revenueRisk;
  const termsErrors = validateCommercialTerms(terms, profile.planId !== null);
  const termsValid = Object.keys(termsErrors).length === 0;
  const finalizeTarget = profile.statements.find((statement) => statement.id === finalizeId) ?? null;
  const periodValid = /^\d{4}-(0[1-9]|1[0-2])$/.test(period);

  return (
    <Stack spacing={2.5}>
      {risk.commercialConfigurationRequired && (
        <Alert severity="error" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Commercial configuration required</AlertTitle>
          {risk.commercialConfigurationState === 'plan-missing'
            ? 'This tenant is Billable or on Trial with no plan, so it is priced by nobody and charged for nothing. Assign a plan, then set the billing mode.'
            : 'This tenant is not billable and no reason is recorded — free service nobody signed for. Record the exemption below.'}
        </Alert>
      )}

      {profile.pinnedRateCardMissing && (
        <Alert severity="error" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>The pinned rate card no longer exists</AlertTitle>
          Billing refuses to compute rather than silently repricing this tenant onto whatever card is active.
          Statements will simply stop appearing until the pin is repaired.
        </Alert>
      )}

      {risk.trialExpired && (
        <Alert severity="error" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>
            This trial ended {profile.trialEndsOn ? fmtDate(profile.trialEndsOn) : 'in the past'}
          </AlertTitle>
          The workspace is still being served for free. Convert it to Billable or suspend it.
        </Alert>
      )}

      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, md: 7 }}>
          <PageSection
            title="Commercial terms"
            actions={
              <Stack direction="row" spacing={1}>
                <RoleGate allowed={permissions.canAdministerBilling} requirement={REQUIRED_ROLE_COPY.billing}>
                  {(disabled) => (
                    <Button
                      startIcon={<PlanIcon />}
                      disabled={disabled}
                      onClick={() => {
                        setPlanId(profile.planId ?? '');
                        setPlanOpen(true);
                      }}
                      sx={{ fontWeight: 700 }}
                    >
                      Change plan
                    </Button>
                  )}
                </RoleGate>
                <RoleGate allowed={permissions.canAdministerBilling} requirement={REQUIRED_ROLE_COPY.billing}>
                  {(disabled) => (
                    <Button
                      startIcon={<EditIcon />}
                      disabled={disabled}
                      onClick={() => {
                        setTerms(formFromProfile(profile));
                        setTermsAttempted(false);
                        setTermsOpen(true);
                      }}
                      sx={{ fontWeight: 700 }}
                    >
                      Edit terms
                    </Button>
                  )}
                </RoleGate>
              </Stack>
            }
          >
            <Stack spacing={1.5}>
              <Row label="Billing mode">
                <BillingModeChip
                  mode={
                    (BILLING_MODES.find((mode) => mode.toLowerCase() === profile.billingMode.toLowerCase()) as
                      | BillingMode
                      | undefined) ?? null
                  }
                />
              </Row>
              {profile.billingModeReason && <Row label="Why not billable">{profile.billingModeReason}</Row>}
              <Row label="Plan">
                {profile.planCode ? `${profile.planName ?? profile.planCode} (${profile.planCode})` : <Dash />}
              </Row>
              <Row label="Plan monthly price">
                {profile.planMonthlyPriceUsd == null ? <Dash /> : fmtMoney(profile.planMonthlyPriceUsd, 'USD')}
              </Row>
              <Row label="Trial ends">{profile.trialEndsOn ? fmtDate(profile.trialEndsOn) : <Dash />}</Row>
              <Row label="Billing starts">
                {profile.billingStartsOn ? fmtDate(profile.billingStartsOn) : <Dash />}
              </Row>
              <Row label="Contract">
                {profile.contractStartOn || profile.contractEndOn
                  ? `${profile.contractStartOn ? fmtDate(profile.contractStartOn) : '—'} → ${profile.contractEndOn ? fmtDate(profile.contractEndOn) : '—'}`
                  : '—'}
              </Row>
              <Row label="Payment terms">
                {profile.paymentTermsDays == null ? <Dash /> : `${profile.paymentTermsDays} days`}
              </Row>
              <Row label="PO reference">{profile.purchaseOrderReference ?? <Dash />}</Row>
              <Row label="Billing contact">
                {[profile.billingContactName, profile.billingContactEmail].filter(Boolean).join(' · ') || <Dash />}
              </Row>
              <Row label="Account owner">{profile.accountOwnerEmail ?? <Dash />}</Row>
            </Stack>
          </PageSection>
        </Grid>

        <Grid size={{ xs: 12, md: 5 }}>
          <PageSection
            title="Pinned rate card"
            subtitle="A pin is what stops a negotiated customer being repriced the day somebody activates a new card."
            actions={
              <RoleGate allowed={permissions.canAdministerBilling} requirement={REQUIRED_ROLE_COPY.billing}>
                {(disabled) => (
                  <Button
                    startIcon={<PinIcon />}
                    disabled={disabled}
                    onClick={() => {
                      setPinCardId(profile.pinnedRateCardId ?? '');
                      setPinOpen(true);
                    }}
                    sx={{ fontWeight: 700 }}
                  >
                    Change
                  </Button>
                )}
              </RoleGate>
            }
          >
            <Stack spacing={1.5}>
              <Row label="Card">
                {profile.pinnedRateCardCode ? (
                  <Box component="code" sx={{ fontWeight: 700 }}>
                    {profile.pinnedRateCardCode}
                  </Box>
                ) : (
                  <Dash />
                )}
              </Row>
              {!profile.pinnedRateCardId && (
                <Typography variant="caption" color="warning.main" sx={{ fontWeight: 700 }}>
                  Unpinned — this tenant is charged on whichever card happens to be active for the period.
                </Typography>
              )}
            </Stack>
          </PageSection>

          <PageSection title="Revenue risk" sx={{ mt: 2.5 }}>
            {risk.leakReasons.length === 0 ? (
              <Typography variant="body2" color="success.main" sx={{ fontWeight: 700 }}>
                Nothing flagged. This tenant is either priced or exempt on the record.
              </Typography>
            ) : (
              <Stack spacing={1}>
                {risk.leakReasons.map((code) => (
                  <Stack key={code} direction="row" spacing={1} alignItems="flex-start">
                    <Chip size="small" label={code} color="warning" variant="outlined" sx={{ fontWeight: 700 }} />
                    <Typography variant="caption" color="text.secondary">
                      {leakReasonLabel(code)}
                    </Typography>
                  </Stack>
                ))}
              </Stack>
            )}
          </PageSection>
        </Grid>
      </Grid>

      <PageSection
        title="Statements"
        subtitle="Draft statements can be recomputed at any time. A Final statement is immutable and pins its rate card forever."
        actions={
          <Stack direction="row" spacing={1} alignItems="center">
            <TextField
              size="small"
              type="month"
              label="Period"
              value={period}
              onChange={(event) => setPeriod(event.target.value)}
              error={!periodValid}
              helperText={periodValid ? undefined : 'YYYY-MM'}
              slotProps={{ inputLabel: { shrink: true } }}
              sx={{ minWidth: 160 }}
            />
            <RoleGate allowed={permissions.canAdministerBilling} requirement={REQUIRED_ROLE_COPY.billing}>
              {(disabled) => (
                <Button
                  variant="contained"
                  startIcon={<ComputeIcon />}
                  disabled={disabled || !periodValid || computeMutation.isPending}
                  onClick={() => computeMutation.mutate()}
                  sx={{ fontWeight: 700 }}
                >
                  {computeMutation.isPending ? 'Computing…' : 'Compute'}
                </Button>
              )}
            </RoleGate>
          </Stack>
        }
      >
        {profile.statements.length === 0 ? (
          <EmptyState
            title="No statements yet"
            message="Nothing has ever been computed for this tenant — which for a Billable customer means they have been served for free."
            minHeight={160}
          />
        ) : (
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 700 }}>Period</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Status</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Total</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Computed</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Finalized</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Actions</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {profile.statements.map((statement) => (
                  <TableRow key={statement.id} hover selected={statement.id === reviewId}>
                    <TableCell sx={{ fontWeight: 600 }}>{statement.period}</TableCell>
                    <TableCell>
                      <SoftChip
                        label={statement.status}
                        tone={statement.status.toLowerCase() === 'final' ? 'success' : 'info'}
                        dot={false}
                      />
                    </TableCell>
                    <TableCell align="right" sx={{ fontWeight: 700 }}>
                      {fmtMoney(statement.totalAmount, statement.currency)}
                    </TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary">
                        {fmtDateTime(statement.computedAtUtc)}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary">
                        {statement.finalizedAtUtc
                          ? `${fmtDateTime(statement.finalizedAtUtc)} · ${statement.finalizedBy ?? ''}`
                          : '—'}
                      </Typography>
                    </TableCell>
                    <TableCell align="right">
                      <Stack direction="row" spacing={0.5} justifyContent="flex-end">
                        <Button size="small" onClick={() => setReviewId(statement.id)} sx={{ fontWeight: 700 }}>
                          Review
                        </Button>
                        {statement.status.toLowerCase() !== 'final' && (
                          <RoleGate
                            allowed={permissions.canAdministerBilling}
                            requirement={REQUIRED_ROLE_COPY.billing}
                          >
                            {(disabled) => (
                              <Button
                                size="small"
                                color="success"
                                startIcon={<FinalizeIcon fontSize="small" />}
                                disabled={disabled}
                                onClick={() => setFinalizeId(statement.id)}
                                sx={{ fontWeight: 700 }}
                              >
                                Finalize
                              </Button>
                            )}
                          </RoleGate>
                        )}
                      </Stack>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        )}
      </PageSection>

      {/* Statement review — the lines an operator has to read BEFORE finalize, because
          after finalize there is nothing to do about them. */}
      <Dialog open={Boolean(reviewId)} onClose={() => setReviewId(null)} fullWidth maxWidth="lg">
        <DialogTitle sx={{ fontWeight: 800 }}>Statement review</DialogTitle>
        <DialogContent dividers>
          {reviewQuery.isLoading ? (
            <LoadingState label="Loading statement lines…" minHeight={200} />
          ) : reviewQuery.isError || !reviewQuery.data ? (
            <ErrorState
              message={platformErrorMessage(reviewQuery.error, 'The statement could not be loaded.')}
              onRetry={() => reviewQuery.refetch()}
              minHeight={200}
            />
          ) : (
            <Stack spacing={2}>
              <Stack direction="row" spacing={1.5} alignItems="center" sx={{ flexWrap: 'wrap' }}>
                <SoftChip
                  label={reviewQuery.data.status}
                  tone={reviewQuery.data.status.toLowerCase() === 'final' ? 'success' : 'info'}
                  dot={false}
                />
                <Typography variant="body2" color="text.secondary">
                  {reviewQuery.data.periodStartUtc.slice(0, 10)} → {reviewQuery.data.periodEndUtc.slice(0, 10)} ·
                  rate card {reviewQuery.data.rateCardId}
                </Typography>
              </Stack>
              <Box sx={{ overflowX: 'auto' }}>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell sx={{ fontWeight: 700 }}>Meter</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Description</TableCell>
                      <TableCell sx={{ fontWeight: 700 }} align="right">Metered</TableCell>
                      <TableCell sx={{ fontWeight: 700 }} align="right">Included</TableCell>
                      <TableCell sx={{ fontWeight: 700 }} align="right">Billable</TableCell>
                      <TableCell sx={{ fontWeight: 700 }} align="right">Unit price</TableCell>
                      <TableCell sx={{ fontWeight: 700 }} align="right">Amount</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {reviewQuery.data.lines.map((line) => (
                      <TableRow key={line.meterKey} hover>
                        <TableCell>
                          <Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>
                            {line.meterKey}
                          </Box>
                        </TableCell>
                        <TableCell>
                          {line.description}
                          {/* A coverage caveat travels beside the price, not inside the
                              provenance string, so a priced line still visibly says the
                              signal behind it is incomplete. */}
                          {line.coverageNote && (
                            <Typography variant="caption" color="warning.main" sx={{ display: 'block', fontWeight: 700 }}>
                              {line.coverageNote}
                            </Typography>
                          )}
                          {line.sourceNote && (
                            <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                              {line.sourceNote}
                            </Typography>
                          )}
                        </TableCell>
                        <TableCell align="right">{fmtNumber(line.meteredQuantity)}</TableCell>
                        <TableCell align="right">{fmtNumber(line.includedQuantity)}</TableCell>
                        <TableCell align="right" sx={{ fontWeight: 700 }}>
                          {fmtNumber(line.billableQuantity)}
                        </TableCell>
                        <TableCell align="right">{fmtMoney(line.unitPrice, reviewQuery.data.currency)}</TableCell>
                        <TableCell align="right" sx={{ fontWeight: 700 }}>
                          {fmtMoney(line.amount, reviewQuery.data.currency)}
                        </TableCell>
                      </TableRow>
                    ))}
                    <TableRow>
                      <TableCell colSpan={6} align="right" sx={{ fontWeight: 800 }}>
                        Total
                      </TableCell>
                      <TableCell align="right" sx={{ fontWeight: 800 }}>
                        {fmtMoney(reviewQuery.data.totalAmount, reviewQuery.data.currency)}
                      </TableCell>
                    </TableRow>
                  </TableBody>
                </Table>
              </Box>
            </Stack>
          )}
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setReviewId(null)} color="inherit">
            Close
          </Button>
          {reviewQuery.data && reviewQuery.data.status.toLowerCase() !== 'final' && (
            <RoleGate allowed={permissions.canAdministerBilling} requirement={REQUIRED_ROLE_COPY.billing}>
              {(disabled) => (
                <Button
                  variant="contained"
                  color="success"
                  startIcon={<FinalizeIcon />}
                  disabled={disabled}
                  onClick={() => setFinalizeId(reviewQuery.data.id)}
                  sx={{ fontWeight: 700 }}
                >
                  Finalize
                </Button>
              )}
            </RoleGate>
          )}
        </DialogActions>
      </Dialog>

      {/* Finalize confirmation. Not a reason dialog: the server takes no reason here, and
          asking for one the API discards would be theatre. */}
      <Dialog open={Boolean(finalizeId)} onClose={() => setFinalizeId(null)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Finalize this statement</DialogTitle>
        <DialogContent dividers>
          <Alert severity="warning" sx={{ borderRadius: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>This cannot be undone</AlertTitle>
            A Final statement is immutable. It can never be recomputed or edited, and it pins its rate card
            forever — if the numbers are wrong, the correction has to be a separate adjustment.
          </Alert>
          {finalizeTarget && (
            <Stack spacing={1} sx={{ mt: 2 }}>
              <Row label="Period">{finalizeTarget.period}</Row>
              <Row label="Total">{fmtMoney(finalizeTarget.totalAmount, finalizeTarget.currency)}</Row>
              <Row label="Computed">{fmtDateTime(finalizeTarget.computedAtUtc)}</Row>
              <Row label="Calculated by">{finalizeTarget.computedBy}</Row>
            </Stack>
          )}
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setFinalizeId(null)} color="inherit">
            Cancel
          </Button>
          <Button
            variant="contained"
            color="success"
            disabled={finalizeMutation.isPending}
            onClick={() => finalizeId && finalizeMutation.mutate(finalizeId)}
            sx={{ fontWeight: 700 }}
          >
            {finalizeMutation.isPending ? 'Finalizing…' : 'Finalize permanently'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Commercial terms editor */}
      <Dialog open={termsOpen} onClose={() => setTermsOpen(false)} maxWidth="sm" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Commercial terms — {tenant.name}</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2.5} sx={{ mt: 0.5 }}>
            <FormControl fullWidth>
              <FormLabel sx={{ fontWeight: 700, mb: 1 }} htmlFor="commercial-billing-mode">
                Billing mode
              </FormLabel>
              <TextField
                id="commercial-billing-mode"
                select
                value={terms.billingMode}
                onChange={(event) => setTerms({ ...terms, billingMode: event.target.value as BillingMode })}
                error={Boolean(termsAttempted && termsErrors.billingMode)}
                helperText={(termsAttempted && termsErrors.billingMode) || ' '}
              >
                {BILLING_MODES.map((mode) => (
                  <MenuItem key={mode} value={mode}>
                    {mode}
                  </MenuItem>
                ))}
              </TextField>
            </FormControl>

            {terms.billingMode !== 'Billable' && (
              <TextField
                fullWidth
                required
                multiline
                minRows={2}
                label={`Why is this tenant ${terms.billingMode} and not billable?`}
                value={terms.billingModeReason}
                onChange={(event) => setTerms({ ...terms, billingModeReason: event.target.value })}
                error={Boolean(termsAttempted && termsErrors.billingModeReason)}
                helperText={
                  (termsAttempted && termsErrors.billingModeReason) ||
                  'Recorded against the tenant and audited. Name the agreement, the sponsor or the ticket.'
                }
              />
            )}

            {terms.billingMode === 'Trial' && (
              <TextField
                fullWidth
                required
                type="date"
                label="Trial ends on"
                value={terms.trialEndsOn}
                onChange={(event) => setTerms({ ...terms, trialEndsOn: event.target.value })}
                error={Boolean(termsAttempted && termsErrors.trialEndsOn)}
                helperText={(termsAttempted && termsErrors.trialEndsOn) || 'Must be in the future.'}
                slotProps={{ inputLabel: { shrink: true } }}
              />
            )}

            <TextField
              fullWidth
              type="date"
              label="Billing starts on"
              value={terms.billingStartsOn}
              onChange={(event) => setTerms({ ...terms, billingStartsOn: event.target.value })}
              error={Boolean(termsAttempted && termsErrors.billingStartsOn)}
              helperText={(termsAttempted && termsErrors.billingStartsOn) || 'The first day of the first billable period.'}
              slotProps={{ inputLabel: { shrink: true } }}
            />

            <Divider />
            <Alert severity="info" sx={{ borderRadius: 2 }}>
              Changing what a customer is charged is written to the platform audit trail with the before and
              after. Plan assignment is a separate action — this form deliberately does not set it.
            </Alert>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setTermsOpen(false)} color="inherit">
            Cancel
          </Button>
          <Button
            variant="contained"
            onClick={() => {
              setTermsAttempted(true);
              if (termsValid) termsMutation.mutate();
            }}
            disabled={termsMutation.isPending || (termsAttempted && !termsValid)}
            sx={{ fontWeight: 700 }}
          >
            {termsMutation.isPending ? 'Saving…' : 'Save terms'}
          </Button>
        </DialogActions>
      </Dialog>

      <ReasonDialog
        open={planOpen}
        title="Change plan"
        confirmLabel="Assign plan"
        minReasonLength={1}
        description={
          <>
            The plan sets {tenant.name}'s quotas, scheduling weight and base subscription price. A Billable
            tenant without one produces statements with no subscription line at all.
          </>
        }
        extra={
          <TextField
            select
            fullWidth
            required
            label="Plan"
            value={planId}
            onChange={(event) => setPlanId(event.target.value)}
            helperText="Only active plans can be assigned; the server refuses the rest."
          >
            {(plansQuery.data ?? [])
              .filter((plan) => plan.isActive || plan.id === profile.planId)
              .map((plan) => (
                <MenuItem key={plan.id} value={plan.id}>
                  {plan.name} ({plan.code}){plan.isActive ? '' : ' — inactive'}
                </MenuItem>
              ))}
          </TextField>
        }
        extraProblem={
          planId === '' ? 'Select a plan.' : planId === (profile.planId ?? '') ? 'That is already the plan.' : null
        }
        busy={planMutation.isPending}
        onClose={() => setPlanOpen(false)}
        onConfirm={(reason) => planMutation.mutate(reason)}
      />

      <ReasonDialog
        open={pinOpen}
        title="Pinned rate card"
        confirmLabel={pinCardId ? 'Pin this card' : 'Clear the pin'}
        confirmColor={pinCardId ? 'primary' : 'warning'}
        description={
          pinCardId ? (
            <>Pins the price list {tenant.name}'s statements are computed against.</>
          ) : (
            <>
              Clearing the pin returns {tenant.name} to whichever card happens to be active for the period, which
              is how a negotiated customer gets silently repriced. The reason is mandatory in this direction.
            </>
          )
        }
        extra={
          <TextField
            select
            fullWidth
            label="Rate card"
            value={pinCardId}
            onChange={(event) => setPinCardId(event.target.value)}
            helperText="Blank clears the pin."
          >
            <MenuItem value="">No pinned card</MenuItem>
            {(rateCardsQuery.data ?? []).map((card) => (
              <MenuItem key={card.id} value={card.id}>
                {card.code} · {card.currency}
                {card.isActive ? '' : ' (inactive)'}
              </MenuItem>
            ))}
          </TextField>
        }
        busy={pinMutation.isPending}
        onClose={() => setPinOpen(false)}
        onConfirm={(reason) => pinMutation.mutate(reason)}
      />
    </Stack>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" spacing={1}>
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
      <Box sx={{ fontWeight: 700, fontSize: 14, textAlign: { sm: 'right' }, overflowWrap: 'anywhere' }}>
        {children}
      </Box>
    </Stack>
  );
}
