import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Stack, Table, TableHead,
  TableRow, TableCell, TableBody, IconButton,
  Breadcrumbs, Link,
} from '@mui/material';
import {
  Description as FileIcon,
  Download as DownloadIcon,
  CheckCircle as AcceptIcon,
  Cancel as RejectIcon,
  NavigateNext as NextIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';

import { toast } from 'react-hot-toast';

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

  const { data: lead, isLoading } = useQuery({
    queryKey: ['lead-detail', Number(id)],
    queryFn: () => leadService.getById(Number(id)),
    enabled: !!id,
  });

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const [isProcessing, setIsProcessing] = React.useState(false);

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

  const handleReject = async () => {
    // Basic rejection for now
    if (!lead) return;
    try {
      await leadService.rejectLead(lead.id, 1); // Default reason
      toast.success('Lead rejected');
      navigate('/procurement/leads/all');
    } catch (error) {
      toast.error('Failed to reject lead');
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
          <Typography variant="h5" sx={{ fontWeight: 950, color: 'text.primary', letterSpacing: '-0.02em', mb: 0.5 }}>
            {lead.rfqno}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            {t('lead_detail_analysis') || 'Lead Details Analysis Engine'}
          </Typography>
        </Box>
        {(!lead.isAccepted && !lead.isRejected && lead.status?.toLowerCase() !== 'accepted' && lead.status?.toLowerCase() !== 'rejected') && (
          <Stack direction="row" spacing={1.5}>
            <Button
              variant="outlined"
              color="error"
              startIcon={<RejectIcon />}
              size="small"
              onClick={handleReject}
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
          </Stack>
        )}
      </Box>

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
                    <TableRow key={item.id} sx={{ '&:hover': { bgcolor: 'action.selected' } }}>
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
                  ))}
                </TableBody>
              </Table>
            </Paper>
          </Box>
        </Grid>
      </Grid>
    </Box>
  );
};

export default LeadDetailPage;
