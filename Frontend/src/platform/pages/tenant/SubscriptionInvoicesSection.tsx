import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import { EmptyState, ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { fmtDate } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import { usePlatformAuth } from '../../auth/usePlatformAuth';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import type {
  BillingStatementSummary,
  CreateSubscriptionInvoiceInput,
  SubscriptionInvoice,
  Tenant,
} from '../../types';

const money = (amount: number, currency: string) =>
  new Intl.NumberFormat('en-US', { style: 'currency', currency, maximumFractionDigits: 2 }).format(amount);

export const invoiceAgingLabel = (invoice: SubscriptionInvoice, now = new Date()): string => {
  if (invoice.status === 'Draft') return 'Draft';
  if (invoice.outstandingAmount <= 0) return 'Settled';
  const due = new Date(invoice.dueAtUtc).getTime();
  const days = Math.floor((now.getTime() - due) / 86_400_000);
  if (days <= 0) return 'Current';
  if (days <= 30) return '1–30 days';
  if (days <= 60) return '31–60 days';
  if (days <= 90) return '61–90 days';
  return '90+ days';
};

const invoiceTone = (status: SubscriptionInvoice['status']) => {
  if (status === 'Paid') return 'success' as const;
  if (status === 'Draft') return 'info' as const;
  if (status === 'PartiallyPaid' || status === 'Corrected') return 'warning' as const;
  return 'neutral' as const;
};

const blankCreate = (): CreateSubscriptionInvoiceInput => ({
  statementId: '',
  taxRatePercent: 0,
  taxTreatment: '',
  sellerLegalName: '',
  sellerTaxNumber: '',
});

type MoneyAction = { invoice: SubscriptionInvoice; amount: string; detail: string };

export default function SubscriptionInvoicesSection({
  tenant,
  statements,
}: {
  tenant: Tenant;
  statements: BillingStatementSummary[];
}) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();
  const { platformUser } = usePlatformAuth();
  const [createOpen, setCreateOpen] = useState(false);
  const [create, setCreate] = useState<CreateSubscriptionInvoiceInput>(blankCreate);
  const [finalize, setFinalize] = useState<SubscriptionInvoice | null>(null);
  const [credit, setCredit] = useState<MoneyAction | null>(null);
  const [payment, setPayment] = useState<MoneyAction | null>(null);

  const query = useQuery({
    queryKey: platformKeys.subscriptionInvoices(tenant.id),
    queryFn: () => platformApi.listSubscriptionInvoices(tenant.id),
    enabled: permissions.canAdministerBilling,
  });

  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.subscriptionInvoices(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantBilling(tenant.id) });
  };
  const fail = (fallback: string) => (error: unknown) =>
    enqueueSnackbar(platformErrorMessage(error, fallback), { variant: 'error' });

  const createMutation = useMutation({
    mutationFn: () => platformApi.createSubscriptionInvoice(create),
    onSuccess: () => {
      enqueueSnackbar('Draft invoice created', { variant: 'success' });
      setCreateOpen(false);
      setCreate(blankCreate());
      invalidate();
    },
    onError: fail('The invoice could not be created'),
  });
  const finalizeMutation = useMutation({
    mutationFn: (invoice: SubscriptionInvoice) => platformApi.finalizeSubscriptionInvoice(invoice.id),
    onSuccess: () => {
      enqueueSnackbar('Invoice finalized', { variant: 'success' });
      setFinalize(null);
      invalidate();
    },
    onError: fail('Finalization was refused'),
  });
  const creditMutation = useMutation({
    mutationFn: (action: MoneyAction) =>
      platformApi.creditSubscriptionInvoice(action.invoice.id, Number(action.amount), action.detail.trim()),
    onSuccess: () => {
      enqueueSnackbar('Credit note posted', { variant: 'success' });
      setCredit(null);
      invalidate();
    },
    onError: fail('The credit was refused'),
  });
  const paymentMutation = useMutation({
    mutationFn: (action: MoneyAction) =>
      platformApi.recordSubscriptionPayment(
        action.invoice.id,
        Number(action.amount),
        action.detail.trim(),
        new Date().toISOString(),
      ),
    onSuccess: () => {
      enqueueSnackbar('Payment recorded', { variant: 'success' });
      setPayment(null);
      invalidate();
    },
    onError: fail('The payment was refused'),
  });

  const invoices = query.data ?? [];
  const invoiceStatementIds = useMemo(() => new Set(invoices.map((item) => item.statementId)), [invoices]);
  const eligibleStatements = statements.filter(
    (statement) => statement.status.toLowerCase() === 'final' && !invoiceStatementIds.has(statement.id),
  );
  const createValid =
    create.statementId !== '' &&
    Number.isFinite(create.taxRatePercent) &&
    create.taxRatePercent >= 0 &&
    create.taxRatePercent <= 100 &&
    create.taxTreatment.trim().length > 0 &&
    create.taxTreatment.trim().length <= 128 &&
    create.sellerLegalName.trim().length > 0 &&
    create.sellerLegalName.trim().length <= 256 &&
    create.sellerTaxNumber.trim().length > 0 &&
    create.sellerTaxNumber.trim().length <= 64;

  if (query.isLoading) return <LoadingState label="Reading subscription invoices…" minHeight={180} />;
  if (query.isError) {
    return (
      <ErrorState
        message={platformErrorMessage(query.error, 'Subscription invoices could not be read.')}
        onRetry={() => query.refetch()}
        minHeight={180}
      />
    );
  }

  return (
    <>
      <PageSection
        title="Subscription invoices & receivables"
        subtitle="Invoices preserve the finalized statement and tax evidence. Credits never rewrite the original invoice."
        actions={
          <Button
            variant="contained"
            disabled={!permissions.canAdministerBilling || eligibleStatements.length === 0}
            onClick={() => setCreateOpen(true)}
            sx={{ fontWeight: 700 }}
          >
            Create invoice
          </Button>
        }
      >
        {eligibleStatements.length === 0 && statements.some((item) => item.status.toLowerCase() === 'final') && (
          <Alert severity="info" sx={{ mb: 2 }}>
            Every finalized statement already has an invoice. Creating the same one again is refused server-side.
          </Alert>
        )}
        {invoices.length === 0 ? (
          <EmptyState
            title="No subscription invoices"
            message="Finalize a billing statement before creating its immutable tax invoice."
            minHeight={150}
          />
        ) : (
          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell sx={{ fontWeight: 700 }}>Invoice</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Status / aging</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Total</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Credits</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Paid</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Outstanding</TableCell>
                  <TableCell sx={{ fontWeight: 700 }}>Due</TableCell>
                  <TableCell sx={{ fontWeight: 700 }} align="right">Actions</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {invoices.map((invoice) => {
                  const sameMaker = platformUser?.email.toLowerCase() === invoice.createdBy.toLowerCase();
                  const posted = invoice.status !== 'Draft';
                  return (
                    <TableRow key={invoice.id} hover>
                      <TableCell>
                        <Typography variant="body2" sx={{ fontWeight: 800 }}>{invoice.invoiceNumber}</Typography>
                        <Typography variant="caption" color="text.secondary">
                          Statement {invoice.statementId} · {invoice.taxTreatment}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Stack spacing={0.5} alignItems="flex-start">
                          <SoftChip label={invoice.status} tone={invoiceTone(invoice.status)} dot={false} />
                          <Typography variant="caption" color="text.secondary">{invoiceAgingLabel(invoice)}</Typography>
                        </Stack>
                      </TableCell>
                      <TableCell align="right">{money(invoice.totalAmount, invoice.currency)}</TableCell>
                      <TableCell align="right">{money(invoice.creditedAmount, invoice.currency)}</TableCell>
                      <TableCell align="right">{money(invoice.paidAmount, invoice.currency)}</TableCell>
                      <TableCell align="right" sx={{ fontWeight: 800 }}>
                        {money(invoice.outstandingAmount, invoice.currency)}
                      </TableCell>
                      <TableCell>{fmtDate(invoice.dueAtUtc)}</TableCell>
                      <TableCell align="right">
                        <Stack spacing={0.5} alignItems="flex-end">
                          {invoice.status === 'Draft' && permissions.isOwner && (
                            <Button
                              size="small"
                              disabled={sameMaker}
                              onClick={() => setFinalize(invoice)}
                            >
                              Finalize
                            </Button>
                          )}
                          {posted && invoice.outstandingAmount > 0 && permissions.canAdministerBilling && (
                            <Button size="small" onClick={() => setPayment({ invoice, amount: '', detail: '' })}>
                              Record payment
                            </Button>
                          )}
                          {posted && invoice.outstandingAmount > 0 && permissions.isOwner && (
                            <Button size="small" color="warning" onClick={() => setCredit({ invoice, amount: '', detail: '' })}>
                              Issue credit
                            </Button>
                          )}
                          {invoice.status === 'Draft' && permissions.isOwner && sameMaker && (
                            <Typography variant="caption" color="warning.main" sx={{ maxWidth: 190 }}>
                              Another Owner with MFA must check and finalize this invoice.
                            </Typography>
                          )}
                        </Stack>
                      </TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          </Box>
        )}
      </PageSection>

      <Dialog open={createOpen} onClose={() => setCreateOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Create subscription invoice</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2} sx={{ mt: 0.5 }}>
            <Alert severity="info">Only a Final statement can become an invoice. Seller and tax details are snapshotted permanently.</Alert>
            <TextField
              select required label="Final statement" value={create.statementId}
              onChange={(event) => setCreate({ ...create, statementId: event.target.value })}
            >
              {eligibleStatements.map((statement) => (
                <MenuItem key={statement.id} value={statement.id}>
                  {statement.period} · {money(statement.totalAmount, statement.currency)}
                </MenuItem>
              ))}
            </TextField>
            <TextField
              required label="Seller legal name" value={create.sellerLegalName}
              slotProps={{ htmlInput: { maxLength: 256 } }}
              onChange={(event) => setCreate({ ...create, sellerLegalName: event.target.value })}
            />
            <TextField
              required label="Seller tax number" value={create.sellerTaxNumber}
              slotProps={{ htmlInput: { maxLength: 64 } }}
              onChange={(event) => setCreate({ ...create, sellerTaxNumber: event.target.value })}
            />
            <TextField
              required label="Tax treatment" value={create.taxTreatment}
              slotProps={{ htmlInput: { maxLength: 128 } }}
              helperText="For example, the jurisdiction and applicable treatment recorded on the invoice."
              onChange={(event) => setCreate({ ...create, taxTreatment: event.target.value })}
            />
            <TextField
              required type="number" label="Tax rate (%)" value={create.taxRatePercent}
              error={create.taxRatePercent < 0 || create.taxRatePercent > 100}
              helperText="Must be from 0 to 100."
              slotProps={{ htmlInput: { min: 0, max: 100, step: 0.01 } }}
              onChange={(event) => setCreate({ ...create, taxRatePercent: Number(event.target.value) })}
            />
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button color="inherit" onClick={() => setCreateOpen(false)}>Cancel</Button>
          <Button variant="contained" disabled={!createValid || createMutation.isPending} onClick={() => createMutation.mutate()}>
            {createMutation.isPending ? 'Creating…' : 'Create draft'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={Boolean(finalize)} onClose={() => setFinalize(null)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Finalize invoice</DialogTitle>
        <DialogContent dividers>
          <Alert severity="warning">
            <AlertTitle sx={{ fontWeight: 800 }}>Owner checker and MFA required</AlertTitle>
            The Owner finalizing must differ from the invoice maker. The server verifies both separation of duties and MFA.
          </Alert>
          {finalize && <Typography sx={{ mt: 2 }}>{finalize.invoiceNumber} · {money(finalize.totalAmount, finalize.currency)}</Typography>}
        </DialogContent>
        <DialogActions>
          <Button color="inherit" onClick={() => setFinalize(null)}>Cancel</Button>
          <Button variant="contained" color="success" disabled={!finalize || finalizeMutation.isPending} onClick={() => finalize && finalizeMutation.mutate(finalize)}>
            {finalizeMutation.isPending ? 'Finalizing…' : 'Finalize invoice'}
          </Button>
        </DialogActions>
      </Dialog>

      <MoneyDialog
        action={credit}
        title="Issue credit note"
        detailLabel="Credit reason"
        detailHelp="At least 5 characters. The reason is retained with the separate credit note."
        busy={creditMutation.isPending}
        onChange={setCredit}
        onConfirm={(action) => creditMutation.mutate(action)}
      />
      <MoneyDialog
        action={payment}
        title="Record payment"
        detailLabel="External payment reference"
        detailHelp="A unique bank or processor reference makes retries idempotent."
        busy={paymentMutation.isPending}
        onChange={setPayment}
        onConfirm={(action) => paymentMutation.mutate(action)}
      />
    </>
  );
}

function MoneyDialog({
  action,
  title,
  detailLabel,
  detailHelp,
  busy,
  onChange,
  onConfirm,
}: {
  action: MoneyAction | null;
  title: string;
  detailLabel: string;
  detailHelp: string;
  busy: boolean;
  onChange: (action: MoneyAction | null) => void;
  onConfirm: (action: MoneyAction) => void;
}) {
  const amount = Number(action?.amount ?? 0);
  const valid = Boolean(action && amount > 0 && amount <= action.invoice.outstandingAmount && action.detail.trim().length >= 5);
  return (
    <Dialog open={Boolean(action)} onClose={() => onChange(null)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>{title}</DialogTitle>
      <DialogContent dividers>
        {action && (
          <Stack spacing={2} sx={{ mt: 0.5 }}>
            <Typography variant="body2">
              {action.invoice.invoiceNumber} · outstanding {money(action.invoice.outstandingAmount, action.invoice.currency)}
            </Typography>
            <TextField
              autoFocus required type="number" label="Amount" value={action.amount}
              slotProps={{ htmlInput: { min: 0.01, max: action.invoice.outstandingAmount, step: 0.01 } }}
              error={action.amount !== '' && (amount <= 0 || amount > action.invoice.outstandingAmount)}
              helperText="Cannot exceed the server-reported outstanding balance."
              onChange={(event) => onChange({ ...action, amount: event.target.value })}
            />
            <TextField
              required multiline minRows={2} label={detailLabel} value={action.detail}
              helperText={detailHelp}
              onChange={(event) => onChange({ ...action, detail: event.target.value })}
            />
          </Stack>
        )}
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => onChange(null)}>Cancel</Button>
        <Button variant="contained" disabled={!valid || busy} onClick={() => action && onConfirm(action)}>
          {busy ? 'Posting…' : 'Post'}
        </Button>
      </DialogActions>
    </Dialog>
  );
}
