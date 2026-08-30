import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Checkbox, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, FormControlLabel, IconButton, Menu, MenuItem, Paper, Stack, Tab, Tabs, Table,
  TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, ToggleButton,
  ToggleButtonGroup, Tooltip, Typography, useMediaQuery, useTheme,
} from '@mui/material';
import {
  Ban, Banknote, Check, CheckCircle2, CreditCard, FileCheck2, FileMinus2, FilePlus2,
  MoreVertical, ReceiptText, RefreshCw, RotateCcw, Send, ShieldCheck, Undo2,
} from 'lucide-react';
import dayjs from 'dayjs';
import { useSnackbar } from 'notistack';
import { useSearchParams } from 'react-router-dom';
import commercialFinanceService, {
  type ArOpenItem,
  type CustomerRefund,
  type CustomerPayment,
  type ReceivableAdjustmentType,
  type ReceivableDocument,
  type ReceivableWriteOff,
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

const writeOffReasons = [
  ['BAD_DEBT', 'Bad debt'],
  ['INSOLVENCY', 'Customer insolvency'],
  ['DISPUTE_SETTLEMENT', 'Dispute settlement'],
  ['IMMATERIAL_BALANCE', 'Immaterial balance'],
  ['OTHER', 'Other'],
] as const;

const refundReasons = [
  ['CUSTOMER_REQUEST', 'Customer request'],
  ['OVERPAYMENT', 'Overpayment'],
  ['DUPLICATE_PAYMENT', 'Duplicate payment'],
  ['ORDER_CANCELLATION', 'Order cancellation'],
  ['OTHER', 'Other'],
] as const;

type ArTab = 'open' | 'documents' | 'payments' | 'write-offs' | 'refunds';
type ExceptionAction = {
  kind: 'write-off' | 'refund';
  action: 'post' | 'approve' | 'release' | 'cancel' | 'reverse' | 'confirm-disbursement' | 'fail-disbursement';
  record: ReceivableWriteOff | CustomerRefund;
};

const statusColor = (status: string): 'default' | 'success' | 'warning' | 'error' | 'info' => {
  if (status === 'Posted' || status === 'Released') return 'success';
  if (status === 'Approved') return 'info';
  if (status === 'Cancelled' || status === 'Reversed') return 'error';
  if (status === 'Draft') return 'warning';
  return 'default';
};

export default function AccountsReceivablePage() {
  const theme = useTheme();
  const adjustmentFullScreen = useMediaQuery(theme.breakpoints.down('md'));
  const [searchParams] = useSearchParams();
  const targetDocumentId = Number(searchParams.get('documentId')) || null;
  const [tab, setTab] = useState<ArTab>(targetDocumentId ? 'documents' : 'open');
  const [selected, setSelected] = useState<ArOpenItem | null>(null);
  // The operator chooses a tenant-local accounting date. Once submitted, this date and the
  // idempotency key freeze together: an uncertain retry must replay the same financial command,
  // not silently replace the date with the browser's new current time.
  const [paymentOperation, setPaymentOperation] = useState<{ idempotencyKey: string; paymentDate: string } | null>(null);
  const [paymentSubmitted, setPaymentSubmitted] = useState(false);
  const [bankAccountId, setBankAccountId] = useState<number | ''>('');
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
  const [writingOff, setWritingOff] = useState<ArOpenItem | null>(null);
  const [writeOffAmount, setWriteOffAmount] = useState('');
  const [writeOffDate, setWriteOffDate] = useState(dayjs().format('YYYY-MM-DD'));
  const [writeOffReasonCode, setWriteOffReasonCode] = useState('BAD_DEBT');
  const [writeOffReason, setWriteOffReason] = useState('');
  const [writeOffEvidence, setWriteOffEvidence] = useState('');
  const [writeOffOperationKey, setWriteOffOperationKey] = useState('');
  const [writeOffSubmitted, setWriteOffSubmitted] = useState(false);
  const [refunding, setRefunding] = useState<CustomerPayment | null>(null);
  const [refundAmount, setRefundAmount] = useState('');
  const [refundDate, setRefundDate] = useState(dayjs().format('YYYY-MM-DD'));
  const [refundMethod, setRefundMethod] = useState('BankTransfer');
  const [refundDestination, setRefundDestination] = useState('');
  const [refundDestinationVerified, setRefundDestinationVerified] = useState(false);
  const [refundReasonCode, setRefundReasonCode] = useState('OVERPAYMENT');
  const [refundReason, setRefundReason] = useState('');
  const [refundEvidence, setRefundEvidence] = useState('');
  const [refundOperationKey, setRefundOperationKey] = useState('');
  const [refundSubmitted, setRefundSubmitted] = useState(false);
  const [exceptionAction, setExceptionAction] = useState<ExceptionAction | null>(null);
  const [exceptionReason, setExceptionReason] = useState('');
  const [exceptionEvidence, setExceptionEvidence] = useState('');
  const targetRowRef = useRef<HTMLTableRowElement | null>(null);
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canEditReceivables = hasPermission('Accounts Receivable', 'edit');
  const canCreateAdjustments = hasPermission('Receivable Adjustments', 'create');
  const canApproveAdjustments = hasPermission('Receivable Adjustments', 'edit');
  const canRecordPayment = hasPermission('Customer Payments', 'create');
  const canViewBankAccounts = hasPermission('Bank Accounts');
  const canViewPayments = hasPermission('Customer Payments');
  const canReversePayment = hasPermission('Customer Payments', 'edit');
  const canViewWriteOffs = hasPermission('Receivable Write-offs');
  const canCreateWriteOffs = hasPermission('Receivable Write-offs', 'create');
  const canEditWriteOffs = hasPermission('Receivable Write-offs', 'edit');
  const canViewRefunds = hasPermission('Customer Refunds');
  const canCreateRefunds = hasPermission('Customer Refunds', 'create');
  const canEditRefunds = hasPermission('Customer Refunds', 'edit');

  const openItems = useQuery({ queryKey: ['ar-open-items'], queryFn: () => commercialFinanceService.getOpenItems() });
  const documents = useQuery({ queryKey: ['receivable-documents'], queryFn: () => commercialFinanceService.getDocuments() });
  const payments = useQuery({
    queryKey: ['customer-payments'],
    queryFn: () => commercialFinanceService.getPayments(),
    enabled: canViewPayments,
  });
  const bankAccounts = useQuery({
    queryKey: ['active-bank-accounts'],
    queryFn: () => commercialFinanceService.getBankAccounts(),
    enabled: canRecordPayment && canViewBankAccounts,
  });
  const writeOffs = useQuery({
    queryKey: ['receivable-write-offs'],
    queryFn: () => commercialFinanceService.getWriteOffs(),
    enabled: canViewWriteOffs,
  });
  const refunds = useQuery({
    queryKey: ['customer-refunds'],
    queryFn: () => commercialFinanceService.getRefunds(),
    enabled: canViewRefunds,
  });
  const writeOffEligibility = useQuery({
    queryKey: ['write-off-eligibility', writingOff?.documentId],
    queryFn: () => commercialFinanceService.getWriteOffEligibility(writingOff!.documentId),
    enabled: Boolean(writingOff),
    refetchOnMount: 'always',
  });
  const refundEligibility = useQuery({
    queryKey: ['refund-eligibility', refunding?.id],
    queryFn: () => commercialFinanceService.getRefundEligibility(refunding!.id),
    enabled: Boolean(refunding),
    refetchOnMount: 'always',
  });
  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ['ar-open-items'] });
    void queryClient.invalidateQueries({ queryKey: ['receivable-documents'] });
    void queryClient.invalidateQueries({ queryKey: ['customer-payments'] });
    void queryClient.invalidateQueries({ queryKey: ['receivable-write-offs'] });
    void queryClient.invalidateQueries({ queryKey: ['customer-refunds'] });
    void queryClient.invalidateQueries({ queryKey: ['write-off-eligibility'] });
    void queryClient.invalidateQueries({ queryKey: ['refund-eligibility'] });
  };

  useEffect(() => {
    if (!targetDocumentId || !documents.data) return;
    setTab('documents');
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
      setTab('documents');
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
      bankAccountId: Number(bankAccountId),
      paymentDate: dayjs(paymentOperation!.paymentDate).startOf('day').toISOString(),
      amount: Number(amount),
      method,
      bankReference: reference || undefined,
      allocations: [{ receivableDocumentId: selected!.documentId, amount: Number(amount) }],
    }, paymentOperation!.idempotencyKey),
    onSuccess: () => {
      enqueueSnackbar('Payment posted and allocated', { variant: 'success' });
      setSelected(null); setPaymentOperation(null); setPaymentSubmitted(false); setBankAccountId(''); setAmount(''); setReference(''); refresh();
    },
    onError: (error: any) => {
      const status = error.response?.status;
      const resultIsUncertain = !status || status >= 500;
      if (!resultIsUncertain) {
        setPaymentSubmitted(false);
        setPaymentOperation(current => current
          ? { ...current, idempotencyKey: crypto.randomUUID() }
          : null);
      }
      enqueueSnackbar(
        error.response?.data?.detail ?? (resultIsUncertain
          ? 'The payment result is uncertain. Retry will use the same operation key and unchanged payment details.'
          : 'Payment could not be posted.'),
        { variant: 'error' },
      );
    },
  });

  const reversePayment = useMutation({
    mutationFn: () => commercialFinanceService.reversePayment(reversing!.id, reversing!.version, reversalReason.trim()),
    onSuccess: () => {
      enqueueSnackbar('Payment reversed', { variant: 'success' });
      setReversing(null); setReversalReason(''); refresh();
    },
    onError: (error: any) => enqueueSnackbar(error.response?.data?.detail ?? 'Payment could not be reversed', { variant: 'error' }),
  });

  const refreshAfterConflict = (error: any) => {
    if (error.response?.status === 409) refresh();
    return error.response?.status === 409;
  };

  const createWriteOff = useMutation({
    mutationFn: () => commercialFinanceService.createWriteOff({
      accountingDate: dayjs(writeOffDate).toISOString(),
      reasonCode: writeOffReasonCode,
      reason: writeOffReason.trim(),
      evidenceReference: writeOffEvidence.trim() || undefined,
      allocations: [{ receivableDocumentId: writingOff!.documentId, amount: Number(writeOffAmount) }],
    }, writeOffOperationKey),
    onSuccess: () => {
      enqueueSnackbar('Write-off draft created', { variant: 'success' });
      setWritingOff(null); setWriteOffSubmitted(false); refresh(); setTab('write-offs');
    },
    onError: (error: any) => {
      const uncertain = !error.response?.status || error.response.status >= 500;
      refreshAfterConflict(error);
      if (!uncertain) {
        setWriteOffSubmitted(false);
        setWriteOffOperationKey(crypto.randomUUID());
      }
      enqueueSnackbar(error.response?.data?.detail ?? (uncertain
        ? 'The result is uncertain. Retry will use the same operation key.'
        : 'Write-off draft could not be created.'), { variant: 'error' });
    },
  });

  const createRefund = useMutation({
    mutationFn: () => commercialFinanceService.createRefund({
      sourcePaymentId: refunding!.id,
      requestedExecutionDate: dayjs(refundDate).toISOString(),
      amount: Number(refundAmount),
      method: refundMethod,
      destinationReference: refundDestination.trim(),
      destinationVerified: refundDestinationVerified,
      reasonCode: refundReasonCode,
      reason: refundReason.trim(),
      evidenceReference: refundEvidence.trim() || undefined,
    }, refundOperationKey),
    onSuccess: () => {
      enqueueSnackbar('Refund draft created', { variant: 'success' });
      setRefunding(null); setRefundSubmitted(false); refresh(); setTab('refunds');
    },
    onError: (error: any) => {
      const uncertain = !error.response?.status || error.response.status >= 500;
      refreshAfterConflict(error);
      if (!uncertain) {
        setRefundSubmitted(false);
        setRefundOperationKey(crypto.randomUUID());
      }
      enqueueSnackbar(error.response?.data?.detail ?? (uncertain
        ? 'The result is uncertain. Retry will use the same operation key.'
        : 'Refund draft could not be created.'), { variant: 'error' });
    },
  });

  const transitionException = useMutation<ReceivableWriteOff | CustomerRefund, any, void>({
    mutationFn: () => {
      const record = exceptionAction!.record;
      const request = {
        expectedVersion: record.version,
        reason: exceptionReason.trim() || undefined,
        evidenceReference: exceptionEvidence.trim() || undefined,
      };
      if (exceptionAction!.action === 'confirm-disbursement' || exceptionAction!.action === 'fail-disbursement') {
        return commercialFinanceService.recordRefundDisbursement(
          record.id,
          exceptionAction!.action === 'confirm-disbursement',
          { expectedVersion: record.version, providerReference: exceptionEvidence.trim(), reason: exceptionReason.trim() || undefined },
        );
      }
      return exceptionAction!.kind === 'write-off'
        ? commercialFinanceService.transitionWriteOff(record.id, exceptionAction!.action as 'post' | 'cancel' | 'reverse', request)
        : commercialFinanceService.transitionRefund(record.id, exceptionAction!.action as 'approve' | 'release' | 'cancel' | 'reverse', request);
    },
    onSuccess: () => {
      enqueueSnackbar(`${exceptionAction!.kind === 'write-off' ? 'Write-off' : 'Refund'} ${exceptionAction!.action} completed`, { variant: 'success' });
      setExceptionAction(null); setExceptionReason(''); setExceptionEvidence(''); refresh();
    },
    onError: (error: any) => {
      if (refreshAfterConflict(error)) setExceptionAction(null);
      enqueueSnackbar(error.response?.data?.detail ?? 'The governed action could not be completed', { variant: 'error' });
    },
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

  const eligibleBankAccounts = useMemo(() => (bankAccounts.data ?? []).filter(account =>
    account.status === 'Active' && (!selected?.currencyId || account.currencyId === selected.currencyId)),
  [bankAccounts.data, selected?.currencyId]);

  useEffect(() => {
    if (!selected || paymentSubmitted || bankAccountId !== '' || eligibleBankAccounts.length !== 1) return;
    setBankAccountId(eligibleBankAccounts[0].id);
  }, [bankAccountId, eligibleBankAccounts, paymentSubmitted, selected]);

  const openPaymentDialog = (item: ArOpenItem) => {
    payment.reset();
    setSelected(item);
    setAmount(String(item.outstandingAmount));
    setReference('');
    setBankAccountId('');
    setPaymentSubmitted(false);
    setPaymentOperation({ idempotencyKey: crypto.randomUUID(), paymentDate: dayjs().format('YYYY-MM-DD') });
  };
  const closePaymentDialog = () => {
    // An uncertain response may already have committed. Keep this exact command available for a
    // keyed replay instead of letting the operator abandon it and unknowingly create a new one.
    if (payment.isPending || paymentSubmitted) return;
    setSelected(null); setPaymentOperation(null); setPaymentSubmitted(false); setBankAccountId(''); setAmount(''); setReference('');
  };

  const submitPayment = () => {
    setPaymentSubmitted(true);
    payment.mutate();
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
    // `adjustmentSubmitted` remains true only when the result is uncertain. The server may already
    // have committed, so closing here would discard the only key capable of a safe replay.
    if (createAdjustment.isPending || adjustmentSubmitted) return;
    setAdjustingInvoice(null);
  };
  const submitAdjustment = () => {
    setAdjustmentSubmitted(true);
    createAdjustment.mutate();
  };

  const openWriteOffDialog = (item: ArOpenItem) => {
    createWriteOff.reset();
    setWritingOff(item);
    setWriteOffAmount(String(item.outstandingAmount));
    setWriteOffDate(dayjs().format('YYYY-MM-DD'));
    setWriteOffReasonCode('BAD_DEBT'); setWriteOffReason(''); setWriteOffEvidence('');
    setWriteOffOperationKey(crypto.randomUUID()); setWriteOffSubmitted(false);
  };
  const closeWriteOffDialog = () => {
    if (createWriteOff.isPending || writeOffSubmitted) return;
    setWritingOff(null); setWriteOffSubmitted(false);
  };
  const openRefundDialog = (item: CustomerPayment) => {
    createRefund.reset();
    setRefunding(item);
    setRefundAmount(String(item.unappliedAmount));
    setRefundDate(dayjs().format('YYYY-MM-DD'));
    setRefundMethod('BankTransfer'); setRefundDestination(''); setRefundDestinationVerified(false);
    setRefundReasonCode('OVERPAYMENT'); setRefundReason(''); setRefundEvidence('');
    setRefundOperationKey(crypto.randomUUID()); setRefundSubmitted(false);
  };
  const closeRefundDialog = () => {
    if (createRefund.isPending || refundSubmitted) return;
    setRefunding(null); setRefundSubmitted(false);
  };
  const openExceptionAction = (action: ExceptionAction) => {
    transitionException.reset(); setExceptionAction(action); setExceptionReason(''); setExceptionEvidence('');
  };

  const writeOffAvailable = writeOffEligibility.data?.availableAmount ?? 0;
  const writeOffProjected = roundMoney((writeOffEligibility.data?.currentBalance ?? 0) - Number(writeOffAmount || 0));
  const refundAvailable = refundEligibility.data?.availableAmount ?? 0;
  const refundProjected = roundMoney(refundAvailable - Number(refundAmount || 0));
  const writeOffAmountValid = Number(writeOffAmount) > 0 && Number(writeOffAmount) <= writeOffAvailable;
  const refundAmountValid = Number(refundAmount) > 0 && Number(refundAmount) <= refundAvailable;
  const exceptionNeedsReason = exceptionAction?.action === 'cancel' || exceptionAction?.action === 'reverse' || exceptionAction?.action === 'fail-disbursement';
  const exceptionNeedsEvidence = exceptionAction?.action === 'reverse' || exceptionAction?.action === 'confirm-disbursement' || exceptionAction?.action === 'fail-disbursement';

  const tabs = [
    { value: 'open' as const, label: `Open items (${openItems.data?.length ?? 0})` },
    { value: 'documents' as const, label: `Documents (${documents.data?.length ?? 0})` },
    ...(canViewPayments ? [{ value: 'payments' as const, label: `Payments (${payments.data?.length ?? 0})` }] : []),
    ...(canViewWriteOffs ? [{ value: 'write-offs' as const, label: `Write-offs (${writeOffs.data?.length ?? 0})` }] : []),
    ...(canViewRefunds ? [{ value: 'refunds' as const, label: `Refunds (${refunds.data?.length ?? 0})` }] : []),
  ];

  if (openItems.isLoading || documents.isLoading || (canViewPayments && payments.isLoading) ||
    (canViewWriteOffs && writeOffs.isLoading) || (canViewRefunds && refunds.isLoading)) return <Box sx={{ display: 'grid', placeItems: 'center', minHeight: 400 }}><CircularProgress /></Box>;
  if (openItems.isError || documents.isError || (canViewPayments && payments.isError) ||
    (canViewWriteOffs && writeOffs.isError) || (canViewRefunds && refunds.isError)) return <Alert severity="error">Accounts receivable data could not be loaded.</Alert>;

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
        {tabs.map(item => <Tab key={item.value} value={item.value} label={item.label} />)}
      </Tabs>

      <Paper variant="outlined" sx={{ borderRadius: 1, overflow: 'hidden' }}>
        {tab === 'open' ? (
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
                <TableCell align="center"><Stack direction="row" spacing={0.5} sx={{ justifyContent: 'center' }}>
                  {canRecordPayment && <Tooltip title="Record payment"><IconButton aria-label={`Record payment for ${item.documentNumber}`} size="small" onClick={() => openPaymentDialog(item)}><CreditCard size={18} /></IconButton></Tooltip>}
                  {canCreateWriteOffs && ['Invoice', 'DebitNote'].includes(item.documentType) && <Tooltip title="Create write-off"><IconButton aria-label={`Create write-off for ${item.documentNumber}`} size="small" onClick={() => openWriteOffDialog(item)}><FileMinus2 size={18} /></IconButton></Tooltip>}
                </Stack></TableCell>
              </TableRow>
            ))}{!openItems.data?.length && <TableRow><TableCell colSpan={8} align="center" sx={{ py: 6 }}>No open receivables.</TableCell></TableRow>}</TableBody>
          </Table></TableContainer>
        ) : tab === 'documents' ? (
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
                  {canRecordPayment && document.status === 'Issued' && document.outstandingAmount > 0 &&
                    <Tooltip title="Record payment"><IconButton aria-label={`Record payment for ${document.documentNumber}`} size="small" onClick={() => openPaymentDialog({
                      documentId: document.id,
                      documentNumber: document.documentNumber ?? `Document #${document.id}`,
                      documentType: document.documentType,
                      customerId: document.customerId,
                      commercialCaseId: document.commercialCaseId,
                      currencyId: document.currencyId,
                      currencyCode: document.currencyCode,
                      documentDate: document.documentDate,
                      dueDate: document.dueDate,
                      originalAmount: document.totalAmount,
                      outstandingAmount: document.outstandingAmount,
                      daysPastDue: Math.max(0, dayjs().startOf('day').diff(dayjs(document.dueDate).startOf('day'), 'day')),
                      agingBucket: 'Current',
                    })}><CreditCard size={18} /></IconButton></Tooltip>}
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
        ) : tab === 'payments' ? (
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
                <TableCell align="center"><Stack direction="row" spacing={0.5} sx={{ justifyContent: 'center' }}>
                  {canCreateRefunds && item.status === 'Posted' && item.unappliedAmount > 0 && <Tooltip title="Create customer refund"><IconButton aria-label={`Create customer refund from ${item.receiptNumber}`} size="small" onClick={() => openRefundDialog(item)}><ReceiptText size={18} /></IconButton></Tooltip>}
                  {canReversePayment && item.status === 'Posted' && <Tooltip title="Reverse payment"><span><IconButton size="small" disabled={reversePayment.isPending} onClick={() => setReversing(item)}><RotateCcw size={18} /></IconButton></span></Tooltip>}
                </Stack></TableCell>
              </TableRow>
            ))}{!payments.data?.length && <TableRow><TableCell colSpan={9} align="center" sx={{ py: 6 }}>No payment history.</TableCell></TableRow>}</TableBody>
          </Table></TableContainer>
        ) : tab === 'write-offs' ? (
          <TableContainer sx={{ overflowX: 'auto' }}><Table size="small" sx={{ minWidth: 1040 }}>
            <TableHead><TableRow><TableCell>Write-off</TableCell><TableCell>Allocations</TableCell><TableCell>Accounting date</TableCell><TableCell>Customer</TableCell><TableCell>Currency</TableCell><TableCell>Status</TableCell><TableCell>Posting</TableCell><TableCell align="right">Amount</TableCell><TableCell align="right">Before / after</TableCell><TableCell align="center">Action</TableCell></TableRow></TableHead>
            <TableBody>{(writeOffs.data ?? []).map(item => {
              return <TableRow key={item.id} hover>
                <TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{item.writeOffNumber ?? `Draft #${item.id}`}</Typography><Typography variant="caption" color="text.secondary" sx={{ display: 'block', maxWidth: 190, overflowWrap: 'anywhere' }}>{item.reason}</Typography></TableCell>
                <TableCell>{item.allocations.map(allocation => <Typography key={allocation.id} variant="body2">{allocation.documentNumber}: {money(allocation.amount)}</Typography>)}</TableCell>
                <TableCell>{dayjs(item.accountingDate).format('DD MMM YYYY')}</TableCell>
                <TableCell>Customer {item.customerId}</TableCell>
                <TableCell>{item.currencyCode || (item.currencyId == null ? 'Unassigned' : `Currency ${item.currencyId}`)}</TableCell>
                <TableCell><Chip size="small" label={item.status} color={statusColor(item.status)} /></TableCell>
                <TableCell><Typography variant="body2">{item.postingStatus}</Typography>{item.journalReference && <Typography variant="caption" color="text.secondary">{item.journalReference}</Typography>}</TableCell>
                <TableCell align="right" sx={{ fontWeight: 700 }}>{money(item.totalAmount)}</TableCell>
                <TableCell align="right">{item.allocations.map(allocation => <Typography key={allocation.id} variant="body2">{money(allocation.balanceBefore)} to {money(allocation.balanceAfter)}</Typography>)}</TableCell>
                <TableCell align="center">{canEditWriteOffs && <Stack direction="row" spacing={0.25} sx={{ justifyContent: 'center' }}>
                  {item.status === 'Draft' && <><Tooltip title="Post write-off"><IconButton size="small" onClick={() => openExceptionAction({ kind: 'write-off', action: 'post', record: item })}><Check size={18} /></IconButton></Tooltip><Tooltip title="Cancel draft"><IconButton size="small" color="error" onClick={() => openExceptionAction({ kind: 'write-off', action: 'cancel', record: item })}><Ban size={18} /></IconButton></Tooltip></>}
                  {item.status === 'Posted' && <Tooltip title="Reverse write-off"><IconButton size="small" onClick={() => openExceptionAction({ kind: 'write-off', action: 'reverse', record: item })}><Undo2 size={18} /></IconButton></Tooltip>}
                </Stack>}</TableCell>
              </TableRow>;
            })}{!writeOffs.data?.length && <TableRow><TableCell colSpan={10} align="center" sx={{ py: 6 }}>No write-offs.</TableCell></TableRow>}</TableBody>
          </Table></TableContainer>
        ) : (
          <TableContainer sx={{ overflowX: 'auto' }}><Table size="small" sx={{ minWidth: 1100 }}>
            <TableHead><TableRow><TableCell>Refund</TableCell><TableCell>Source receipt</TableCell><TableCell>Execution date</TableCell><TableCell>Customer</TableCell><TableCell>Currency</TableCell><TableCell>Method</TableCell><TableCell>Status</TableCell><TableCell>Posting</TableCell><TableCell align="right">Amount</TableCell><TableCell align="center">Action</TableCell></TableRow></TableHead>
            <TableBody>{(refunds.data ?? []).map(item => <TableRow key={item.id} hover>
              <TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{item.refundNumber ?? `Draft #${item.id}`}</Typography><Typography variant="caption" color="text.secondary" sx={{ display: 'block', maxWidth: 190, overflowWrap: 'anywhere' }}>{item.reason}</Typography></TableCell>
              <TableCell>{item.receiptNumber}</TableCell>
              <TableCell>{dayjs(item.requestedExecutionDate).format('DD MMM YYYY')}</TableCell>
              <TableCell>Customer {item.customerId}</TableCell>
              <TableCell>{item.currencyCode || (item.currencyId == null ? 'Unassigned' : `Currency ${item.currencyId}`)}</TableCell>
              <TableCell>{item.method}</TableCell>
              <TableCell><Chip size="small" label={item.status} color={statusColor(item.status)} /></TableCell>
              <TableCell><Typography variant="body2">{item.postingStatus}</Typography>{item.journalReference && <Typography variant="caption" color="text.secondary">{item.journalReference}</Typography>}</TableCell>
              <TableCell align="right" sx={{ fontWeight: 700 }}>{money(item.amount)}</TableCell>
              <TableCell align="center">{canEditRefunds && <Stack direction="row" spacing={0.25} sx={{ justifyContent: 'center' }}>
                {item.status === 'Draft' && <><Tooltip title="Approve refund"><IconButton size="small" onClick={() => openExceptionAction({ kind: 'refund', action: 'approve', record: item })}><ShieldCheck size={18} /></IconButton></Tooltip><Tooltip title="Cancel draft"><IconButton size="small" color="error" onClick={() => openExceptionAction({ kind: 'refund', action: 'cancel', record: item })}><Ban size={18} /></IconButton></Tooltip></>}
                {item.status === 'Approved' && <><Tooltip title="Release refund"><IconButton size="small" onClick={() => openExceptionAction({ kind: 'refund', action: 'release', record: item })}><Send size={18} /></IconButton></Tooltip><Tooltip title="Cancel approved refund"><IconButton size="small" color="error" onClick={() => openExceptionAction({ kind: 'refund', action: 'cancel', record: item })}><Ban size={18} /></IconButton></Tooltip></>}
                {item.status === 'Released' && item.postingStatus === 'PendingDisbursement' && <><Tooltip title="Confirm provider disbursement"><IconButton size="small" onClick={() => openExceptionAction({ kind: 'refund', action: 'confirm-disbursement', record: item })}><CheckCircle2 size={18} /></IconButton></Tooltip><Tooltip title="Record failed disbursement"><IconButton size="small" color="error" onClick={() => openExceptionAction({ kind: 'refund', action: 'fail-disbursement', record: item })}><Ban size={18} /></IconButton></Tooltip></>}
                {item.status === 'Released' && item.postingStatus === 'Failed' && <Tooltip title="Restore funds after failed disbursement"><IconButton size="small" onClick={() => openExceptionAction({ kind: 'refund', action: 'reverse', record: item })}><Undo2 size={18} /></IconButton></Tooltip>}
              </Stack>}</TableCell>
            </TableRow>)}{!refunds.data?.length && <TableRow><TableCell colSpan={10} align="center" sx={{ py: 6 }}>No customer refunds.</TableCell></TableRow>}</TableBody>
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
          <Button disabled={createAdjustment.isPending || adjustmentSubmitted} onClick={closeAdjustmentDialog}>Cancel</Button>
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
            {!canViewBankAccounts && <Alert severity="error">Bank Accounts view permission is required to select the governed deposit account.</Alert>}
            {canViewBankAccounts && bankAccounts.isLoading && <Alert icon={<CircularProgress size={18} />} severity="info">Loading authorized bank accounts...</Alert>}
            {canViewBankAccounts && bankAccounts.isError && <Alert severity="error" action={<Button size="small" onClick={() => void bankAccounts.refetch()}>Retry</Button>}>Authorized bank accounts could not be loaded.</Alert>}
            {canViewBankAccounts && !bankAccounts.isLoading && !bankAccounts.isError && eligibleBankAccounts.length === 0 && <Alert severity="warning">No active governed bank account matches this receivable currency. Configure one before posting cash.</Alert>}
            {canViewBankAccounts && eligibleBankAccounts.length > 0 && (
              <TextField
                select
                required
                label="Deposit bank account"
                value={bankAccountId}
                disabled={paymentSubmitted}
                onChange={event => setBankAccountId(Number(event.target.value))}
                helperText="Only active accounts you are authorized to view are listed."
              >
                {eligibleBankAccounts.map(account => (
                  <MenuItem key={account.id} value={account.id}>
                    {account.name} · {account.institutionName} · {account.maskedAccountNumber}
                  </MenuItem>
                ))}
              </TextField>
            )}
            {paymentSubmitted && payment.isError && <Alert severity="warning">Retry safely with the same operation key and unchanged payment details. A duplicate receipt will not be created.</Alert>}
            <TextField
              required
              type="date"
              label="Payment date"
              value={paymentOperation?.paymentDate ?? ''}
              disabled={paymentSubmitted}
              onChange={event => setPaymentOperation(current => current
                ? { ...current, paymentDate: event.target.value }
                : current)}
              helperText="Recorded as the selected local accounting date."
              slotProps={{
                inputLabel: { shrink: true },
                htmlInput: { 'aria-label': 'Payment date' },
              }}
            />
            <TextField label="Amount" type="number" value={amount} disabled={paymentSubmitted} onChange={event => setAmount(event.target.value)} slotProps={{ htmlInput: { min: 0.01, max: selected?.outstandingAmount, step: 0.01 } }} />
            <TextField select label="Method" value={method} disabled={paymentSubmitted} onChange={event => setMethod(event.target.value)}><MenuItem value="BankTransfer">Bank transfer</MenuItem><MenuItem value="Card">Card</MenuItem><MenuItem value="Cheque">Cheque</MenuItem><MenuItem value="Cash">Cash</MenuItem></TextField>
            <TextField label="Bank reference" value={reference} disabled={paymentSubmitted} onChange={event => setReference(event.target.value)} />
          </Stack>
        </DialogContent>
        <DialogActions><Button disabled={payment.isPending || paymentSubmitted} onClick={closePaymentDialog}>Cancel</Button><Button variant="contained" disabled={payment.isPending || !paymentOperation?.paymentDate || bankAccountId === '' || Number(amount) <= 0 || Number(amount) > (selected?.outstandingAmount ?? 0)} onClick={submitPayment}>{paymentSubmitted && payment.isError ? 'Retry safely' : 'Post payment'}</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(reversing)} onClose={closeReversalDialog} fullWidth maxWidth="xs">
        <DialogTitle>Reverse payment</DialogTitle><Divider />
        <DialogContent sx={{ pt: 2 }}>
          <Stack spacing={2}>
            <Alert severity="warning">This governed action reverses receipt {reversing?.receiptNumber} and restores its invoice balances.</Alert>
            <TextField label="Reason" value={reversalReason} onChange={event => setReversalReason(event.target.value)} required multiline minRows={3} />
          </Stack>
        </DialogContent>
        <DialogActions><Button disabled={reversePayment.isPending} onClick={closeReversalDialog}>Cancel</Button><Button color="error" variant="contained" disabled={reversePayment.isPending || !reversalReason.trim()} onClick={() => reversePayment.mutate()}>Reverse payment</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(cancelling)} onClose={closeCancellationDialog} fullWidth maxWidth="xs">
        <DialogTitle>Cancel {documentTypeLabel(cancelling?.documentType ?? 'Invoice')} draft</DialogTitle><Divider />
        <DialogContent sx={{ pt: 2 }}>
          <Stack spacing={2}>
            <Alert severity="warning">This {documentTypeLabel(cancelling?.documentType ?? 'Invoice')} draft will no longer be available for issuing.</Alert>
            <TextField label="Reason" value={cancellationReason} onChange={event => setCancellationReason(event.target.value)} required multiline minRows={3} helperText={`${cancellationReason.length}/500`} slotProps={{ htmlInput: { maxLength: 500 } }} />
          </Stack>
        </DialogContent>
        <DialogActions><Button disabled={cancelDocument.isPending} onClick={closeCancellationDialog}>Keep draft</Button><Button color="error" variant="contained" disabled={cancelDocument.isPending || !cancellationReason.trim()} onClick={() => cancelDocument.mutate()}>Cancel draft</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(writingOff)} onClose={closeWriteOffDialog} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Create write-off draft</DialogTitle><Divider />
        <DialogContent sx={{ p: 2.5 }}>
          <Stack spacing={2}>
            <Box><Typography variant="caption" color="text.secondary">Receivable document</Typography><Typography sx={{ fontWeight: 800 }}>{writingOff?.documentNumber}</Typography><Typography variant="body2" color="text.secondary">{writingOff?.currencyCode || 'Currency unassigned'} · Customer {writingOff?.customerId}</Typography></Box>
            {writeOffEligibility.isLoading && <Alert icon={<CircularProgress size={18} />} severity="info">Refreshing governed write-off availability...</Alert>}
            {writeOffEligibility.isError && <Alert severity="error" action={<Button size="small" onClick={() => void writeOffEligibility.refetch()}>Retry</Button>}>Current availability could not be verified.</Alert>}
            {writeOffSubmitted && createWriteOff.isError && <Alert severity="warning">Retry safely with the same operation key if the prior result is uncertain. A duplicate draft will not be created.</Alert>}
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(3, minmax(0, 1fr))' }, borderBlock: '1px solid', borderColor: 'divider' }}>
              {[['Current balance', writeOffEligibility.data?.currentBalance ?? 0], ['Pending drafts', writeOffEligibility.data?.pendingAmount ?? 0], ['Available', writeOffAvailable]].map(([label, value], index) => <Box key={String(label)} sx={{ p: 1.25, borderRight: index < 2 ? { sm: '1px solid' } : 0, borderTop: index ? { xs: '1px solid', sm: 0 } : 0, borderColor: 'divider' }}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography sx={{ fontWeight: 800 }}>{money(Number(value))}</Typography></Box>)}
            </Box>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 2 }}>
              <TextField size="small" type="date" label="Accounting date" value={writeOffDate} disabled={writeOffSubmitted} onChange={event => setWriteOffDate(event.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              <TextField size="small" type="number" required label="Write-off amount" value={writeOffAmount} disabled={writeOffSubmitted} error={Boolean(writeOffAmount) && !writeOffAmountValid} helperText={writeOffAmount && !writeOffAmountValid ? `Maximum ${money(writeOffAvailable)}` : ' '} onChange={event => setWriteOffAmount(event.target.value)} slotProps={{ htmlInput: { min: 0.01, max: writeOffAvailable, step: 0.01 } }} />
              <TextField select size="small" required label="Reason code" value={writeOffReasonCode} disabled={writeOffSubmitted} onChange={event => setWriteOffReasonCode(event.target.value)}>{writeOffReasons.map(([value, label]) => <MenuItem key={value} value={value}>{label}</MenuItem>)}</TextField>
              <TextField size="small" label="Evidence reference" value={writeOffEvidence} disabled={writeOffSubmitted} onChange={event => setWriteOffEvidence(event.target.value)} slotProps={{ htmlInput: { maxLength: 250 } }} />
            </Box>
            <TextField size="small" required multiline minRows={2} label="Business reason" value={writeOffReason} disabled={writeOffSubmitted} onChange={event => setWriteOffReason(event.target.value)} helperText={`${writeOffReason.length}/500 · minimum 20 characters`} slotProps={{ htmlInput: { maxLength: 500 } }} />
            <Box sx={{ borderBlock: '1px solid', borderColor: 'divider', p: 1.5 }}><Typography variant="caption" color="text.secondary">Projected document balance</Typography><Typography variant="h6" color={writeOffProjected < 0 ? 'error.main' : 'text.primary'} sx={{ fontWeight: 800 }}>{money(writeOffProjected)}</Typography></Box>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 2.5, py: 1.5, borderTop: '1px solid', borderColor: 'divider' }}><Button disabled={createWriteOff.isPending || writeOffSubmitted} onClick={closeWriteOffDialog}>Cancel</Button><Button variant="contained" disabled={createWriteOff.isPending || writeOffEligibility.isFetching || !writeOffAmountValid || !writeOffDate || writeOffReason.trim().length < 20} onClick={() => { setWriteOffSubmitted(true); createWriteOff.mutate(); }}>{writeOffSubmitted && createWriteOff.isError ? 'Retry safely' : 'Create draft'}</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(refunding)} onClose={closeRefundDialog} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Create customer refund draft</DialogTitle><Divider />
        <DialogContent sx={{ p: 2.5 }}>
          <Stack spacing={2}>
            <Box><Typography variant="caption" color="text.secondary">Source receipt</Typography><Typography sx={{ fontWeight: 800 }}>{refunding?.receiptNumber}</Typography><Typography variant="body2" color="text.secondary">{refunding?.currencyCode || 'Currency unassigned'} · Customer {refunding?.customerId}</Typography></Box>
            {refundEligibility.isLoading && <Alert icon={<CircularProgress size={18} />} severity="info">Refreshing refundable receipt balance...</Alert>}
            {refundEligibility.isError && <Alert severity="error" action={<Button size="small" onClick={() => void refundEligibility.refetch()}>Retry</Button>}>Current refund availability could not be verified.</Alert>}
            {refundSubmitted && createRefund.isError && <Alert severity="warning">Retry safely with the same operation key if the prior result is uncertain. A duplicate draft will not be created.</Alert>}
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', sm: 'repeat(4, minmax(0, 1fr))' }, borderBlock: '1px solid', borderColor: 'divider' }}>
              {[['Receipt', refundEligibility.data?.paymentAmount ?? 0], ['Allocated', refundEligibility.data?.allocatedAmount ?? 0], ['Reserved', refundEligibility.data?.reservedAmount ?? 0], ['Available', refundAvailable]].map(([label, value], index) => <Box key={String(label)} sx={{ p: 1.25, borderRight: index % 2 === 0 ? '1px solid' : { xs: 0, sm: index < 3 ? '1px solid' : 0 }, borderTop: index > 1 ? { xs: '1px solid', sm: 0 } : 0, borderColor: 'divider' }}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography sx={{ fontWeight: 800 }}>{money(Number(value))}</Typography></Box>)}
            </Box>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 2 }}>
              <TextField size="small" type="date" label="Requested execution date" value={refundDate} disabled={refundSubmitted} onChange={event => setRefundDate(event.target.value)} slotProps={{ inputLabel: { shrink: true } }} />
              <TextField size="small" type="number" required label="Refund amount" value={refundAmount} disabled={refundSubmitted} error={Boolean(refundAmount) && !refundAmountValid} helperText={refundAmount && !refundAmountValid ? `Maximum ${money(refundAvailable)}` : ' '} onChange={event => setRefundAmount(event.target.value)} slotProps={{ htmlInput: { min: 0.01, max: refundAvailable, step: 0.01 } }} />
              <TextField select size="small" required label="Refund method" value={refundMethod} disabled={refundSubmitted} onChange={event => setRefundMethod(event.target.value)}><MenuItem value="BankTransfer">Bank transfer</MenuItem><MenuItem value="CardReversal">Card reversal</MenuItem><MenuItem value="Cheque">Cheque</MenuItem></TextField>
              <TextField size="small" required label="Provider destination token" value={refundDestination} disabled={refundSubmitted} onChange={event => setRefundDestination(event.target.value)} helperText="Use token: followed by the approved provider token. Raw bank or card details are rejected." slotProps={{ htmlInput: { maxLength: 186 } }} />
              <TextField select size="small" required label="Reason code" value={refundReasonCode} disabled={refundSubmitted} onChange={event => setRefundReasonCode(event.target.value)}>{refundReasons.map(([value, label]) => <MenuItem key={value} value={value}>{label}</MenuItem>)}</TextField>
              <TextField size="small" label="Evidence reference" value={refundEvidence} disabled={refundSubmitted} onChange={event => setRefundEvidence(event.target.value)} slotProps={{ htmlInput: { maxLength: 250 } }} />
            </Box>
            <TextField size="small" required multiline minRows={2} label="Business reason" value={refundReason} disabled={refundSubmitted} onChange={event => setRefundReason(event.target.value)} helperText={`${refundReason.length}/500 · minimum 20 characters`} slotProps={{ htmlInput: { maxLength: 500 } }} />
            <FormControlLabel control={<Checkbox checked={refundDestinationVerified} disabled={refundSubmitted} onChange={event => setRefundDestinationVerified(event.target.checked)} />} label="I verified the destination against approved customer payment instructions" />
            <Box sx={{ borderBlock: '1px solid', borderColor: 'divider', p: 1.5 }}><Typography variant="caption" color="text.secondary">Projected refundable balance</Typography><Typography variant="h6" color={refundProjected < 0 ? 'error.main' : 'text.primary'} sx={{ fontWeight: 800 }}>{money(refundProjected)}</Typography></Box>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 2.5, py: 1.5, borderTop: '1px solid', borderColor: 'divider' }}><Button disabled={createRefund.isPending || refundSubmitted} onClick={closeRefundDialog}>Cancel</Button><Button variant="contained" disabled={createRefund.isPending || refundEligibility.isFetching || !refundAmountValid || !refundDate || !/^token:[A-Za-z0-9_-]{8,180}$/.test(refundDestination.trim()) || !refundDestinationVerified || refundReason.trim().length < 20} onClick={() => { setRefundSubmitted(true); createRefund.mutate(); }}>{refundSubmitted && createRefund.isError ? 'Retry safely' : 'Create draft'}</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(exceptionAction)} onClose={() => !transitionException.isPending && setExceptionAction(null)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>{exceptionAction?.action ? `${exceptionAction.action[0].toUpperCase()}${exceptionAction.action.slice(1)}` : ''} {exceptionAction?.kind}</DialogTitle><Divider />
        <DialogContent sx={{ pt: 2.5 }}>
          <Stack spacing={2}>
            <Alert severity={exceptionAction?.action === 'cancel' || exceptionAction?.action === 'reverse' ? 'warning' : 'info'}>
              {exceptionAction?.action === 'post' && 'Posting applies the write-off to the receivable balance. A different authorized operator must perform this action.'}
              {exceptionAction?.action === 'approve' && 'Approval reserves refundable receipt funds. The maker cannot approve their own request.'}
              {exceptionAction?.action === 'release' && 'Release submits the approved refund for disbursement. A third independent operator is required.'}
              {exceptionAction?.action === 'confirm-disbursement' && 'Confirm only after the provider acknowledges successful settlement. Settled refunds cannot restore customer credit.'}
              {exceptionAction?.action === 'fail-disbursement' && 'Record a provider-confirmed failure before restoring the reserved customer funds.'}
              {exceptionAction?.action === 'cancel' && 'Cancellation closes this request without applying or releasing funds.'}
              {exceptionAction?.action === 'reverse' && 'Reversal restores the financial position and requires independent evidence.'}
            </Alert>
            {exceptionNeedsReason && <TextField required multiline minRows={3} label="Reason" value={exceptionReason} onChange={event => setExceptionReason(event.target.value)} helperText={`${exceptionReason.length}/500 · minimum 20 characters`} slotProps={{ htmlInput: { maxLength: 500 } }} />}
            {exceptionNeedsEvidence && <TextField required label={exceptionAction?.action.includes('disbursement') ? 'Provider reference' : 'Evidence reference'} value={exceptionEvidence} onChange={event => setExceptionEvidence(event.target.value)} slotProps={{ htmlInput: { maxLength: exceptionAction?.action.includes('disbursement') ? 100 : 250 } }} />}
          </Stack>
        </DialogContent>
        <DialogActions><Button disabled={transitionException.isPending} onClick={() => setExceptionAction(null)}>Back</Button><Button variant="contained" color={exceptionAction?.action === 'cancel' || exceptionAction?.action === 'reverse' || exceptionAction?.action === 'fail-disbursement' ? 'error' : 'primary'} disabled={transitionException.isPending || (exceptionNeedsReason && exceptionReason.trim().length < 20) || (exceptionNeedsEvidence && !exceptionEvidence.trim()) || (Boolean(exceptionAction?.action.includes('disbursement')) && !/^[A-Za-z0-9][A-Za-z0-9._:/-]{7,99}$/.test(exceptionEvidence.trim()))} onClick={() => transitionException.mutate()}>Confirm {exceptionAction?.action}</Button></DialogActions>
      </Dialog>
    </Box>
  );
}
