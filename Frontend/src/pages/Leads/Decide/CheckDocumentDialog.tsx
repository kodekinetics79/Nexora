import React from 'react';
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
  IconButton,
  MenuItem,
  Select,
  Stack,
  Switch,
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
import { fetchAuthenticatedObjectUrl } from '../../../utils/authenticatedFile';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { inspectableEvidenceUrl } from '../Workbench/evidenceRules';
import { lineLabel } from './decideRules';

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
}

interface LineEdit {
  productShortName: string;
  manufacturerPartNumber: string;
  quantity: string;
  unitOfMeasure: string;
  currency: string;
}

export const DEFAULT_CHECK_REASON = 'Checked against the source document on the decision screen.';

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

const editFrom = (line: LeadDecisionLineDTO, item: LeadItemResponseDTO | undefined): LineEdit => ({
  productShortName: item?.productShortName ?? line.productName ?? line.description ?? '',
  manufacturerPartNumber: item?.manufacturerPartNumber ?? line.manufacturerPartNumber ?? '',
  quantity: item?.quantity != null ? String(item.quantity) : line.quantity != null ? String(line.quantity) : '',
  unitOfMeasure: item?.unitOfMeasure ?? line.unitOfMeasure ?? '',
  currency: item?.currency ?? line.currency ?? '',
});

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
    itemMaterialCode: item.itemMaterialCode || undefined,
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

/** Browsers download rather than display these inside a frame, so they are shown as text. */
const isTextLike = (contentType: string, name: string): boolean =>
  /^text\//.test(contentType) || /json|csv|xml/.test(contentType) || /\.(csv|txt|json|xml|md)$/i.test(name);

type ViewerState = { url: string; contentType: string; text?: string } | { error: string } | null;

