import React from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  FormControlLabel,
  FormHelperText,
  IconButton,
  Link,
  MenuItem,
  Select,
  Stack,
  Switch,
  TablePagination,
  TextField,
  Typography,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import { Close as CloseIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import leadService, { type LeadItemResponseDTO } from '../../../api/services/leadService';
import extractionReviewService, { type ReviewItemPayload } from '../../../api/services/extractionReviewService';
import type { LeadDecisionEvidenceDTO, LeadDecisionLineDTO, LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';
import { downloadAuthenticatedFile, fetchAuthenticatedObjectUrl, openAuthenticatedFile } from '../../../utils/authenticatedFile';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { inspectableEvidenceUrl } from '../Workbench/evidenceRules';
import type { DecisionMap, EditableLineDecision } from '../Workbench/workbenchRules';
import { lineLabel } from './decideRules';
import { readUnit, tenantUnitCode, unitCaption, type UnitOption } from './unitRules';

export interface ConfirmedLine {
  lineItemNo: string;
  quantity?: number;
  unitOfMeasure?: string;
  currency?: string;
}

export interface CheckDocumentDialogProps {
  open: boolean;
  leadId: number;
  workbench: LeadDecisionWorkbenchDTO;
  /** The line the rep clicked from, scrolled into view when the dialog opens. */
  focusLineId?: number | null;
  onClose: () => void;
  /** Fired after the server accepted the check, with the values the rep confirmed per line. */
  onConfirmed: (confirmed: ConfirmedLine[]) => void;
  /**
   * What the rep already chose on the lines. A line whose record has no quantity, no unit the
   * tenant quotes in, or no currency opens with that choice, so nothing is typed twice.
   */
  decisions?: DecisionMap;
}

export interface LineEdit {
  productShortName: string;
  itemMaterialCode: string;
  manufacturerPartNumber: string;
  quantity: string;
  unitOfMeasure: string;
  currency: string;
}

export const DEFAULT_CHECK_REASON = 'Checked against the source document on the decision screen.';

/** Lines drawn at once in the check. A 641-line list with no units must not draw 641 forms. */
export const CHECK_LINES_PER_PAGE = 50;

const toDateInput = (iso: string | null | undefined): string => {
  const match = /^(\d{4}-\d{2}-\d{2})/.exec(iso ?? '');
  return match ? match[1] : '';
};

/** Finds the canonical lead item behind a decision line: by line number first, then by position. */
export const matchLeadItem = (
  line: LeadDecisionLineDTO,
  index: number,
  items: LeadItemResponseDTO[],
): LeadItemResponseDTO | undefined => {
  const label = line.lineItemNo?.trim();
  if (label) {
    const byNumber = items.find((item) => item.lineItemNo?.trim() === label);
    if (byNumber) return byNumber;
    const numeric = Number(label);
    if (Number.isFinite(numeric) && items[numeric - 1]) return items[numeric - 1];
  }
  return items[index];
};

/**
 * The values a line opens with: what the record says, and — where the record has no quantity,
 * no unit the tenant quotes in, or no currency — what the rep already chose for it on the lines.
 * A unit the tenant does not quote in (Roll, Pack) is never a value here; it is shown as written
 * beside the picker instead, because confirming it would approve a unit no line can be quoted in.
 */
export const editFrom = (
  line: LeadDecisionLineDTO,
  item: LeadItemResponseDTO | undefined,
  decision?: EditableLineDecision,
  unitOptions: UnitOption[] = [],
): LineEdit => {
  const recordUnit = item?.unitOfMeasure ?? line.unitOfMeasure ?? '';
  const recordQuantity = item?.quantity ?? line.quantity;
  return {
    productShortName: item?.productShortName ?? line.productName ?? line.description ?? '',
    itemMaterialCode: item?.itemMaterialCode ?? line.itemMaterialCode ?? '',
    manufacturerPartNumber: item?.manufacturerPartNumber ?? line.manufacturerPartNumber ?? '',
    quantity: recordQuantity != null ? String(recordQuantity) : decision?.quantity != null ? String(decision.quantity) : '',
    unitOfMeasure: unitOptions.length > 0
      ? tenantUnitCode(recordUnit, unitOptions) ?? tenantUnitCode(decision?.unitOfMeasure, unitOptions) ?? ''
      : recordUnit || decision?.unitOfMeasure || '',
    currency: item?.currency ?? line.currency ?? decision?.currency ?? '',
  };
};

/** Every current line goes back to the server: a line left out of a review is a line deleted. */
export const buildReviewItems = (
  items: LeadItemResponseDTO[],
  edits: Map<number, LineEdit>,
): ReviewItemPayload[] => items.map((item) => {
  const edit = edits.get(item.id);
  const quantity = edit ? Number(edit.quantity) : item.quantity;
  return {
    id: item.id,
    lineItemNo: item.lineItemNo || undefined,
    productShortName: (edit?.productShortName ?? item.productShortName) || undefined,
    productShortDescription: item.productShortDescription || undefined,
    commodityProduct: item.commodityProduct || undefined,
    itemMaterialCode: (edit?.itemMaterialCode ?? item.itemMaterialCode) || undefined,
    currency: (edit?.currency ?? item.currency) || undefined,
    unitOfMeasure: (edit?.unitOfMeasure ?? item.unitOfMeasure) || undefined,
    unitPrice: item.unitPrice ?? undefined,
    quantity: quantity != null && Number.isFinite(quantity) && edit?.quantity !== '' ? quantity : undefined,
    manufacturerName: item.manufacturerName || undefined,
    manufacturerPartNumber: (edit?.manufacturerPartNumber ?? item.manufacturerPartNumber) || undefined,
    alternateProductName: item.alternateProductName || undefined,
    alternatePartNumber: item.alternatePartNumber || undefined,
    itemText: item.itemText || undefined,
    leadTime: item.leadTime || undefined,
  };
});

/**
 * How a document can be shown, decided from what the workbench already knows about the file so
 * nothing is downloaded only to be discarded. Browsers frame PDFs, images and HTML; they will
 * not frame CSV or plain text (shown as text here) and cannot draw Office files at all.
 */
export type DocumentKind = 'frame' | 'image' | 'text' | 'file';

export const documentKind = (contentType: string, name: string): DocumentKind => {
  const type = contentType.toLowerCase();
  const file = name.toLowerCase();
  if (type.startsWith('image/') || /\.(png|jpe?g|gif|webp|bmp|tiff?)$/.test(file)) return 'image';
  if (/pdf|html/.test(type) || /\.(pdf|html?)$/.test(file)) return 'frame';
  // Office types also end in "xml" (spreadsheetml, wordprocessingml); only bare data types count.
  if (/^text\//.test(type) || /^application\/(json|xml|csv)$/.test(type) || /\.(csv|txt|json|xml|md)$/.test(file)) return 'text';
  return 'file';
};

type ViewerState = { url: string; kind: DocumentKind; text?: string } | { error: string } | null;

const DocumentViewer: React.FC<{ evidence: LeadDecisionEvidenceDTO | null }> = ({ evidence }) => {
  const [state, setState] = React.useState<ViewerState>(null);
  const path = evidence ? inspectableEvidenceUrl(evidence) : null;
  const name = evidence?.name ?? '';
  const knownKind = documentKind(evidence?.mediaType ?? '', name);

  React.useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    setState(null);
    if (!path || knownKind === 'file') return undefined;
    fetchAuthenticatedObjectUrl(path)
      .then(async (result) => {
        // The server's content type wins over the file name once the bytes are here.
        const kind = result.contentType ? documentKind(result.contentType, name) : knownKind;
        const text = kind === 'text' ? await result.blob.text() : undefined;
        if (cancelled) { URL.revokeObjectURL(result.url); return; }
        url = result.url;
        setState({ url: result.url, kind, text });
      })
      .catch((error: unknown) => {
        if (!cancelled) setState({ error: presentableErrorMessage(error, 'The document could not be opened.') });
      });
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [path, name, knownKind]);

  if (!evidence || !path) {
    return (
      <Alert severity="warning">No document is on file for this request, so there is nothing to check against.</Alert>
    );
  }
  const fileOffer = (
    <Alert
      severity="info"
      action={(
        <Stack direction="row" spacing={1}>
          <Button color="inherit" size="small" onClick={() => void openAuthenticatedFile(path)}>Open in a new tab</Button>
          <Button color="inherit" size="small" onClick={() => void downloadAuthenticatedFile(path, evidence.name)}>Download</Button>
        </Stack>
      )}
    >
      <strong>{evidence.name}</strong> is a file the browser cannot show here. Open it beside this window to cross-check.
    </Alert>
  );
  if (knownKind === 'file') return fileOffer;
  if (!state) {
    return (
      <Box sx={{ display: 'grid', placeItems: 'center', minHeight: 320 }}>
        <CircularProgress size={28} />
      </Box>
    );
  }
  if ('error' in state) return <Alert severity="error">{state.error}</Alert>;
  if (state.text != null) {
    return (
      <Box
        component="pre"
        aria-label={evidence.name}
        sx={{
          m: 0, p: 2, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontFamily: 'ui-monospace, SFMono-Regular, Menlo, monospace',
          fontSize: '0.85rem', lineHeight: 1.6, bgcolor: 'background.paper', border: 1, borderColor: 'divider', borderRadius: 2,
          maxHeight: { xs: '48vh', md: '70vh' }, overflow: 'auto',
        }}
      >
        {state.text}
      </Box>
    );
  }
  if (state.kind === 'file') return fileOffer;
  return state.kind === 'image' ? (
    <Box component="img" src={state.url} alt={evidence.name} sx={{ maxWidth: '100%', display: 'block' }} />
  ) : (
    <Box
      component="iframe"
      title={evidence.name}
      src={state.url}
      sx={{ width: '100%', height: { xs: '48vh', md: '70vh' }, border: 0, bgcolor: 'background.paper' }}
    />
  );
};

/**
 * The original document beside the lines that need checking, on the same screen. What the rep
 * confirms here goes through the same governed review call the extraction review screen uses:
 * every current line, the corrected values, and an approval reason, against the lead's review
 * version. The server then treats the lines as verified and this dialog closes.
 */
const CheckDocumentDialog: React.FC<CheckDocumentDialogProps> = ({
  open, leadId, workbench, focusLineId, onClose, onConfirmed, decisions,
}) => {
  const theme = useTheme();
  const fullScreen = useMediaQuery(theme.breakpoints.down('md'));
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const [showAll, setShowAll] = React.useState(false);
  const [edits, setEdits] = React.useState<Map<number, LineEdit>>(new Map());
  const [dueDate, setDueDate] = React.useState('');
  const [note, setNote] = React.useState('');
  const [evidenceIndex, setEvidenceIndex] = React.useState(0);
  const [refusal, setRefusal] = React.useState<string | null>(null);
  const seeded = React.useRef<number | null>(null);
  const [page, setPage] = React.useState(0);
  const [scrollTarget, setScrollTarget] = React.useState<number | null>(null);

  const leadQuery = useQuery({
    queryKey: ['lead-detail', leadId],
    queryFn: () => leadService.getById(leadId),
    enabled: open && leadId > 0,
  });
  const lead = leadQuery.data;
  const items = React.useMemo(() => lead?.leadItems ?? [], [lead]);
  // The same retained file can be listed under both its email occurrence and its document
  // record; one file is one tab here.
  const inspectable = React.useMemo(() => {
    const seen = new Set<string>();
    return workbench.evidence.filter((evidence) => {
      const url = inspectableEvidenceUrl(evidence);
      if (!url || seen.has(url)) return false;
      seen.add(url);
      return true;
    });
  }, [workbench.evidence]);
  /** Each decision line paired with the lead item behind it, computed once per record. */
  const rows = React.useMemo(
    () => workbench.lines.map((line, index) => ({ line, item: matchLeadItem(line, index, items) })),
    [workbench.lines, items],
  );
  const unverified = React.useMemo(() => rows.filter(({ line }) => line.verificationStatus !== 'VERIFIED'), [rows]);
  const visibleRows = showAll || unverified.length === 0 ? rows : unverified;

  React.useEffect(() => {
    if (!open) { seeded.current = null; return; }
    if (!lead || seeded.current === lead.reviewVersion) return;
    const next = new Map<number, LineEdit>();
    for (const { line, item } of rows) {
      if (item) next.set(item.id, editFrom(line, item, decisions?.[line.revisionLineId], workbench.unitOptions ?? []));
    }
    setEdits(next);
    setDueDate(toDateInput(lead.bidClosingDate));
    setNote('');
    setShowAll(false);
    setPage(0);
    setEvidenceIndex(0);
    setRefusal(null);
    seeded.current = lead.reviewVersion;
  }, [open, lead, rows, decisions, workbench.unitOptions]);

  const mutation = useMutation({
    mutationFn: async () => {
      if (!lead) throw new Error('The request has not loaded yet.');
      const originalDue = toDateInput(lead.bidClosingDate);
      return extractionReviewService.submitReview(leadId, {
        action: 'approve',
        expectedVersion: lead.reviewVersion ?? 0,
        reason: note.trim() || DEFAULT_CHECK_REASON,
        header: dueDate && dueDate !== originalDue ? { bidClosingDate: dueDate } : {},
        items: buildReviewItems(items, edits),
      });
    },
    onSuccess: async () => {
      const confirmed: ConfirmedLine[] = rows.flatMap(({ line, item }) => {
        const edit = item ? edits.get(item.id) : undefined;
        if (!item || !edit) return [];
        const quantity = Number(edit.quantity);
        return [{
          lineItemNo: lineLabel(line),
          quantity: edit.quantity !== '' && Number.isFinite(quantity) ? quantity : undefined,
          unitOfMeasure: edit.unitOfMeasure || undefined,
          currency: edit.currency || undefined,
        }];
      });
      enqueueSnackbar('Confirmed against the document.', { variant: 'success' });
      // Hand the values back and close before the refetches, so the rep is not held on a closed
      // form; the page re-reads the workbench and carries the choices across the new revision.
      onConfirmed(confirmed);
      onClose();
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['lead-decision-workbench', leadId] }),
        queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] }),
        queryClient.invalidateQueries({ queryKey: ['needs-review'] }),
      ]);
    },
    onError: (error: unknown) => {
      // The refusal stays on the form, in the server's words, with the way out: a line the
      // dialog hid may be the one at fault, or the lead may no longer be open to this check.
      setRefusal(presentableErrorMessage(error, 'The check could not be recorded. Nothing was changed.'));
      setShowAll(true);
    },
  });

  const patch = (itemId: number, change: Partial<LineEdit>) =>
    setEdits((current) => {
      const next = new Map(current);
      next.set(itemId, { ...(current.get(itemId) ?? { productShortName: '', itemMaterialCode: '', manufacturerPartNumber: '', quantity: '', unitOfMeasure: '', currency: '' }), ...change });
      return next;
    });

  const unitOptions = workbench.unitOptions ?? [];
  const currencyOptions = workbench.currencyOptions ?? [];
  const unitCodes = new Set(unitOptions.map((option) => option.code.toUpperCase()));

  /** What still stops a line being confirmed, in words for the sentence beside the button. */
  const rowGap = ({ item }: { item?: LeadItemResponseDTO }): string | null => {
    const edit = item ? edits.get(item.id) : undefined;
    if (!item || !edit) return 'has no record that can be corrected here';
    // The review refuses a line with no product name or material code; a part number alone
    // does not satisfy it, so the name stays required here rather than failing on confirm.
    if (!edit.productShortName.trim()) return 'needs what they asked for';
    const quantity = Number(edit.quantity);
    if (edit.quantity === '' || !Number.isFinite(quantity) || quantity <= 0) return 'needs a quantity';
    // A unit the tenant does not quote in cannot be confirmed: the approval would record it, and
    // a line carrying it could never be quoted afterwards.
    if (!edit.unitOfMeasure.trim() || (unitOptions.length > 0 && !tenantUnitCode(edit.unitOfMeasure, unitOptions))) return 'needs a unit';
    return null;
  };
  const firstGap = (() => {
    for (const row of visibleRows) {
      const gap = rowGap(row);
      if (gap) return { line: row.line, gap };
    }
    return null;
  })();
  const incomplete = firstGap != null;

  // A page of lines at a time; completeness above still covers every line to check.
  const pageCount = Math.max(1, Math.ceil(visibleRows.length / CHECK_LINES_PER_PAGE));
  const currentPage = Math.min(page, pageCount - 1);
  const drawnRows = visibleRows.length > CHECK_LINES_PER_PAGE
    ? visibleRows.slice(currentPage * CHECK_LINES_PER_PAGE, (currentPage + 1) * CHECK_LINES_PER_PAGE)
    : visibleRows;
  const goToLine = (revisionLineId: number) => {
    const index = visibleRows.findIndex(({ line }) => line.revisionLineId === revisionLineId);
    if (index >= 0) setPage(Math.floor(index / CHECK_LINES_PER_PAGE));
    setScrollTarget(revisionLineId);
  };

  React.useEffect(() => {
    if (!open || focusLineId == null) return;
    goToLine(focusLineId);
    // Re-run once the record has loaded, so the rows exist to scroll to.
  }, [open, focusLineId, leadQuery.data]);

  React.useEffect(() => {
    if (scrollTarget == null) return;
    document.getElementById(`check-line-${scrollTarget}`)?.scrollIntoView?.({ block: 'center' });
    setScrollTarget(null);
  }, [scrollTarget, currentPage]);

  // Forty lines with no unit are one choice, not forty. Fills only lines still without a unit
  // the tenant quotes in; a unit already on a line stays.
  const unitlessRows = unitOptions.length === 0 ? [] : visibleRows.filter(({ item }) => {
    const edit = item ? edits.get(item.id) : undefined;
    return Boolean(item && edit && !tenantUnitCode(edit.unitOfMeasure, unitOptions));
  });
  const setUnitOnUnitless = (code: string) => setEdits((current) => {
    const next = new Map(current);
    for (const { item } of unitlessRows) {
      const edit = item ? current.get(item.id) : undefined;
      if (item && edit && !tenantUnitCode(edit.unitOfMeasure, unitOptions)) next.set(item.id, { ...edit, unitOfMeasure: code });
    }
    return next;
  });

  return (
    <Dialog open={open} onClose={mutation.isPending ? undefined : onClose} fullWidth maxWidth="xl" fullScreen={fullScreen} aria-labelledby="check-document-title">
      <DialogTitle id="check-document-title" sx={{ display: 'flex', alignItems: 'center', gap: 1.5, pr: 7 }}>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Typography component="span" sx={{ fontWeight: 800, fontSize: '1.1rem', display: 'block' }}>Check against the document</Typography>
          <Typography variant="body2" color="text.secondary">
            Read the original on the left. Correct anything Nexora got wrong on the right, then confirm. Lines you confirm count as checked.
          </Typography>
        </Box>
        <IconButton aria-label="Close" onClick={onClose} disabled={mutation.isPending} sx={{ position: 'absolute', right: 12, top: 12 }}>
          <CloseIcon />
        </IconButton>
      </DialogTitle>
      <DialogContent dividers sx={{ p: { xs: 1.5, md: 2 } }}>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ alignItems: 'stretch' }}>
          <Box sx={{ flex: '1 1 58%', minWidth: 0 }}>
            {inspectable.length > 1 ? (
              <Stack direction="row" spacing={1} sx={{ mb: 1, flexWrap: 'wrap' }}>
                {inspectable.map((evidence, index) => (
                  <Chip
                    key={`${evidence.occurrenceId}-${evidence.name}-${index}`}
                    label={evidence.name}
                    size="small"
                    color={index === evidenceIndex ? 'primary' : 'default'}
                    variant={index === evidenceIndex ? 'filled' : 'outlined'}
                    onClick={() => setEvidenceIndex(index)}
                  />
                ))}
              </Stack>
            ) : inspectable[0] ? (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>{inspectable[0].name}</Typography>
            ) : null}
            <DocumentViewer evidence={inspectable[evidenceIndex] ?? null} />
          </Box>

          <Box component="section" aria-label="Lines to check" sx={{ flex: '1 1 42%', minWidth: 0 }}>
            {leadQuery.isLoading ? (
              <Box sx={{ display: 'grid', placeItems: 'center', minHeight: 200 }}><CircularProgress size={28} /></Box>
            ) : !lead ? (
              <Alert severity="error" action={<Button color="inherit" onClick={() => leadQuery.refetch()}>Retry</Button>}>
                The request could not be loaded. Nothing was changed.
              </Alert>
            ) : (
              <Stack spacing={1.5}>
                {/* A background re-read that fails keeps the form and what was corrected on it. */}
                {leadQuery.isRefetchError ? (
                  <Alert severity="warning">Couldn&apos;t refresh this request just now. Your corrections here are kept.</Alert>
                ) : null}
                {refusal ? (
                  <Alert
                    severity="error"
                    onClose={() => setRefusal(null)}
                    action={(
                      <Button color="inherit" size="small" onClick={() => { onClose(); navigate(`/procurement/extraction/review/${leadId}`); }}>
                        Open the full review
                      </Button>
                    )}
                  >
                    {refusal}
                  </Alert>
                ) : null}
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' }}>
                  <Typography sx={{ fontWeight: 700 }}>
                    {unverified.length > 0
                      ? `${unverified.length} of ${workbench.lines.length} line${workbench.lines.length === 1 ? '' : 's'} to check`
                      : 'Every line is already checked'}
                  </Typography>
                  {unverified.length > 0 && unverified.length < workbench.lines.length ? (
                    <FormControlLabel
                      control={<Switch size="small" checked={showAll} onChange={(event) => { setShowAll(event.target.checked); setPage(0); }} />}
                      label={<Typography variant="body2">Show every line</Typography>}
                    />
                  ) : null}
                </Stack>

                <TextField
                  size="small"
                  type="date"
                  label="Quote due"
                  value={dueDate}
                  onChange={(event) => setDueDate(event.target.value)}
                  slotProps={{ inputLabel: { shrink: true } }}
                  sx={{ maxWidth: 220 }}
                />

                {unitlessRows.length >= 2 ? (
                  <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', flexWrap: 'wrap', rowGap: 1, p: 1.25, borderRadius: 2, bgcolor: 'action.hover' }}>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>{unitlessRows.length} lines to check have no unit.</Typography>
                    <FormControl size="small" error sx={{ minWidth: 160 }}>
                      <Select
                        value=""
                        displayEmpty
                        renderValue={() => <em>Unit for all {unitlessRows.length}</em>}
                        inputProps={{ 'aria-label': `Unit for the ${unitlessRows.length} lines to check without one` }}
                        onChange={(event) => { if (event.target.value) setUnitOnUnitless(String(event.target.value)); }}
                      >
                        {unitOptions.map((option) => (
                          <MenuItem key={option.code} value={option.code}>
                            {option.label && option.label !== option.code ? `${option.code} · ${option.label}` : option.code}
                          </MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                  </Stack>
                ) : null}

                {visibleRows.length > CHECK_LINES_PER_PAGE ? (
                  <TablePagination
                    component="div"
                    count={visibleRows.length}
                    page={currentPage}
                    onPageChange={(_event, next) => setPage(next)}
                    rowsPerPage={CHECK_LINES_PER_PAGE}
                    rowsPerPageOptions={[]}
                    labelDisplayedRows={({ from, to, count }) => `Lines ${from}–${to} of ${count} to check`}
                    getItemAriaLabel={(type) => `${type} page of lines to check`}
                  />
                ) : null}

                {drawnRows.map(({ line, item }) => {
                  const label = lineLabel(line);
                  const edit = item ? edits.get(item.id) : undefined;
                  if (!item || !edit) {
                    return (
                      <Alert key={line.revisionLineId} severity="warning" id={`check-line-${line.revisionLineId}`}>
                        Line {label} has no editable record behind it. Correct it on the full review screen.
                      </Alert>
                    );
                  }
                  const missingSource = line.verificationStatus === 'MISSING_SOURCE';
                  // Said under the unit picker: the customer's own word, that they gave none, or
                  // that the unit came from the rep's choice on the lines.
                  const unitReading = readUnit(line, unitCodes);
                  const chosenOnLines = unitOptions.length > 0 && unitReading.kind !== 'mapped' && Boolean(edit.unitOfMeasure)
                    && tenantUnitCode(decisions?.[line.revisionLineId]?.unitOfMeasure, unitOptions) === edit.unitOfMeasure;
                  const unitHint = chosenOnLines
                    ? `you chose ${edit.unitOfMeasure} on the lines${unitReading.kind === 'unrecognised' ? ` · as written: ${unitReading.asWritten}` : ''}`
                    : unitCaption(unitReading, edit.unitOfMeasure);
                  return (
                    <Box
                      key={line.revisionLineId}
                      id={`check-line-${line.revisionLineId}`}
                      sx={{ p: 1.5, border: 1, borderColor: line.revisionLineId === focusLineId ? 'primary.main' : 'divider', borderRadius: 2 }}
                    >
                      <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', mb: 1 }}>
                        <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>Line {label}</Typography>
                        {line.verificationStatus === 'VERIFIED' ? <Chip size="small" label="Checked" color="success" variant="outlined" /> : null}
                        {line.verificationDetail && !missingSource ? (
                          <Typography variant="caption" color="warning.main">{line.verificationDetail}</Typography>
                        ) : null}
                      </Stack>
                      {missingSource ? (
                        <Alert severity="warning" sx={{ mb: 1 }}>No source document is on file for this line. Confirming will not mark it checked.</Alert>
                      ) : null}
                      <Stack spacing={1}>
                        <TextField
                          size="small"
                          label="What they asked for"
                          value={edit.productShortName}
                          error={!edit.productShortName.trim()}
                          helperText={!edit.productShortName.trim() ? 'Needed. If the document only gives a part number, name the item in your own words.' : undefined}
                          onChange={(event) => patch(item.id, { productShortName: event.target.value })}
                          slotProps={{ htmlInput: { 'aria-label': `What they asked for, line ${label}` } }}
                        />
                        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
                          <TextField
                            size="small"
                            fullWidth
                            label="Their material code"
                            value={edit.itemMaterialCode}
                            onChange={(event) => patch(item.id, { itemMaterialCode: event.target.value })}
                            slotProps={{ htmlInput: { 'aria-label': `Their material code, line ${label}` } }}
                            helperText="The buyer's own number for this line."
                          />
                          <TextField
                            size="small"
                            fullWidth
                            label={line.manufacturerName ? `Part number (${line.manufacturerName})` : 'Maker part number'}
                            value={edit.manufacturerPartNumber}
                            onChange={(event) => patch(item.id, { manufacturerPartNumber: event.target.value })}
                            slotProps={{ htmlInput: { 'aria-label': `Maker part number, line ${label}` } }}
                            helperText="The maker's number, if the document names one."
                          />
                        </Stack>
                        {line.specification ? (
                          <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap', color: 'text.secondary', maxHeight: 160, overflowY: 'auto', px: 1, py: 0.5, border: 1, borderColor: 'divider', borderRadius: 1 }}>
                            {line.specification}
                          </Typography>
                        ) : null}
                        <Stack direction="row" spacing={1}>
                          <TextField
                            size="small"
                            type="number"
                            label="Quantity"
                            value={edit.quantity}
                            error={edit.quantity === '' || Number(edit.quantity) <= 0}
                            onChange={(event) => patch(item.id, { quantity: event.target.value })}
                            slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': `Quantity, line ${label}` } }}
                            sx={{ width: 120 }}
                          />
                          {unitOptions.length > 0 ? (
                            <FormControl size="small" error={!tenantUnitCode(edit.unitOfMeasure, unitOptions)} sx={{ minWidth: 110 }}>
                              <Select
                                value={tenantUnitCode(edit.unitOfMeasure, unitOptions) ?? ''}
                                displayEmpty
                                renderValue={(value: string) => value || <em>Unit</em>}
                                inputProps={{ 'aria-label': `Unit, line ${label}` }}
                                onChange={(event) => patch(item.id, { unitOfMeasure: event.target.value })}
                              >
                                {unitOptions.map((option) => <MenuItem key={option.code} value={option.code}>{option.code}</MenuItem>)}
                              </Select>
                              {unitHint ? <FormHelperText sx={{ mx: 0, maxWidth: 240 }}>{unitHint}</FormHelperText> : null}
                            </FormControl>
                          ) : (
                            <TextField
                              size="small"
                              label="Unit"
                              value={edit.unitOfMeasure}
                              error={!edit.unitOfMeasure}
                              onChange={(event) => patch(item.id, { unitOfMeasure: event.target.value })}
                              slotProps={{ htmlInput: { 'aria-label': `Unit, line ${label}` } }}
                              sx={{ width: 110 }}
                            />
                          )}
                          <FormControl size="small" sx={{ minWidth: 130 }}>
                            <Select
                              value={edit.currency}
                              displayEmpty
                              renderValue={(value: string) => value || <em>Currency</em>}
                              inputProps={{ 'aria-label': `Currency, line ${label}` }}
                              onChange={(event) => patch(item.id, { currency: event.target.value })}
                            >
                              <MenuItem value=""><em>Not stated</em></MenuItem>
                              {currencyOptions.map((option) => <MenuItem key={option.code} value={option.code}>{option.code}</MenuItem>)}
                              {!currencyOptions.some((option) => option.code === edit.currency) && edit.currency ? (
                                <MenuItem value={edit.currency}>{edit.currency}</MenuItem>
                              ) : null}
                            </Select>
                          </FormControl>
                        </Stack>
                      </Stack>
                    </Box>
                  );
                })}

                <TextField
                  size="small"
                  label="Note for the audit trail (optional)"
                  placeholder={DEFAULT_CHECK_REASON}
                  value={note}
                  onChange={(event) => setNote(event.target.value.slice(0, 500))}
                  multiline
                  minRows={2}
                />
              </Stack>
            )}
          </Box>
        </Stack>
      </DialogContent>
      <DialogActions sx={{ px: 2.5, py: 1.5, gap: 1 }}>
        {firstGap && lead && items.length > 0 ? (
          // The disabled button says why, and takes the rep to the line — which may be on
          // another page of the check.
          <Typography variant="body2" sx={{ flex: 1, color: 'warning.dark', fontWeight: 600 }}>
            Line {lineLabel(firstGap.line)} {firstGap.gap}.{' '}
            <Link component="button" type="button" onClick={() => goToLine(firstGap.line.revisionLineId)} sx={{ fontWeight: 700, verticalAlign: 'baseline' }}>
              Show line {lineLabel(firstGap.line)}
            </Link>
          </Typography>
        ) : (
          <Typography variant="body2" color="text.secondary" sx={{ flex: 1 }}>
            Confirming records that a person checked these lines against the document.
          </Typography>
        )}
        <Button color="inherit" onClick={onClose} disabled={mutation.isPending}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!lead || mutation.isPending || incomplete || items.length === 0}
          onClick={() => mutation.mutate()}
          sx={{ fontWeight: 800 }}
        >
          {mutation.isPending ? 'Recording…' : 'Confirm what the document says'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default CheckDocumentDialog;
