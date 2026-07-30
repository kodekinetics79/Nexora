import React, { useState, useEffect, useMemo } from 'react';
import { useParams, useNavigate, useLocation } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Chip, Grid, CircularProgress, Stack,
  IconButton, Breadcrumbs, Link, Dialog, DialogTitle, DialogContent,
  DialogActions, TextField, Alert, Tooltip, Divider,
} from '@mui/material';
import {
  DataGrid, GridActionsCellItem,
  type GridColDef, type GridRowModel, type GridRowId,
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
} from '@mui/icons-material';
import dayjs from 'dayjs';
import { ChevronDown, ChevronUp, Cloud, Cpu, FileSearch } from 'lucide-react';
import { useSnackbar } from 'notistack';
import extractionReviewService from '../../api/services/extractionReviewService';
import type { LeadProcessingEvidence, SubmitReviewPayload, ReviewItemPayload } from '../../api/services/extractionReviewService';
import type { LeadItemResponseDTO } from '../../api/services/leadService';
import { useAuth } from '../../context/AuthContext';
import { openAuthenticatedFile } from '../../utils/authenticatedFile';

// Local editable representation of a line item. `id` doubles as the DataGrid
// row id; new rows added during review use negative ids and set `isNew` so the
// payload builder knows to omit the id.
interface ReviewLineItem {
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
  aiconfidence?: number | null;
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

const LOW_CONFIDENCE = 0.5;

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
    aiconfidence: it.aiconfidence ?? null,
    extraFields: it.extraFields ?? null,
  }));

const ExtractionReviewDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canEditLeads = hasPermission('Leads', 'edit');

  // Review reason can be passed from the queue via router state; it isn't part
  // of the lead detail payload, so we fall back gracefully when navigated to
  // directly.
  const reviewReasonFromState = (location.state as { reviewReason?: string } | null)?.reviewReason;

  const { data: lead, isLoading, isError, refetch } = useQuery({
    queryKey: ['needs-review-detail', Number(id)],
    queryFn: () => extractionReviewService.getLead(Number(id)),
    enabled: !!id,
  });
  const processingEvidence = useQuery({
    queryKey: ['lead-processing-evidence', Number(id)],
    queryFn: () => extractionReviewService.getProcessingEvidence(Number(id)),
    enabled: !!id && Number.isFinite(Number(id)),
    retry: false,
  });

  const [header, setHeader] = useState<ReviewHeaderState>({
    rfqno: '', buyersName: '', bidClosingDate: '', opportunityNo: '', headerRemarks: '',
  });
  const [items, setItems] = useState<ReviewLineItem[]>([]);
  const [newRowSeq, setNewRowSeq] = useState(-1);
  const [approveDialogOpen, setApproveDialogOpen] = useState(false);
  const [approvalReason, setApprovalReason] = useState('Verified against the source document.');
  const [processingEvidenceExpanded, setProcessingEvidenceExpanded] = useState(false);

  // Seed editable state once the lead loads.
  useEffect(() => {
    if (!lead) return;
    setHeader({
      rfqno: lead.rfqno ?? '',
      buyersName: lead.buyersName ?? '',
      bidClosingDate: toDateInput(lead.bidClosingDate),
      opportunityNo: lead.opportunityNo ?? '',
      headerRemarks: lead.headerRemarks ?? '',
    });
    setItems(mapItems(lead.leadItems));
  }, [lead]);

  const aiConfidence = Math.round((lead?.aiconfidence ?? 0) * 100);
  const confidenceColor = aiConfidence >= 70 ? 'success' : aiConfidence >= 50 ? 'warning' : 'error';
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
        alternateProductName: '', alternatePartNumber: '', itemText: '', leadTime: '', aiconfidence: null,
      },
    ]);
  };

  const handleDeleteRow = (rowId: GridRowId) => () => {
    setItems((prev) => prev.filter((row) => row.id !== rowId));
  };

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
      extractionReviewService.submitReview(Number(id), buildPayload(action)),
    onSuccess: (_data, action) => {
      enqueueSnackbar(
        action === 'approve' ? 'Extraction approved successfully' : 'Corrections saved and kept in the review queue',
        { variant: 'success' },
      );
      queryClient.invalidateQueries({ queryKey: ['needs-review'] });
      queryClient.invalidateQueries({ queryKey: ['needs-review-detail', Number(id)] });
      navigate('/procurement/extraction/review');
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

  const columns: GridColDef[] = useMemo(() => [
    { field: 'lineItemNo', headerName: 'Line #', width: 90, editable: canEditLeads },
    { field: 'productShortName', headerName: 'Product', width: 200, editable: canEditLeads },
    { field: 'productShortDescription', headerName: 'Description', width: 240, editable: canEditLeads },
    { field: 'commodityProduct', headerName: 'Commodity', width: 150, editable: canEditLeads },
    { field: 'itemMaterialCode', headerName: 'Material Code', width: 150, editable: canEditLeads },
    { field: 'quantity', headerName: 'Qty', width: 90, type: 'number', editable: canEditLeads },
    { field: 'unitOfMeasure', headerName: 'UoM', width: 90, editable: canEditLeads },
    { field: 'unitPrice', headerName: 'Unit Price', width: 120, type: 'number', editable: canEditLeads },
    { field: 'currency', headerName: 'Currency', width: 100, editable: canEditLeads },
    { field: 'manufacturerName', headerName: 'Manufacturer', width: 170, editable: canEditLeads },
    { field: 'manufacturerPartNumber', headerName: 'Mfr Part #', width: 150, editable: canEditLeads },
    { field: 'alternateProductName', headerName: 'Alt Product', width: 170, editable: canEditLeads },
    { field: 'alternatePartNumber', headerName: 'Alt Part #', width: 150, editable: canEditLeads },
    { field: 'leadTime', headerName: 'Lead Time', width: 120, editable: canEditLeads },
    { field: 'itemText', headerName: 'Item Text', width: 200, editable: canEditLeads },
    {
      field: 'aiconfidence',
      headerName: 'Confidence',
      width: 120,
      editable: false,
      renderCell: (p) => {
        const v = p.row.aiconfidence;
        if (v == null) return <Chip label="New" size="small" variant="outlined" sx={{ height: 20, fontSize: '0.65rem' }} />;
        const pct = Math.round(v * 100);
        const color = v < 0.5 ? 'error' : v < 0.7 ? 'warning' : 'success';
        return (
          <Chip
            label={`${pct}%`}
            size="small"
            color={color}
            variant="outlined"
            aria-label={`Line confidence ${pct} percent`}
            sx={{ fontWeight: 900, fontSize: '0.65rem', height: 20, borderWidth: 2 }}
          />
        );
      },
    },
    ...(canEditLeads ? [{
      field: 'actions',
      type: 'actions',
      headerName: '',
      width: 60,
      getActions: (params) => [
        <GridActionsCellItem
          key="delete"
          icon={<DeleteIcon />}
          label="Delete row"
          onClick={handleDeleteRow(params.id)}
          showInMenu={false}
        />,
      ],
    } as GridColDef] : []),
  ], [canEditLeads]);

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
        <Link component="button" variant="caption" onClick={() => navigate('/procurement/extraction/review')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          Extraction Review
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
          {lead.rfqno || `Document #${lead.id}`}
        </Typography>
      </Breadcrumbs>

      {/* Header row: title + confidence + actions */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 3, flexWrap: 'wrap', gap: 2 }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, mb: 0.5 }}>
            Review Extraction — {lead.rfqno || `#${lead.id}`}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Verify the extracted data against its source evidence before approving
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
          {/* Prominent overall confidence */}
          <Paper elevation={0} sx={{ px: 2, py: 1, borderRadius: 2, border: '1px solid', borderColor: 'divider', display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <Box role="img" aria-label={`Overall extraction confidence ${aiConfidence} percent`}>
              <Typography sx={{ fontSize: '0.6rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>Extraction confidence</Typography>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Typography sx={{ fontWeight: 900, fontSize: '1.1rem', color: `${confidenceColor}.main` }}>{aiConfidence}%</Typography>
                <Box sx={{ width: 90, height: 6, bgcolor: 'action.hover', borderRadius: 3, overflow: 'hidden' }}>
                  <Box sx={{ height: '100%', width: `${aiConfidence}%`, bgcolor: `${confidenceColor}.main` }} />
                </Box>
              </Stack>
            </Box>
          </Paper>
          <Button
            variant="outlined"
            startIcon={<BackIcon />}
            onClick={() => navigate('/procurement/extraction/review')}
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

      {/* Review reason banner */}
      <Alert severity="warning" sx={{ mb: 3, borderRadius: 2, fontWeight: 600 }}>
        {reviewReasonFromState
          ? reviewReasonFromState
          : `This document was flagged for manual review${aiConfidence < 70 ? ` (extraction confidence ${aiConfidence}%)` : ''}. Please verify the fields below before approving.`}
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
                  <Tooltip title="Open document">
                    <IconButton
                      size="small"
                      aria-label={`Open source document ${file.fileName ?? file.id}`}
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
            <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 2 }}>
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                <Typography sx={{ fontWeight: 900, fontSize: '1rem', textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                  Line Items
                </Typography>
                <Chip label={items.length} size="small" sx={{ fontWeight: 900, height: 18, fontSize: '0.7rem' }} />
              </Stack>
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
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              {canEditLeads ? 'Double-click a cell to edit. Low-confidence rows are highlighted.' : 'This extraction is read-only for your role. Low-confidence rows are highlighted.'}
            </Typography>
            <Paper sx={{ borderRadius: 3, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
              <DataGrid
                rows={items}
                columns={columns}
                getRowId={(r) => r.id}
                editMode="row"
                processRowUpdate={processRowUpdate}
                onProcessRowUpdateError={(err) => enqueueSnackbar(String(err?.message ?? 'Row update failed'), { variant: 'error' })}
                disableRowSelectionOnClick
                hideFooterSelectedRowCount
                autoHeight
                pageSizeOptions={[10, 25, 50]}
                initialState={{ pagination: { paginationModel: { pageSize: 10, page: 0 } } }}
                getRowClassName={(params) =>
                  params.row.aiconfidence != null && params.row.aiconfidence < LOW_CONFIDENCE ? 'low-confidence-row' : ''
                }
                sx={{
                  '& .low-confidence-row': { bgcolor: 'error.lighter' },
                  '& .low-confidence-row:hover': { bgcolor: 'error.light' },
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
          </Box>
        </Grid>
      </Grid>

      <Divider sx={{ my: 3 }} />
      <Typography variant="caption" color="text.secondary">
        Received {dayjs(lead.recDate).isValid() ? dayjs(lead.recDate).format('DD MMM YYYY') : '—'} · Source {lead.leadSource || '—'}
      </Typography>

      {/* Approve confirmation */}
      <Dialog open={approveDialogOpen && canEditLeads && approvalBlockedReason === null} onClose={() => !isSubmitting && setApproveDialogOpen(false)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>Approve extraction?</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2">
            Approving confirms the extracted data for <strong>{header.rfqno || `document #${lead.id}`}</strong> is correct.
            It will be removed from the review queue and released downstream.
          </Typography>
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
