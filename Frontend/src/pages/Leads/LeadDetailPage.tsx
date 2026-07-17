import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Stack, Table, TableHead,
  TableRow, TableCell, TableBody, IconButton,
  Breadcrumbs, Link, Dialog, DialogTitle, DialogContent,
  DialogActions, TextField, MenuItem, Alert, AlertTitle,
} from '@mui/material';
import {
  Description as FileIcon,
  Download as DownloadIcon,
  CheckCircle as AcceptIcon,
  Cancel as RejectIcon,
  NavigateNext as NextIcon,
  AutoAwesome as SparkleIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import { parseDateSafe, formatDateSafe } from '../../utils/dates';

import { toast } from 'react-hot-toast';

/**
 * SLA deadline chip (WP-A2): urgency at a glance for the bid closing date.
 * Overdue = red, closing within 3 days = amber, otherwise neutral. Sentinel
 * dates (< 2000, e.g. DateTime.MinValue) are treated as "no deadline" and
 * render nothing — parseDateSafe handles that.
 */
const DeadlineChip: React.FC<{ bidClosingDate?: string | null }> = ({ bidClosingDate }) => {
  const close = parseDateSafe(bidClosingDate);
  if (!close) return null;

  const msPerDay = 24 * 60 * 60 * 1000;
  const daysLeft = Math.ceil((close.getTime() - Date.now()) / msPerDay);

  const label = daysLeft < 0
    ? `Overdue by ${Math.abs(daysLeft)} day${Math.abs(daysLeft) === 1 ? '' : 's'}`
    : daysLeft === 0
      ? 'Closes today'
      : `${daysLeft} day${daysLeft === 1 ? '' : 's'} left`;

  const palette = daysLeft < 0
    ? { bgcolor: 'error.lighter', color: 'error.main', borderColor: 'error.light' }
    : daysLeft <= 3
      ? { bgcolor: 'warning.lighter', color: 'warning.main', borderColor: 'warning.light' }
      : { bgcolor: 'action.hover', color: 'text.secondary', borderColor: 'divider' };

  return (
    <Chip
      size="small"
      label={label}
      title={`Bid closes ${formatDateSafe(bidClosingDate)}`}
      sx={{ fontWeight: 900, height: 24, fontSize: '0.7rem', border: '1px solid', ...palette }}
    />
  );
};

const SectionTitle: React.FC<{ title: string; count?: number }> = ({ title, count }) => (
  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
    <Typography sx={{ fontWeight: 900, fontSize: '1rem', color: 'text.primary', textTransform: 'uppercase', letterSpacing: '0.025em' }}>{title}</Typography>
    {count !== undefined && (
      <Chip label={count} size="small" sx={{ fontWeight: 900, bgcolor: 'primary.lighter', color: 'primary.main', height: 18, fontSize: '0.7rem' }} />
    )}
  </Box>
);

const DataField: React.FC<{ label: string; value: string | number | null; boldValue?: boolean }> = ({ label, value, boldValue = true }) => (
  <Box sx={{ mb: 1.5 }}>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.2, fontSize: '0.65rem' }}>
      {label}
    </Typography>
    <Typography sx={{ fontWeight: boldValue ? 800 : 500, fontSize: '0.85rem', color: 'text.primary', whiteSpace: 'pre-wrap' }}>
      {value || '—'}
    </Typography>
  </Box>
);

import axiosInstance from '../../api/axiosInstance';

