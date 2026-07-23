import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, IconButton, Menu, MenuItem, Paper, Stack, Tab, Tabs, Table,
  TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, ToggleButton,
  ToggleButtonGroup, Tooltip, Typography, useMediaQuery, useTheme,
} from '@mui/material';
import { Ban, Banknote, CheckCircle2, CreditCard, FileCheck2, FileMinus2, FilePlus2, MoreVertical, RefreshCw, RotateCcw } from 'lucide-react';
import dayjs from 'dayjs';
import { useSnackbar } from 'notistack';
import { useSearchParams } from 'react-router-dom';
import commercialFinanceService, {
  type ArOpenItem,
  type CustomerPayment,
  type ReceivableAdjustmentType,
  type ReceivableDocument,
} from '../../../api/services/commercialFinanceService';
import { useAuth } from '../../../context/AuthContext';

const money = (value: number) => value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const roundMoney = (value: number) => Math.round((value + Number.EPSILON) * 100) / 100;
const documentTypeLabel = (type: string) => type === 'CreditNote' ? 'credit note' : type === 'DebitNote' ? 'debit note' : 'invoice';
const documentTypeTitle = (type: string) => `${documentTypeLabel(type)[0].toUpperCase()}${documentTypeLabel(type).slice(1)}`;

const adjustmentReasons = [
  ['PRICE_ADJUSTMENT', 'Price adjustment'],
  ['RETURN_ALLOWANCE', 'Return or allowance'],
  ['TAX_CORRECTION', 'Tax correction'],
  ['SERVICE_ADJUSTMENT', 'Service adjustment'],
  ['OTHER', 'Other'],
] as const;

