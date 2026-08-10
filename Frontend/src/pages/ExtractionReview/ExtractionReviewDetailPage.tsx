import React, { useState, useEffect, useMemo, useCallback, useRef } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Chip, Grid, CircularProgress, Stack,
  IconButton, Breadcrumbs, Link, Dialog, DialogTitle, DialogContent,
  DialogActions, TextField, Alert, Tooltip, Divider, Popover, FormGroup,
  FormControlLabel, Checkbox,
} from '@mui/material';
import {
  DataGrid, GridActionsCellItem, useGridApiRef,
  type GridColDef, type GridRowModel, type GridRowId, type GridCellParams,
  type GridColumnVisibilityModel, type GridRowSelectionModel, type GridPaginationModel,
} from '@mui/x-data-grid';
import {
  Description as FileIcon,
  OpenInNew as OpenIcon,
  NavigateNext as NextIcon,
  Save as SaveIcon,
  CheckCircle as ApproveIcon,
  Add as AddIcon,
  Delete as DeleteIcon,
  ArrowBack as BackIcon,
  ViewColumn as ColumnsIcon,
  EditNote as BulkEditIcon,
  RadioButtonUnchecked as VerifiedDotIcon,
  ErrorOutlined as NeedsCheckDotIcon,
} from '@mui/icons-material';
import dayjs from 'dayjs';
import { ChevronDown, ChevronUp, Cloud, Cpu, FileSearch } from 'lucide-react';
import { useSnackbar } from 'notistack';
import extractionReviewService from '../../api/services/extractionReviewService';
import type {
  LeadProcessingEvidence, SubmitReviewPayload, ReviewItemPayload,
  LeadFieldEvidenceEntry,
} from '../../api/services/extractionReviewService';
import { fieldEvidenceKey } from '../../api/services/extractionReviewService';
import type { LeadItemResponseDTO } from '../../api/services/leadService';
import ClientIdentityPanel from '../Leads/ClientIdentityPanel';
import type { ClientSelection } from '../Leads/ResolveClientDialog';
import { useAuth } from '../../context/AuthContext';
import { openAuthenticatedFile } from '../../utils/authenticatedFile';
import FieldEvidencePopover from './FieldEvidencePopover';
import {
  checkLine,
  summariseChecks,
  checkHeadline,
  documentAssertions,
  type CheckableLine,
  type FieldCheckSignal,
} from './needsCheck';

// Local editable representation of a line item. `id` doubles as the DataGrid
// row id; new rows added during review use negative ids and set `isNew` so the
// payload builder knows to omit the id.
//
// Deliberately carries NO confidence field. The extraction confidence this page
// used to render was never measured — see ./needsCheck.ts — so the local model
// simply does not hold it and no future edit can reintroduce the display.
interface ReviewLineItem extends CheckableLine {
  id: number;
  isNew?: boolean;
  lineItemNo?: string;
  productShortName?: string;
  productShortDescription?: string;
  commodityProduct?: string;
  itemMaterialCode?: string;
  currency?: string;
  unitOfMeasure?: string;
  unitPrice?: number | null;
  quantity?: number | null;
  manufacturerName?: string;
  manufacturerPartNumber?: string;
  alternateProductName?: string;
  alternatePartNumber?: string;
  itemText?: string;
  leadTime?: string;
  // Unrecognized customer-document columns captured at extraction time. Read-only
  // in the workbench and deliberately excluded from the submit payload so the
  // backend preserves the stored values untouched.
  extraFields?: Record<string, string> | null;
}

interface ReviewHeaderState {
  rfqno: string;
  buyersName: string;
  bidClosingDate: string;
  opportunityNo: string;
  headerRemarks: string;
}

// ---------------------------------------------------------------------------
// Per-user view preferences (column visibility), persisted locally. Key shape
// copied from LeadsPage so one reviewer's preferences never leak into another
// account sharing the same browser.
// ---------------------------------------------------------------------------

const COLUMNS_KEY_BASE = 'nexora.extractionReview.columnVisibility';

const userScopedKey = (base: string): string => {
  try {
    const raw = localStorage.getItem('userData');
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && 'id' in parsed) {
        const id = (parsed as { id?: unknown }).id;
        if (typeof id === 'number' || typeof id === 'string') return `${base}:user-${id}`;
      }
    }
  } catch {
    // Corrupted userData — fall back to a global preference key.
  }
  return `${base}:global`;
};

// Curated default column set. Four rarely-populated columns start hidden so the
// grid fits a laptop viewport without horizontal scrolling; they stay one click
// away rather than being deleted, because the data behind them is real.
const DEFAULT_COLUMN_VISIBILITY: GridColumnVisibilityModel = {
  checkStatus: true,
  lineItemNo: true,
  productShortName: true,
  productShortDescription: true,
  quantity: true,
  unitOfMeasure: true,
  unitPrice: true,
  currency: true,
  manufacturerName: true,
  manufacturerPartNumber: true,
  itemMaterialCode: true,
  leadTime: true,
  commodityProduct: false,
  alternateProductName: false,
  alternatePartNumber: false,
  itemText: false,
  actions: true,
};

const HIDEABLE_COLUMNS: ReadonlyArray<{ field: string; label: string }> = [
  { field: 'lineItemNo', label: 'Line #' },
  { field: 'productShortName', label: 'Product' },
  { field: 'productShortDescription', label: 'Description' },
  { field: 'quantity', label: 'Qty' },
  { field: 'unitOfMeasure', label: 'UoM' },
  { field: 'unitPrice', label: 'Unit Price' },
  { field: 'currency', label: 'Currency' },
  { field: 'manufacturerName', label: 'Manufacturer' },
  { field: 'manufacturerPartNumber', label: 'Mfr Part #' },
  { field: 'itemMaterialCode', label: 'Material Code' },
  { field: 'leadTime', label: 'Lead Time' },
  { field: 'commodityProduct', label: 'Commodity' },
  { field: 'alternateProductName', label: 'Alt Product' },
  { field: 'alternatePartNumber', label: 'Alt Part #' },
  { field: 'itemText', label: 'Item Text' },
];

const loadColumnVisibility = (): GridColumnVisibilityModel => {
  try {
    const raw = localStorage.getItem(userScopedKey(COLUMNS_KEY_BASE));
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) {
        return { ...DEFAULT_COLUMN_VISIBILITY, ...(parsed as GridColumnVisibilityModel), checkStatus: true, actions: true };
      }
    }
  } catch {
    // Unreadable preference — use the curated defaults.
  }
  return { ...DEFAULT_COLUMN_VISIBILITY };
};

// ---------------------------------------------------------------------------
// Draft persistence. A reviewer who loses twenty minutes of corrections once
// goes back to Excel permanently, so unsent work is written to sessionStorage
// on every keystroke and restored on return. This is the guard that actually
// holds: `useBlocker` needs a data router, and this app mounts <BrowserRouter>.
// ---------------------------------------------------------------------------

const draftKey = (leadId: number): string => `nexora.extractionReview.draft.${leadId}`;

interface ReviewDraft {
  savedAt: string;
  header: ReviewHeaderState;
  items: ReviewLineItem[];
}

const readDraft = (leadId: number): ReviewDraft | null => {
  try {
    const raw = sessionStorage.getItem(draftKey(leadId));
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (parsed && typeof parsed === 'object' && 'header' in parsed && 'items' in parsed) {
      return parsed as ReviewDraft;
    }
  } catch {
    // Unreadable draft — start from the server copy rather than guessing.
  }
  return null;
};

