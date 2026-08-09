import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Divider,
  FormControl,
  FormControlLabel,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  Add as AddIcon,
  CheckCircle as ConfirmIcon,
  DeleteOutlined as DeleteIcon,
  Refresh as RetryIcon,
  ShoppingCartCheckout as ConvertIcon,
  CancelOutlined as CancelIcon,
  Link as MatchIcon,
} from '@mui/icons-material';
import customerAwardService, {
  createCustomerAwardCommandIdentity,
  type CustomerAward,
  type CustomerAwardOrder,
  type CustomerPurchaseOrder,
  type CustomerPurchaseOrderDocumentExtraction,
  type PurchaseOrderLineMatchProposal,
  type QuoteAwardProjection,
} from '../../../../api/services/customerAwardService';
import CustomerPoDocumentIntake from './CustomerPoDocumentIntake';

const EPSILON = 0.000001;

export interface CustomerAwardQuoteLine {
  id: number;
  productId?: number | null;
  productName?: string | null;
  description: string;
  quantity: number;
  uomId?: number | null;
  uomCode?: string | null;
  unitPrice: number;
}

export interface CustomerAwardQuote {
  id: number;
  quoteNo: string;
  version: number;
  commercialCaseId: number;
  customerId: number;
  currencyId: number;
  currencyCode?: string | null;
  lines: CustomerAwardQuoteLine[];
}

export interface CustomerAwardCompletion {
  purchaseOrder?: CustomerPurchaseOrder;
  award: CustomerAward;
  order?: CustomerAwardOrder;
}

export interface CustomerAwardWorkspaceProps {
  quote: CustomerAwardQuote;
  initialProjection?: QuoteAwardProjection;
  onCancel?: () => void;
  onCompleted?: (result: CustomerAwardCompletion) => void;
  onBusyChange?: (busy: boolean) => void;
}

/**
 * One line of the BUYER's purchase order.
 *
 * FR-COM-04: every field here is transcribed from the customer's document. None of them may be
 * defaulted from our own quote line — the discrepancy engine compares this capture against the
 * quotation, so a value seeded from the quotation makes the engine compare the system against
 * itself and "no discrepancy" becomes the only possible answer. The quoted figures are displayed
 * beside these inputs for comparison, never inside them.
 */
interface CaptureLine {
  clientId: string;
  externalLineReference: string;
  description: string;
  customerItemCode: string;
  manufacturerName: string;
  manufacturerPartNumber: string;
  quoteItemId: number | '';
  orderedQuantity: string;
  awardedQuantity: string;
  unitPrice: string;
}

type RowErrorField = 'reference' | 'description' | 'quoteItem' | 'ordered' | 'awarded' | 'unitPrice';

interface ValidationResult {
  form?: string;
  rows: Record<string, Partial<Record<RowErrorField, string>>>;
}

const today = () => new Date().toISOString().slice(0, 10);
const normalizeReference = (value: string) => value.trim().replace(/\s+/g, ' ').toUpperCase();
const numberValue = (value: string) => Number(value);
const validPositive = (value: string) => Number.isFinite(numberValue(value)) && numberValue(value) > 0;
const trimmedOrNull = (value: string) => (value.trim() ? value.trim() : null);

const newClientId = () => {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  return `${Date.now()}-${Math.random()}`;
};

const apiErrorMessage = (error: unknown): string => {
  const response = (error as { response?: { data?: unknown } })?.response?.data;
  if (typeof response === 'string') return response;
  if (response && typeof response === 'object') {
    const problem = response as { detail?: string; message?: string; title?: string };
    return problem.detail || problem.message || problem.title || 'The award could not be saved.';
  }
  return error instanceof Error ? error.message : 'The award could not be saved.';
};

const sourceBalance = (quote: CustomerAwardQuote): QuoteAwardProjection => ({
  quoteId: quote.id,
  quoteNo: quote.quoteNo,
  quoteVersion: quote.version,
  outcome: 'UNAWARDED',
  quotedQuantity: quote.lines.reduce((total, line) => total + line.quantity, 0),
  confirmedAwardQuantity: 0,
  remainingQuantity: quote.lines.reduce((total, line) => total + line.quantity, 0),
  lines: quote.lines.map((line) => ({
    quoteItemId: line.id,
    productId: line.productId,
    productName: line.productName,
    description: line.description,
    quotedQuantity: line.quantity,
    confirmedAwardQuantity: 0,
    remainingQuantity: line.quantity,
    uomId: line.uomId,
    uomCode: line.uomCode,
    unitPrice: line.unitPrice,
  })),
  awards: [],
});

/**
 * A blank buyer line. Deliberately blank: the operator transcribes or extracts the customer's
 * figures, and until they do there is nothing to compare the quotation against.
 */
