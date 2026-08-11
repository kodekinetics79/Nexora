import { useMemo, useState, type ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import Stack from '../components/Flex';
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
  DialogContentText,
  DialogTitle,
  Divider,
  FormControlLabel,
  Grid,
  IconButton,
  MenuItem,
  Paper,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  Add as AddIcon,
  Calculate as ComputeIcon,
  DeleteOutlined as RemoveLineIcon,
  EditOutlined as EditIcon,
  GppGoodOutlined as FinalizeIcon,
  MonetizationOnOutlined as RevenueIcon,
  ReportProblemOutlined as RiskIcon,
  RuleOutlined as ConfigIcon,
  TimerOffOutlined as ExpiredTrialIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import type {
  BillingStatement,
  RateCard,
  RateCardLineInput,
  TenantRevenueRisk,
} from '../types';
import PageHeader from '../components/PageHeader';
import PageSection from '../components/PageSection';
import StatTile from '../components/StatTile';
import { Dash, SoftChip } from '../components/StatusChip';
import { EmptyState, ErrorState, LoadingState, TilesSkeleton } from '../components/States';
import { leakReasonLabel } from '../components/commercialTermsValidation';
import { fmtDate, fmtDateTime, fmtNumber } from '../components/format';

const currentPeriod = () => new Date().toISOString().slice(0, 7);

const fmtMoney = (amount: number, currency: string) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency, maximumFractionDigits: 2 }).format(amount);

const fmtUsd = (amount: number) => fmtMoney(amount, 'USD');

type LineForm = RateCardLineInput;

interface RateCardForm {
  code: string;
  currency: string;
  effectiveFromUtc: string; // yyyy-mm-dd
  effectiveToUtc: string; // yyyy-mm-dd or ''
  isActive: boolean;
  lines: LineForm[];
}

const emptyLine: LineForm = { meterKey: '', includedQuantity: 0, unitPrice: 0, unit: '', tierNote: null };

const emptyRateCard: RateCardForm = {
  code: '',
  currency: 'USD',
  effectiveFromUtc: new Date().toISOString().slice(0, 10),
  effectiveToUtc: '',
  isActive: true,
  lines: [{ ...emptyLine }],
};

const formFromCard = (card: RateCard): RateCardForm => ({
  code: card.code,
  currency: card.currency,
  effectiveFromUtc: card.effectiveFromUtc.slice(0, 10),
  effectiveToUtc: card.effectiveToUtc ? card.effectiveToUtc.slice(0, 10) : '',
  isActive: card.isActive,
  lines: card.lines.map((line) => ({
    meterKey: line.meterKey,
    includedQuantity: line.includedQuantity,
    unitPrice: line.unitPrice,
    unit: line.unit,
    tierNote: line.tierNote,
  })),
});

/** Which slice of the revenue board is on screen. Kept as one control because the counts
 *  describe the whole fleet and the filter describes what is listed — mixing the two is
 *  how a badge starts changing when you apply a filter. */
type RiskFilter = 'all' | 'at-risk' | 'configuration-required';

