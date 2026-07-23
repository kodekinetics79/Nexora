import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, IconButton, MenuItem, Paper, Stack, Tab, Tabs, Table,
  TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material';
import { Banknote, CheckCircle2, CreditCard, FileCheck2, RefreshCw, RotateCcw } from 'lucide-react';
import dayjs from 'dayjs';
import { useSnackbar } from 'notistack';
import { useSearchParams } from 'react-router-dom';
import commercialFinanceService, { type ArOpenItem, type CustomerPayment } from '../../../api/services/commercialFinanceService';
import { useAuth } from '../../../context/AuthContext';

const money = (value: number) => value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export default function AccountsReceivablePage() {
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
  const targetRowRef = useRef<HTMLTableRowElement | null>(null);
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canIssue = hasPermission('Accounts Receivable', 'edit');
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

  const issue = useMutation({
    mutationFn: ({ id, version }: { id: number; version: number }) => commercialFinanceService.issueDocument(id, version),
    onSuccess: () => { enqueueSnackbar('Invoice issued', { variant: 'success' }); refresh(); },
    onError: (error: any) => enqueueSnackbar(error.response?.data?.detail ?? 'Invoice could not be issued', { variant: 'error' }),
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
            <TableHead><TableRow><TableCell>Invoice</TableCell><TableCell>Currency</TableCell><TableCell>Due</TableCell><TableCell>Aging</TableCell><TableCell align="right">Original</TableCell><TableCell align="right">Outstanding</TableCell><TableCell align="center">Action</TableCell></TableRow></TableHead>
            <TableBody>{(openItems.data ?? []).map(item => (
              <TableRow key={item.documentId} hover>
                <TableCell sx={{ fontWeight: 700 }}>{item.documentNumber}</TableCell>
                <TableCell>{item.currencyCode || (item.currencyId == null ? 'Unassigned' : `Currency ${item.currencyId}`)}</TableCell>
                <TableCell>{dayjs(item.dueDate).format('DD MMM YYYY')}</TableCell>
                <TableCell><Chip size="small" label={item.agingBucket} color={item.daysPastDue > 30 ? 'error' : item.daysPastDue > 0 ? 'warning' : 'default'} /></TableCell>
                <TableCell align="right">{money(item.originalAmount)}</TableCell>
                <TableCell align="right" sx={{ fontWeight: 800 }}>{money(item.outstandingAmount)}</TableCell>
                <TableCell align="center">{canRecordPayment && <Button size="small" startIcon={<CreditCard size={16} />} onClick={() => openPaymentDialog(item)}>Record payment</Button>}</TableCell>
              </TableRow>
            ))}{!openItems.data?.length && <TableRow><TableCell colSpan={7} align="center" sx={{ py: 6 }}>No open receivables.</TableCell></TableRow>}</TableBody>
          </Table></TableContainer>
        ) : tab === 1 ? (
          <TableContainer sx={{ overflowX: 'auto' }}><Table size="small" sx={{ minWidth: 820 }}>
            <TableHead><TableRow><TableCell>Number</TableCell><TableCell>Currency</TableCell><TableCell>Status</TableCell><TableCell>Document date</TableCell><TableCell>Due date</TableCell><TableCell align="right">Total</TableCell><TableCell align="right">Balance</TableCell><TableCell align="center">Action</TableCell></TableRow></TableHead>
            <TableBody>{(documents.data ?? []).map(document => (
              <TableRow
                key={document.id}
                ref={document.id === targetDocumentId ? targetRowRef : undefined}
                hover
                selected={document.id === targetDocumentId}
                sx={document.id === targetDocumentId ? { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: '-2px' } : undefined}
              >
                <TableCell sx={{ fontWeight: 700 }}>{document.documentNumber ?? `Draft #${document.id}`}</TableCell>
                <TableCell>{document.currencyCode || (document.currencyId == null ? 'Unassigned' : `Currency ${document.currencyId}`)}</TableCell>
                <TableCell><Chip size="small" label={document.status} color={document.status === 'Issued' ? 'success' : 'default'} /></TableCell>
                <TableCell>{dayjs(document.documentDate).format('DD MMM YYYY')}</TableCell>
                <TableCell>{dayjs(document.dueDate).format('DD MMM YYYY')}</TableCell>
                <TableCell align="right">{money(document.totalAmount)}</TableCell>
                <TableCell align="right">{money(document.outstandingAmount)}</TableCell>
                <TableCell align="center">{canIssue && document.status === 'Draft' && <Tooltip title="Issue invoice"><span><IconButton size="small" disabled={issue.isPending} onClick={() => issue.mutate({ id: document.id, version: document.version })}><FileCheck2 size={18} /></IconButton></span></Tooltip>}</TableCell>
              </TableRow>
            ))}{!documents.data?.length && <TableRow><TableCell colSpan={8} align="center" sx={{ py: 6 }}>No receivable documents.</TableCell></TableRow>}</TableBody>
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
    </Box>
  );
}