const DocumentViewer: React.FC<{ evidence: LeadDecisionEvidenceDTO | null }> = ({ evidence }) => {
  const [state, setState] = React.useState<ViewerState>(null);
  const path = evidence ? inspectableEvidenceUrl(evidence) : null;
  const name = evidence?.name ?? '';

  React.useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    setState(null);
    if (!path) return undefined;
    fetchAuthenticatedObjectUrl(path)
      .then(async (result) => {
        const text = isTextLike(result.contentType, name) ? await result.blob.text() : undefined;
        if (cancelled) { URL.revokeObjectURL(result.url); return; }
        url = result.url;
        setState({ url: result.url, contentType: result.contentType, text });
      })
      .catch((error: unknown) => {
        if (!cancelled) setState({ error: presentableErrorMessage(error, 'The document could not be opened.') });
      });
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [path, name]);

  if (!evidence || !path) {
    return (
      <Alert severity="warning">No document is on file for this request, so there is nothing to check against.</Alert>
    );
  }
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
  const isImage = state.contentType.startsWith('image/');
  return isImage ? (
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
  open, leadId, workbench, focusLineId, onClose, onConfirmed,
}) => {
  const theme = useTheme();
  const fullScreen = useMediaQuery(theme.breakpoints.down('md'));
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [showAll, setShowAll] = React.useState(false);
  const [edits, setEdits] = React.useState<Map<number, LineEdit>>(new Map());
  const [dueDate, setDueDate] = React.useState('');
  const [note, setNote] = React.useState('');
  const [evidenceIndex, setEvidenceIndex] = React.useState(0);
  const seeded = React.useRef<number | null>(null);

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
  const unverified = workbench.lines.filter((line) => line.verificationStatus !== 'VERIFIED');
  const visibleLines = showAll || unverified.length === 0 ? workbench.lines : unverified;

  React.useEffect(() => {
    if (!open) { seeded.current = null; return; }
    if (!lead || seeded.current === lead.reviewVersion) return;
    const next = new Map<number, LineEdit>();
    workbench.lines.forEach((line, index) => {
      const item = matchLeadItem(line, index, items);
      if (item) next.set(item.id, editFrom(line, item));
    });
    setEdits(next);
    setDueDate(toDateInput(lead.bidClosingDate));
    setNote('');
    setShowAll(false);
    setEvidenceIndex(0);
    seeded.current = lead.reviewVersion;
  }, [open, lead, items, workbench.lines]);

  React.useEffect(() => {
    if (!open || focusLineId == null) return;
    const node = document.getElementById(`check-line-${focusLineId}`);
    node?.scrollIntoView({ block: 'center' });
  }, [open, focusLineId, leadQuery.data]);

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
      const confirmed: ConfirmedLine[] = workbench.lines.flatMap((line, index) => {
        const item = matchLeadItem(line, index, items);
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
      await queryClient.invalidateQueries({ queryKey: ['lead-decision-workbench', leadId] });
      await queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] });
      await queryClient.invalidateQueries({ queryKey: ['needs-review'] });
      onConfirmed(confirmed);
      onClose();
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The check could not be recorded. Nothing was changed.'),
      { variant: 'error' },
    ),
  });

  const patch = (itemId: number, change: Partial<LineEdit>) =>
    setEdits((current) => {
      const next = new Map(current);
      next.set(itemId, { ...(current.get(itemId) ?? { productShortName: '', manufacturerPartNumber: '', quantity: '', unitOfMeasure: '', currency: '' }), ...change });
      return next;
    });

  const unitOptions = workbench.unitOptions ?? [];
  const currencyOptions = workbench.currencyOptions ?? [];
  const incomplete = visibleLines.some((line, index) => {
    const item = matchLeadItem(line, workbench.lines.indexOf(line), items);
    const edit = item ? edits.get(item.id) : undefined;
    if (!edit) return index >= 0;
    const quantity = Number(edit.quantity);
    // The review refuses a line with no product name or material code; a part number alone
    // does not satisfy it, so the name stays required here rather than failing on confirm.
    return !edit.productShortName.trim() || edit.quantity === '' || !Number.isFinite(quantity) || quantity <= 0 || !edit.unitOfMeasure.trim();
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
            ) : leadQuery.isError || !lead ? (
              <Alert severity="error" action={<Button color="inherit" onClick={() => leadQuery.refetch()}>Retry</Button>}>
                The request could not be loaded. Nothing was changed.
              </Alert>
            ) : (
              <Stack spacing={1.5}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' }}>
                  <Typography sx={{ fontWeight: 700 }}>
                    {unverified.length > 0
                      ? `${unverified.length} of ${workbench.lines.length} line${workbench.lines.length === 1 ? '' : 's'} to check`
                      : 'Every line is already checked'}
                  </Typography>
                  {unverified.length > 0 && unverified.length < workbench.lines.length ? (
                    <FormControlLabel
                      control={<Switch size="small" checked={showAll} onChange={(event) => setShowAll(event.target.checked)} />}
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

                {visibleLines.map((line) => {
                  const index = workbench.lines.indexOf(line);
                  const item = matchLeadItem(line, index, items);
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
                        <TextField
                          size="small"
                          label="Part number"
                          value={edit.manufacturerPartNumber}
                          onChange={(event) => patch(item.id, { manufacturerPartNumber: event.target.value })}
                          slotProps={{ htmlInput: { 'aria-label': `Part number, line ${label}` } }}
                        />
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
                            <FormControl size="small" error={!edit.unitOfMeasure} sx={{ minWidth: 110 }}>
                              <Select
                                value={edit.unitOfMeasure}
                                displayEmpty
                                renderValue={(value: string) => value || <em>Unit</em>}
                                inputProps={{ 'aria-label': `Unit, line ${label}` }}
                                onChange={(event) => patch(item.id, { unitOfMeasure: event.target.value })}
                              >
                                {unitOptions.map((option) => <MenuItem key={option.code} value={option.code}>{option.code}</MenuItem>)}
                              </Select>
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
        <Typography variant="body2" color="text.secondary" sx={{ flex: 1 }}>
          Confirming records that a person checked these lines against the document.
        </Typography>
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