const emptyRow = (reference: string): CaptureLine => ({
  clientId: newClientId(),
  externalLineReference: reference,
  description: '',
  customerItemCode: '',
  manufacturerName: '',
  manufacturerPartNumber: '',
  quoteItemId: '',
  orderedQuantity: '',
  awardedQuantity: '',
  unitPrice: '',
});

const matchStatusChip = (proposal: PurchaseOrderLineMatchProposal) => {
  if (proposal.status === 'PROPOSED') {
    return { label: `Proposed · ${proposal.confidence.toLowerCase()} confidence`, color: 'success' as const };
  }
  if (proposal.status === 'AMBIGUOUS') return { label: 'Ambiguous · needs a decision', color: 'warning' as const };
  return { label: 'No key matched · needs review', color: 'default' as const };
};

const keyLabel = (matchedKey?: string | null) => {
  switch (matchedKey) {
    case 'MANUFACTURER_PART_NUMBER':
      return 'manufacturer part number';
    case 'CATALOGUE_PART_NUMBER':
      return 'catalogue part number';
    case 'MANUFACTURER_AND_ITEM_CODE':
      return 'manufacturer + item code';
    case 'ITEM_CODE':
      return 'item code';
    case 'DESCRIPTION':
      return 'wording only';
    default:
      return 'no key';
  }
};

