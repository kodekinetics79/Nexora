import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  FormControl,
  FormControlLabel,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
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
} from '@mui/icons-material';
import customerAwardService, {
  createCustomerAwardCommandIdentity,
  type CustomerAward,
  type CustomerAwardOrder,
  type CustomerPurchaseOrder,
  type QuoteAwardBalanceLine,
  type QuoteAwardProjection,
} from '../../../../api/services/customerAwardService';

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
  purchaseOrder: CustomerPurchaseOrder;
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

interface CaptureLine {
  clientId: string;
  externalLineReference: string;
  quoteItemId: number | '';
  orderedQuantity: string;
  awardedQuantity: string;
}

interface ValidationResult {
  form?: string;
  rows: Record<string, Partial<Record<'reference' | 'quoteItem' | 'ordered' | 'awarded', string>>>;
}

const today = () => new Date().toISOString().slice(0, 10);
const normalizeReference = (value: string) => value.trim().replace(/\s+/g, ' ').toUpperCase();
const numberValue = (value: string) => Number(value);
const validPositive = (value: string) => Number.isFinite(numberValue(value)) && numberValue(value) > 0;

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

const createDefaultRows = (lines: QuoteAwardBalanceLine[]): CaptureLine[] =>
  lines
    .filter((line) => line.remainingQuantity > EPSILON)
    .map((line, index) => ({
      clientId: newClientId(),
      externalLineReference: String(index + 1),
      quoteItemId: line.quoteItemId,
      orderedQuantity: String(line.remainingQuantity),
      awardedQuantity: String(line.remainingQuantity),
    }));

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
  const [rows, setRows] = React.useState<CaptureLine[]>([]);
  const [hasInitialisedRows, setHasInitialisedRows] = React.useState(false);
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
    setRows([]);
    setHasInitialisedRows(false);
    setValidation({ rows: {} });
    setSubmitPhase('');
    identities.current = {
      purchaseOrder: createCustomerAwardCommandIdentity('create-po'),
      award: createCustomerAwardCommandIdentity('create'),
      confirmation: createCustomerAwardCommandIdentity('confirm'),
      conversion: createCustomerAwardCommandIdentity('convert'),
    };
  }, [quote.id]);

  React.useEffect(() => {
    if (!hasInitialisedRows && projectionQuery.isSuccess) {
      setRows(createDefaultRows(projection.lines));
      setHasInitialisedRows(true);
    }
  }, [hasInitialisedRows, projection.lines, projectionQuery.isSuccess]);

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

  const updateRow = (clientId: string, change: Partial<CaptureLine>) => {
    setRows((current) => current.map((row) => (row.clientId === clientId ? { ...row, ...change } : row)));
    setValidation((current) => {
      const nextRows = { ...current.rows };
      delete nextRows[clientId];
      return { rows: nextRows };
    });
  };

  const addRow = () => {
    const nextLine = projection.lines.find(
      (line) => line.remainingQuantity > EPSILON && !rows.some((row) => row.quoteItemId === line.quoteItemId),
    );
    setRows((current) => [
      ...current,
      {
        clientId: newClientId(),
        externalLineReference: String(current.length + 1),
        quoteItemId: nextLine?.quoteItemId ?? '',
        orderedQuantity: nextLine ? String(nextLine.remainingQuantity) : '',
        awardedQuantity: nextLine ? String(nextLine.remainingQuantity) : '',
      },
    ]);
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

      if (row.quoteItemId === '' || !balanceByItem.has(row.quoteItemId)) errors.quoteItem = 'Select a quote line';
      if (!validPositive(row.orderedQuantity)) errors.ordered = 'Must be greater than zero';
      if (!validPositive(row.awardedQuantity)) errors.awarded = 'Must be greater than zero';

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
        lines: rows.map((row) => {
          const quoteLine = balanceByItem.get(Number(row.quoteItemId))!;
          return {
            externalLineReference: row.externalLineReference.trim(),
            productId: quoteLine.productId,
            description: quoteLine.description,
            orderedQuantity: numberValue(row.orderedQuantity),
            uomId: quoteLine.uomId,
          };
        }),
      }, identities.current.purchaseOrder);

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

  React.useEffect(() => {
    onBusyChange?.(saveMutation.isPending);
  }, [onBusyChange, saveMutation.isPending]);

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
    return <Alert severity="success">Quote {quote.quoteNo} is fully awarded.</Alert>;
  }

  return (
    <Stack spacing={2.5}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={1}
        sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' } }}
      >
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Customer PO award</Typography>
          <Typography variant="body2" color="text.secondary">Quote {quote.quoteNo}</Typography>
        </Box>
        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }}>
          <Chip size="small" label={`Quoted ${projection.quotedQuantity}`} variant="outlined" />
          <Chip size="small" label={`Awarded ${projection.confirmedAwardQuantity}`} color="success" variant="outlined" />
          <Chip size="small" label={`Remaining ${projection.remainingQuantity}`} color="warning" variant="outlined" />
          <Chip size="small" label={outcome} color={outcome === 'Full award' ? 'success' : 'info'} />
        </Stack>
      </Stack>

      {(validation.form || saveMutation.isError) && (
        <Alert severity="error">
          {validation.form || apiErrorMessage(saveMutation.error)}
        </Alert>
      )}

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
          disabled={saveMutation.isPending}
          slotProps={{ htmlInput: { maxLength: 100 } }}
        />
        <TextField
          required
          size="small"
          type="date"
          label="PO date"
          value={poDate}
          onChange={(event) => setPoDate(event.target.value)}
          disabled={saveMutation.isPending}
          slotProps={{ inputLabel: { shrink: true } }}
        />
        <TextField
          required
          size="small"
          type="date"
          label="Received date"
          value={receivedOn}
          onChange={(event) => setReceivedOn(event.target.value)}
          disabled={saveMutation.isPending}
          slotProps={{ inputLabel: { shrink: true } }}
        />
      </Box>

      <TableContainer sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 1, overflowX: 'auto' }}>
        <Table size="small" sx={{ minWidth: 880 }}>
          <TableHead>
            <TableRow sx={{ bgcolor: 'action.hover' }}>
              <TableCell sx={{ fontWeight: 800, width: 150 }}>PO line</TableCell>
              <TableCell sx={{ fontWeight: 800, minWidth: 280 }}>Quote line</TableCell>
              <TableCell align="right" sx={{ fontWeight: 800, width: 130 }}>PO quantity</TableCell>
              <TableCell align="right" sx={{ fontWeight: 800, width: 140 }}>Award quantity</TableCell>
              <TableCell align="right" sx={{ fontWeight: 800, width: 90 }}>Remaining</TableCell>
              <TableCell sx={{ width: 48 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {rows.map((row) => {
              const rowErrors = validation.rows[row.clientId] ?? {};
              const selectedLine = row.quoteItemId === '' ? undefined : balanceByItem.get(row.quoteItemId);
              return (
                <TableRow key={row.clientId}>
                  <TableCell sx={{ verticalAlign: 'top' }}>
                    <TextField
                      required
                      fullWidth
                      size="small"
                      value={row.externalLineReference}
                      onChange={(event) => updateRow(row.clientId, { externalLineReference: event.target.value })}
                      error={Boolean(rowErrors.reference)}
                      helperText={rowErrors.reference}
                      disabled={saveMutation.isPending}
                      slotProps={{ htmlInput: { 'aria-label': 'Customer PO line reference', maxLength: 50 } }}
                    />
                  </TableCell>
                  <TableCell sx={{ verticalAlign: 'top' }}>
                    <FormControl fullWidth size="small" error={Boolean(rowErrors.quoteItem)}>
                      <InputLabel id={`quote-line-${row.clientId}`}>Quote line</InputLabel>
                      <Select
                        labelId={`quote-line-${row.clientId}`}
                        label="Quote line"
                        value={row.quoteItemId}
                        onChange={(event) => {
                          const quoteItemId = Number(event.target.value);
                          const line = balanceByItem.get(quoteItemId);
                          updateRow(row.clientId, {
                            quoteItemId,
                            orderedQuantity: line ? String(line.remainingQuantity) : row.orderedQuantity,
                            awardedQuantity: line ? String(line.remainingQuantity) : row.awardedQuantity,
                          });
                        }}
                        disabled={saveMutation.isPending}
                      >
                        {projection.lines.filter((line) => line.remainingQuantity > EPSILON).map((line) => (
                          <MenuItem key={line.quoteItemId} value={line.quoteItemId}>
                            {line.productName || line.description} ({line.remainingQuantity} remaining)
                          </MenuItem>
                        ))}
                      </Select>
                      {rowErrors.quoteItem && <Typography variant="caption" color="error" sx={{ mx: 1.75, mt: 0.5 }}>{rowErrors.quoteItem}</Typography>}
                    </FormControl>
                  </TableCell>
                  <TableCell sx={{ verticalAlign: 'top' }}>
                    <TextField
                      required
                      fullWidth
                      size="small"
                      type="number"
                      value={row.orderedQuantity}
                      onChange={(event) => updateRow(row.clientId, { orderedQuantity: event.target.value })}
                      error={Boolean(rowErrors.ordered)}
                      helperText={rowErrors.ordered}
                      disabled={saveMutation.isPending}
                      slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': 'Customer PO quantity' } }}
                    />
                  </TableCell>
                  <TableCell sx={{ verticalAlign: 'top' }}>
                    <TextField
                      required
                      fullWidth
                      size="small"
                      type="number"
                      value={row.awardedQuantity}
                      onChange={(event) => updateRow(row.clientId, { awardedQuantity: event.target.value })}
                      error={Boolean(rowErrors.awarded)}
                      helperText={rowErrors.awarded}
                      disabled={saveMutation.isPending}
                      slotProps={{ htmlInput: { min: 0, max: selectedLine?.remainingQuantity, step: 'any', 'aria-label': 'Award quantity' } }}
                    />
                  </TableCell>
                  <TableCell align="right" sx={{ verticalAlign: 'top', pt: 2.1, fontWeight: 700 }}>
                    {selectedLine?.remainingQuantity ?? '-'}
                  </TableCell>
                  <TableCell sx={{ verticalAlign: 'top', pt: 1.2 }}>
                    <Tooltip title="Remove line">
                      <span>
                        <IconButton
                          size="small"
                          aria-label="Remove PO line"
                          onClick={() => setRows((current) => current.filter((item) => item.clientId !== row.clientId))}
                          disabled={saveMutation.isPending}
                        >
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </span>
                    </Tooltip>
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </TableContainer>

      <Button
        variant="outlined"
        size="small"
        startIcon={<AddIcon />}
        onClick={addRow}
        disabled={saveMutation.isPending}
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
              disabled={saveMutation.isPending}
            />
          )}
          label="Create sales order after confirmation"
        />
        <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
          {onCancel && <Button color="inherit" onClick={onCancel} disabled={saveMutation.isPending}>Cancel</Button>}
          <Button
            variant="contained"
            onClick={submit}
            disabled={saveMutation.isPending || rows.length === 0}
            startIcon={saveMutation.isPending
              ? <CircularProgress size={17} color="inherit" />
              : convertAfterConfirmation ? <ConvertIcon /> : <ConfirmIcon />}
            sx={{ minWidth: 190, fontWeight: 800 }}
          >
            {saveMutation.isPending ? submitPhase : convertAfterConfirmation ? 'Confirm and create order' : 'Confirm award'}
          </Button>
        </Stack>
      </Stack>
    </Stack>
  );
};

export default CustomerAwardWorkspace;
