import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Grid, Stack, Chip,
  Table, TableHead, TableRow, TableCell, TableBody,
  CircularProgress, Divider, Breadcrumbs, Link, Alert,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  CheckCircle as ApproveIcon,
  RequestQuote as QuoteDraftIcon,
  Download as ExportIcon,
  NavigateNext as NextIcon,
  Schedule as HistoryIcon,
  OpenInNew as WorkspaceIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import { useAuth } from '../../../context/AuthContext';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { useSnackbar } from 'notistack';
import LifecycleActions from '../../../components/common/LifecycleActions';
import lifecycleService from '../../../api/services/commercialLifecycleService';
import CommercialLineIntelligence from '../../../components/common/CommercialLineIntelligence';

const DataField: React.FC<{ label: string; value: string | number | null; bold?: boolean; color?: string }> = ({ label, value, bold = true, color = 'text.primary' }) => (
  <Box sx={{ mb: 1.5 }}>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.2, fontSize: '0.65rem' }}>
      {label}
    </Typography>
    <Typography sx={{ fontWeight: bold ? 800 : 500, fontSize: '0.85rem', color: color, whiteSpace: 'pre-wrap' }}>
      {value || '—'}
    </Typography>
  </Box>
);

const ViewRFQPage: React.FC = () => {
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();

  const [approvalDialogOpen, setApprovalDialogOpen] = React.useState(false);

  const { data: rfq, isLoading, isError, refetch } = useQuery({
    queryKey: ['rfq-detail', Number(id)],
    queryFn: () => rfqService.getById(Number(id), userData?.businessUnitId || 0),
    enabled: !!id && !!userData?.businessUnitId,
  });
  const { data: lifecycle } = useQuery({
    queryKey: ['lifecycle', 'rfqs', Number(id)],
    queryFn: () => lifecycleService.getState('rfqs', Number(id)),
    enabled: !!id,
  });

  const approveMutation = useMutation({
    mutationFn: (payload: { id: number; approvedBy: string; email?: string; subject?: string; body?: string; customerId?: number }) =>
      rfqService.approve(payload.id, payload.approvedBy, payload.email, payload.subject, payload.body, payload.customerId),
    onSuccess: () => {
      enqueueSnackbar('RFQ Approved and Sent successfully!', { variant: 'success' });
      setApprovalDialogOpen(false);
      queryClient.invalidateQueries({ queryKey: ['rfq-detail', Number(id)] });
    },
    onError: () => enqueueSnackbar('Failed to approve RFQ', { variant: 'error' }),
  });

  const quoteDraftMutation = useMutation({
    mutationFn: () => rfqService.prepareQuoteDraft(Number(id)),
    onSuccess: (quote) => {
      enqueueSnackbar('Quote Draft prepared with pricing and commercial terms pending.', { variant: 'success' });
      navigate(`/sales/quotes/view/${quote.id}`);
    },
    onError: (error: any) => enqueueSnackbar(
      error?.response?.data?.message || 'The RFQ still has fields that require review.',
      { variant: 'error' },
    ),
  });

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (isError) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>We couldn't load this RFQ.</Alert></Box>;
  if (!rfq) return <Box sx={{ p: 4 }}><Typography>RFQ not found.</Typography></Box>;

  const isDraft = lifecycle?.currentStatusCode === 'DRAFT';
  const canPrepareQuote = Boolean(rfq.customerId && rfq.leadId && rfq.rfqitems.length > 0);

  return (
    <Box sx={{ p: 3, maxWidth: 1800, mx: 'auto' }}>
      {/* Header */}
      <Box sx={{ mb: 3 }}>
        <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
          <Link component="button" variant="caption" onClick={() => navigate('/procurement/rfqs/all')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
            {t('rfq_management')}
          </Link>
          <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
            {rfq.rfqno}
          </Typography>
        </Breadcrumbs>

        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
            <Typography variant="h4" sx={{ fontWeight: 950, color: 'text.primary', letterSpacing: '-0.02em' }}>
              {rfq.rfqno}
            </Typography>
            {(rfq.nexoraSerial || rfq.commercialCaseReference) && (
              <Chip
                label={`Nexora Serial: ${rfq.nexoraSerial || rfq.commercialCaseReference}`}
                size="small"
                variant="outlined"
                sx={{ fontWeight: 900, fontFamily: 'monospace' }}
              />
            )}
            <Chip 
              label={rfq.rfqstatusValue || 'Unknown'} 
              color={isDraft ? "warning" : "success"}
              size="small"
              sx={{ fontWeight: 900, fontSize: '0.65rem', textTransform: 'uppercase' }}
            />
          </Box>
          <Stack direction="row" spacing={1.5}>
            {rfq.commercialCaseId && (
              <Button
                variant="outlined"
                startIcon={<WorkspaceIcon />}
                onClick={() => navigate(`/commercial-cases/${rfq.commercialCaseId}`)}
                sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
              >
                Workspace
              </Button>
            )}
            {hasPermission('RFQ Management', 'edit') && <LifecycleActions aggregate="rfqs" id={rfq.id} onChanged={() => queryClient.invalidateQueries({ queryKey: ['rfq-detail', Number(id)] })} />}
            <Button
              variant="outlined"
              startIcon={<BackIcon />}
              onClick={() => navigate(-1)}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3, borderColor: 'divider', color: 'text.secondary' }}
            >
              Back
            </Button>
            {isDraft && hasPermission('RFQ Management', 'edit') && (
              <>
                <Button
                  variant="outlined"
                  color="primary"
                  startIcon={<EditIcon />}
                  sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
                >
                  Edit Draft
                </Button>
              </>
            )}
            {hasPermission('Quotations', 'create') && <Button variant="contained" color="success" startIcon={<QuoteDraftIcon />}
              onClick={() => quoteDraftMutation.mutate()} disabled={!canPrepareQuote || quoteDraftMutation.isPending}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}>
              Prepare Quote Draft
            </Button>}
            <Button
              variant="outlined"
              startIcon={<ExportIcon />}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3, borderColor: 'divider', color: 'text.secondary' }}
            >
              Export
            </Button>
          </Stack>
        </Box>
      </Box>

      <Grid container spacing={3}>
        {/* RFQ Details */}
        <Grid size={{ xs: 12, lg: 9 }}>
          <Stack spacing={3}>
            {/* General Info */}
            <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 3 }}>
                <Typography sx={{ fontWeight: 900, fontSize: '1rem', color: 'text.primary', textTransform: 'uppercase', letterSpacing: '0.025em' }}>
                  General Information
                </Typography>
              </Box>
              <Grid container spacing={2}>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="RFQ #" value={rfq.rfqno} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Active Lead Revision" value={`Revision ${rfq.activeLeadRevision || 1}`} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Readiness" value={rfq.readiness} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}>
                  <Button size="small" variant="outlined" onClick={() => navigate(`/procurement/leads/view/${rfq.leadId}`)}>Open Canonical Lead</Button>
                </Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer / Buyer" value={rfq.buyersName || rfq.customerName || 'N/A'} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer Email" value={rfq.customerEmail || rfq.leadEmail || 'N/A'} /></Grid>
                
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Received Date" value={formatDate(rfq.recDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Bid Closing Date" value={formatDate(rfq.bidClosingDate || null)} color="error.main" /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="RFQ Type" value={rfq.rfqtype || 'Agreement'} /></Grid>

                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created By" value={rfq.createdBy} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created On" value={formatDate(rfq.createdDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Business Unit" value={rfq.businessUnitName || null} /></Grid>
              </Grid>

              {rfq.headerRemarks && (
                <Box sx={{ mt: 2 }}>
                  <Divider sx={{ mb: 2 }} />
                  <DataField label="Header Remarks" value={rfq.headerRemarks} bold={false} />
                </Box>
              )}
            </Paper>

            <Box sx={{ mb: 2 }}><CommercialLineIntelligence stage="rfq" recordId={rfq.id} /></Box>

            {/* Line Items */}
            <Paper sx={{ borderRadius: 3, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
              <Box sx={{ p: 2.5, bgcolor: '#fafafa', borderBottom: '1px solid', borderColor: 'divider' }}>
                <Typography sx={{ fontWeight: 950, fontSize: '0.9rem', color: 'text.primary', textTransform: 'uppercase' }}>
                  RFQ Lines ({rfq.rfqitems.length})
                </Typography>
              </Box>
              <Table size="small">
                <TableHead sx={{ bgcolor: '#fcfcfc' }}>
                  <TableRow>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>#</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Product / Description</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Manufacturer / Part #</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }} align="center">Qty</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Resolution</TableCell>
                    <TableCell sx={{ fontWeight: 800, fontSize: '0.75rem' }}>Next Action</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {rfq.rfqitems.map((item, idx) => (
                    <TableRow key={item.id} hover sx={{ '&:last-child td': { border: 0 } }}>
                      <TableCell sx={{ fontSize: '0.8rem', fontWeight: 700, color: 'text.secondary' }}>{idx + 1}</TableCell>
                      <TableCell>
                        <Typography sx={{ fontSize: '0.8rem', fontWeight: 800, color: 'primary.main' }}>
                          {item.productName || item.productShortName || 'Unknown Product'}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>
                          {item.productShortDescription || 'No description'}
                        </Typography>
                      </TableCell>
                      <TableCell>
                        <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>
                          {item.manufacturerName || 'N/A'}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.disabled' }}>
                          {item.manufacturerPartNumber || 'N/A'}
                        </Typography>
                      </TableCell>
                      <TableCell align="center" sx={{ fontSize: '0.85rem', fontWeight: 900 }}>
                        {item.quantity} {item.unitOfMeasure || 'EA'}
                      </TableCell>
                      <TableCell><Chip size="small" label={item.productId ? 'Request data verified' : 'Item Resolution Pending'} color={item.productId ? 'success' : 'warning'} variant="outlined" /></TableCell>
                      <TableCell><Typography variant="caption" color="text.secondary">Inventory Check Pending · Pricing Pending</Typography></TableCell>
                    </TableRow>
                  ))}
                  {rfq.rfqitems.length === 0 && (
                    <TableRow>
                      <TableCell colSpan={6} sx={{ py: 4, textAlign: 'center', color: 'text.disabled' }}>
                        No items found in this RFQ.
                      </TableCell>
                    </TableRow>
                  )}
                </TableBody>
              </Table>
              <Box sx={{ p: 2, display: 'flex', justifyContent: 'flex-end', bgcolor: '#fafafa' }}>
                <Stack direction="row" spacing={4}>
                   <Box sx={{ textAlign: 'right' }}>
                      <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Total Items</Typography>
                      <Typography sx={{ fontWeight: 900, fontSize: '1.1rem' }}>{rfq.rfqitems.length}</Typography>
                   </Box>
                   <Chip label="Commercial Review Required" color="warning" variant="outlined" />
                </Stack>
              </Box>
            </Paper>
          </Stack>
        </Grid>

        {/* Sidebar */}
        <Grid size={{ xs: 12, lg: 3 }}>
          <Stack spacing={3}>
            {/* Lead Connection */}
            {rfq.leadId && (
              <Paper sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider', bgcolor: 'primary.lighter' }}>
                <Typography variant="caption" sx={{ fontWeight: 900, color: 'primary.main', textTransform: 'uppercase', mb: 1, display: 'block' }}>
                  Connected Lead
                </Typography>
                <Typography sx={{ fontWeight: 800, fontSize: '0.9rem', mb: 1.5 }}>
                  This RFQ was generated from Lead #{rfq.leadId}
                </Typography>
                <Button 
                  fullWidth variant="contained" size="small" 
                  onClick={() => navigate(`/procurement/leads/view/${rfq.leadId}`)}
                  sx={{ textTransform: 'none', fontWeight: 800, borderRadius: 2 }}
                >
                  View Original Lead
                </Button>
              </Paper>
            )}

            {/* Audit / Timeline */}
            <Paper sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
               <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 2 }}>
                 <HistoryIcon sx={{ color: 'text.secondary', fontSize: 20 }} />
                 <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase' }}>Workflow History</Typography>
               </Box>
               <Stack spacing={2}>
                  {[
                    { title: 'RFQ Created', user: rfq.createdBy, date: rfq.createdDate, icon: <EditIcon sx={{ fontSize: 14 }} /> },
                    rfq.modifiedBy && { title: 'Last Modified', user: rfq.modifiedBy, date: rfq.modifiedDate, icon: <HistoryIcon sx={{ fontSize: 14 }} /> },
                    !isDraft && { title: 'Approved & Sent', user: userData?.userName, date: new Date().toISOString(), icon: <ApproveIcon sx={{ fontSize: 14 }} /> }
                  ].filter(Boolean).map((log: any, i) => (
                    <Box key={i} sx={{ display: 'flex', gap: 1.5 }}>
                       <Box sx={{ mt: 0.5, p: 0.5, borderRadius: '50%', bgcolor: 'action.hover', display: 'flex' }}>
                         {log.icon}
                       </Box>
                       <Box>
                          <Typography sx={{ fontSize: '0.75rem', fontWeight: 800 }}>{log.title}</Typography>
                          <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block' }}>by {log.user}</Typography>
                          <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.65rem' }}>{formatDate(log.date)}</Typography>
                       </Box>
                    </Box>
                  ))}
               </Stack>
            </Paper>
          </Stack>
        </Grid>
      </Grid>

      {rfq && (
        <EmailPromptDialog
          open={approvalDialogOpen}
          initialEmail={rfq.customerEmail || rfq.leadEmail}
          initialSubject={`Quote for RFQ #${rfq.rfqno}`}
          initialBody={`Dear Customer,\n\nPlease find the quote for your RFQ #${rfq.rfqno} attached.\n\nBest Regards,\n${userData?.userName}`}
          businessUnitId={userData?.businessUnitId || 0}
          customerId={rfq.customerId}
          loading={approveMutation.isPending}
          onCancel={() => setApprovalDialogOpen(false)}
          onConfirm={(email, subject, body, customerId) => {
            approveMutation.mutate({
              id: rfq.id,
              approvedBy: userData?.userName || 'System',
              email,
              subject,
              body,
              customerId
            });
          }}
        />
      )}
    </Box>
  );
};

export default ViewRFQPage;