export default function AccountsReceivablePage() {
  const theme = useTheme();
  const adjustmentFullScreen = useMediaQuery(theme.breakpoints.down('md'));
  const [searchParams] = useSearchParams();
  const targetDocumentId = Number(searchParams.get('documentId')) || null;
  const [tab, setTab] = useState(targetDocumentId ? 1 : 0);
  const [selected, setSelected] = useState<ArOpenItem | null>(null);
  const [paymentOperation, setPaymentOperation] = useState<{ idempotencyKey: string; paymentDate: string } | null>(null);
  const [amount, setAmount] = useState('');
  const [method, setMethod] = useState('BankTransfer');
  const [reference, setReference] = useState('');
  const [reversing, setReversing] = useState<CustomerPayment | null>(null);
  const [reversalReason, setReversalReason] = useState('');
  const [cancelling, setCancelling] = useState<ReceivableDocument | null>(null);
  const [cancellationReason, setCancellationReason] = useState('');
  const [documentMenu, setDocumentMenu] = useState<{ anchor: HTMLElement; invoice: ReceivableDocument } | null>(null);
  const [adjustingInvoice, setAdjustingInvoice] = useState<ReceivableDocument | null>(null);
  const [adjustmentType, setAdjustmentType] = useState<ReceivableAdjustmentType>('CreditNote');
  const [adjustmentReasonCode, setAdjustmentReasonCode] = useState('PRICE_ADJUSTMENT');
  const [adjustmentReason, setAdjustmentReason] = useState('');
  const [adjustmentQuantities, setAdjustmentQuantities] = useState<Record<number, string>>({});
  const [adjustmentIdempotencyKey, setAdjustmentIdempotencyKey] = useState('');
  const [adjustmentSubmitted, setAdjustmentSubmitted] = useState(false);
  const targetRowRef = useRef<HTMLTableRowElement | null>(null);
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canEditReceivables = hasPermission('Accounts Receivable', 'edit');
  const canCreateAdjustments = hasPermission('Receivable Adjustments', 'create');
  const canApproveAdjustments = hasPermission('Receivable Adjustments', 'edit');
  const canRecordPayment = hasPermission('Customer Payments', 'create');
  const canViewPayments = hasPermission('Customer Payments');
  const canReversePayment = hasPermission('Customer Payments', 'edit');

  const openItems = useQuery({ queryKey: ['ar-open-items'], queryFn: () => commercialFinanceService.getOpenItems() });
  const documents = useQuery({ queryKey: ['receivable-documents'], queryFn: () => commercialFinanceService.getDocuments() });
  const payments = useQuery({
    queryKey: ['customer-payments'],
    queryFn: () => commercialFinanceService.getPayments(),
    enabled: canViewPayments,
  });
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['ar-open-items'] });
    void queryClient.invalidateQueries({ queryKey: ['receivable-documents'] });
    void queryClient.invalidateQueries({ queryKey: ['customer-payments'] });
  };

  useEffect(() => {
    if (!targetDocumentId || !documents.data) return;
    setTab(1);
    const frame = window.requestAnimationFrame(() => targetRowRef.current?.scrollIntoView({ behavior: 'smooth', block: 'center' }));
    return () => window.cancelAnimationFrame(frame);
  }, [documents.data, targetDocumentId]);

  useEffect(() => {
    if (!adjustingInvoice || !documents.data) return;
    const refreshed = documents.data.find(document => document.id === adjustingInvoice.id);
    if (refreshed && refreshed !== adjustingInvoice) setAdjustingInvoice(refreshed);
  }, [adjustingInvoice, documents.data]);

  const issue = useMutation({
    mutationFn: (document: ReceivableDocument) => commercialFinanceService.issueDocument(document.id, document.version, document.documentType),
    onSuccess: (_, document) => { enqueueSnackbar(`${documentTypeTitle(document.documentType)} issued`, { variant: 'success' }); refresh(); },
    onError: (error: any, document) => enqueueSnackbar(error.response?.data?.detail ?? `${documentTypeTitle(document.documentType)} could not be issued`, { variant: 'error' }),
  });
  const cancelDocument = useMutation({
    mutationFn: () => commercialFinanceService.cancelDocument(cancelling!.id, cancelling!.documentType, {
      reason: cancellationReason.trim(),
      expectedVersion: cancelling!.version,
    }),
    onSuccess: () => {
      enqueueSnackbar(`${documentTypeTitle(cancelling!.documentType)} draft cancelled`, { variant: 'success' });
      setCancelling(null); setCancellationReason(''); refresh();
    },
    onError: (error: any) => enqueueSnackbar(error.response?.data?.detail ?? `${documentTypeTitle(cancelling?.documentType ?? 'Invoice')} draft could not be cancelled`, { variant: 'error' }),
  });
  const createAdjustment = useMutation({
    mutationFn: () => commercialFinanceService.createAdjustment(adjustingInvoice!.id, {
      documentType: adjustmentType,
      documentDate: null,
      dueDate: null,
      reasonCode: adjustmentReasonCode,
      reason: adjustmentReason.trim(),
      lines: adjustingInvoice!.lines.flatMap(line => {
        const quantity = Number(adjustmentQuantities[line.id]);
        return quantity > 0 ? [{ parentLineId: line.id, quantity }] : [];
      }),
    }, adjustmentIdempotencyKey),
    onSuccess: document => {
      enqueueSnackbar(`${documentTypeTitle(document.documentType)} draft created`, { variant: 'success' });
      setAdjustingInvoice(null);
      refresh();
      setTab(1);
    },
    onError: (error: any) => {
      const status = error.response?.status;
      const resultIsUncertain = !status || status >= 500;
      if (status === 409) {
        void queryClient.invalidateQueries({ queryKey: ['ar-open-items'] });
        void queryClient.invalidateQueries({ queryKey: ['receivable-documents'] });
      }
      if (!resultIsUncertain) {
        setAdjustmentSubmitted(false);
        setAdjustmentIdempotencyKey(crypto.randomUUID());
      }
      enqueueSnackbar(
        error.response?.data?.detail ?? (resultIsUncertain
          ? 'The result is uncertain. Retry will use the same operation key.'
          : 'Adjustment draft could not be created.'),
        { variant: 'error' },
      );
    },
  });
  const payment = useMutation({
    mutationFn: () => commercialFinanceService.postPayment({
      customerId: selected!.customerId,
      commercialCaseId: selected!.commercialCaseId,
      currencyId: selected!.currencyId,
      paymentDate: paymentOperation!.paymentDate,
      amount: Number(amount),
      method,
      bankReference: reference || undefined,
      allocations: [{ receivableDocumentId: selected!.documentId, amount: Number(amount) }],
    }, paymentOperation!.idempotencyKey),
    onSuccess: () => {
      enqueueSnackbar('Payment posted and allocated', { variant: 'success' });
      setSelected(null); setPaymentOperation(null); setAmount(''); setReference(''); refresh();
    },
    onError: (error: any) => enqueueSnackbar(error.response?.data?.detail ?? 'Payment could not be posted', { variant: 'error' }),
  });

  const reversePayment = useMutation({
    mutationFn: () => commercialFinanceService.reversePayment(reversing!.id, reversing!.version, reversalReason.trim()),
    onSuccess: () => {
      enqueueSnackbar('Payment reversed', { variant: 'success' });
      setReversing(null); setReversalReason(''); refresh();
    },
    onError: (error: any) => enqueueSnackbar(error.response?.data?.detail ?? 'Payment could not be reversed', { variant: 'error' }),
  });

  const metrics = useMemo(() => {
    const rows = openItems.data ?? [];
    const grouped = new Map<string, { currencyId: number | null; currencyLabel: string; outstanding: number; overdue: number; current: number }>();
    rows.forEach(row => {
      const currencyLabel = row.currencyCode || (row.currencyId == null ? 'Currency unassigned' : `Currency ${row.currencyId}`);
      const key = `${row.currencyId ?? 'none'}:${currencyLabel}`;
      const group = grouped.get(key) ?? { currencyId: row.currencyId ?? null, currencyLabel, outstanding: 0, overdue: 0, current: 0 };
      group.outstanding += row.outstandingAmount;
      if (row.daysPastDue > 0) group.overdue += row.outstandingAmount;
      else group.current += row.outstandingAmount;
      grouped.set(key, group);
    });
    return [...grouped.values()].sort((a, b) => (a.currencyId ?? Number.MAX_SAFE_INTEGER) - (b.currencyId ?? Number.MAX_SAFE_INTEGER));
  }, [openItems.data]);

  const adjustmentTotal = useMemo(() => {
    if (!adjustingInvoice) return 0;
    return roundMoney(adjustingInvoice.lines.reduce((total, line) => {
      const quantity = Number(adjustmentQuantities[line.id]);
      if (!(quantity > 0) || quantity > line.quantity) return total;
      const ratio = quantity / line.quantity;
      const gross = roundMoney(quantity * line.unitPrice);
      const discount = roundMoney(line.discountAmount * ratio);
      const tax = roundMoney(line.taxAmount * ratio);
      return total + roundMoney(gross - discount + tax);
    }, 0));
  }, [adjustingInvoice, adjustmentQuantities]);

  const availableAdjustmentQuantities = useMemo(() => {
    const result = new Map<number, number>();
    if (!adjustingInvoice) return result;
    const used = new Map<number, number>();
    (documents.data ?? [])
      .filter(document => document.parentDocumentId === adjustingInvoice.id &&
        document.documentType === adjustmentType && document.status === 'Issued')
      .flatMap(document => document.lines)
      .forEach(line => {
        if (line.parentDocumentLineId) {
          used.set(line.parentDocumentLineId, (used.get(line.parentDocumentLineId) ?? 0) + line.quantity);
        }
      });
    adjustingInvoice.lines.forEach(line => result.set(line.id, Math.max(0, line.quantity - (used.get(line.id) ?? 0))));
    return result;
  }, [adjustingInvoice, adjustmentType, documents.data]);

  const selectedAdjustmentLines = adjustingInvoice?.lines.filter(line => Number(adjustmentQuantities[line.id]) > 0) ?? [];
  const adjustmentHasInvalidQuantity = selectedAdjustmentLines.some(line =>
    Number(adjustmentQuantities[line.id]) > (availableAdjustmentQuantities.get(line.id) ?? 0));
  const projectedInvoiceBalance = adjustingInvoice
    ? roundMoney(adjustingInvoice.outstandingAmount + (adjustmentType === 'CreditNote' ? -adjustmentTotal : adjustmentTotal))
    : 0;

  const openPaymentDialog = (item: ArOpenItem) => {
    setSelected(item);
    setAmount(String(item.outstandingAmount));
    setPaymentOperation({ idempotencyKey: crypto.randomUUID(), paymentDate: new Date().toISOString() });
  };
  const closePaymentDialog = () => {
    if (payment.isPending) return;
    setSelected(null); setPaymentOperation(null); setAmount(''); setReference('');
  };
  const closeReversalDialog = () => {
    if (reversePayment.isPending) return;
    setReversing(null); setReversalReason('');
  };
  const closeCancellationDialog = () => {
    if (cancelDocument.isPending) return;
    setCancelling(null); setCancellationReason('');
  };
  const openAdjustmentDialog = (invoice: ReceivableDocument, type: ReceivableAdjustmentType) => {
    setDocumentMenu(null);
    createAdjustment.reset();
    setAdjustingInvoice(invoice);
    setAdjustmentType(type);
    setAdjustmentReasonCode('PRICE_ADJUSTMENT');
    setAdjustmentReason('');
    setAdjustmentQuantities({});
    setAdjustmentIdempotencyKey(crypto.randomUUID());
    setAdjustmentSubmitted(false);
  };
  const closeAdjustmentDialog = () => {
    if (createAdjustment.isPending) return;
    setAdjustingInvoice(null);
  };
  const submitAdjustment = () => {
    setAdjustmentSubmitted(true);
    createAdjustment.mutate();
  };

  if (openItems.isLoading || documents.isLoading || (canViewPayments && payments.isLoading)) return <Box sx={{ display: 'grid', placeItems: 'center', minHeight: 400 }}><CircularProgress /></Box>;
  if (openItems.isError || documents.isError || (canViewPayments && payments.isError)) return <Alert severity="error">Accounts receivable data could not be loaded.</Alert>;

  return (
    <Box sx={{ p: 2 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' }, mb: 2 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800 }}>Accounts Receivable</Typography>
          <Typography variant="body2" color="text.secondary">Issued invoices, collections and aging</Typography>
        </Box>
        <Tooltip title="Refresh balances"><IconButton onClick={refresh}><RefreshCw size={19} /></IconButton></Tooltip>
      </Stack>

      <Box sx={{ borderBlock: '1px solid', borderColor: 'divider', mb: 2 }}>
        {metrics.length ? metrics.map((group, groupIndex) => (
          <Box key={`${group.currencyId ?? 'unassigned'}:${group.currencyLabel}`} sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(3, 1fr)' }, borderTop: groupIndex ? '1px solid' : 0, borderColor: 'divider' }}>
            {[
              ['Open receivables', group.outstanding, <Banknote size={20} />],
              ['Overdue', group.overdue, <CreditCard size={20} />],
              ['Current', group.current, <CheckCircle2 size={20} />],
            ].map(([label, value, icon], index) => (
              <Stack key={String(label)} direction="row" spacing={1.5} sx={{ alignItems: 'center', p: 2, minWidth: 0, borderRight: index < 2 ? { sm: '1px solid' } : 0, borderColor: 'divider' }}>
                {icon}<Box sx={{ minWidth: 0 }}><Typography variant="caption" color="text.secondary">{group.currencyLabel} - {label}</Typography><Typography variant="h6" sx={{ fontWeight: 800, overflowWrap: 'anywhere' }}>{money(Number(value))}</Typography></Box>
              </Stack>
            ))}
          </Box>
        )) : <Typography color="text.secondary" sx={{ p: 2 }}>No open receivable balances.</Typography>}
      </Box>

      <Tabs value={tab} onChange={(_, value) => setTab(value)} variant="scrollable" scrollButtons="auto" allowScrollButtonsMobile sx={{ mb: 1 }}>
        <Tab label={`Open items (${openItems.data?.length ?? 0})`} />
        <Tab label={`Documents (${documents.data?.length ?? 0})`} />
        {canViewPayments && <Tab label={`Payments (${payments.data?.length ?? 0})`} />}
      </Tabs>

      <Paper variant="outlined" sx={{ borderRadius: 1, overflow: 'hidden' }}>
        {tab === 0 ? (
          <TableContainer sx={{ overflowX: 'auto' }}><Table size="small" sx={{ minWidth: 760 }}>
            <TableHead><TableRow><TableCell>Document</TableCell><TableCell>Type</TableCell><TableCell>Currency</TableCell><TableCell>Due</TableCell><TableCell>Aging</TableCell><TableCell align="right">Original</TableCell><TableCell align="right">Outstanding</TableCell><TableCell align="center">Action</TableCell></TableRow></TableHead>
            <TableBody>{(openItems.data ?? []).map(item => (
              <TableRow key={item.documentId} hover>
                <TableCell sx={{ fontWeight: 700 }}>{item.documentNumber}</TableCell>
                <TableCell><Chip size="small" variant="outlined" label={documentTypeTitle(item.documentType)} /></TableCell>
                <TableCell>{item.currencyCode || (item.currencyId == null ? 'Unassigned' : `Currency ${item.currencyId}`)}</TableCell>
                <TableCell>{dayjs(item.dueDate).format('DD MMM YYYY')}</TableCell>
                <TableCell><Chip size="small" label={item.agingBucket} color={item.daysPastDue > 30 ? 'error' : item.daysPastDue > 0 ? 'warning' : 'default'} /></TableCell>
                <TableCell align="right">{money(item.originalAmount)}</TableCell>
                <TableCell align="right" sx={{ fontWeight: 800 }}>{money(item.outstandingAmount)}</TableCell>
                <TableCell align="center">{canRecordPayment && <Button size="small" startIcon={<CreditCard size={16} />} onClick={() => openPaymentDialog(item)}>Record payment</Button>}</TableCell>
              </TableRow>
            ))}{!openItems.data?.length && <TableRow><TableCell colSpan={8} align="center" sx={{ py: 6 }}>No open receivables.</TableCell></TableRow>}</TableBody>
          </Table></TableContainer>
        ) : tab === 1 ? (
          <TableContainer sx={{ overflowX: 'auto' }}><Table size="small" sx={{ minWidth: 900 }}>
            <TableHead><TableRow><TableCell>Number</TableCell><TableCell>Type</TableCell><TableCell>Currency</TableCell><TableCell>Status</TableCell><TableCell>Document date</TableCell><TableCell>Due date</TableCell><TableCell align="right">Total</TableCell><TableCell align="right">Balance</TableCell><TableCell align="center">Action</TableCell></TableRow></TableHead>
            <TableBody>{(documents.data ?? []).map(document => (
              <TableRow
                key={document.id}
                ref={document.id === targetDocumentId ? targetRowRef : undefined}
                hover
                selected={document.id === targetDocumentId}
                sx={document.id === targetDocumentId ? { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: '-2px' } : undefined}
              >
                <TableCell>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>{document.documentNumber ?? `${document.status === 'Cancelled' ? 'Cancelled' : 'Draft'} #${document.id}`}</Typography>
                  {document.status === 'Cancelled' && <Typography variant="caption" color="text.secondary" sx={{ display: 'block', maxWidth: 260, overflowWrap: 'anywhere' }}>
                    {document.voidReason}{document.voidedBy ? ` - ${document.voidedBy}` : ''}{document.voidedOn ? ` - ${dayjs(document.voidedOn).format('DD MMM YYYY HH:mm')}` : ''}
                  </Typography>}
                </TableCell>
                <TableCell><Chip size="small" variant="outlined" label={document.documentType === 'CreditNote' ? 'Credit note' : document.documentType === 'DebitNote' ? 'Debit note' : 'Invoice'} /></TableCell>
                <TableCell>{document.currencyCode || (document.currencyId == null ? 'Unassigned' : `Currency ${document.currencyId}`)}</TableCell>
                <TableCell><Chip size="small" label={document.status} color={document.status === 'Issued' ? 'success' : 'default'} /></TableCell>
                <TableCell>{dayjs(document.documentDate).format('DD MMM YYYY')}</TableCell>
                <TableCell>{dayjs(document.dueDate).format('DD MMM YYYY')}</TableCell>
                <TableCell align="right">{money(document.totalAmount)}</TableCell>
                <TableCell align="right">{money(document.outstandingAmount)}</TableCell>
                <TableCell align="center">
                  {((document.documentType === 'Invoice' && canEditReceivables) || (document.documentType !== 'Invoice' && canApproveAdjustments)) && document.status === 'Draft' && <Stack direction="row" spacing={0.5} sx={{ justifyContent: 'center' }}>
                    <Tooltip title={`Issue ${documentTypeLabel(document.documentType)}`}><span><IconButton size="small" disabled={issue.isPending || cancelDocument.isPending} onClick={() => issue.mutate(document)}><FileCheck2 size={18} /></IconButton></span></Tooltip>
                    <Tooltip title={`Cancel ${documentTypeLabel(document.documentType)} draft`}><span><IconButton size="small" color="error" disabled={issue.isPending || cancelDocument.isPending} onClick={() => setCancelling(document)}><Ban size={18} /></IconButton></span></Tooltip>
                  </Stack>}
                  {canCreateAdjustments && document.documentType === 'Invoice' && document.status === 'Issued' && <Tooltip title="Invoice adjustments">
                    <IconButton size="small" aria-label={`Adjust invoice ${document.documentNumber ?? document.id}`} onClick={event => setDocumentMenu({ anchor: event.currentTarget, invoice: document })}><MoreVertical size={18} /></IconButton>
                  </Tooltip>}
                </TableCell>
              </TableRow>
            ))}{!documents.data?.length && <TableRow><TableCell colSpan={9} align="center" sx={{ py: 6 }}>No receivable documents.</TableCell></TableRow>}</TableBody>
          </Table></TableContainer>
        ) : (
          <TableContainer sx={{ overflowX: 'auto' }}><Table size="small" sx={{ minWidth: 860 }}>
            <TableHead><TableRow><TableCell>Receipt</TableCell><TableCell>Payment date</TableCell><TableCell>Customer</TableCell><TableCell>Currency</TableCell><TableCell>Status</TableCell><TableCell align="right">Amount</TableCell><TableCell align="right">Allocated</TableCell><TableCell align="right">Unapplied</TableCell><TableCell align="center">Action</TableCell></TableRow></TableHead>
            <TableBody>{(payments.data ?? []).map(item => (
              <TableRow key={item.id} hover>
                <TableCell sx={{ fontWeight: 700 }}>{item.receiptNumber}</TableCell>
                <TableCell>{dayjs(item.paymentDate).format('DD MMM YYYY')}</TableCell>
                <TableCell>Customer {item.customerId}</TableCell>
                <TableCell>{item.currencyCode || (item.currencyId == null ? 'Unassigned' : `Currency ${item.currencyId}`)}</TableCell>
                <TableCell><Chip size="small" label={item.status} color={item.status === 'Posted' ? 'success' : 'default'} /></TableCell>
                <TableCell align="right">{money(item.amount)}</TableCell>
                <TableCell align="right">{money(item.allocatedAmount)}</TableCell>
                <TableCell align="right">{money(item.unappliedAmount)}</TableCell>
                <TableCell align="center">{canReversePayment && item.status === 'Posted' && <Tooltip title="Reverse payment"><span><IconButton size="small" disabled={reversePayment.isPending} onClick={() => setReversing(item)}><RotateCcw size={18} /></IconButton></span></Tooltip>}</TableCell>
              </TableRow>
            ))}{!payments.data?.length && <TableRow><TableCell colSpan={9} align="center" sx={{ py: 6 }}>No payment history.</TableCell></TableRow>}</TableBody>
          </Table></TableContainer>
        )}
      </Paper>

      <Menu anchorEl={documentMenu?.anchor ?? null} open={Boolean(documentMenu)} onClose={() => setDocumentMenu(null)}>
        <MenuItem onClick={() => documentMenu && openAdjustmentDialog(documentMenu.invoice, 'CreditNote')}><FileMinus2 size={17} style={{ marginRight: 10 }} />Create credit note</MenuItem>
        <MenuItem onClick={() => documentMenu && openAdjustmentDialog(documentMenu.invoice, 'DebitNote')}><FilePlus2 size={17} style={{ marginRight: 10 }} />Create debit note</MenuItem>
      </Menu>

      <Dialog open={Boolean(adjustingInvoice)} onClose={closeAdjustmentDialog} fullWidth fullScreen={adjustmentFullScreen} maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800 }}>Create receivable adjustment</DialogTitle><Divider />
        <DialogContent sx={{ p: { xs: 2, sm: 2.5 } }}>
          <Stack spacing={2.5}>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'minmax(0, 1fr) auto' }, gap: 1, alignItems: 'center' }}>
              <Box sx={{ minWidth: 0 }}>
                <Typography variant="caption" color="text.secondary">Issued invoice</Typography>
                <Typography variant="body1" sx={{ fontWeight: 800, overflowWrap: 'anywhere' }}>{adjustingInvoice?.documentNumber}</Typography>
                <Typography variant="body2" color="text.secondary">
                  {adjustingInvoice?.currencyCode || (adjustingInvoice?.currencyId == null ? 'Currency unassigned' : `Currency ${adjustingInvoice.currencyId}`)} · Balance {money(adjustingInvoice?.outstandingAmount ?? 0)}
                </Typography>
              </Box>
              <ToggleButtonGroup
                exclusive
                size="small"
                value={adjustmentType}
                disabled={adjustmentSubmitted}
                onChange={(_, value: ReceivableAdjustmentType | null) => value && setAdjustmentType(value)}
                aria-label="Adjustment type"
                sx={{ justifySelf: { sm: 'end' }, '& .MuiToggleButton-root': { minWidth: 118 } }}
              >
                <ToggleButton value="CreditNote"><FileMinus2 size={16} style={{ marginRight: 7 }} />Credit note</ToggleButton>
                <ToggleButton value="DebitNote"><FilePlus2 size={16} style={{ marginRight: 7 }} />Debit note</ToggleButton>
              </ToggleButtonGroup>
            </Box>

            {adjustmentSubmitted && createAdjustment.isError && <Alert severity="warning">The request may have reached the server. Retry safely with the same operation key; the financial draft will not be duplicated.</Alert>}

            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'minmax(180px, 0.45fr) minmax(0, 1fr)' }, gap: 2 }}>
              <TextField select size="small" required label="Reason code" value={adjustmentReasonCode} disabled={adjustmentSubmitted} onChange={event => setAdjustmentReasonCode(event.target.value)}>
                {adjustmentReasons.map(([value, label]) => <MenuItem key={value} value={value}>{label}</MenuItem>)}
              </TextField>
              <TextField size="small" required label="Reason" value={adjustmentReason} disabled={adjustmentSubmitted} onChange={event => setAdjustmentReason(event.target.value)} helperText={`${adjustmentReason.length}/500`} slotProps={{ htmlInput: { maxLength: 500 } }} />
            </Box>

            <Box sx={{ borderBlock: '1px solid', borderColor: 'divider' }}>
              <Box sx={{ display: { xs: 'none', sm: 'grid' }, gridTemplateColumns: 'minmax(0, 1fr) 100px 105px 112px', gap: 1.5, px: 1.5, py: 1, bgcolor: 'action.hover' }}>
                <Typography variant="caption" sx={{ fontWeight: 700 }}>Invoice line</Typography>
                <Typography variant="caption" sx={{ fontWeight: 700 }} align="right">Available</Typography>
                <Typography variant="caption" sx={{ fontWeight: 700 }} align="right">Quantity</Typography>
                <Typography variant="caption" sx={{ fontWeight: 700 }} align="right">Projected</Typography>
              </Box>
              {(adjustingInvoice?.lines ?? []).map((line, index) => {
                const quantity = Number(adjustmentQuantities[line.id]);
                const availableQuantity = availableAdjustmentQuantities.get(line.id) ?? 0;
                const validQuantity = quantity > 0 && quantity <= availableQuantity;
                const ratio = validQuantity ? quantity / line.quantity : 0;
                const lineProjection = validQuantity ? roundMoney(
                  roundMoney(quantity * line.unitPrice) - roundMoney(line.discountAmount * ratio) + roundMoney(line.taxAmount * ratio),
                ) : 0;
                return <Box key={line.id} sx={{ display: 'grid', gridTemplateColumns: { xs: 'minmax(0, 1fr) 112px', sm: 'minmax(0, 1fr) 100px 105px 112px' }, gap: 1.5, alignItems: 'center', px: 1.5, py: 1.25, borderTop: index ? '1px solid' : 0, borderColor: 'divider' }}>
                  <Box sx={{ minWidth: 0 }}>
                    <Typography variant="body2" sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>{line.description}</Typography>
                    <Typography variant="caption" color="text.secondary">{money(line.unitPrice)} each</Typography>
                  </Box>
                  <Typography variant="body2" align="right" sx={{ display: { xs: 'none', sm: 'block' } }}>{availableQuantity}</Typography>
                  <TextField
                    size="small"
                    type="number"
                    value={adjustmentQuantities[line.id] ?? ''}
                    disabled={adjustmentSubmitted}
                    error={quantity > availableQuantity}
                    onChange={event => setAdjustmentQuantities(current => ({ ...current, [line.id]: event.target.value }))}
                    slotProps={{ htmlInput: { min: 0, max: availableQuantity, step: 'any', 'aria-label': `Adjustment quantity for ${line.description}` } }}
                  />
                  <Typography variant="body2" align="right" sx={{ fontWeight: lineProjection ? 700 : 400, gridColumn: { xs: '1 / -1', sm: 'auto' } }}>
                    <Box component="span" sx={{ display: { sm: 'none' }, color: 'text.secondary', mr: 1 }}>Projected</Box>{money(lineProjection)}
                  </Typography>
                </Box>;
              })}
            </Box>

            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, minmax(0, 1fr))' }, borderBlock: '1px solid', borderColor: 'divider' }}>
              <Box sx={{ p: 1.5, borderRight: { sm: '1px solid' }, borderColor: 'divider' }}><Typography variant="caption" color="text.secondary">{adjustmentType === 'CreditNote' ? 'Credit' : 'Debit'} note total</Typography><Typography variant="h6" sx={{ fontWeight: 800 }}>{money(adjustmentTotal)}</Typography></Box>
              <Box sx={{ p: 1.5 }}><Typography variant="caption" color="text.secondary">{adjustmentType === 'CreditNote' ? 'Projected invoice balance' : 'Projected account exposure'}</Typography><Typography variant="h6" color={projectedInvoiceBalance < 0 ? 'error.main' : 'text.primary'} sx={{ fontWeight: 800 }}>{money(projectedInvoiceBalance)}</Typography></Box>
            </Box>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 2.5, py: 1.5, borderTop: '1px solid', borderColor: 'divider' }}>
          <Button disabled={createAdjustment.isPending} onClick={closeAdjustmentDialog}>Cancel</Button>
          <Button
            variant="contained"
            disabled={createAdjustment.isPending || !adjustmentReasonCode || !adjustmentReason.trim() || !selectedAdjustmentLines.length || adjustmentHasInvalidQuantity || adjustmentTotal <= 0}
            onClick={submitAdjustment}
          >
            {adjustmentSubmitted && createAdjustment.isError ? 'Retry safely' : 'Create draft'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={Boolean(selected)} onClose={closePaymentDialog} fullWidth maxWidth="xs">
        <DialogTitle>Record payment</DialogTitle><Divider />
        <DialogContent sx={{ pt: 2 }}>
          <Stack spacing={2}>
            <TextField label="Invoice" value={selected?.documentNumber ?? ''} disabled />
            <TextField label="Amount" type="number" value={amount} onChange={event => setAmount(event.target.value)} slotProps={{ htmlInput: { min: 0.01, max: selected?.outstandingAmount, step: 0.01 } }} />
            <TextField select label="Method" value={method} onChange={event => setMethod(event.target.value)}><MenuItem value="BankTransfer">Bank transfer</MenuItem><MenuItem value="Card">Card</MenuItem><MenuItem value="Cheque">Cheque</MenuItem><MenuItem value="Cash">Cash</MenuItem></TextField>
            <TextField label="Bank reference" value={reference} onChange={event => setReference(event.target.value)} />
          </Stack>
        </DialogContent>
        <DialogActions><Button onClick={closePaymentDialog}>Cancel</Button><Button variant="contained" disabled={payment.isPending || !paymentOperation || Number(amount) <= 0 || Number(amount) > (selected?.outstandingAmount ?? 0)} onClick={() => payment.mutate()}>Post payment</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(reversing)} onClose={closeReversalDialog} fullWidth maxWidth="xs">
        <DialogTitle>Reverse payment</DialogTitle><Divider />
        <DialogContent sx={{ pt: 2 }}>
          <Stack spacing={2}>
            <Alert severity="warning">This governed action reverses receipt {reversing?.receiptNumber} and restores its invoice balances.</Alert>
            <TextField label="Reason" value={reversalReason} onChange={event => setReversalReason(event.target.value)} required multiline minRows={3} autoFocus />
          </Stack>
        </DialogContent>
        <DialogActions><Button disabled={reversePayment.isPending} onClick={closeReversalDialog}>Cancel</Button><Button color="error" variant="contained" disabled={reversePayment.isPending || !reversalReason.trim()} onClick={() => reversePayment.mutate()}>Reverse payment</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(cancelling)} onClose={closeCancellationDialog} fullWidth maxWidth="xs">
        <DialogTitle>Cancel {documentTypeLabel(cancelling?.documentType ?? 'Invoice')} draft</DialogTitle><Divider />
        <DialogContent sx={{ pt: 2 }}>
          <Stack spacing={2}>
            <Alert severity="warning">This {documentTypeLabel(cancelling?.documentType ?? 'Invoice')} draft will no longer be available for issuing.</Alert>
            <TextField label="Reason" value={cancellationReason} onChange={event => setCancellationReason(event.target.value)} required multiline minRows={3} autoFocus helperText={`${cancellationReason.length}/500`} slotProps={{ htmlInput: { maxLength: 500 } }} />
          </Stack>
        </DialogContent>
        <DialogActions><Button disabled={cancelDocument.isPending} onClick={closeCancellationDialog}>Keep draft</Button><Button color="error" variant="contained" disabled={cancelDocument.isPending || !cancellationReason.trim()} onClick={() => cancelDocument.mutate()}>Cancel draft</Button></DialogActions>
      </Dialog>
    </Box>
  );
}