const CustomerAwardWorkspace: React.FC<CustomerAwardWorkspaceProps> = ({
  quote,
  initialProjection,
  onCancel,
  onCompleted,
  onBusyChange,
}) => {
  const queryClient = useQueryClient();
  const fallbackProjection = React.useMemo(() => sourceBalance(quote), [quote]);
  const [externalPoNumber, setExternalPoNumber] = React.useState('');
  const [poDate, setPoDate] = React.useState(today());
  const [receivedOn, setReceivedOn] = React.useState(today());
  const [convertAfterConfirmation, setConvertAfterConfirmation] = React.useState(true);
  const [rows, setRows] = React.useState<CaptureLine[]>([emptyRow('1')]);
  const [proposals, setProposals] = React.useState<Record<string, PurchaseOrderLineMatchProposal>>({});
  /** FR-COM-01. The uploaded PO document these figures were read from, when there was one. */
  const [sourceAttachmentId, setSourceAttachmentId] = React.useState<number | null>(null);
  const [validation, setValidation] = React.useState<ValidationResult>({ rows: {} });
  const [submitPhase, setSubmitPhase] = React.useState('');
  const identities = React.useRef({
    purchaseOrder: createCustomerAwardCommandIdentity('create-po'),
    award: createCustomerAwardCommandIdentity('create'),
    confirmation: createCustomerAwardCommandIdentity('confirm'),
    conversion: createCustomerAwardCommandIdentity('convert'),
  });

  const projectionQuery = useQuery({
    queryKey: ['customer-awards', 'quote', quote.id],
    queryFn: () => customerAwardService.getByQuote(quote.id),
    initialData: initialProjection,
  });

  const projection = projectionQuery.data ?? fallbackProjection;

  React.useEffect(() => {
    setExternalPoNumber('');
    setPoDate(today());
    setReceivedOn(today());
    setConvertAfterConfirmation(true);
    setRows([emptyRow('1')]);
    setProposals({});
    setSourceAttachmentId(null);
    setValidation({ rows: {} });
    setSubmitPhase('');
    identities.current = {
      purchaseOrder: createCustomerAwardCommandIdentity('create-po'),
      award: createCustomerAwardCommandIdentity('create'),
      confirmation: createCustomerAwardCommandIdentity('confirm'),
      conversion: createCustomerAwardCommandIdentity('convert'),
    };
  }, [quote.id]);

  const balanceByItem = React.useMemo(
    () => new Map(projection.lines.map((line) => [line.quoteItemId, line])),
    [projection.lines],
  );

  const selectedQuantity = rows.reduce(
    (total, row) => total + (validPositive(row.awardedQuantity) ? numberValue(row.awardedQuantity) : 0),
    0,
  );
  const remainingAfterAward = Math.max(0, projection.remainingQuantity - selectedQuantity);
  const outcome = remainingAfterAward <= EPSILON ? 'Full award' : 'Partial award';

  /** Identity keys are what the matcher reasons over, so editing one invalidates its proposal. */
  const identityFields: Array<keyof CaptureLine> = React.useMemo(
    () => ['externalLineReference', 'description', 'customerItemCode', 'manufacturerName', 'manufacturerPartNumber'],
    [],
  );

  const updateRow = (clientId: string, change: Partial<CaptureLine>) => {
    setRows((current) => current.map((row) => {
      if (row.clientId !== clientId) return row;
      const next = { ...row, ...change };
      // Award quantity is OUR acceptance of the buyer's ask, not a buyer figure, so it may track
      // the quantity the buyer ordered until the operator overrides it.
      if (change.orderedQuantity !== undefined && (row.awardedQuantity === '' || row.awardedQuantity === row.orderedQuantity)) {
        next.awardedQuantity = change.orderedQuantity;
      }
      return next;
    }));
    if (identityFields.some((field) => field in change)) {
      setProposals((current) => {
        if (!(clientId in current)) return current;
        const next = { ...current };
        delete next[clientId];
        return next;
      });
    }
    setValidation((current) => {
      const nextRows = { ...current.rows };
      delete nextRows[clientId];
      return { rows: nextRows };
    });
  };

  /**
   * FR-COM-01. Replaces the capture form with what was read out of the BUYER's document.
   *
   * A figure the document did not state, or stated unreadably, arrives blank so the operator has
   * to supply it. It must never fall back to the quote line the row is later matched to — that is
   * exactly the substitution that made the discrepancy check compare the system against itself.
   * The quote line stays unmatched here too: matching is a separate, evidenced decision.
   */
  const applyExtraction = (extraction: CustomerPurchaseOrderDocumentExtraction) => {
    setSourceAttachmentId(extraction.sourceAttachmentId);
    if (extraction.externalPoNumber) setExternalPoNumber(extraction.externalPoNumber);
    if (extraction.poDate) {
      const stated = extraction.poDate.slice(0, 10);
      setPoDate(stated);
      setReceivedOn((current) => (current < stated ? stated : current));
    }
    setRows(extraction.lines.map((line) => ({
      clientId: newClientId(),
      externalLineReference: line.externalLineReference || String(line.lineNumber),
      description: line.description ?? '',
      customerItemCode: line.customerItemCode ?? '',
      manufacturerName: line.manufacturerName ?? '',
      manufacturerPartNumber: line.manufacturerPartNumber ?? '',
      quoteItemId: '',
      orderedQuantity: line.orderedQuantity == null ? '' : String(line.orderedQuantity),
      awardedQuantity: line.orderedQuantity == null ? '' : String(line.orderedQuantity),
      unitPrice: line.unitPrice == null ? '' : String(line.unitPrice),
    })));
    setProposals({});
    setValidation({ rows: {} });
  };

  const addRow = () => setRows((current) => [...current, emptyRow(String(current.length + 1))]);

  const removeRow = (clientId: string) => {
    setRows((current) => current.filter((row) => row.clientId !== clientId));
    setProposals((current) => {
      const next = { ...current };
      delete next[clientId];
      return next;
    });
  };

  const hasAnyIdentityKey = rows.some((row) => row.customerItemCode.trim()
    || row.manufacturerName.trim() || row.manufacturerPartNumber.trim());

  const matchMutation = useMutation({
    mutationFn: () => customerAwardService.proposeQuoteLineMatches({
      quoteId: quote.id,
      customerId: quote.customerId,
      lines: rows.map((row) => ({
        externalLineReference: row.externalLineReference.trim() || row.clientId,
        description: trimmedOrNull(row.description),
        customerItemCode: trimmedOrNull(row.customerItemCode),
        manufacturerName: trimmedOrNull(row.manufacturerName),
        manufacturerPartNumber: trimmedOrNull(row.manufacturerPartNumber),
      })),
    }),
    onSuccess: (result) => {
      // The server answers in the order it was asked, so proposals map back to rows by position.
      const entries = result.lines
        .map((line, index): [string | undefined, PurchaseOrderLineMatchProposal] => [rows[index]?.clientId, line])
        .filter((entry): entry is [string, PurchaseOrderLineMatchProposal] => Boolean(entry[0]));
      setProposals(Object.fromEntries(entries));
    },
  });

  const proposedCount = Object.entries(proposals).filter(([clientId, proposal]) => proposal.status === 'PROPOSED'
    && rows.find((row) => row.clientId === clientId)?.quoteItemId !== proposal.proposedQuoteItemId).length;

  const applyProposal = (clientId: string, quoteItemId: number) => {
    setRows((current) => current.map((row) => (row.clientId === clientId ? { ...row, quoteItemId } : row)));
    setValidation((current) => {
      const nextRows = { ...current.rows };
      delete nextRows[clientId];
      return { rows: nextRows };
    });
  };

  const applyAllProposals = () => {
    setRows((current) => current.map((row) => {
      const proposal = proposals[row.clientId];
      return proposal?.status === 'PROPOSED' && proposal.proposedQuoteItemId
        ? { ...row, quoteItemId: proposal.proposedQuoteItemId }
        : row;
    }));
    setValidation({ rows: {} });
  };

  const validate = (): ValidationResult => {
    const result: ValidationResult = { rows: {} };
    if (!externalPoNumber.trim()) result.form = 'Customer PO number is required.';
    else if (!poDate || !receivedOn) result.form = 'PO date and received date are required.';
    else if (receivedOn < poDate) result.form = 'Received date cannot be earlier than the PO date.';
    else if (rows.length === 0) result.form = 'At least one customer PO line is required.';

    const references = new Map<string, number>();
    const awardedByQuoteItem = new Map<number, number>();

    rows.forEach((row) => {
      const errors: ValidationResult['rows'][string] = {};
      const normalizedReference = normalizeReference(row.externalLineReference);
      if (!normalizedReference) errors.reference = 'Required';
      else references.set(normalizedReference, (references.get(normalizedReference) ?? 0) + 1);

      if (!row.description.trim()) errors.description = "Enter the buyer's description";
      if (row.quoteItemId === '' || !balanceByItem.has(row.quoteItemId)) errors.quoteItem = 'Confirm the quote line';
      if (!validPositive(row.orderedQuantity)) errors.ordered = 'Enter the ordered quantity';
      if (!validPositive(row.awardedQuantity)) errors.awarded = 'Must be greater than zero';
      if (row.unitPrice.trim() === '' || !Number.isFinite(numberValue(row.unitPrice)) || numberValue(row.unitPrice) < 0) {
        errors.unitPrice = "Enter the buyer's unit price";
      }

      if (validPositive(row.orderedQuantity) && validPositive(row.awardedQuantity)
        && numberValue(row.awardedQuantity) - numberValue(row.orderedQuantity) > EPSILON) {
        errors.awarded = 'Cannot exceed PO quantity';
      }

      if (row.quoteItemId !== '' && validPositive(row.awardedQuantity)) {
        awardedByQuoteItem.set(
          row.quoteItemId,
          (awardedByQuoteItem.get(row.quoteItemId) ?? 0) + numberValue(row.awardedQuantity),
        );
      }
      if (Object.keys(errors).length > 0) result.rows[row.clientId] = errors;
    });

    rows.forEach((row) => {
      if (references.get(normalizeReference(row.externalLineReference))! > 1) {
        result.rows[row.clientId] = { ...result.rows[row.clientId], reference: 'PO line reference must be unique' };
      }
      if (row.quoteItemId !== '') {
        const remaining = balanceByItem.get(row.quoteItemId)?.remainingQuantity ?? 0;
        if ((awardedByQuoteItem.get(row.quoteItemId) ?? 0) - remaining > EPSILON) {
          result.rows[row.clientId] = { ...result.rows[row.clientId], awarded: `Total exceeds remaining ${remaining}` };
        }
      }
    });

    return result;
  };

  const saveMutation = useMutation({
    mutationFn: async (): Promise<CustomerAwardCompletion> => {
      setSubmitPhase('Saving customer PO');
      const purchaseOrder = await customerAwardService.createPurchaseOrder({
        quoteId: quote.id,
        commercialCaseId: quote.commercialCaseId,
        customerId: quote.customerId,
        currencyId: quote.currencyId,
        externalPoNumber: externalPoNumber.trim(),
        poDate,
        receivedOn,
        expectedVersion: 0,
        // Buyer figures only. Nothing on this payload is read from the quote line the operator
        // matched the row to; the quotation is the thing being compared against, not a source.
        lines: rows.map((row) => ({
          externalLineReference: row.externalLineReference.trim(),
          description: row.description.trim(),
          orderedQuantity: numberValue(row.orderedQuantity),
          unitPrice: numberValue(row.unitPrice),
          lineAmount: numberValue(row.unitPrice) * numberValue(row.orderedQuantity),
          customerItemCode: trimmedOrNull(row.customerItemCode),
          manufacturerName: trimmedOrNull(row.manufacturerName),
          manufacturerPartNumber: trimmedOrNull(row.manufacturerPartNumber),
        })),
      }, identities.current.purchaseOrder, sourceAttachmentId);

      const poLineByReference = new Map(
        purchaseOrder.lines.map((line) => [normalizeReference(line.externalLineReference), line]),
      );
      const allocations = rows.map((row) => {
        const poLine = poLineByReference.get(normalizeReference(row.externalLineReference));
        if (!poLine) throw new Error(`The saved PO did not return line ${row.externalLineReference}.`);
        return {
          customerPurchaseOrderLineId: poLine.id,
          quoteItemId: Number(row.quoteItemId),
          awardedQuantity: numberValue(row.awardedQuantity),
        };
      });

      setSubmitPhase('Creating award');
      const draftAward = await customerAwardService.createAward({
        customerPurchaseOrderId: purchaseOrder.id,
        quoteId: quote.id,
        expectedVersion: 0,
        customerPurchaseOrderExpectedVersion: purchaseOrder.version,
        quoteExpectedVersion: projection.quoteVersion,
        allocations,
      }, identities.current.award);

      setSubmitPhase('Confirming award');
      const award = await customerAwardService.confirmAward(
        draftAward.id,
        { expectedVersion: draftAward.version },
        identities.current.confirmation,
      );

      if (!convertAfterConfirmation) return { purchaseOrder, award };

      setSubmitPhase('Creating sales order');
      const order = await customerAwardService.convertToOrder(
        award.id,
        { expectedVersion: award.version },
        identities.current.conversion,
      );
      return { purchaseOrder, award, order };
    },
    onSuccess: async (result) => {
      setSubmitPhase('');
      await queryClient.invalidateQueries({ queryKey: ['customer-awards', 'quote', quote.id] });
      await queryClient.invalidateQueries({ queryKey: ['quote-detail', String(quote.id)] });
      await queryClient.invalidateQueries({ queryKey: ['quotes'] });
      if (result.order) await queryClient.invalidateQueries({ queryKey: ['orders'] });
      onCompleted?.(result);
    },
    onError: () => setSubmitPhase(''),
  });

  const existingAwardMutation = useMutation({
    mutationFn: async ({ award, action, reason }: {
      award: CustomerAward;
      action: 'confirm' | 'convert' | 'cancel';
      reason?: string;
    }): Promise<CustomerAwardCompletion> => {
      if (action === 'confirm') {
        const confirmed = await customerAwardService.confirmAward(
          award.id,
          { expectedVersion: award.version },
          createCustomerAwardCommandIdentity('resume-confirm'),
        );
        return { award: confirmed };
      }
      if (action === 'cancel') {
        const cancelled = await customerAwardService.cancelAward(
          award.id,
          { expectedVersion: award.version, reason: reason || 'Cancelled by user' },
          createCustomerAwardCommandIdentity('cancel'),
        );
        return { award: cancelled };
      }
      const order = await customerAwardService.convertToOrder(
        award.id,
        { expectedVersion: award.version },
        createCustomerAwardCommandIdentity('resume-convert'),
      );
      return { award, order };
    },
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['customer-awards', 'quote', quote.id] });
      await queryClient.invalidateQueries({ queryKey: ['orders'] });
      if (result.order) onCompleted?.(result);
    },
  });

  const awardHistory = projection.awards.length > 0 && (
    <Stack spacing={1}>
      <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Existing awards</Typography>
      {projection.awards.map((award) => (
        <Stack
          key={award.id}
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1}
          sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between', py: 1, borderBottom: '1px solid', borderColor: 'divider' }}
        >
          <Box>
            <Typography variant="body2" sx={{ fontWeight: 700 }}>{award.awardNumber}</Typography>
            <Typography variant="caption" color="text.secondary">
              {award.status} · {award.allocations.reduce((sum, item) => sum + item.awardedQuantity, 0)} awarded
            </Typography>
          </Box>
          <Stack direction="row" spacing={1}>
            {award.status === 'DRAFT' && (
              <Button
                size="small"
                startIcon={<ConfirmIcon />}
                disabled={existingAwardMutation.isPending}
                onClick={() => existingAwardMutation.mutate({ award, action: 'confirm' })}
              >
                Confirm
              </Button>
            )}
            {award.status === 'CONFIRMED' && (
              <Button
                size="small"
                variant="contained"
                startIcon={<ConvertIcon />}
                disabled={existingAwardMutation.isPending}
                onClick={() => existingAwardMutation.mutate({ award, action: 'convert' })}
              >
                Create order
              </Button>
            )}
            {(award.status === 'DRAFT' || award.status === 'CONFIRMED') && (
              <Button
                size="small"
                color="error"
                startIcon={<CancelIcon />}
                disabled={existingAwardMutation.isPending}
                onClick={() => {
                  const reason = window.prompt('Cancellation reason');
                  if (reason?.trim()) existingAwardMutation.mutate({ award, action: 'cancel', reason: reason.trim() });
                }}
              >
                Cancel
              </Button>
            )}
          </Stack>
        </Stack>
      ))}
      {existingAwardMutation.isError && <Alert severity="error">{apiErrorMessage(existingAwardMutation.error)}</Alert>}
    </Stack>
  );

  React.useEffect(() => {
    onBusyChange?.(saveMutation.isPending || existingAwardMutation.isPending);
  }, [existingAwardMutation.isPending, onBusyChange, saveMutation.isPending]);

  const submit = () => {
    const result = validate();
    setValidation(result);
    if (!result.form && Object.keys(result.rows).length === 0) saveMutation.mutate();
  };

  if (projectionQuery.isLoading) {
    return (
      <Stack spacing={1.5} sx={{ minHeight: 320, alignItems: 'center', justifyContent: 'center' }}>
        <CircularProgress size={28} />
        <Typography color="text.secondary">Loading award balances</Typography>
      </Stack>
    );
  }

  if (projectionQuery.isError) {
    return (
      <Alert
        severity="error"
        action={(
          <Button color="inherit" size="small" startIcon={<RetryIcon />} onClick={() => projectionQuery.refetch()}>
            Retry
          </Button>
        )}
      >
        {apiErrorMessage(projectionQuery.error)}
      </Alert>
    );
  }

  if (projection.remainingQuantity <= EPSILON) {
    return (
      <Stack spacing={2}>
        <Alert severity="success">Quote {quote.quoteNo} is fully awarded.</Alert>
        {awardHistory}
      </Stack>
    );
  }

  const busy = saveMutation.isPending;

  return (
    <Stack spacing={2.5}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={1}
        sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' } }}
      >
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Client PO matching</Typography>
          <Typography variant="body2" color="text.secondary">Quote {quote.quoteNo}</Typography>
        </Box>
        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
          <Chip size="small" label={`Quoted ${projection.quotedQuantity}`} variant="outlined" />
          <Chip size="small" label={`Awarded ${projection.confirmedAwardQuantity}`} color="success" variant="outlined" />
          <Chip size="small" label={`Remaining ${projection.remainingQuantity}`} color="warning" variant="outlined" />
          <Chip size="small" label={outcome} color={outcome === 'Full award' ? 'success' : 'info'} />
        </Stack>
      </Stack>

      {awardHistory}

      {(validation.form || saveMutation.isError) && (
        <Alert severity="error">
          {validation.form || apiErrorMessage(saveMutation.error)}
        </Alert>
      )}

      <Alert severity="info" variant="outlined">
        Enter every figure from the customer&apos;s purchase order. What we quoted is shown beside each
        field for comparison — it is never used to fill one in, so a real difference in price, quantity
        or part shows up as a difference.
      </Alert>

      {/* FR-COM-01: upload the buyer's PO and read it, rather than retyping it. */}
      <CustomerPoDocumentIntake disabled={busy} onApply={applyExtraction} />

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '2fr 1fr 1fr' }, gap: 2 }}>
        <TextField
          required
          size="small"
          label="Customer PO number"
          value={externalPoNumber}
          onChange={(event) => {
            setExternalPoNumber(event.target.value);
            setValidation((current) => ({ ...current, form: undefined }));
          }}
          disabled={busy}
          slotProps={{ htmlInput: { maxLength: 100 } }}
        />
        <TextField
          required
          size="small"
          type="date"
          label="PO date"
          value={poDate}
          onChange={(event) => setPoDate(event.target.value)}
          disabled={busy}
          slotProps={{ inputLabel: { shrink: true } }}
        />
        <TextField
          required
          size="small"
          type="date"
          label="Received date"
          value={receivedOn}
          onChange={(event) => setReceivedOn(event.target.value)}
          disabled={busy}
          slotProps={{ inputLabel: { shrink: true } }}
        />
      </Box>

      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={1}
        sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between' }}
      >
        <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Buyer PO lines</Typography>
        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
          <Button
            size="small"
            variant="outlined"
            startIcon={matchMutation.isPending ? <CircularProgress size={15} color="inherit" /> : <MatchIcon />}
            onClick={() => matchMutation.mutate()}
            disabled={busy || matchMutation.isPending || !hasAnyIdentityKey}
          >
            Match to quote lines
          </Button>
          {proposedCount > 0 && (
            <Button size="small" variant="contained" startIcon={<ConfirmIcon />} onClick={applyAllProposals} disabled={busy}>
              {`Accept ${proposedCount} proposed ${proposedCount === 1 ? 'match' : 'matches'}`}
            </Button>
          )}
        </Stack>
      </Stack>

      {!hasAnyIdentityKey && (
        <Typography variant="caption" color="text.secondary">
          Enter an item code, manufacturer or part number on at least one line to match against the quotation.
        </Typography>
      )}
      {matchMutation.isError && <Alert severity="error">{apiErrorMessage(matchMutation.error)}</Alert>}
      {matchMutation.isSuccess && (
        <Alert severity={matchMutation.data.reviewCount > 0 ? 'warning' : 'success'} variant="outlined">
          {`${matchMutation.data.proposedCount} of ${matchMutation.data.lines.length} lines matched a quote line on item `
            + `code, manufacturer or part number. `}
          {matchMutation.data.reviewCount > 0
            ? `${matchMutation.data.reviewCount} need a decision — every match is a proposal until you accept it.`
            : 'Every match is a proposal until you accept it.'}
        </Alert>
      )}

      <Stack spacing={2}>
        {rows.map((row, index) => {
          const rowErrors = validation.rows[row.clientId] ?? {};
          const selectedLine = row.quoteItemId === '' ? undefined : balanceByItem.get(row.quoteItemId);
          const proposal = proposals[row.clientId];
          const chip = proposal ? matchStatusChip(proposal) : undefined;
          const priceVariance = selectedLine && row.unitPrice.trim() !== ''
            && Number.isFinite(numberValue(row.unitPrice))
            && Math.abs(numberValue(row.unitPrice) - selectedLine.unitPrice) > EPSILON;
          const quantityVariance = selectedLine && validPositive(row.orderedQuantity)
            && Math.abs(numberValue(row.orderedQuantity) - selectedLine.remainingQuantity) > EPSILON;

          return (
            <Box
              key={row.clientId}
              sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 1, p: 2 }}
            >
              <Stack
                direction="row"
                spacing={1}
                sx={{ alignItems: 'center', justifyContent: 'space-between', mb: 1.5 }}
              >
                <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>{`Buyer line ${index + 1}`}</Typography>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  {chip && <Chip size="small" label={chip.label} color={chip.color} variant="outlined" />}
                  <Tooltip title="Remove line">
                    <span>
                      <IconButton
                        size="small"
                        aria-label={`Remove buyer line ${index + 1}`}
                        onClick={() => removeRow(row.clientId)}
                        disabled={busy}
                      >
                        <DeleteIcon fontSize="small" />
                      </IconButton>
                    </span>
                  </Tooltip>
                </Stack>
              </Stack>

              <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 2fr' }, gap: 1.5 }}>
                <TextField
                  required
                  size="small"
                  label="Buyer line reference"
                  value={row.externalLineReference}
                  onChange={(event) => updateRow(row.clientId, { externalLineReference: event.target.value })}
                  error={Boolean(rowErrors.reference)}
                  helperText={rowErrors.reference}
                  disabled={busy}
                  slotProps={{ htmlInput: { maxLength: 50 } }}
                />
                <TextField
                  required
                  size="small"
                  label="Buyer description"
                  value={row.description}
                  onChange={(event) => updateRow(row.clientId, { description: event.target.value })}
                  error={Boolean(rowErrors.description)}
                  helperText={rowErrors.description}
                  disabled={busy}
                  slotProps={{ htmlInput: { maxLength: 1000 } }}
                />
              </Box>

              <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: 'repeat(3, 1fr)' }, gap: 1.5, mt: 1.5 }}>
                <TextField
                  size="small"
                  label="Buyer item code"
                  value={row.customerItemCode}
                  onChange={(event) => updateRow(row.clientId, { customerItemCode: event.target.value })}
                  disabled={busy}
                  helperText="Used to match the quote line"
                />
                <TextField
                  size="small"
                  label="Manufacturer"
                  value={row.manufacturerName}
                  onChange={(event) => updateRow(row.clientId, { manufacturerName: event.target.value })}
                  disabled={busy}
                />
                <TextField
                  size="small"
                  label="Manufacturer part number"
                  value={row.manufacturerPartNumber}
                  onChange={(event) => updateRow(row.clientId, { manufacturerPartNumber: event.target.value })}
                  disabled={busy}
                />
              </Box>

              {proposal && (
                <Box sx={{ mt: 1.5, p: 1.5, bgcolor: 'action.hover', borderRadius: 1 }}>
                  <Typography variant="caption" sx={{ fontWeight: 700, display: 'block' }}>
                    {`Matched on ${keyLabel(proposal.matchedKey)}`}
                  </Typography>
                  <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
                    {proposal.reason}
                  </Typography>
                  <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
                    {proposal.candidates.map((candidate) => (
                      <Button
                        key={candidate.quoteItemId}
                        size="small"
                        variant={row.quoteItemId === candidate.quoteItemId ? 'contained' : 'outlined'}
                        onClick={() => applyProposal(row.clientId, candidate.quoteItemId)}
                        disabled={busy}
                      >
                        {`${candidate.quoteDescription} · ${candidate.remainingQuantity} left @ ${candidate.quotedUnitPrice}`}
                      </Button>
                    ))}
                  </Stack>
                </Box>
              )}

              <Divider sx={{ my: 1.5 }} />

              <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '2fr 1fr 1fr 1fr' }, gap: 1.5 }}>
                <FormControl fullWidth size="small" error={Boolean(rowErrors.quoteItem)}>
                  <InputLabel id={`quote-line-${row.clientId}`}>Quote line</InputLabel>
                  <Select
                    labelId={`quote-line-${row.clientId}`}
                    label="Quote line"
                    value={row.quoteItemId}
                    onChange={(event) => applyProposal(row.clientId, Number(event.target.value))}
                    disabled={busy}
                  >
                    {projection.lines.filter((line) => line.remainingQuantity > EPSILON).map((line) => (
                      <MenuItem key={line.quoteItemId} value={line.quoteItemId}>
                        {line.productName || line.description} ({line.remainingQuantity} remaining)
                      </MenuItem>
                    ))}
                  </Select>
                  {rowErrors.quoteItem && (
                    <Typography variant="caption" color="error" sx={{ mx: 1.75, mt: 0.5 }}>{rowErrors.quoteItem}</Typography>
                  )}
                </FormControl>

                <TextField
                  required
                  size="small"
                  type="number"
                  label="Buyer ordered"
                  value={row.orderedQuantity}
                  onChange={(event) => updateRow(row.clientId, { orderedQuantity: event.target.value })}
                  error={Boolean(rowErrors.ordered)}
                  helperText={rowErrors.ordered
                    ?? (selectedLine ? `You quoted ${selectedLine.quotedQuantity}` : 'From the buyer PO')}
                  disabled={busy}
                  slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': 'Quantity the buyer ordered' } }}
                />

                <TextField
                  required
                  size="small"
                  type="number"
                  label="Buyer unit price"
                  value={row.unitPrice}
                  onChange={(event) => updateRow(row.clientId, { unitPrice: event.target.value })}
                  error={Boolean(rowErrors.unitPrice)}
                  helperText={rowErrors.unitPrice
                    ?? (selectedLine ? `You quoted ${selectedLine.unitPrice}` : 'From the buyer PO')}
                  disabled={busy}
                  slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': 'Unit price the buyer ordered at' } }}
                />

                <TextField
                  required
                  size="small"
                  type="number"
                  label="Award quantity"
                  value={row.awardedQuantity}
                  onChange={(event) => updateRow(row.clientId, { awardedQuantity: event.target.value })}
                  error={Boolean(rowErrors.awarded)}
                  helperText={rowErrors.awarded
                    ?? (selectedLine ? `${selectedLine.remainingQuantity} remaining on the quote` : 'What we accept')}
                  disabled={busy}
                  slotProps={{ htmlInput: { min: 0, max: selectedLine?.remainingQuantity, step: 'any', 'aria-label': 'Award quantity' } }}
                />
              </Box>

              {(priceVariance || quantityVariance) && (
                <Stack direction="row" spacing={1} sx={{ mt: 1.5, flexWrap: 'wrap', gap: 1 }}>
                  {priceVariance && (
                    <Chip
                      size="small"
                      color="warning"
                      label={`Price differs — you quoted ${selectedLine!.unitPrice}, buyer ordered ${row.unitPrice}`}
                    />
                  )}
                  {quantityVariance && (
                    <Chip
                      size="small"
                      color="info"
                      label={`Quantity differs — ${selectedLine!.remainingQuantity} remaining, buyer ordered ${row.orderedQuantity}`}
                    />
                  )}
                </Stack>
              )}
            </Box>
          );
        })}
      </Stack>

      <Button
        variant="outlined"
        size="small"
        startIcon={<AddIcon />}
        onClick={addRow}
        disabled={busy}
        sx={{ alignSelf: 'flex-start' }}
      >
        Add PO line
      </Button>

      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={2}
        sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' } }}
      >
        <FormControlLabel
          control={(
            <Checkbox
              checked={convertAfterConfirmation}
              onChange={(event) => setConvertAfterConfirmation(event.target.checked)}
              disabled={busy}
            />
          )}
          label="Create sales order after confirmation"
        />
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
          {onCancel && <Button color="inherit" onClick={onCancel} disabled={busy}>Cancel</Button>}
          <Button
            variant="contained"
            onClick={submit}
            disabled={busy || rows.length === 0}
            startIcon={busy
              ? <CircularProgress size={17} color="inherit" />
              : convertAfterConfirmation ? <ConvertIcon /> : <ConfirmIcon />}
            sx={{ minWidth: 190, fontWeight: 800 }}
          >
            {busy ? submitPhase : convertAfterConfirmation ? 'Confirm and create order' : 'Confirm award'}
          </Button>
        </Stack>
      </Stack>
    </Stack>
  );
};

export default CustomerAwardWorkspace;
