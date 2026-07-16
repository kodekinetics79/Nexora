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
import { useSnackbar } from 'notistack';
import extractionReviewService from '../../api/services/extractionReviewService';
import type { SubmitReviewPayload, ReviewItemPayload } from '../../api/services/extractionReviewService';
import type { LeadItemResponseDTO } from '../../api/services/leadService';
import axiosInstance from '../../api/axiosInstance';

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
}

interface ReviewHeaderState {
  rfqno: string;
  buyersName: string;
  bidClosingDate: string;
  opportunityNo: string;
  headerRemarks: string;
}

const LOW_CONFIDENCE = 0.5;

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
  }));

const ExtractionReviewDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  // Review reason can be passed from the queue via router state; it isn't part
  // of the lead detail payload, so we fall back gracefully when navigated to
  // directly.
  const reviewReasonFromState = (location.state as { reviewReason?: string } | null)?.reviewReason;

  const { data: lead, isLoading, isError, refetch } = useQuery({
    queryKey: ['needs-review-detail', Number(id)],
    queryFn: () => extractionReviewService.getLead(Number(id)),
    enabled: !!id,
  });

  const [header, setHeader] = useState<ReviewHeaderState>({
    rfqno: '', buyersName: '', bidClosingDate: '', opportunityNo: '', headerRemarks: '',
  });
  const [items, setItems] = useState<ReviewLineItem[]>([]);
  const [newRowSeq, setNewRowSeq] = useState(-1);
  const [approveDialogOpen, setApproveDialogOpen] = useState(false);

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
        action === 'approve' ? 'Extraction approved successfully' : 'Corrections saved — cleared from the review queue',
        { variant: 'success' },
      );
      queryClient.invalidateQueries({ queryKey: ['needs-review'] });
      queryClient.invalidateQueries({ queryKey: ['needs-review-detail', Number(id)] });
      navigate('/procurement/extraction/review');
    },
    onError: (err: any) =>
      enqueueSnackbar(err?.response?.data?.message || err?.response?.data || 'Failed to submit review', { variant: 'error' }),
  });

  const isSubmitting = mutation.isPending;

  const columns: GridColDef[] = useMemo(() => [
    { field: 'lineItemNo', headerName: 'Line #', width: 90, editable: true },
    { field: 'productShortName', headerName: 'Product', width: 200, editable: true },
    { field: 'productShortDescription', headerName: 'Description', width: 240, editable: true },
    { field: 'commodityProduct', headerName: 'Commodity', width: 150, editable: true },
    { field: 'itemMaterialCode', headerName: 'Material Code', width: 150, editable: true },
    { field: 'quantity', headerName: 'Qty', width: 90, type: 'number', editable: true },
    { field: 'unitOfMeasure', headerName: 'UoM', width: 90, editable: true },
    { field: 'unitPrice', headerName: 'Unit Price', width: 120, type: 'number', editable: true },
    { field: 'currency', headerName: 'Currency', width: 100, editable: true },
    { field: 'manufacturerName', headerName: 'Manufacturer', width: 170, editable: true },
    { field: 'manufacturerPartNumber', headerName: 'Mfr Part #', width: 150, editable: true },
    { field: 'alternateProductName', headerName: 'Alt Product', width: 170, editable: true },
    { field: 'alternatePartNumber', headerName: 'Alt Part #', width: 150, editable: true },
    { field: 'leadTime', headerName: 'Lead Time', width: 120, editable: true },
    { field: 'itemText', headerName: 'Item Text', width: 200, editable: true },
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
    {
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
    },
  ], []);

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
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>
            Review Extraction — {lead.rfqno || `#${lead.id}`}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Verify and correct the AI-extracted data before approving
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
          {/* Prominent overall confidence */}
          <Paper elevation={0} sx={{ px: 2, py: 1, borderRadius: 2, border: '1px solid', borderColor: 'divider', display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <Box role="img" aria-label={`Overall AI confidence ${aiConfidence} percent`}>
              <Typography sx={{ fontSize: '0.6rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>AI Confidence</Typography>
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
          <Button
            variant="outlined"
            color="primary"
            startIcon={isSubmitting ? <CircularProgress size={18} color="inherit" /> : <SaveIcon />}
            onClick={() => mutation.mutate('save')}
            disabled={isSubmitting}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Save corrections
          </Button>
          <Button
            variant="contained"
            color="success"
            startIcon={<ApproveIcon />}
            onClick={() => setApproveDialogOpen(true)}
            disabled={isSubmitting}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Approve
          </Button>
        </Stack>
      </Box>

      {/* Review reason banner */}
      <Alert severity="warning" sx={{ mb: 3, borderRadius: 2, fontWeight: 600 }}>
        {reviewReasonFromState
          ? reviewReasonFromState
          : `This document was flagged for manual review${aiConfidence < 70 ? ` (extraction confidence ${aiConfidence}%)` : ''}. Please verify the fields below before approving.`}
      </Alert>

      <Grid container spacing={3}>
        {/* Editable header */}
        <Grid size={{ xs: 12, lg: 9 }}>
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <Typography sx={{ fontWeight: 900, fontSize: '1rem', mb: 2, textTransform: 'uppercase', letterSpacing: '0.025em' }}>
              Header
            </Typography>
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField fullWidth size="small" label="RFQ #" value={header.rfqno} onChange={handleHeaderChange('rfqno')} disabled={isSubmitting} />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField fullWidth size="small" label="Buyer" value={header.buyersName} onChange={handleHeaderChange('buyersName')} disabled={isSubmitting} />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField
                  fullWidth size="small" type="date" label="Bid Closing Date"
                  value={header.bidClosingDate} onChange={handleHeaderChange('bidClosingDate')}
                  disabled={isSubmitting} slotProps={{ inputLabel: { shrink: true } }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField fullWidth size="small" label="Opportunity #" value={header.opportunityNo} onChange={handleHeaderChange('opportunityNo')} disabled={isSubmitting} />
              </Grid>
              <Grid size={{ xs: 12, md: 8 }}>
                <TextField fullWidth size="small" label="Remarks" value={header.headerRemarks} onChange={handleHeaderChange('headerRemarks')} disabled={isSubmitting} multiline maxRows={3} />
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
                      onClick={() => window.open(`${axiosInstance.defaults.baseURL}/api/Lead/attachment/${file.id}`, '_blank', 'noopener,noreferrer')}
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
              <Button
                variant="outlined"
                size="small"
                startIcon={<AddIcon />}
                onClick={handleAddRow}
                disabled={isSubmitting}
                sx={{ fontWeight: 800, borderRadius: 2 }}
              >
                Add row
              </Button>
            </Stack>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              Double-click a cell to edit. Low-confidence rows are highlighted.
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
          </Box>
        </Grid>
      </Grid>

      <Divider sx={{ my: 3 }} />
      <Typography variant="caption" color="text.secondary">
        Received {dayjs(lead.recDate).isValid() ? dayjs(lead.recDate).format('DD MMM YYYY') : '—'} · Source {lead.leadSource || '—'}
      </Typography>

      {/* Approve confirmation */}
      <Dialog open={approveDialogOpen} onClose={() => !isSubmitting && setApproveDialogOpen(false)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>Approve extraction?</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2">
            Approving confirms the extracted data for <strong>{header.rfqno || `document #${lead.id}`}</strong> is correct.
            It will be removed from the review queue and released downstream.
          </Typography>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setApproveDialogOpen(false)} color="inherit" disabled={isSubmitting}>Cancel</Button>
          <Button
            variant="contained"
            color="success"
            startIcon={isSubmitting ? <CircularProgress size={18} color="inherit" /> : <ApproveIcon />}
            onClick={() => { setApproveDialogOpen(false); mutation.mutate('approve'); }}
            disabled={isSubmitting}
          >
            Approve
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default ExtractionReviewDetailPage;