const LeadDetailPage: React.FC = () => {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const { data: lead, isLoading } = useQuery({
    queryKey: ['lead-detail', Number(id)],
    queryFn: () => leadService.getById(Number(id)),
    enabled: !!id,
  });

  // WP-A3: duplicate-flag resolution ("Not a duplicate" unblocks conversion;
  // "Confirm duplicate" keeps it blocked).
  const [duplicateAction, setDuplicateAction] = React.useState<'not_duplicate' | 'confirm' | null>(null);
  const resolveDuplicateMutation = useMutation({
    mutationFn: (action: 'not_duplicate' | 'confirm') => leadService.resolveDuplicate(Number(id), action),
    onSuccess: (_data, action) => {
      toast.success(action === 'not_duplicate'
        ? 'Marked as not a duplicate. This lead can be converted again.'
        : 'Confirmed as a duplicate. This lead stays blocked from conversion.');
      setDuplicateAction(null);
      queryClient.invalidateQueries({ queryKey: ['lead-detail', Number(id)] });
    },
    onError: (error: any) => {
      toast.error(error.response?.data?.message || error.response?.data || 'Could not update the duplicate flag');
    },
  });

  // FE-14: fetch the real rejection reasons instead of hardcoding reason id 1.
  const { data: rejectionReasons = [] } = useQuery({
    queryKey: ['rejection-reasons'],
    queryFn: () => leadService.getRejectionReasons(),
  });

  // Sentinel-safe formatting (dates before 2000 render as "—", never "01 Jan 1").
  const formatDate = (dateStr: string | null) => formatDateSafe(dateStr);

  const [isProcessing, setIsProcessing] = React.useState(false);
  const [rejectDialogOpen, setRejectDialogOpen] = React.useState(false);
  const [rejectReasonId, setRejectReasonId] = React.useState<string>('');

  const handleAccept = async () => {
    if (!lead) return;
    setIsProcessing(true);
    try {
      await leadService.acceptLead(lead.id);
      toast.success('Lead accepted successfully');
      navigate('/procurement/leads/outstanding');
    } catch (error: any) {
      console.error('Acceptance failed', error);
      toast.error(error.response?.data?.message || 'Failed to accept lead');
    } finally {
      setIsProcessing(false);
    }
  };

  const handleConfirmReject = async () => {
    if (!lead || !rejectReasonId) return;
    setIsProcessing(true);
    try {
      await leadService.rejectLead(lead.id, Number(rejectReasonId));
      toast.success('Lead rejected');
      setRejectDialogOpen(false);
      navigate('/procurement/leads/all');
    } catch (error: any) {
      toast.error(error.response?.data?.message || 'Failed to reject lead');
    } finally {
      setIsProcessing(false);
    }
  };

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (!lead) return <Box sx={{ p: 4 }}><Typography>Lead not found.</Typography></Box>;

  const aiConfidence = (lead.aiconfidence || 0) * 100;

  return (
    <Box sx={{ p: 3, maxWidth: 1800, mx: 'auto' }}>
      {/* Breadcrumb Header */}
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
        <Link component="button" variant="caption" onClick={() => navigate('/dashboard')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          {t('rfqs_management') || 'RFQs Management'}
        </Link>
        <Link component="button" variant="caption" onClick={() => navigate('/procurement/leads/all')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          {t('leads') || 'Leads'}
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
          {lead.rfqno}
        </Typography>
      </Breadcrumbs>

      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 3 }}>
        <Box>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
            <Typography variant="h5" sx={{ fontWeight: 950, color: 'text.primary', letterSpacing: '-0.02em' }}>
              {lead.rfqno}
            </Typography>
            <DeadlineChip bidClosingDate={lead.bidClosingDate} />
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            {t('lead_detail_analysis') || 'Lead Details Analysis Engine'}
          </Typography>
        </Box>
        <Stack direction="row" spacing={1.5}>
          <Button
            variant="contained"
            startIcon={<SparkleIcon />}
            size="small"
            onClick={() => navigate(`/procurement/leads/${lead.id}/convert`)}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            Convert with AI
          </Button>
        {(!lead.isAccepted && !lead.isRejected && lead.status?.toLowerCase() !== 'accepted' && lead.status?.toLowerCase() !== 'rejected') && (
          <>
            <Button
              variant="outlined"
              color="error"
              startIcon={<RejectIcon />}
              size="small"
              onClick={() => { setRejectReasonId(''); setRejectDialogOpen(true); }}
              disabled={isProcessing}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
            >
              {t('reject') || 'Reject'}
            </Button>
            <Button
              variant="contained"
              color="success"
              startIcon={isProcessing ? <CircularProgress size={20} color="inherit" /> : <AcceptIcon />}
              size="small"
              onClick={handleAccept}
              disabled={isProcessing}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
            >
              {t('accept') || 'Accept'}
            </Button>
          </>
        )}
        </Stack>
      </Box>

      {/* WP-A3: duplicate warning banner */}
      {(lead.duplicateStatus === 'suspected' || lead.duplicateStatus === 'confirmed') && (
        <Alert
          severity={lead.duplicateStatus === 'confirmed' ? 'error' : 'warning'}
          sx={{ mb: 3, borderRadius: 2, '& .MuiAlert-message': { width: '100%' } }}
        >
          <AlertTitle sx={{ fontWeight: 800 }}>
            {lead.duplicateStatus === 'confirmed'
              ? `Confirmed duplicate of Lead #${lead.duplicateOfLeadId}`
              : `This looks like a duplicate of Lead #${lead.duplicateOfLeadId}`}
          </AlertTitle>
          <Typography variant="body2" sx={{ mb: 1.5 }}>
            {lead.duplicateStatus === 'confirmed'
              ? 'Someone confirmed this lead is a duplicate, so it stays blocked from being converted to an RFQ. If that was a mistake, choose "Not a duplicate" below.'
              : 'It cannot be converted to an RFQ until someone decides. Compare it with the original lead, then choose one of the options below.'}
          </Typography>
          <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap' }}>
            <Button
              size="small"
              variant="outlined"
              color="inherit"
              onClick={() => navigate(`/procurement/leads/view/${lead.duplicateOfLeadId}`)}
              sx={{ fontWeight: 800, borderRadius: 2 }}
            >
              View original
            </Button>
            <Button
              size="small"
              variant="contained"
              color="success"
              onClick={() => setDuplicateAction('not_duplicate')}
              disabled={resolveDuplicateMutation.isPending}
              sx={{ fontWeight: 800, borderRadius: 2 }}
            >
              Not a duplicate
            </Button>
            {lead.duplicateStatus === 'suspected' && (
              <Button
                size="small"
                variant="contained"
                color="error"
                onClick={() => setDuplicateAction('confirm')}
                disabled={resolveDuplicateMutation.isPending}
                sx={{ fontWeight: 800, borderRadius: 2 }}
              >
                Confirm duplicate
              </Button>
            )}
          </Stack>
        </Alert>
      )}

      <Grid container spacing={3}>
        {/* Left Column: General Information */}
        <Grid size={{ xs: 12, lg: 9 }} component="div">
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <SectionTitle title={t('general_information') || 'General Information'} />
            <Grid container spacing={2} component="div">
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="RFQ #" value={lead.rfqno} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Buyer Name" value={lead.buyersName} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Client Email" value={lead.clientemail} /></Grid>

              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Received" value={formatDate(lead.recDate)} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Bid Close" value={formatDate(lead.bidClosingDate)} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Source" value={lead.leadSource} /></Grid>

              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="RFQ Type" value={lead.rfqtype ?? 'N/A'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Opportunity No" value={lead.opportunityNo ?? 'N/A'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div">
                <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.5, fontSize: '0.65rem' }}>AI Confidence</Typography>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
                  <Typography sx={{ fontWeight: 900, color: 'primary.main', fontSize: '0.9rem' }}>{Math.round(aiConfidence)}% Match</Typography>
                  <Box sx={{ flex: 1, height: 6, bgcolor: 'action.hover', borderRadius: 3, overflow: 'hidden', maxWidth: 120 }}>
                    <Box sx={{ height: '100%', width: `${aiConfidence}%`, bgcolor: 'primary.main' }} />
                  </Box>
                </Box>
              </Grid>
            </Grid>
          </Paper>
        </Grid>

        {/* Right Column: Attachments */}
        <Grid size={{ xs: 12, lg: 3 }} component="div">
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <SectionTitle title={t('attachments') || 'Attachments'} count={lead.attachments?.length || 0} />
            <Stack spacing={1.5} sx={{ mt: 1 }}>
              {lead.attachments?.map((file: any) => (
                <Paper
                  key={file.id}
                  elevation={0}
                  sx={{
                    p: 1.5,
                    display: 'flex',
                    alignItems: 'center',
                    gap: 1.5,
                    borderRadius: 2,
                    border: '1px solid',
                    borderColor: 'divider',
                    bgcolor: 'background.paper',
                    transition: 'all 0.2s',
                    '&:hover': { borderColor: 'primary.main', bgcolor: 'action.hover', transform: 'translateY(-2px)' }
                  }}
                >
                  <Box sx={{ color: 'primary.main', bgcolor: 'primary.lighter', p: 0.8, borderRadius: 1.5 }}>
                    <FileIcon sx={{ fontSize: 20 }} />
                  </Box>
                  <Box sx={{ flex: 1, overflow: 'hidden' }}>
                    <Typography noWrap sx={{ fontSize: '0.75rem', fontWeight: 800 }}>{file.fileName}</Typography>
                    <Typography variant="caption" color="text.disabled" sx={{ fontSize: '0.65rem' }}>{(file.fileSize / 1024).toFixed(1)} KB</Typography>
                  </Box>
                  <IconButton size="small" onClick={() => window.open(`${axiosInstance.defaults.baseURL}/api/Lead/attachment/${file.id}`, '_blank')}><DownloadIcon sx={{ fontSize: 16 }} /></IconButton>
                </Paper>
              ))}
              {(!lead.attachments || lead.attachments.length === 0) && (
                <Box sx={{ py: 4, textAlign: 'center', opacity: 0.5 }}>
                  <FileIcon sx={{ fontSize: 40, color: 'text.disabled', mb: 1 }} />
                  <Typography variant="caption" sx={{ fontWeight: 700 }}>No attachments found</Typography>
                </Box>
              )}
            </Stack>
          </Paper>
        </Grid>

        {/* Full Width Bottom: Line Items */}
        <Grid size={{ xs: 12 }} component="div">
          <Box sx={{ mt: 2 }}>
            <SectionTitle title={t('extracted_line_items') || 'Extracted Line Items'} count={lead.leadItems?.length || 0} />
            <Paper sx={{ borderRadius: 3, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
              <Table size="small">
                <TableHead>
                  <TableRow sx={{ bgcolor: 'action.hover' }}>
                    <TableCell sx={{ fontWeight: 900, color: 'text.secondary', fontSize: '0.7rem', py: 1.5 }}>PRODUCT INTELLIGENCE</TableCell>
                    <TableCell sx={{ fontWeight: 900, color: 'text.secondary', fontSize: '0.7rem', py: 1.5 }}>MANUFACTURER</TableCell>
                    <TableCell sx={{ fontWeight: 900, color: 'text.secondary', fontSize: '0.7rem', py: 1.5 }} align="right">QTY</TableCell>
                    <TableCell sx={{ fontWeight: 900, color: 'text.secondary', fontSize: '0.7rem', py: 1.5 }} align="right">UNIT PRICE</TableCell>
                    <TableCell sx={{ fontWeight: 900, color: 'text.secondary', fontSize: '0.7rem', py: 1.5 }}>TRACEABILITY</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {lead.leadItems?.map((item: any) => (
                    <React.Fragment key={item.id}>
                    <TableRow sx={{ '&:hover': { bgcolor: 'action.selected' } }}>
                      <TableCell sx={{ width: '35%', py: 2 }}>
                        <Typography sx={{ fontWeight: 800, fontSize: '0.85rem', color: 'primary.main', mb: 0.5 }}>{item.productShortName}</Typography>
                        <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block', lineHeight: 1.4, fontSize: '0.75rem' }}>{item.productShortDescription}</Typography>
                      </TableCell>
                      <TableCell sx={{ width: '20%', py: 2 }}>
                        <Typography sx={{ fontWeight: 800, fontSize: '0.8rem' }}>{item.manufacturerName || '—'}</Typography>
                        <Typography sx={{ fontSize: '0.7rem', color: 'text.secondary', fontFamily: 'monospace' }}>{item.manufacturerPartNumber}</Typography>
                      </TableCell>
                      <TableCell align="right" sx={{ width: '10%', py: 2 }}>
                        <Typography sx={{ fontWeight: 800, fontSize: '0.85rem' }}>{item.quantity}</Typography>
                        <Typography variant="caption" color="text.disabled" sx={{ fontSize: '0.65rem' }}>{item.unitOfMeasure}</Typography>
                      </TableCell>
                      <TableCell align="right" sx={{ width: '10%', py: 2 }}>
                        <Typography sx={{ fontWeight: 800, fontSize: '0.85rem' }}>{item.unitPrice ? `${item.currency || ''} ${item.unitPrice}` : '—'}</Typography>
                      </TableCell>
                      <TableCell sx={{ py: 2 }}>
                        <Stack spacing={0.5}>
                          <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
                            <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', minWidth: 60, fontSize: '0.65rem' }}>CUST RFQ:</Typography>
                            <Typography sx={{ fontSize: '0.7rem', fontWeight: 700, color: 'text.secondary' }}>{item.customerRfqno || 'Internal'}</Typography>
                          </Box>
                          <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
                            <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', minWidth: 60, fontSize: '0.65rem' }}>CODE:</Typography>
                            <Typography sx={{ fontSize: '0.7rem', fontWeight: 800, color: 'primary.main', fontFamily: 'monospace' }}>{item.itemMaterialCode}</Typography>
                          </Box>
                          <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
                            <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', minWidth: 60, fontSize: '0.65rem' }}>LOC:</Typography>
                            <Typography sx={{ fontSize: '0.7rem', fontWeight: 700, color: 'text.secondary' }}>{item.storageLocation}</Typography>
                          </Box>
                        </Stack>
                      </TableCell>
                    </TableRow>
                    {item.extraFields && Object.keys(item.extraFields).length > 0 && (
                      <TableRow>
                        <TableCell colSpan={5} sx={{ py: 1.25, bgcolor: 'action.hover', borderTop: 'none' }}>
                          <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.5, fontSize: '0.6rem' }}>
                            Additional columns from customer document
                          </Typography>
                          <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 0.75 }}>
                            {Object.entries(item.extraFields as Record<string, string>).map(([key, value]) => (
                              <Chip
                                key={key}
                                size="small"
                                variant="outlined"
                                label={`${key}: ${value}`}
                                sx={{ height: 20, fontSize: '0.65rem', fontWeight: 600, color: 'text.secondary', maxWidth: 320 }}
                              />
                            ))}
                          </Box>
                        </TableCell>
                      </TableRow>
                    )}
                    </React.Fragment>
                  ))}
                </TableBody>
              </Table>
            </Paper>
          </Box>
        </Grid>
      </Grid>

      {/* WP-A3: confirm dialog for duplicate resolution */}
      <Dialog open={duplicateAction !== null} onClose={() => setDuplicateAction(null)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>
          {duplicateAction === 'not_duplicate' ? 'Not a duplicate?' : 'Confirm duplicate?'}
        </DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2">
            {duplicateAction === 'not_duplicate'
              ? `This tells the system the lead is NOT a copy of Lead #${lead.duplicateOfLeadId}. The warning goes away and the lead can be converted to an RFQ again.`
              : `This marks the lead as a real duplicate of Lead #${lead.duplicateOfLeadId}. It stays blocked from being converted. You can still change this later.`}
          </Typography>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setDuplicateAction(null)} color="inherit">{t('cancel') || 'Cancel'}</Button>
          <Button
            variant="contained"
            color={duplicateAction === 'not_duplicate' ? 'success' : 'error'}
            onClick={() => duplicateAction && resolveDuplicateMutation.mutate(duplicateAction)}
            disabled={resolveDuplicateMutation.isPending}
          >
            {duplicateAction === 'not_duplicate' ? 'Yes, not a duplicate' : 'Yes, it is a duplicate'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* FE-14: real rejection-reason selector (no more hardcoded reason id 1) */}
      <Dialog open={rejectDialogOpen} onClose={() => setRejectDialogOpen(false)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>{t('reject') || 'Reject'} {lead.rfqno}</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2" sx={{ mb: 2 }}>Please select a reason for rejecting this lead.</Typography>
          <TextField
            select
            fullWidth
            label="Rejection Reason"
            value={rejectReasonId}
            onChange={(e) => setRejectReasonId(e.target.value)}
          >
            {rejectionReasons.map((r: any) => (
              <MenuItem key={r.id} value={r.id}>{r.reasonName}</MenuItem>
            ))}
          </TextField>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setRejectDialogOpen(false)} color="inherit">{t('cancel') || 'Cancel'}</Button>
          <Button
            variant="contained"
            color="error"
            onClick={handleConfirmReject}
            disabled={!rejectReasonId || isProcessing}
          >
            {t('reject') || 'Reject'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default LeadDetailPage;