export default function BillingPage() {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const navigate = useNavigate();
  const permissions = usePlatformPermissions();

  const [riskFilter, setRiskFilter] = useState<RiskFilter>('at-risk');
  const [includeArchived, setIncludeArchived] = useState(false);
  const [tenantId, setTenantId] = useState('');
  const [period, setPeriod] = useState(currentPeriod());
  const [selectedStatementId, setSelectedStatementId] = useState<string | null>(null);
  const [finalizeTarget, setFinalizeTarget] = useState<BillingStatement | null>(null);
  const [cardDialog, setCardDialog] = useState<{ mode: 'create' } | { mode: 'edit'; card: RateCard } | null>(null);
  const [cardForm, setCardForm] = useState<RateCardForm>(emptyRateCard);

  const periodValid = /^\d{4}-(0[1-9]|1[0-2])$/.test(period);

  const { data: tenants } = useQuery({
    queryKey: platformKeys.tenants(),
    queryFn: () => platformApi.listTenants(),
  });

  const riskQuery = useQuery({
    queryKey: platformKeys.revenueRisk({ riskFilter, includeArchived }),
    queryFn: () =>
      platformApi.getRevenueRisk({
        onlyAtRisk: riskFilter === 'at-risk',
        onlyCommercialConfigurationRequired: riskFilter === 'configuration-required',
        includeArchived,
      }),
    enabled: permissions.canAdministerBilling,
  });

  const usageQuery = useQuery({
    queryKey: platformKeys.billingUsage(tenantId, period),
    queryFn: () => platformApi.getBillingUsage(tenantId, period),
    enabled: !!tenantId && periodValid,
  });

  const costQuery = useQuery({
    queryKey: platformKeys.billingCost(tenantId, period),
    queryFn: () => platformApi.getBillingCost(tenantId, period),
    enabled: !!tenantId && periodValid,
  });

  const statementsQuery = useQuery({
    queryKey: platformKeys.statements({ tenantId }),
    queryFn: () => platformApi.listStatements(tenantId ? { tenantId } : undefined),
    enabled: !!tenantId,
  });

  // Gated like every other read on this page. It was the one that was not, so a SupportAdmin
  // who opened /platform/billing was soft-landed on the "Billing is a separate duty" notice
  // while the console fired a request that could only ever 403 — a red line in the network tab
  // and an audited refusal, for a screen that had already decided not to show them anything.
  const rateCardsQuery = useQuery({
    queryKey: platformKeys.rateCards(),
    queryFn: () => platformApi.listRateCards(),
    enabled: permissions.canAdministerBilling,
  });

  const invalidateBilling = () => {
    queryClient.invalidateQueries({ queryKey: [...platformKeys.all, 'billing'] });
  };

  const computeMutation = useMutation({
    mutationFn: () => platformApi.computeStatement(tenantId, period),
    onSuccess: (statement) => {
      enqueueSnackbar(`Statement computed — total ${fmtMoney(statement.totalAmount, statement.currency)}`, { variant: 'success' });
      setSelectedStatementId(statement.id);
      invalidateBilling();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Statement compute failed'), { variant: 'error' }),
  });

  const finalizeMutation = useMutation({
    mutationFn: (statement: BillingStatement) => platformApi.finalizeStatement(statement.id),
    onSuccess: (statement) => {
      enqueueSnackbar(`Statement ${statement.id} finalized`, { variant: 'success' });
      setFinalizeTarget(null);
      invalidateBilling();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Finalize failed'), { variant: 'error' }),
  });

  const cardMutation = useMutation({
    mutationFn: async () => {
      const lines = cardForm.lines.map((line) => ({
        meterKey: line.meterKey.trim(),
        includedQuantity: Number(line.includedQuantity),
        unitPrice: Number(line.unitPrice),
        unit: line.unit.trim(),
        tierNote: line.tierNote?.trim() || null,
      }));
      const shared = {
        currency: cardForm.currency.trim().toUpperCase(),
        effectiveFromUtc: cardForm.effectiveFromUtc,
        effectiveToUtc: cardForm.effectiveToUtc || null,
        isActive: cardForm.isActive,
        lines,
      };
      return cardDialog?.mode === 'edit'
        ? platformApi.updateRateCard(cardDialog.card.id, shared)
        : platformApi.createRateCard({ code: cardForm.code.trim(), ...shared });
    },
    onSuccess: (card) => {
      enqueueSnackbar(`Rate card ${card.code} saved`, { variant: 'success' });
      setCardDialog(null);
      invalidateBilling();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Failed to save the rate card'), { variant: 'error' }),
  });

  const statements = statementsQuery.data ?? [];
  const selectedStatement = useMemo(
    () => statements.find((s) => s.id === selectedStatementId) ?? null,
    [statements, selectedStatementId],
  );

  const cost = costQuery.data;
  const cardLinesValid =
    cardForm.lines.length > 0 &&
    cardForm.lines.every((line) =>
      line.meterKey.trim() && line.unit.trim() &&
      Number(line.includedQuantity) >= 0 && Number(line.unitPrice) >= 0) &&
    new Set(cardForm.lines.map((line) => line.meterKey.trim())).size === cardForm.lines.length;
  const cardValid =
    (cardDialog?.mode === 'edit' || cardForm.code.trim().length > 0) &&
    cardForm.currency.trim().length === 3 &&
    !!cardForm.effectiveFromUtc &&
    cardLinesValid;

  const setLine = (index: number, patch: Partial<LineForm>) =>
    setCardForm((form) => ({
      ...form,
      lines: form.lines.map((line, i) => (i === index ? { ...line, ...patch } : line)),
    }));

  if (!permissions.canAdministerBilling) {
    // Every endpoint on this page carries the Billing policy. Rendering the page and
    // letting six queries 403 in the network tab would tell the operator nothing.
    return (
      <Box>
        <PageHeader title="Billing" subtitle="Revenue posture, statements and rate cards." />
        <Alert severity="info" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Billing is a separate duty</AlertTitle>
          {REQUIRED_ROLE_COPY.billing} A support administrator can suspend, resume and archive a tenant — those
          are operational acts — and must never be able to move a customer onto a cheaper rate card, extend a
          trial, or convert one to an unbilled mode.
        </Alert>
      </Box>
    );
  }

  const risk = riskQuery.data;

  return (
    <Box>
      <PageHeader
        title="Billing"
        subtitle="Who is paying, who is not, and what has actually been charged."
      />

      {/* The revenue board leads the page: a list you have to scan for red is not read,
          and "which of our customers is getting this for free?" is the question this
          page exists to answer without opening forty tenants in turn. */}
      {riskQuery.isLoading ? (
        <TilesSkeleton count={4} />
      ) : riskQuery.isError || !risk ? (
        <ErrorState
          message={platformErrorMessage(riskQuery.error, 'The revenue readout could not be loaded.')}
          onRetry={() => riskQuery.refetch()}
          minHeight={200}
        />
      ) : (
        <>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr', lg: 'repeat(4, 1fr)' }, gap: 2.5, mb: 2.5 }}>
            <StatTile
              label="Tenants"
              value={fmtNumber(risk.tenantCount)}
              icon={<RevenueIcon />}
              color="#6366f1"
              caption={`as at ${fmtDateTime(risk.generatedAtUtc)}`}
            />
            <StatTile
              label="At risk"
              value={fmtNumber(risk.atRiskCount)}
              icon={<RiskIcon />}
              color={risk.atRiskCount > 0 ? '#f59e0b' : '#10b981'}
              caption="leaking revenue for at least one reason"
            />
            <StatTile
              label="Expired trials"
              value={fmtNumber(risk.expiredTrialCount)}
              icon={<ExpiredTrialIcon />}
              color={risk.expiredTrialCount > 0 ? '#ef4444' : '#10b981'}
              caption="past their end date, still served"
            />
            <StatTile
              label="Configuration required"
              value={fmtNumber(risk.commercialConfigurationRequiredCount)}
              icon={<ConfigIcon />}
              color={risk.commercialConfigurationRequiredCount > 0 ? '#ef4444' : '#10b981'}
              caption="running on terms nobody set"
            />
          </Box>

          {risk.billableTenantsChargedNothingCount > 0 && (
            <Alert severity="warning" sx={{ mb: 2.5, borderRadius: 2 }}>
              <AlertTitle sx={{ fontWeight: 800 }}>
                {risk.billableTenantsChargedNothingCount} billable{' '}
                {risk.billableTenantsChargedNothingCount === 1 ? 'tenant has' : 'tenants have'} been charged nothing
              </AlertTitle>
              Either no statement has ever been computed for them, or the most recent one totalled zero.
            </Alert>
          )}

          <PageSection
            title="Revenue risk"
            subtitle="One row per tenant. The counts above are always over the whole fleet, never over this list."
            actions={
              <Stack direction="row" spacing={1} alignItems="center">
                <TextField
                  size="small"
                  select
                  label="Show"
                  value={riskFilter}
                  onChange={(event) => setRiskFilter(event.target.value as RiskFilter)}
                  sx={{ minWidth: 230 }}
                >
                  <MenuItem value="all">Every tenant</MenuItem>
                  <MenuItem value="at-risk">At risk only</MenuItem>
                  <MenuItem value="configuration-required">Commercial configuration required</MenuItem>
                </TextField>
                <FormControlLabel
                  control={
                    <Switch checked={includeArchived} onChange={(event) => setIncludeArchived(event.target.checked)} />
                  }
                  label="Include archived"
                />
              </Stack>
            }
            sx={{ mb: 2.5 }}
          >
            {risk.tenants.length === 0 ? (
              <EmptyState
                title="Nothing flagged"
                message="Every tenant in this view is either priced or exempt on the record."
                minHeight={160}
              />
            ) : (
              <Box sx={{ overflowX: 'auto' }}>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell sx={{ fontWeight: 700 }}>Tenant</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Mode</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Plan</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Rate card</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Last statement</TableCell>
                      <TableCell sx={{ fontWeight: 700 }}>Why it is at risk</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {risk.tenants.map((row: TenantRevenueRisk) => (
                      <TableRow
                        key={row.tenantId}
                        hover
                        sx={{ cursor: 'pointer' }}
                        onClick={() => navigate(`/platform/tenants/${row.tenantId}?tab=commercial`)}
                      >
                        <TableCell>
                          <Typography variant="body2" sx={{ fontWeight: 700 }}>
                            {row.tenantName}
                          </Typography>
                          <Typography variant="caption" color="text.secondary">
                            {row.tenantSlug} · {row.tenantStatus}
                          </Typography>
                        </TableCell>
                        <TableCell>
                          <Stack direction="row" spacing={0.5} alignItems="center">
                            <SoftChip
                              label={row.billingMode}
                              tone={row.billingMode === 'Billable' ? 'success' : row.trialExpired ? 'error' : 'warning'}
                              dot={false}
                            />
                            {row.commercialConfigurationRequired && (
                              <SoftChip label="config required" tone="error" dot={false} />
                            )}
                          </Stack>
                        </TableCell>
                        <TableCell>{row.planCode ?? <Dash />}</TableCell>
                        <TableCell>
                          {row.pinnedRateCardCode ? (
                            <Box component="code" sx={{ fontSize: 12.5 }}>
                              {row.pinnedRateCardCode}
                            </Box>
                          ) : (
                            <Typography variant="caption" color="warning.main" sx={{ fontWeight: 700 }}>
                              unpinned
                            </Typography>
                          )}
                        </TableCell>
                        <TableCell>
                          {row.lastStatementPeriod ? (
                            <Box>
                              <Typography variant="body2" sx={{ fontWeight: 700 }}>
                                {row.lastStatementTotal == null
                                  ? '—'
                                  : fmtMoney(row.lastStatementTotal, 'USD')}
                              </Typography>
                              <Typography variant="caption" color="text.secondary">
                                {row.lastStatementPeriod} · {row.lastStatementStatus ?? '—'}
                              </Typography>
                            </Box>
                          ) : (
                            <Typography variant="caption" color="error.main" sx={{ fontWeight: 700 }}>
                              never billed
                            </Typography>
                          )}
                        </TableCell>
                        <TableCell sx={{ maxWidth: 340 }}>
                          {row.leakReasons.length === 0 ? (
                            <Dash />
                          ) : (
                            <Stack spacing={0.25}>
                              {row.leakReasons.map((code) => (
                                <Typography key={code} variant="caption" color="text.secondary">
                                  {leakReasonLabel(code)}
                                </Typography>
                              ))}
                            </Stack>
                          )}
                        </TableCell>
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </Box>
            )}
          </PageSection>
        </>
      )}

      {/* Tenant + period selector */}
      <Paper sx={{ p: 1.5, mb: 2.5, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} alignItems={{ md: 'center' }}>
          <TextField
            size="small"
            select
            label="Tenant"
            value={tenantId}
            onChange={(e) => { setTenantId(e.target.value); setSelectedStatementId(null); }}
            sx={{ minWidth: 260 }}
          >
            <MenuItem value="">Select a tenant…</MenuItem>
            {(tenants ?? []).map((tenant) => (
              <MenuItem key={tenant.id} value={tenant.id}>
                {tenant.name} ({tenant.slug})
              </MenuItem>
            ))}
          </TextField>
          <TextField
            size="small"
            type="month"
            label="Period"
            value={period}
            onChange={(e) => setPeriod(e.target.value)}
            error={!periodValid}
            helperText={periodValid ? undefined : 'YYYY-MM'}
            sx={{ minWidth: 170 }}
            slotProps={{ inputLabel: { shrink: true } }}
          />
          <Box sx={{ flex: 1 }} />
          <Tooltip title={tenantId ? 'Compute (or recompute) the Draft statement for this tenant-period' : 'Select a tenant first'}>
            <span>
              <Button
                variant="contained"
                startIcon={<ComputeIcon />}
                disabled={!tenantId || !periodValid || computeMutation.isPending}
                onClick={() => computeMutation.mutate()}
                sx={{ fontWeight: 700 }}
              >
                {computeMutation.isPending ? 'Computing…' : 'Compute statement'}
              </Button>
            </span>
          </Tooltip>
        </Stack>
      </Paper>

      {!tenantId ? (
        <Paper sx={{ borderRadius: 3, mb: 2.5 }}>
          <EmptyState
            title="Select a tenant"
            message="Usage meters, statements, and cost reporting are all scoped to one tenant-period."
          />
        </Paper>
      ) : (
        <Grid container spacing={2.5} sx={{ mb: 2.5 }}>
          {/* Usage meters */}
          <Grid size={{ xs: 12, lg: 7 }}>
            <Paper sx={{ p: 3, borderRadius: 3, height: '100%' }}>
              <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>Usage Meters</Typography>
              <Typography variant="caption" color="text.secondary">
                Live readings from the source ledgers for {period}.
              </Typography>
              {usageQuery.isLoading ? (
                <LoadingState label="Reading meters…" minHeight={180} />
              ) : usageQuery.isError ? (
                <ErrorState
                  minHeight={180}
                  message={platformErrorMessage(usageQuery.error, 'Usage could not be read for this tenant-period.')}
                  onRetry={() => usageQuery.refetch()}
                />
              ) : (usageQuery.data?.meters.length ?? 0) === 0 ? (
                <EmptyState title="No metered usage" message="Nothing was metered for this tenant in the selected period." minHeight={180} />
              ) : (
                <Box sx={{ overflowX: 'auto', mt: 1.5 }}>
                  <Table size="small">
                    <TableHead>
                      <TableRow>
                        <TableCell sx={{ fontWeight: 700 }}>Meter</TableCell>
                        <TableCell sx={{ fontWeight: 700 }} align="right">Quantity</TableCell>
                        <TableCell sx={{ fontWeight: 700 }}>Unit</TableCell>
                        <TableCell sx={{ fontWeight: 700 }}>Source</TableCell>
                      </TableRow>
                    </TableHead>
                    <TableBody>
                      {usageQuery.data?.meters.map((meter) => (
                        <TableRow key={meter.meterKey} hover>
                          <TableCell><Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>{meter.meterKey}</Box></TableCell>
                          <TableCell align="right" sx={{ fontWeight: 700 }}>{fmtNumber(meter.quantity)}</TableCell>
                          <TableCell>{meter.unit}</TableCell>
                          <TableCell>
                            <Typography variant="caption" color="text.secondary">{meter.sourceNote}</Typography>
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </Box>
              )}
            </Paper>
          </Grid>

          {/* Cost / margin */}
          <Grid size={{ xs: 12, lg: 5 }}>
            <Paper sx={{ p: 3, borderRadius: 3, height: '100%' }}>
              <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>Cost & Margin</Typography>
              <Typography variant="caption" color="text.secondary">
                AI cost vs statement revenue for {period}.
              </Typography>
              {costQuery.isLoading ? (
                <LoadingState label="Computing cost…" minHeight={180} />
              ) : costQuery.isError ? (
                <ErrorState
                  minHeight={180}
                  message={platformErrorMessage(costQuery.error, 'Cost could not be read for this tenant-period.')}
                  onRetry={() => costQuery.refetch()}
                />
              ) : cost ? (
                <Stack spacing={1.5} sx={{ mt: 2 }}>
                  <CostRow
                    label="Statement total"
                    value={cost.statementTotal == null
                      ? '— no statement'
                      : fmtMoney(cost.statementTotal, cost.statementCurrency ?? 'USD')}
                    suffix={cost.statementStatus ? <Chip size="small" label={cost.statementStatus} /> : null}
                  />
                  <CostRow label="Settled AI requests" value={fmtNumber(cost.settledAiRequestCount)} />
                  <CostRow label="Priced AI cost subtotal" value={fmtUsd(cost.pricedAiCostSubtotal)} />
                  <Divider />
                  {cost.aiCostTotal == null ? (
                    <Box sx={{ p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>
                        AI cost not fully priced
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        {fmtNumber(cost.unpricedAiRequestCount)} AI request{cost.unpricedAiRequestCount === 1 ? '' : 's'} in this period
                        {' '}could not be priced, so total cost and gross margin are not reported rather than fabricated.
                      </Typography>
                    </Box>
                  ) : (
                    <>
                      <CostRow label="AI cost total" value={fmtUsd(cost.aiCostTotal)} />
                      <CostRow
                        label="Gross margin"
                        value={cost.grossMargin == null ? '— not computable' : fmtUsd(cost.grossMargin)}
                      />
                    </>
                  )}
                  <Typography variant="caption" color="text.secondary">{cost.note}</Typography>
                </Stack>
              ) : null}
            </Paper>
          </Grid>

          {/* Statements */}
          <Grid size={{ xs: 12 }}>
            <Paper sx={{ p: 3, borderRadius: 3 }}>
              <Typography variant="h6" sx={{ fontWeight: 800, mb: 0.5 }}>Statements</Typography>
              <Typography variant="caption" color="text.secondary">
                Draft statements can be recomputed at any time; Final statements are immutable.
              </Typography>
              {statementsQuery.isLoading ? (
                <LoadingState label="Loading statements…" minHeight={160} />
              ) : statementsQuery.isError ? (
                <ErrorState minHeight={160} message="Statements could not be loaded." onRetry={() => statementsQuery.refetch()} />
              ) : statements.length === 0 ? (
                <EmptyState
                  title="No statements yet"
                  message="Compute the first statement for this tenant-period with the button above."
                  minHeight={160}
                />
              ) : (
                <Box sx={{ overflowX: 'auto', mt: 1.5 }}>
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
                      {statements.map((statement) => (
                        <TableRow
                          key={statement.id}
                          hover
                          selected={statement.id === selectedStatementId}
                          onClick={() => setSelectedStatementId(statement.id)}
                          sx={{ cursor: 'pointer' }}
                        >
                          <TableCell sx={{ fontWeight: 600 }}>
                            {statement.periodStartUtc.slice(0, 7)}
                          </TableCell>
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
                            <Typography variant="caption" color="text.secondary">{fmtDateTime(statement.computedAtUtc)}</Typography>
                          </TableCell>
                          <TableCell>
                            <Typography variant="caption" color="text.secondary">
                              {statement.finalizedAtUtc ? `${fmtDateTime(statement.finalizedAtUtc)} · ${statement.finalizedBy ?? ''}` : '—'}
                            </Typography>
                          </TableCell>
                          <TableCell align="right" onClick={(e) => e.stopPropagation()}>
                            {statement.status.toLowerCase() !== 'final' && (
                              <Button
                                size="small"
                                color="success"
                                startIcon={<FinalizeIcon fontSize="small" />}
                                onClick={() => setFinalizeTarget(statement)}
                                sx={{ fontWeight: 700 }}
                              >
                                Finalize
                              </Button>
                            )}
                          </TableCell>
                        </TableRow>
                      ))}
                    </TableBody>
                  </Table>
                </Box>
              )}

              {/* Statement line preview */}
              {selectedStatement && (
                <Box sx={{ mt: 3 }}>
                  <Divider sx={{ mb: 2 }} />
                  <Stack direction="row" alignItems="center" spacing={1} sx={{ mb: 1 }}>
                    <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>
                      Statement {selectedStatement.id} · {selectedStatement.periodStartUtc.slice(0, 7)}
                    </Typography>
                    <SoftChip
                      label={selectedStatement.status}
                      tone={selectedStatement.status.toLowerCase() === 'final' ? 'success' : 'info'}
                      dot={false}
                    />
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
                        {selectedStatement.lines.map((line) => (
                          <TableRow key={line.meterKey} hover>
                            <TableCell><Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>{line.meterKey}</Box></TableCell>
                            <TableCell>{line.description}</TableCell>
                            <TableCell align="right">{fmtNumber(line.meteredQuantity)}</TableCell>
                            <TableCell align="right">{fmtNumber(line.includedQuantity)}</TableCell>
                            <TableCell align="right" sx={{ fontWeight: 700 }}>{fmtNumber(line.billableQuantity)}</TableCell>
                            <TableCell align="right">{fmtMoney(line.unitPrice, selectedStatement.currency)}</TableCell>
                            <TableCell align="right" sx={{ fontWeight: 700 }}>{fmtMoney(line.amount, selectedStatement.currency)}</TableCell>
                          </TableRow>
                        ))}
                        <TableRow>
                          <TableCell colSpan={6} align="right" sx={{ fontWeight: 800 }}>Total</TableCell>
                          <TableCell align="right" sx={{ fontWeight: 800 }}>
                            {fmtMoney(selectedStatement.totalAmount, selectedStatement.currency)}
                          </TableCell>
                        </TableRow>
                      </TableBody>
                    </Table>
                  </Box>
                </Box>
              )}
            </Paper>
          </Grid>
        </Grid>
      )}

      {/* Rate cards */}
      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Stack direction="row" alignItems="center" sx={{ mb: 0.5 }}>
          <Typography variant="h6" sx={{ fontWeight: 800, flex: 1 }}>Rate Cards</Typography>
          <Button
            variant="outlined"
            startIcon={<AddIcon />}
            onClick={() => { setCardForm({ ...emptyRateCard, lines: [{ ...emptyLine }] }); setCardDialog({ mode: 'create' }); }}
            sx={{ fontWeight: 700 }}
          >
            New rate card
          </Button>
        </Stack>
        <Typography variant="caption" color="text.secondary">
          A rate card referenced by a Final statement is immutable — create a new card instead.
        </Typography>
        {rateCardsQuery.isLoading ? (
          <LoadingState label="Loading rate cards…" minHeight={160} />
        ) : rateCardsQuery.isError ? (
          <ErrorState
            minHeight={160}
            message={platformErrorMessage(rateCardsQuery.error, 'Rate cards could not be loaded')}
            onRetry={() => rateCardsQuery.refetch()}
          />
        ) : (rateCardsQuery.data?.length ?? 0) === 0 ? (
          <EmptyState title="No rate cards" message="Create a rate card before computing statements." minHeight={160} />
        ) : (
          <Box sx={{ overflowX: 'auto', mt: 1.5 }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 700 }}>Code</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Currency</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Effective</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Status</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Lines</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Version</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Actions</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {rateCardsQuery.data?.map((card) => (
                  <TableRow key={card.id} hover>
                    <TableCell><Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>{card.code}</Box></TableCell>
                    <TableCell>{card.currency}</TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary">
                        {fmtDate(card.effectiveFromUtc)} → {card.effectiveToUtc ? fmtDate(card.effectiveToUtc) : 'open'}
                      </Typography>
                    </TableCell>
                    <TableCell>
                      <SoftChip label={card.isActive ? 'active' : 'inactive'} tone={card.isActive ? 'success' : 'neutral'} />
                    </TableCell>
                    <TableCell align="right">{card.lines.length}</TableCell>
                    <TableCell align="right">{card.version}</TableCell>
                    <TableCell align="right">
                      <Tooltip title="Edit rate card">
                        <IconButton size="small" onClick={() => { setCardForm(formFromCard(card)); setCardDialog({ mode: 'edit', card }); }}>
                          <EditIcon fontSize="small" />
                        </IconButton>
                      </Tooltip>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        )}
      </Paper>

      {/* Finalize confirm */}
      <Dialog open={!!finalizeTarget} onClose={() => setFinalizeTarget(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Finalize statement</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Finalize this {finalizeTarget ? fmtMoney(finalizeTarget.totalAmount, finalizeTarget.currency) : ''} statement
            for period {finalizeTarget?.periodStartUtc.slice(0, 7)}? <strong>A Final statement is immutable</strong> — it can
            never be recomputed or edited, and it pins its rate card forever.
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setFinalizeTarget(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            color="success"
            onClick={() => finalizeTarget && finalizeMutation.mutate(finalizeTarget)}
            disabled={finalizeMutation.isPending}
            sx={{ fontWeight: 700 }}
          >
            {finalizeMutation.isPending ? 'Finalizing…' : 'Finalize'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Rate card create / edit dialog */}
      <Dialog open={!!cardDialog} onClose={() => setCardDialog(null)} fullWidth maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800 }}>
          {cardDialog?.mode === 'edit' ? `Edit rate card — ${cardDialog.card.code}` : 'Create rate card'}
        </DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2.5} sx={{ mt: 0.5 }}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              {cardDialog?.mode !== 'edit' && (
                <TextField
                  label="Code"
                  value={cardForm.code}
                  onChange={(e) => setCardForm({ ...cardForm, code: e.target.value })}
                  fullWidth
                  required
                />
              )}
              <TextField
                label="Currency"
                value={cardForm.currency}
                onChange={(e) => setCardForm({ ...cardForm, currency: e.target.value })}
                helperText="3-letter ISO code"
                fullWidth
                required
              />
              <TextField
                label="Effective from"
                type="date"
                value={cardForm.effectiveFromUtc}
                onChange={(e) => setCardForm({ ...cardForm, effectiveFromUtc: e.target.value })}
                fullWidth
                required
                slotProps={{ inputLabel: { shrink: true } }}
              />
              <TextField
                label="Effective to"
                type="date"
                value={cardForm.effectiveToUtc}
                onChange={(e) => setCardForm({ ...cardForm, effectiveToUtc: e.target.value })}
                helperText="Blank = open-ended"
                fullWidth
                slotProps={{ inputLabel: { shrink: true } }}
              />
            </Stack>
            <FormControlLabel
              control={<Switch checked={cardForm.isActive} onChange={(e) => setCardForm({ ...cardForm, isActive: e.target.checked })} />}
              label="Active"
            />
            <Divider />
            <Stack direction="row" alignItems="center">
              <Typography variant="subtitle2" sx={{ fontWeight: 800, flex: 1 }}>Lines (one per meter key)</Typography>
              <Button
                size="small"
                startIcon={<AddIcon fontSize="small" />}
                onClick={() => setCardForm({ ...cardForm, lines: [...cardForm.lines, { ...emptyLine }] })}
                sx={{ fontWeight: 700 }}
              >
                Add line
              </Button>
            </Stack>
            {cardForm.lines.map((line, index) => (
              <Stack key={index} direction={{ xs: 'column', md: 'row' }} spacing={1.5} alignItems={{ md: 'center' }}>
                <TextField
                  size="small"
                  label="Meter key"
                  value={line.meterKey}
                  onChange={(e) => setLine(index, { meterKey: e.target.value })}
                  sx={{ flex: 1.4 }}
                  required
                />
                <TextField
                  size="small"
                  label="Included qty"
                  value={String(line.includedQuantity)}
                  onChange={(e) => setLine(index, { includedQuantity: Number(e.target.value) || 0 })}
                  sx={{ flex: 1 }}
                />
                <TextField
                  size="small"
                  label="Unit price"
                  value={String(line.unitPrice)}
                  onChange={(e) => setLine(index, { unitPrice: Number(e.target.value) || 0 })}
                  sx={{ flex: 1 }}
                />
                <TextField
                  size="small"
                  label="Unit"
                  value={line.unit}
                  onChange={(e) => setLine(index, { unit: e.target.value })}
                  sx={{ flex: 1 }}
                  required
                />
                <TextField
                  size="small"
                  label="Tier note"
                  value={line.tierNote ?? ''}
                  onChange={(e) => setLine(index, { tierNote: e.target.value || null })}
                  sx={{ flex: 1.2 }}
                />
                <Tooltip title="Remove line">
                  <span>
                    <IconButton
                      size="small"
                      color="error"
                      disabled={cardForm.lines.length === 1}
                      onClick={() => setCardForm({ ...cardForm, lines: cardForm.lines.filter((_, i) => i !== index) })}
                    >
                      <RemoveLineIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
              </Stack>
            ))}
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setCardDialog(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            onClick={() => cardMutation.mutate()}
            disabled={!cardValid || cardMutation.isPending}
            sx={{ fontWeight: 700, px: 3 }}
          >
            {cardMutation.isPending ? 'Saving…' : cardDialog?.mode === 'edit' ? 'Save changes' : 'Create rate card'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}

function CostRow({ label, value, suffix }: { label: string; value: string; suffix?: ReactNode }) {
  return (
    <Stack direction="row" justifyContent="space-between" alignItems="center" spacing={2}>
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Stack direction="row" alignItems="center" spacing={1}>
        <Typography variant="body2" sx={{ fontWeight: 700 }}>{value}</Typography>
        {suffix}
      </Stack>
    </Stack>
  );
}