const clearDraft = (leadId: number) => {
  try {
    sessionStorage.removeItem(draftKey(leadId));
  } catch {
    // Storage unavailable — nothing to clear.
  }
};

const readable = (text?: string | null): string =>
  (text || 'Not recorded').replaceAll('_', ' ').toLowerCase().replace(/^./, (letter) => letter.toUpperCase());

const costLabel = (
  amount: number | null | undefined,
  currency: string | null | undefined,
  status: string | null | undefined,
): string => {
  const normalizedStatus = (status || '').toUpperCase();
  if (amount == null || !Number.isFinite(amount) || normalizedStatus.includes('UNKNOWN') || normalizedStatus.includes('UNAVAILABLE') || normalizedStatus.includes('UNPRICED')) {
    return 'Cost unavailable';
  }
  return `${currency || 'Currency not recorded'} ${amount.toLocaleString(undefined, { maximumFractionDigits: 6 })}`;
};

const evidenceValue = (value: string | number | null | undefined): string => value == null || value === '' ? 'Not linked' : String(value);

const toDateInput = (value?: string | null): string => {
  if (!value) return '';
  const d = dayjs(value);
  return d.isValid() ? d.format('YYYY-MM-DD') : '';
};

const mapItems = (items: LeadItemResponseDTO[] | undefined): ReviewLineItem[] =>
  (items ?? []).map((it) => ({
    id: it.id,
    lineItemNo: it.lineItemNo ?? '',
    productShortName: it.productShortName ?? '',
    productShortDescription: it.productShortDescription ?? '',
    commodityProduct: it.commodityProduct ?? '',
    itemMaterialCode: it.itemMaterialCode ?? '',
    currency: it.currency ?? '',
    unitOfMeasure: it.unitOfMeasure ?? '',
    unitPrice: it.unitPrice ?? null,
    quantity: it.quantity ?? null,
    manufacturerName: it.manufacturerName ?? '',
    manufacturerPartNumber: it.manufacturerPartNumber ?? '',
    alternateProductName: it.alternateProductName ?? '',
    alternatePartNumber: it.alternatePartNumber ?? '',
    itemText: it.itemText ?? '',
    leadTime: it.leadTime ?? '',
    extraFields: it.extraFields ?? null,
  }));

/** Bulk-settable columns. Both are low-cardinality and wrong on whole documents at a time. */
type BulkField = 'unitOfMeasure' | 'currency';
const BULK_FIELD_LABEL: Record<BulkField, string> = {
  unitOfMeasure: 'Unit of measure',
  currency: 'Currency',
};

const ExtractionReviewDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const leadId = Number(id);
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canEditLeads = hasPermission('Leads', 'edit');
  const apiRef = useGridApiRef();

  // Review reason can be passed from the queue via router state; it isn't part
  // of the lead detail payload, so we fall back gracefully when navigated to
  // directly.
  const reviewReasonFromState = (location.state as { reviewReason?: string } | null)?.reviewReason;

  const { data: lead, isLoading, isError, refetch } = useQuery({
    queryKey: ['needs-review-detail', leadId],
    queryFn: () => extractionReviewService.getLead(leadId),
    enabled: !!id,
  });
  const processingEvidence = useQuery({
    queryKey: ['lead-processing-evidence', leadId],
    queryFn: () => extractionReviewService.getProcessingEvidence(leadId),
    enabled: !!id && Number.isFinite(leadId),
    retry: false,
  });
  // Where each stored value sat in the customer's own document. Absent for
  // PDF/OCR extractions, which never persisted word boxes — the UI says so.
  const fieldEvidence = useQuery({
    queryKey: ['lead-field-evidence', leadId],
    queryFn: () => extractionReviewService.getFieldEvidence(leadId),
    enabled: !!id && Number.isFinite(leadId),
    retry: false,
  });

  const [header, setHeader] = useState<ReviewHeaderState>({
    rfqno: '', buyersName: '', bidClosingDate: '', opportunityNo: '', headerRemarks: '',
  });
  const [items, setItems] = useState<ReviewLineItem[]>([]);
  // Client organisation staged by the reviewer. Deliberately NOT written on
  // selection: it travels in this page's single submission alongside the header
  // and line-item corrections, so one save carries one review version and the
  // reviewer's other edits cannot be lost to a version conflict.
  const [clientSelection, setClientSelection] = useState<ClientSelection | null>(null);
  const [newRowSeq, setNewRowSeq] = useState(-1);
  const [approveDialogOpen, setApproveDialogOpen] = useState(false);
  const [approvalReason, setApprovalReason] = useState('Verified against the source document.');
  const [processingEvidenceExpanded, setProcessingEvidenceExpanded] = useState(false);
  const [columnVisibility, setColumnVisibility] = useState<GridColumnVisibilityModel>(loadColumnVisibility);
  const [columnsAnchor, setColumnsAnchor] = useState<HTMLElement | null>(null);
  const [selection, setSelection] = useState<GridRowSelectionModel>({ type: 'include', ids: new Set<GridRowId>() });
  const [bulkField, setBulkField] = useState<BulkField | null>(null);
  const [bulkValue, setBulkValue] = useState('');
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 50, page: 0 });
  const [focusedLineId, setFocusedLineId] = useState<number | null>(null);
  const [pendingDelete, setPendingDelete] = useState<ReviewLineItem | null>(null);
  const [leaveConfirmTarget, setLeaveConfirmTarget] = useState<string | null>(null);
  const [restoredDraftAt, setRestoredDraftAt] = useState<string | null>(null);
  const [evidenceAnchor, setEvidenceAnchor] = useState<{ el: HTMLElement; field: string; label: string; lineId: number; lineLabel: string } | null>(null);

  // Serialized server copy, used to decide whether there is unsent work.
  const baselineRef = useRef<string>('');

  // Seed editable state once the lead loads, preferring an unsent draft.
  useEffect(() => {
    if (!lead) return;
    const serverHeader: ReviewHeaderState = {
      rfqno: lead.rfqno ?? '',
      buyersName: lead.buyersName ?? '',
      bidClosingDate: toDateInput(lead.bidClosingDate),
      opportunityNo: lead.opportunityNo ?? '',
      headerRemarks: lead.headerRemarks ?? '',
    };
    const serverItems = mapItems(lead.leadItems);
    baselineRef.current = JSON.stringify({ header: serverHeader, items: serverItems });

    const draft = readDraft(lead.id);
    if (draft && JSON.stringify({ header: draft.header, items: draft.items }) !== baselineRef.current) {
      setHeader(draft.header);
      setItems(draft.items);
      setRestoredDraftAt(draft.savedAt);
      // Rows the reviewer had added carry negative ids. Resume the sequence
      // below the lowest of them, or two ids would collide on the next add.
      const lowest = draft.items.reduce((min, row) => Math.min(min, row.id), 0);
      setNewRowSeq(lowest < 0 ? lowest - 1 : -1);
    } else {
      setHeader(serverHeader);
      setItems(serverItems);
      setRestoredDraftAt(null);
      setNewRowSeq(-1);
    }
    setClientSelection(null);
    setSelection({ type: 'include', ids: new Set<GridRowId>() });
  }, [lead]);

  const isDirty = useMemo(() => {
    if (!baselineRef.current) return false;
    if (clientSelection) return true;
    return JSON.stringify({ header, items }) !== baselineRef.current;
  }, [header, items, clientSelection]);

  // Persist unsent work so a mistaken sidebar click costs nothing. Debounced:
  // the largest pilot document carries ~1,450 lines, and serialising that on
  // every keystroke would make typing stutter for no benefit.
  useEffect(() => {
    if (!lead || !baselineRef.current) return;
    if (!isDirty) {
      clearDraft(lead.id);
      return;
    }
    const handle = window.setTimeout(() => {
      try {
        sessionStorage.setItem(draftKey(lead.id), JSON.stringify({ savedAt: new Date().toISOString(), header, items } satisfies ReviewDraft));
      } catch {
        // Storage full or blocked — the in-page guard still protects the session.
      }
    }, 800);
    return () => window.clearTimeout(handle);
  }, [lead, header, items, isDirty]);

  // Browser-level guard: refresh, tab close, and any navigation the router
  // never sees.
  useEffect(() => {
    if (!isDirty) return;
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => window.removeEventListener('beforeunload', onBeforeUnload);
  }, [isDirty]);

  // Field evidence indexed for O(1) cell lookup, plus the per-line validation
  // statuses that feed the needs-check verdict.
  const { evidenceByKey, flaggedByLine, documentUnmapped } = useMemo(() => {
    const byKey = new Map<string, LeadFieldEvidenceEntry>();
    const flagged = new Map<number, Map<string, FieldCheckSignal>>();
    const entries = fieldEvidence.data?.entries ?? [];
    for (const entry of entries) {
      byKey.set(fieldEvidenceKey(entry.lineItemId, entry.fieldName), entry);
      if (entry.lineItemId != null && entry.validationStatus) {
        const forLine = flagged.get(entry.lineItemId) ?? new Map<string, FieldCheckSignal>();
        // The recorded RAW text travels with the status. Without it a ledger
        // warning for "the buyer left this for the supplier to price" is
        // indistinguishable from "there was text here we could not read", and
        // the screen flagged both.
        forLine.set(entry.fieldName, { status: entry.validationStatus, rawValue: entry.rawValue });
        flagged.set(entry.lineItemId, forLine);
      }
    }
    const mapped = fieldEvidence.data?.mapped ?? entries.length > 0;
    return { evidenceByKey: byKey, flaggedByLine: flagged, documentUnmapped: !mapped };
  }, [fieldEvidence.data]);

  const documentNarrative = (fieldEvidence.data?.documentNarrative ?? '').trim() || null;

  // What this document states at all. A unit of measure no line carries is a
  // unit the buyer never wrote, not a value we failed to read.
  const assertions = useMemo(() => documentAssertions(items), [items]);
  const summary = useMemo(
    () => summariseChecks(items, flaggedByLine),
    [items, flaggedByLine],
  );

  const hasAuthoritativeSource = (lead?.attachments?.length ?? 0) > 0
    || (processingEvidence.data?.occurrences.some((occurrence) => occurrence.sourceDocumentId != null) ?? false);
  const approvalBlockedReason = processingEvidence.isLoading
      ? 'Authoritative processing evidence is still loading.'
      : processingEvidence.isError
        ? 'Authoritative processing evidence could not be loaded.'
        : !processingEvidence.data
          ? 'No authoritative processing evidence is linked to this Lead.'
          : !hasAuthoritativeSource
            ? 'No source attachment is available for verification.'
          : null;

  const handleHeaderChange = (field: keyof ReviewHeaderState) =>
    (e: React.ChangeEvent<HTMLInputElement>) => setHeader((prev) => ({ ...prev, [field]: e.target.value }));

  const processRowUpdate = (newRow: GridRowModel): GridRowModel => {
    const updated = newRow as ReviewLineItem;
    setItems((prev) => prev.map((row) => (row.id === updated.id ? updated : row)));
    return newRow;
  };

  const handleAddRow = () => {
    const tempId = newRowSeq;
    setNewRowSeq((s) => s - 1);
    setItems((prev) => [
      ...prev,
      {
        id: tempId, isNew: true, lineItemNo: '', productShortName: '', productShortDescription: '',
        commodityProduct: '', itemMaterialCode: '', currency: '', unitOfMeasure: '',
        unitPrice: null, quantity: null, manufacturerName: '', manufacturerPartNumber: '',
        alternateProductName: '', alternatePartNumber: '', itemText: '', leadTime: '',
      },
    ]);
  };

  const applyColumnVisibility = (model: GridColumnVisibilityModel) => {
    const next: GridColumnVisibilityModel = { ...model, checkStatus: true, actions: true };
    setColumnVisibility(next);
    try {
      localStorage.setItem(userScopedKey(COLUMNS_KEY_BASE), JSON.stringify(next));
    } catch {
      // Storage unavailable — preference just won't persist.
    }
  };

  const selectedIds = useMemo<number[]>(() => {
    if (selection.type === 'exclude') {
      return items.filter((row) => !selection.ids.has(row.id)).map((row) => row.id);
    }
    return items.filter((row) => selection.ids.has(row.id)).map((row) => row.id);
  }, [selection, items]);

  const applyBulkValue = () => {
    if (!bulkField) return;
    const value = bulkValue.trim();
    const target = new Set(selectedIds);
    setItems((prev) => prev.map((row) => (target.has(row.id) ? { ...row, [bulkField]: value } : row)));
    enqueueSnackbar(
      `${BULK_FIELD_LABEL[bulkField]} set to "${value || '(blank)'}" on ${target.size} line${target.size === 1 ? '' : 's'}`,
      { variant: 'success' },
    );
    setBulkField(null);
    setBulkValue('');
  };

  /** Existing values for the bulk field, offered as suggestions. */
  const bulkSuggestions = useMemo<string[]>(() => {
    if (!bulkField) return [];
    const values = new Set<string>();
    for (const row of items) {
      const value = (row[bulkField] ?? '').toString().trim();
      if (value) values.add(value);
    }
    return [...values].sort((a, b) => a.localeCompare(b));
  }, [bulkField, items]);

  /** Moves the grid to the next line still needing a check. */
  const jumpToNextCheck = useCallback(() => {
    if (summary.needsCheckIds.length === 0) return;
    const currentIndex = focusedLineId == null ? -1 : summary.needsCheckIds.indexOf(focusedLineId);
    const nextId = summary.needsCheckIds[(currentIndex + 1) % summary.needsCheckIds.length];
    const rowIndex = items.findIndex((row) => row.id === nextId);
    if (rowIndex < 0) return;
    const targetPage = Math.floor(rowIndex / paginationModel.pageSize);
    if (targetPage !== paginationModel.page) {
      setPaginationModel((prev) => ({ ...prev, page: targetPage }));
    }
    setFocusedLineId(nextId);
    window.setTimeout(() => {
      try {
        apiRef.current?.scrollToIndexes({ rowIndex: rowIndex - targetPage * paginationModel.pageSize });
      } catch {
        // Grid not mounted yet — the page change alone already surfaces the row.
      }
    }, 0);
  }, [summary.needsCheckIds, focusedLineId, items, paginationModel, apiRef]);

  const buildPayload = (action: 'save' | 'approve'): SubmitReviewPayload => ({
    action,
    expectedVersion: lead?.reviewVersion ?? 0,
    reason: action === 'approve' ? approvalReason.trim() : undefined,
    header: {
      rfqno: header.rfqno || undefined,
      buyersName: header.buyersName || undefined,
      bidClosingDate: header.bidClosingDate || undefined,
      opportunityNo: header.opportunityNo || undefined,
      headerRemarks: header.headerRemarks || undefined,
      // Only sent when the reviewer picked a client in this session. Omitted
      // otherwise, so a save never re-stamps (or clears) the existing link.
      ...(clientSelection ? { customerId: clientSelection.customerId } : {}),
      ...(clientSelection?.contactId != null ? { contactId: clientSelection.contactId } : {}),
    },
    items: items.map<ReviewItemPayload>((it) => ({
      id: it.isNew ? undefined : it.id,
      lineItemNo: it.lineItemNo || undefined,
      productShortName: it.productShortName || undefined,
      productShortDescription: it.productShortDescription || undefined,
      commodityProduct: it.commodityProduct || undefined,
      itemMaterialCode: it.itemMaterialCode || undefined,
      currency: it.currency || undefined,
      unitOfMeasure: it.unitOfMeasure || undefined,
      unitPrice: it.unitPrice ?? undefined,
      quantity: it.quantity ?? undefined,
      manufacturerName: it.manufacturerName || undefined,
      manufacturerPartNumber: it.manufacturerPartNumber || undefined,
      alternateProductName: it.alternateProductName || undefined,
      alternatePartNumber: it.alternatePartNumber || undefined,
      itemText: it.itemText || undefined,
      leadTime: it.leadTime || undefined,
    })),
  });

  const mutation = useMutation({
    mutationFn: (action: 'save' | 'approve') =>
      extractionReviewService.submitReview(leadId, buildPayload(action)),
    onSuccess: (_data, action) => {
      clearDraft(leadId);
      setRestoredDraftAt(null);
      enqueueSnackbar(
        action === 'approve' ? 'Extraction approved successfully' : 'Corrections saved and kept in the review queue',
        { variant: 'success' },
      );
      queryClient.invalidateQueries({ queryKey: ['needs-review'] });
      queryClient.invalidateQueries({ queryKey: ['needs-review-detail', leadId] });
      // A save keeps the reviewer where they are — only an approval finishes the
      // document and returns to the queue. Navigating away on save discarded the
      // reviewer's place in a long document for no reason.
      if (action === 'approve') navigate('/procurement/extraction/review');
    },
    onError: (err: any) => {
      const data = err?.response?.data;
      const validationMessage = data?.errors
        ? Object.values(data.errors).flat().find((value): value is string => typeof value === 'string')
        : undefined;
      enqueueSnackbar(data?.error || data?.message || validationMessage || 'Failed to submit review', { variant: 'error' });
    },
  });

  const isSubmitting = mutation.isPending;

  /** Guards every exit this page owns. Sidebar links are covered by the draft. */
  const leaveTo = (path: string) => {
    if (isDirty) setLeaveConfirmTarget(path);
    else navigate(path);
  };

  const columns: GridColDef[] = useMemo(() => [
    {
      // Column index 0: the answer to "which lines do I still have to look at",
      // in place of a percentage nobody could act on.
      field: 'checkStatus',
      headerName: 'Status',
      width: 56,
      sortable: false,
      filterable: false,
      hideable: false,
      align: 'center',
      headerAlign: 'center',
      renderCell: (p) => {
        const result = checkLine(p.row as ReviewLineItem, flaggedByLine.get(p.row.id), assertions);
        const needsCheck = result.state === 'needs-check';
        const title = needsCheck
          ? result.reasons.join(' · ')
          : 'Every required field is present and nothing flagged it. Confirm against the source document.';
        return (
          <Tooltip title={title}>
            <Box
              role="img"
              aria-label={needsCheck ? `Needs check: ${result.reasons.join('. ')}` : 'Verified: all required fields present'}
              sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}
            >
              {needsCheck
                ? <NeedsCheckDotIcon sx={{ fontSize: 18, color: 'error.main' }} />
                : <VerifiedDotIcon sx={{ fontSize: 16, color: 'success.main' }} />}
            </Box>
          </Tooltip>
        );
      },
    },
    { field: 'lineItemNo', headerName: 'Line #', width: 80, editable: canEditLeads },
    { field: 'productShortName', headerName: 'Product', width: 180, editable: canEditLeads },
    { field: 'productShortDescription', headerName: 'Description', width: 260, editable: canEditLeads },
    { field: 'quantity', headerName: 'Qty', width: 90, type: 'number', editable: canEditLeads },
    { field: 'unitOfMeasure', headerName: 'UoM', width: 100, editable: canEditLeads },
    { field: 'unitPrice', headerName: 'Unit Price', width: 110, type: 'number', editable: canEditLeads },
    { field: 'currency', headerName: 'Currency', width: 100, editable: canEditLeads },
    { field: 'manufacturerName', headerName: 'Manufacturer', width: 160, editable: canEditLeads },
    { field: 'manufacturerPartNumber', headerName: 'Mfr Part #', width: 150, editable: canEditLeads },
    { field: 'itemMaterialCode', headerName: 'Material Code', width: 140, editable: canEditLeads },
    { field: 'leadTime', headerName: 'Lead Time', width: 110, editable: canEditLeads },
    { field: 'commodityProduct', headerName: 'Commodity', width: 150, editable: canEditLeads },
    { field: 'alternateProductName', headerName: 'Alt Product', width: 170, editable: canEditLeads },
    { field: 'alternatePartNumber', headerName: 'Alt Part #', width: 150, editable: canEditLeads },
    { field: 'itemText', headerName: 'Item Text', width: 200, editable: canEditLeads },
    ...(canEditLeads ? [{
      field: 'actions',
      type: 'actions',
      headerName: '',
      width: 60,
      hideable: false,
      getActions: (params) => [
        <GridActionsCellItem
          key="delete"
          icon={<DeleteIcon />}
          label="Delete row"
          // Reads params.row rather than looking the row up in `items`, so the
          // column definitions do not depend on the rows and are not rebuilt
          // (re-rendering the whole grid) on every keystroke of a long review.
          onClick={() => setPendingDelete(params.row as ReviewLineItem)}
          showInMenu={false}
        />,
      ],
    } as GridColDef] : []),
  ], [canEditLeads, flaggedByLine, assertions]);

  const columnLabel = useCallback(
    (field: string) => columns.find((column) => column.field === field)?.headerName ?? field,
    [columns],
  );

  const handleCellClick = (params: GridCellParams, event: React.MouseEvent) => {
    if (params.field === 'checkStatus' || params.field === 'actions' || params.field === '__check__') return;
    const row = params.row as ReviewLineItem;
    if (row.isNew) return; // Nothing was extracted for a row the reviewer typed.
    // The grid delegates its listeners from the root, so `currentTarget` is the
    // grid, not the cell. Anchor to the cell element the click actually landed
    // in, falling back to the delegate target so the popover always has an
    // anchor rather than silently not opening.
    const target = event.target as HTMLElement | null;
    const cell = target?.closest?.('.MuiDataGrid-cell') as HTMLElement | null;
    const anchor = cell ?? target ?? (event.currentTarget as HTMLElement);
    if (!anchor) return;
    setEvidenceAnchor({
      el: anchor,
      field: params.field,
      label: columnLabel(params.field),
      lineId: row.id,
      lineLabel: `Line ${row.lineItemNo || row.id}`,
    });
  };

  if (isLoading) {
    return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  }
  if (isError || !lead) {
    return (
      <Box sx={{ p: 4, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 2 }}>
        <Alert severity="error" sx={{ maxWidth: 480 }}>We couldn't load this document for review.</Alert>
        <Stack direction="row" spacing={1.5}>
          <Button variant="outlined" onClick={() => navigate('/procurement/extraction/review')}>Back to queue</Button>
          <Button variant="contained" onClick={() => refetch()}>Retry</Button>
        </Stack>
      </Box>
    );
  }

  return (
    <Box sx={{ p: 3, maxWidth: 1800, mx: 'auto' }}>
      {/* Breadcrumb */}
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
        <Link component="button" variant="caption" onClick={() => leaveTo('/procurement/extraction/review')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          Extraction Review
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
          {lead.rfqno || `Document #${lead.id}`}
        </Typography>
      </Breadcrumbs>

      {/* Header row: title + what still needs a look + actions */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 3, flexWrap: 'wrap', gap: 2 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, mb: 0.5 }}>
            Review Extraction — {lead.rfqno || `#${lead.id}`}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Verify the extracted data against its source evidence before approving
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          {/* Replaces the extraction-confidence percentage. A count of lines to
              look at is derived from data we actually persist, and it is the
              next action rather than a verdict. */}
          <Paper elevation={0} sx={{ px: 2, py: 1, borderRadius: 2, border: '1px solid', borderColor: 'divider', display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <Box>
              <Typography sx={{ fontSize: '0.6rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>Review progress</Typography>
              <Typography sx={{ fontWeight: 900, fontSize: '1rem', color: summary.needsCheck > 0 ? 'error.main' : 'success.main' }}>
                {checkHeadline(summary)}
              </Typography>
            </Box>
            {summary.needsCheck > 0 && (
              <Button
                size="small"
                variant="contained"
                onClick={jumpToNextCheck}
                sx={{ fontWeight: 800, borderRadius: 2, textTransform: 'none' }}
              >
                Next
              </Button>
            )}
          </Paper>
          {isDirty && (
            <Chip size="small" color="warning" variant="outlined" label="Unsaved corrections" sx={{ fontWeight: 800 }} />
          )}
          <Button
            variant="outlined"
            startIcon={<BackIcon />}
            onClick={() => leaveTo('/procurement/extraction/review')}
            disabled={isSubmitting}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Back
          </Button>
          {canEditLeads && <Button
            variant="outlined"
            color="primary"
            startIcon={isSubmitting ? <CircularProgress size={18} color="inherit" /> : <SaveIcon />}
            onClick={() => mutation.mutate('save')}
            disabled={isSubmitting}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Save corrections
          </Button>}
          {canEditLeads && <Button
            variant="contained"
            color="success"
            startIcon={<ApproveIcon />}
            onClick={() => setApproveDialogOpen(true)}
            disabled={isSubmitting || approvalBlockedReason !== null}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Approve
          </Button>}
        </Stack>
      </Box>

      {restoredDraftAt && (
        <Alert
          severity="info"
          sx={{ mb: 2 }}
          action={
            <Button
              size="small"
              color="inherit"
              onClick={() => {
                const restored: { header: ReviewHeaderState; items: ReviewLineItem[] } = JSON.parse(baselineRef.current);
                setHeader(restored.header);
                setItems(restored.items);
                setRestoredDraftAt(null);
                clearDraft(lead.id);
              }}
            >
              Discard draft
            </Button>
          }
        >
          Unsaved corrections from {dayjs(restoredDraftAt).isValid() ? dayjs(restoredDraftAt).format('DD MMM, HH:mm') : 'an earlier session'} were restored. Save them to keep them.
        </Alert>
      )}

      {/* Review reason banner */}
      <Alert severity="warning" sx={{ mb: 3, borderRadius: 2, fontWeight: 600 }}>
        {reviewReasonFromState
          ? reviewReasonFromState
          : 'Every extraction is routed here for a human to verify before it moves downstream. Check the fields below against the source document.'}
      </Alert>

      {approvalBlockedReason && (
        <Alert severity="error" sx={{ mb: 3 }}>
          Approval is blocked: {approvalBlockedReason}
          {canEditLeads ? ' You can still save corrections while the evidence issue is resolved.' : ''}
        </Alert>
      )}

      <ProcessingEvidencePanel
        evidence={processingEvidence.data}
        loading={processingEvidence.isLoading}
        failed={processingEvidence.isError}
        expanded={processingEvidenceExpanded}
        onToggle={() => setProcessingEvidenceExpanded((current) => !current)}
        onRetry={() => void processingEvidence.refetch()}
      />

      <Grid container spacing={3}>
        {/* Editable header */}
        <Grid size={{ xs: 12, lg: 9 }}>
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <Typography sx={{ fontWeight: 900, fontSize: '1rem', mb: 2, textTransform: 'uppercase', letterSpacing: '0.025em' }}>
              Client
            </Typography>
            {/* Which organisation this enquiry came from — verified first,
                above the field-level corrections, because every other header
                field is meaningless if the lead is attributed to nobody. */}
            <ClientIdentityPanel
              lead={lead}
              canEdit={canEditLeads && !isSubmitting}
              onSelect={setClientSelection}
              pendingSelection={clientSelection}
            />
            <Typography sx={{ fontWeight: 900, fontSize: '1rem', mt: 3, mb: 2, textTransform: 'uppercase', letterSpacing: '0.025em' }}>
              Header
            </Typography>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField fullWidth size="small" label="RFQ #" value={header.rfqno} onChange={handleHeaderChange('rfqno')} disabled={isSubmitting || !canEditLeads} />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField fullWidth size="small" label="Buyer" value={header.buyersName} onChange={handleHeaderChange('buyersName')} disabled={isSubmitting || !canEditLeads} />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  fullWidth size="small" type="date" label="Bid Closing Date"
                  value={header.bidClosingDate} onChange={handleHeaderChange('bidClosingDate')}
                  disabled={isSubmitting || !canEditLeads} slotProps={{ inputLabel: { shrink: true } }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField fullWidth size="small" label="Opportunity #" value={header.opportunityNo} onChange={handleHeaderChange('opportunityNo')} disabled={isSubmitting || !canEditLeads} />
              </Grid>
              <Grid size={{ xs: 12, md: 8 }}>
                <TextField fullWidth size="small" label="Remarks" value={header.headerRemarks} onChange={handleHeaderChange('headerRemarks')} disabled={isSubmitting || !canEditLeads} multiline maxRows={3} />
              </Grid>
            </Grid>
          </Paper>
        </Grid>

        {/* Source evidence panel */}
        <Grid size={{ xs: 12, lg: 3 }}>
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 2 }}>
              <Typography sx={{ fontWeight: 900, fontSize: '1rem', textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                Source Evidence
              </Typography>
              <Chip label={lead.attachments?.length ?? 0} size="small" sx={{ fontWeight: 900, height: 18, fontSize: '0.7rem' }} />
            </Stack>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
              Open the original document to cross-check while correcting.
            </Typography>
            <Stack spacing={1.5}>
              {lead.attachments?.map((file) => (
                <Paper
                  key={file.id}
                  elevation={0}
                  sx={{ p: 1.5, display: 'flex', alignItems: 'center', gap: 1.5, borderRadius: 2, border: '1px solid', borderColor: 'divider', transition: 'all 0.2s', '&:hover': { borderColor: 'primary.main', bgcolor: 'action.hover' } }}
                >
                  <Box sx={{ color: 'primary.main', bgcolor: 'primary.lighter', p: 0.8, borderRadius: 1.5 }}>
                    <FileIcon sx={{ fontSize: 20 }} />
                  </Box>
                  <Box sx={{ flex: 1, overflow: 'hidden' }}>
                    <Typography noWrap sx={{ fontSize: '0.75rem', fontWeight: 800 }}>{file.fileName}</Typography>
                    {typeof file.fileSize === 'number' && (
                      <Typography variant="caption" color="text.disabled" sx={{ fontSize: '0.65rem' }}>{(file.fileSize / 1024).toFixed(1)} KB</Typography>
                    )}
                  </Box>
                  <Tooltip title="Download and open in your own viewer">
                    <IconButton
                      size="small"
                      aria-label={`Download source document ${file.fileName ?? file.id}`}
                      onClick={async () => {
                        try {
                          await openAuthenticatedFile(`/api/File/attachment/${file.id}`);
                        } catch {
                          enqueueSnackbar('Could not open the source document', { variant: 'error' });
                        }
                      }}
                    >
                      <OpenIcon sx={{ fontSize: 16 }} />
                    </IconButton>
                  </Tooltip>
                </Paper>
              ))}
              {(!lead.attachments || lead.attachments.length === 0) && (
                <Box sx={{ py: 4, textAlign: 'center', opacity: 0.5 }}>
                  <FileIcon sx={{ fontSize: 40, color: 'text.disabled', mb: 1 }} />
                  <Typography variant="caption" sx={{ fontWeight: 700, display: 'block' }}>No source attachments</Typography>
                </Box>
              )}
            </Stack>
          </Paper>
        </Grid>

        {/* Editable line items */}
        <Grid size={{ xs: 12 }}>
          <Box sx={{ mt: 1 }}>
            <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 2, flexWrap: 'wrap', gap: 1 }}>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Typography sx={{ fontWeight: 900, fontSize: '1rem', textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                  Line Items
                </Typography>
                <Chip label={items.length} size="small" sx={{ fontWeight: 900, height: 18, fontSize: '0.7rem' }} />
              </Stack>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                <Button
                  variant="outlined"
                  size="small"
                  startIcon={<ColumnsIcon />}
                  onClick={(event) => setColumnsAnchor(event.currentTarget)}
                  sx={{ fontWeight: 800, borderRadius: 2 }}
                >
                  Columns
                </Button>
                {canEditLeads && <Button
                  variant="outlined"
                  size="small"
                  startIcon={<AddIcon />}
                  onClick={handleAddRow}
                  disabled={isSubmitting}
                  sx={{ fontWeight: 800, borderRadius: 2 }}
                >
                  Add row
                </Button>}
              </Stack>
            </Stack>

            {/* Bulk actions. Unit of measure and currency are the two fields a
                document gets wrong for every line at once; setting them one cell
                at a time is most of the interaction cost of a long review. */}
            {canEditLeads && selectedIds.length > 0 && (
              <Paper
                elevation={0}
                role="region"
                aria-label="Bulk actions for selected lines"
                sx={{ mb: 1.5, p: 1.25, borderRadius: 2, border: '1px solid', borderColor: 'primary.main', display: 'flex', alignItems: 'center', gap: 1.5, flexWrap: 'wrap' }}
              >
                <Typography sx={{ fontWeight: 800, fontSize: '0.85rem' }}>
                  {selectedIds.length} line{selectedIds.length === 1 ? '' : 's'} selected
                </Typography>
                <Button size="small" startIcon={<BulkEditIcon />} onClick={() => { setBulkField('unitOfMeasure'); setBulkValue(''); }} sx={{ fontWeight: 800, textTransform: 'none' }}>
                  Set unit of measure
                </Button>
                <Button size="small" startIcon={<BulkEditIcon />} onClick={() => { setBulkField('currency'); setBulkValue(''); }} sx={{ fontWeight: 800, textTransform: 'none' }}>
                  Set currency
                </Button>
                <Box sx={{ flex: 1 }} />
                <Button size="small" color="inherit" onClick={() => setSelection({ type: 'include', ids: new Set<GridRowId>() })} sx={{ textTransform: 'none' }}>
                  Clear selection
                </Button>
              </Paper>
            )}

            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              {canEditLeads
                ? 'Click a cell to see where the value came from in the source document. Double-click (or press Enter) to edit, Tab to move on. Rows still needing a check are marked in the first column.'
                : 'This extraction is read-only for your role. Click a cell to see where the value came from. Rows still needing a check are marked in the first column.'}
            </Typography>
            <Paper sx={{ borderRadius: 3, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
              <DataGrid
                apiRef={apiRef}
                rows={items}
                columns={columns}
                getRowId={(r) => r.id}
                editMode="cell"
                processRowUpdate={processRowUpdate}
                onProcessRowUpdateError={(err) => enqueueSnackbar(String(err?.message ?? 'Row update failed'), { variant: 'error' })}
                columnVisibilityModel={columnVisibility}
                onColumnVisibilityModelChange={applyColumnVisibility}
                checkboxSelection={canEditLeads}
                rowSelectionModel={selection}
                onRowSelectionModelChange={setSelection}
                disableRowSelectionOnClick
                onCellClick={handleCellClick}
                hideFooterSelectedRowCount
                autoHeight
                pageSizeOptions={[25, 50, 100]}
                paginationModel={paginationModel}
                onPaginationModelChange={setPaginationModel}
                getRowClassName={(params) => {
                  const classes: string[] = [];
                  if (checkLine(params.row as ReviewLineItem, flaggedByLine.get(params.row.id), assertions).state === 'needs-check') {
                    classes.push('needs-check-row');
                  }
                  if (params.row.id === focusedLineId) classes.push('focused-check-row');
                  return classes.join(' ');
                }}
                sx={{
                  '& .needs-check-row': { bgcolor: 'action.hover' },
                  '& .focused-check-row': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: '-2px' },
                }}
              />
            </Paper>
            {items.some((it) => it.extraFields && Object.keys(it.extraFields).length > 0) && (
              <Paper elevation={0} sx={{ mt: 2, p: 2, borderRadius: 3, border: '1px dashed', borderColor: 'divider' }}>
                <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 1, fontSize: '0.65rem' }}>
                  Additional columns from customer document (read-only)
                </Typography>
                <Stack spacing={1}>
                  {items
                    .filter((it) => it.extraFields && Object.keys(it.extraFields).length > 0)
                    .map((it) => (
                      <Box key={it.id} sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.75, alignItems: 'center' }}>
                        <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', minWidth: 80, fontSize: '0.7rem' }}>
                          Line {it.lineItemNo || it.id}
                        </Typography>
                        {Object.entries(it.extraFields as Record<string, string>).map(([key, value]) => (
                          <Chip
                            key={key}
                            size="small"
                            variant="outlined"
                            label={`${key}: ${value}`}
                            sx={{ height: 20, fontSize: '0.65rem', fontWeight: 600, color: 'text.secondary', maxWidth: 320 }}
                          />
                        ))}
                      </Box>
                    ))}
                </Stack>
              </Paper>
            )}
            {documentNarrative && (
              /* What the document says AROUND its table. The structured input was
                 built with an empty header and the line regions alone, so this text
                 was read and thrown away for every document on this path — and it is
                 where the required warranty, the validity period, the country of
                 origin, the Incoterms, the submission method and "as per attached
                 specification" live. It is shown verbatim and parsed into nothing:
                 a specification reference is not a value, and inventing fields from
                 prose is how it becomes a wrong commercial fact. */
              <Paper elevation={0} sx={{ mt: 2, p: 2, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
                <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 1, fontSize: '0.65rem' }}>
                  What the document says around the table (verbatim, not extracted)
                </Typography>
                <Typography
                  component="pre"
                  sx={{ m: 0, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontFamily: 'inherit', fontSize: '0.8rem', color: 'text.secondary', maxHeight: 260, overflowY: 'auto' }}
                >
                  {documentNarrative}
                </Typography>
                <Typography variant="caption" sx={{ color: 'text.disabled', display: 'block', mt: 1 }}>
                  Terms stated here — warranty, validity, country of origin, Incoterms, delivery
                  and submission instructions — are not extracted into fields. Read them before approving.
                </Typography>
              </Paper>
            )}
          </Box>
        </Grid>
      </Grid>

      <Divider sx={{ my: 3 }} />
      <Typography variant="caption" color="text.secondary">
        Received {dayjs(lead.recDate).isValid() ? dayjs(lead.recDate).format('DD MMM YYYY') : '—'} · Source {lead.leadSource || '—'}
      </Typography>

      {/* Column visibility */}
      <Popover
        open={Boolean(columnsAnchor)}
        anchorEl={columnsAnchor}
        onClose={() => setColumnsAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }}
        transformOrigin={{ vertical: 'top', horizontal: 'right' }}
      >
        <Box sx={{ p: 2, minWidth: 240 }}>
          <Typography sx={{ fontWeight: 800, fontSize: '0.8rem', mb: 1 }}>Visible columns</Typography>
          <FormGroup>
            {HIDEABLE_COLUMNS.map((column) => (
              <FormControlLabel
                key={column.field}
                control={
                  <Checkbox
                    size="small"
                    checked={columnVisibility[column.field] !== false}
                    onChange={(event) => applyColumnVisibility({ ...columnVisibility, [column.field]: event.target.checked })}
                  />
                }
                label={<Typography sx={{ fontSize: '0.8rem' }}>{column.label}</Typography>}
              />
            ))}
          </FormGroup>
        </Box>
      </Popover>

      {/* Glass box: where this value came from */}
      <FieldEvidencePopover
        anchorEl={evidenceAnchor?.el ?? null}
        onClose={() => setEvidenceAnchor(null)}
        fieldLabel={evidenceAnchor?.label ?? ''}
        lineLabel={evidenceAnchor?.lineLabel}
        entry={evidenceAnchor ? evidenceByKey.get(fieldEvidenceKey(evidenceAnchor.lineId, evidenceAnchor.field)) ?? null : null}
        documentUnmapped={documentUnmapped}
        loading={fieldEvidence.isLoading}
      />

      {/* Bulk set */}
      <Dialog open={bulkField !== null} onClose={() => setBulkField(null)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>
          Set {bulkField ? BULK_FIELD_LABEL[bulkField].toLowerCase() : ''} on {selectedIds.length} line{selectedIds.length === 1 ? '' : 's'}
        </DialogTitle>
        <DialogContent dividers>
          {/* Free text, not a picker. The whole reason to bulk-set a column is
              usually that every line carries the same WRONG value, so the right
              one is by definition not among the values already on the document.
              Existing values are offered as one-click shortcuts beneath it. */}
          <TextField
            autoFocus
            fullWidth
            label={bulkField ? BULK_FIELD_LABEL[bulkField] : ''}
            value={bulkValue}
            onChange={(event) => setBulkValue(event.target.value)}
            onKeyDown={(event) => { if (event.key === 'Enter' && bulkValue.trim()) applyBulkValue(); }}
            helperText="Applies to every selected line"
          />
          {bulkSuggestions.length > 0 && (
            <Box sx={{ mt: 1.5 }}>
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.75 }}>
                Already used on this document
              </Typography>
              <Stack direction="row" spacing={0.75} useFlexGap sx={{ flexWrap: 'wrap' }}>
                {bulkSuggestions.map((option) => (
                  <Chip
                    key={option}
                    size="small"
                    variant={bulkValue === option ? 'filled' : 'outlined'}
                    color={bulkValue === option ? 'primary' : 'default'}
                    label={option}
                    onClick={() => setBulkValue(option)}
                  />
                ))}
              </Stack>
            </Box>
          )}
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setBulkField(null)} color="inherit">Cancel</Button>
          <Button variant="contained" onClick={applyBulkValue} disabled={!bulkValue.trim()}>Apply</Button>
        </DialogActions>
      </Dialog>

      {/* Delete-row confirmation — a removed line is a commercial fact deleted. */}
      <Dialog open={pendingDelete !== null} onClose={() => setPendingDelete(null)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>Remove this line?</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2">
            Line <strong>{pendingDelete?.lineItemNo || pendingDelete?.id}</strong>
            {pendingDelete?.productShortDescription ? ` — ${pendingDelete.productShortDescription}` : ''} will be dropped from
            this document. It is only removed for good once you save or approve.
          </Typography>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setPendingDelete(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            color="error"
            onClick={() => {
              const target = pendingDelete;
              setPendingDelete(null);
              if (target) setItems((prev) => prev.filter((row) => row.id !== target.id));
            }}
          >
            Remove line
          </Button>
        </DialogActions>
      </Dialog>

      {/* Unsaved-work guard for this page's own exits. */}
      <Dialog open={leaveConfirmTarget !== null} onClose={() => setLeaveConfirmTarget(null)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>Leave without saving?</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2">
            You have corrections that have not been sent. They are kept in this browser and will be waiting when you come
            back, but they are not on the record until you save.
          </Typography>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setLeaveConfirmTarget(null)} color="inherit">Stay</Button>
          {canEditLeads && (
            <Button
              onClick={() => { setLeaveConfirmTarget(null); mutation.mutate('save'); }}
              disabled={isSubmitting}
            >
              Save and stay
            </Button>
          )}
          <Button
            variant="contained"
            onClick={() => {
              const target = leaveConfirmTarget;
              setLeaveConfirmTarget(null);
              if (target) navigate(target);
            }}
          >
            Leave
          </Button>
        </DialogActions>
      </Dialog>

      {/* Approve confirmation */}
      <Dialog open={approveDialogOpen && canEditLeads && approvalBlockedReason === null} onClose={() => !isSubmitting && setApproveDialogOpen(false)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>Approve extraction?</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2">
            Approving confirms the extracted data for <strong>{header.rfqno || `document #${lead.id}`}</strong> is correct.
            It will be removed from the review queue and released downstream.
          </Typography>
          {summary.needsCheck > 0 && (
            <Alert severity="warning" sx={{ mt: 2 }}>
              {checkHeadline(summary)}. Approving records your judgement that they are right as they stand.
            </Alert>
          )}
          <TextField
            autoFocus
            fullWidth
            required
            multiline
            minRows={2}
            label="Approval reason"
            value={approvalReason}
            onChange={(event) => setApprovalReason(event.target.value)}
            sx={{ mt: 2 }}
          />
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setApproveDialogOpen(false)} color="inherit" disabled={isSubmitting}>Cancel</Button>
          <Button
            variant="contained"
            color="success"
            startIcon={isSubmitting ? <CircularProgress size={18} color="inherit" /> : <ApproveIcon />}
            onClick={() => { setApproveDialogOpen(false); mutation.mutate('approve'); }}
            disabled={isSubmitting || !approvalReason.trim() || approvalBlockedReason !== null}
          >
            Approve
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default ExtractionReviewDetailPage;

interface ProcessingEvidencePanelProps {
  evidence: LeadProcessingEvidence | null | undefined;
  loading: boolean;
  failed: boolean;
  expanded: boolean;
  onToggle: () => void;
  onRetry: () => void;
}

function ProcessingEvidencePanel({ evidence, loading, failed, expanded, onToggle, onRetry }: ProcessingEvidencePanelProps) {
  if (loading) {
    return <Paper variant="outlined" sx={{ mb: 3, p: 2 }}><Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}><CircularProgress size={20} /><Typography variant="body2">Loading authoritative processing evidence...</Typography></Stack></Paper>;
  }
  if (failed) {
    return <Alert severity="warning" sx={{ mb: 3 }} action={<Button size="small" onClick={onRetry}>Retry</Button>}>Processing evidence is temporarily unavailable. You can still review the source document and extracted fields.</Alert>;
  }
  if (!evidence) {
    return <Alert severity="info" sx={{ mb: 3 }}>No authoritative processing record is linked to this Lead yet.</Alert>;
  }

  const latestOccurrence = evidence.occurrences[evidence.occurrences.length - 1];
  const latestJob = evidence.jobs[evidence.jobs.length - 1];
  const latestRun = evidence.runs[evidence.runs.length - 1];
  const isExternal = evidence.externalRequestCount > 0;
  const localRate = `${Math.round(evidence.localRequestRate * 100)}%`;
  return (
    <Paper variant="outlined" sx={{ mb: 3, p: 2.5 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { xs: 'flex-start', md: 'center' } }}>
        <Box>
          <Stack direction="row" spacing={1} useFlexGap sx={{ alignItems: 'center', flexWrap: 'wrap', mb: 0.5 }}>
            <FileSearch size={20} />
            <Typography sx={{ fontWeight: 900 }}>Processing evidence</Typography>
            <Chip size="small" icon={isExternal ? <Cloud size={15} /> : <Cpu size={15} />} color={isExternal ? 'warning' : 'success'} label={isExternal ? 'External provider used' : 'Local processing'} />
            <Chip size="small" variant="outlined" label={readable(latestRun?.status ?? latestJob?.status)} />
          </Stack>
          <Typography variant="body2" color="text.secondary">Authoritative path, OCR outcome, provider use, and cost linkage for this extraction.</Typography>
        </Box>
        <Button variant="outlined" startIcon={expanded ? <ChevronUp size={17} /> : <ChevronDown size={17} />} onClick={onToggle}>{expanded ? 'Hide evidence' : 'Show evidence'}</Button>
      </Stack>

      <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr 1fr', md: 'repeat(5, minmax(0, 1fr))' }, gap: 2, mt: 2 }}>
        <Box><Typography variant="caption" color="text.secondary">Processing path</Typography><Typography sx={{ fontWeight: 700 }}>{readable(latestRun?.processingPath)}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">OCR outcome</Typography><Typography sx={{ fontWeight: 700 }}>{readable(latestRun?.ocrStatus)}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">OCR pages</Typography><Typography sx={{ fontWeight: 700 }}>{latestRun?.ocrPageCount ?? 'Not recorded'}</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">Local model share</Typography><Typography sx={{ fontWeight: 700 }}>{localRate}</Typography><Typography variant="caption" color="text.secondary">{evidence.localRequestCount} local · {evidence.externalRequestCount} external</Typography></Box>
        <Box><Typography variant="caption" color="text.secondary">External provider cost</Typography><Typography sx={{ fontWeight: 700 }}>{costLabel(evidence.externalCostAmount, evidence.externalCostCurrency, evidence.externalCostStatus)}</Typography><Typography variant="caption" color="text.secondary">{readable(evidence.externalCostStatus)}</Typography></Box>
      </Box>

      {expanded && <Box sx={{ mt: 2, pt: 2, borderTop: '1px solid', borderColor: 'divider' }}>
        <Typography variant="subtitle2" sx={{ mb: 1 }}>Traceability</Typography>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, minmax(0, 1fr))', lg: 'repeat(4, minmax(0, 1fr))' }, gap: 1.5 }}>
          <EvidenceField label="Nexora Serial" value={evidenceValue(evidence.nexoraSerial)} />
          <EvidenceField label="Intake occurrence" value={evidenceValue(latestOccurrence?.occurrenceId)} />
          <EvidenceField label="Extraction job" value={evidenceValue(latestJob?.extractionJobId)} />
          <EvidenceField label="Extraction run" value={evidenceValue(latestRun?.runId)} />
          <EvidenceField label="Correlation" value={evidenceValue(latestOccurrence?.correlationId)} />
          <EvidenceField label="Source documents" value={String(new Set(evidence.occurrences.map((item) => item.sourceDocumentId).filter((value) => value != null)).size)} />
          <EvidenceField label="OCR compute status" value={readable(latestRun?.ocrCostStatus)} />
          <EvidenceField label="Completed" value={latestRun?.completedOn ? new Date(latestRun.completedOn).toLocaleString() : 'Not recorded'} />
          <EvidenceField label="Linked RFQs" value={evidence.rfqs.length === 0 ? 'None linked' : evidence.rfqs.map((rfq) => rfq.rfqNumber).join(', ')} />
          <EvidenceField label="Parser version" value={evidenceValue(latestRun?.parserVersion)} />
          <EvidenceField label="Schema version" value={evidenceValue(latestRun?.schemaVersion)} />
          <EvidenceField label="Run attempt" value={latestRun ? `${latestRun.attemptNumber}` : 'Not recorded'} />
        </Box>

        <Typography variant="subtitle2" sx={{ mt: 2, mb: 1 }}>Provider decisions</Typography>
        {evidence.aiRequests.length === 0 ? <Typography variant="body2" color="text.secondary">No model-provider call is linked to this extraction.</Typography> : <Stack spacing={1}>
          {evidence.aiRequests.map((call) => <Box key={call.requestId} sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '2fr 1fr 1fr 1fr' }, gap: 1, py: 1, borderBottom: '1px solid', borderColor: 'divider' }}>
            <Box><Typography variant="body2" sx={{ fontWeight: 700 }}>{call.provider}{call.model ? ` · ${call.model}` : ''}</Typography><Typography variant="caption" color="text.secondary">{call.version} · {call.reason} · {call.attempts.length} attempt{call.attempts.length === 1 ? '' : 's'}{call.budgetWarning ? ' · budget warning' : ''}</Typography></Box>
            <EvidenceField label="Location" value={readable(call.providerClass)} />
            <EvidenceField label="Result" value={readable(call.result)} />
            <EvidenceField label="Cost" value={`${costLabel(call.estimatedCost, call.costCurrency, call.costStatus)} · ${readable(call.costStatus)}${call.costPricingVersion ? ` · ${call.costPricingVersion}` : ''}`} />
          </Box>)}
        </Stack>}
      </Box>}
    </Paper>
  );
}

function EvidenceField({ label, value }: { label: string; value: string }) {
  return <Box sx={{ minWidth: 0 }}><Typography variant="caption" color="text.secondary">{label}</Typography><Typography variant="body2" sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>{value}</Typography></Box>;
}
