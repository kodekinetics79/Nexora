import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Breadcrumbs,
  Button,
  Grid,
  Link,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
  alpha,
  useTheme,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  CheckCircle as ApproveIcon,
  Download as ExportIcon,
  Edit as EditIcon,
  History as HistoryIcon,
  Inventory2 as ItemsIcon,
  Link as LinkIcon,
  Notes as NotesIcon,
  Person as PersonIcon,
  ReceiptLong as RfqIcon,
} from '@mui/icons-material';
import rfqService from '../../../api/services/rfqService';
import { useAuth } from '../../../context/AuthContext';
import EmailPromptDialog from '../../../components/common/EmailPromptDialog';
import { useSnackbar } from 'notistack';
import InfoCard from '../../../components/common/InfoCard';
import TimelineCard from '../../../components/common/TimelineCard';
import LoadingPage from '../../../components/common/LoadingPage';
import EmptyState from '../../../components/common/EmptyState';
import StatusBadge from '../../../components/common/StatusBadge';
import AttachmentCard from '../../../components/common/AttachmentCard';
import ActionMenu from '../../../components/common/ActionMenu';

const DataField: React.FC<{ label: string; value: React.ReactNode; tone?: 'default' | 'danger' | 'success' }> = ({
  label,
  value,
  tone = 'default',
}) => (
  <Box
    sx={{
      p: 1.5,
      borderRadius: 2,
      bgcolor: 'action.hover',
      border: '1px solid',
      borderColor: 'divider',
      minHeight: 78,
    }}
  >
    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 850, textTransform: 'uppercase' }}>
      {label}
    </Typography>
    <Typography
      variant="body2"
      sx={{
        mt: 0.5,
        fontWeight: 850,
        color: tone === 'danger' ? 'error.main' : tone === 'success' ? 'success.main' : 'text.primary',
        overflowWrap: 'anywhere',
      }}
    >
      {value || '-'}
    </Typography>
  </Box>
);

const ViewRFQPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const theme = useTheme();
  const { userData } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const [approvalDialogOpen, setApprovalDialogOpen] = React.useState(false);

  const { data: rfq, isLoading } = useQuery({
    queryKey: ['rfq-detail', Number(id)],
    queryFn: () => rfqService.getById(Number(id), userData?.businessUnitId || 0),
    enabled: !!id && !!userData?.businessUnitId,
    staleTime: 2 * 60_000,
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

  const formatDate = (dateStr: string | null | undefined) => {
    if (!dateStr) return '-';
    const d = new Date(dateStr);
    return Number.isNaN(d.getTime()) ? '-' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const formatMoney = (amount: number, currency = '$') =>
    `${currency} ${Number(amount || 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;

  if (isLoading) return <LoadingPage variant="form" />;
  if (!rfq) return <EmptyState title="RFQ not found" message="The selected RFQ could not be loaded." onAction={() => navigate('/procurement/rfqs/all')} actionLabel="Back to RFQs" />;

  const items = rfq.rfqitems || [];
  const isDraft = rfq.rfqstatusId === 34;
  const total = items.reduce((sum, item) => sum + ((item.quantity || 0) * (item.unitPrice || 0)), 0);

  return (
    <Box>
      <Box
        sx={{
          mb: 2.5,
        }}
      >
        <Stack direction={{ xs: 'column', lg: 'row' }} sx={{ justifyContent: 'space-between', gap: 2.5, alignItems: { lg: 'flex-end' } }}>
          <Box>
            <Breadcrumbs separator=">" sx={{ mb: 1.25, '& .MuiBreadcrumbs-separator': { color: 'text.secondary', fontWeight: 800 } }}>
              <Link component="button" onClick={() => navigate('/procurement/rfqs/all')} sx={{ color: 'text.secondary', fontWeight: 850, textDecoration: 'none' }}>
                RFQ Management
              </Link>
              <Typography sx={{ fontWeight: 900, color: 'text.primary' }}>RFQ Details</Typography>
            </Breadcrumbs>
            <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
              <Box
                sx={{
                  width: 48,
                  height: 48,
                  borderRadius: 2,
                  display: 'grid',
                  placeItems: 'center',
                  color: '#fff',
                  background: `linear-gradient(135deg, ${theme.palette.primary.main}, ${theme.palette.primary.dark})`,
                  boxShadow: `0 16px 30px ${alpha(theme.palette.primary.main, 0.22)}`,
                }}
              >
                <RfqIcon />
              </Box>
              <Box>
                <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
                  <Typography variant="h4" sx={{ fontWeight: 950, lineHeight: 1.05 }}>
                    RFQ # - {rfq.rfqno}
                  </Typography>
                  <StatusBadge label={rfq.rfqstatusValue || 'Unknown'} tone={isDraft ? 'warning' : 'success'} />
                </Stack>
                <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, fontWeight: 650 }}>
                  {rfq.buyersName || rfq.customerName || 'Customer not assigned'} / {items.length} line items / {formatDate(rfq.recDate)}
                </Typography>
              </Box>
            </Stack>
          </Box>

          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
            <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate(-1)}>
              Back
            </Button>
            <Button variant="outlined" startIcon={<ExportIcon />} onClick={() => window.print()}>
              Export
            </Button>
            <ActionMenu
              items={[
                ...(isDraft
                  ? [
                      { label: 'Edit Draft', icon: <EditIcon fontSize="small" />, onClick: () => navigate(`/procurement/rfqs/process/${rfq.id}`) },
                      { label: 'Approve & Send', icon: <ApproveIcon fontSize="small" />, onClick: () => setApprovalDialogOpen(true) },
                    ]
                  : []),
                { label: 'Print / Export', icon: <ExportIcon fontSize="small" />, onClick: () => window.print() },
              ]}
            />
          </Stack>
        </Stack>
      </Box>

      <Grid container spacing={2.5}>
        <Grid size={{ xs: 12, lg: 8.5 }}>
          <Stack spacing={2.5}>
            <InfoCard title="General Information" subtitle="Customer, dates, ownership, and business unit context." icon={<PersonIcon />} accent={theme.palette.primary.main}>
              <Grid container spacing={1.5}>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="RFQ #" value={rfq.rfqno} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer / Buyer" value={rfq.buyersName || rfq.customerName} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Customer Email" value={rfq.customerEmail || rfq.leadEmail} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Received Date" value={formatDate(rfq.recDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Bid Closing Date" value={formatDate(rfq.bidClosingDate)} tone="danger" /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="RFQ Type" value={rfq.rfqtype || 'Agreement'} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created By" value={rfq.createdBy} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Created On" value={formatDate(rfq.createdDate)} /></Grid>
                <Grid size={{ xs: 12, md: 4 }}><DataField label="Business Unit" value={rfq.businessUnitName} /></Grid>
              </Grid>
            </InfoCard>

            {rfq.headerRemarks ? (
              <InfoCard title="Header Remarks" icon={<NotesIcon />} accent={theme.palette.warning.main}>
                <Typography variant="body2" color="text.secondary" sx={{ whiteSpace: 'pre-wrap' }}>
                  {rfq.headerRemarks}
                </Typography>
              </InfoCard>
            ) : null}

            <InfoCard title={`Invoice Items (${items.length})`} subtitle="Quoted products, quantities, pricing, and totals." icon={<ItemsIcon />} accent={theme.palette.primary.main}>
              <Box sx={{ overflowX: 'auto', mx: -2.5, mb: -2.5 }}>
                <Table size="small" stickyHeader>
                  <TableHead>
                    <TableRow>
                      <TableCell>#</TableCell>
                      <TableCell>Product / Description</TableCell>
                      <TableCell>Manufacturer / Part #</TableCell>
                      <TableCell align="center">Qty</TableCell>
                      <TableCell align="right">Unit Price</TableCell>
                      <TableCell align="right">Total</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {items.map((item, idx) => {
                      const currency = item.currency || '$';
                      return (
                        <TableRow key={item.id || idx} hover>
                          <TableCell sx={{ fontWeight: 850, color: 'text.secondary' }}>{idx + 1}</TableCell>
                          <TableCell sx={{ minWidth: 260 }}>
                            <Typography variant="body2" sx={{ fontWeight: 850, color: 'primary.main' }}>
                              {item.productName || item.productShortName || 'Unknown Product'}
                            </Typography>
                            <Typography variant="caption" color="text.secondary">
                              {item.productShortDescription || 'No description'}
                            </Typography>
                          </TableCell>
                          <TableCell sx={{ minWidth: 190 }}>
                            <Typography variant="body2" sx={{ fontWeight: 750 }}>{item.manufacturerName || '-'}</Typography>
                            <Typography variant="caption" color="text.secondary">{item.manufacturerPartNumber || '-'}</Typography>
                          </TableCell>
                          <TableCell align="center">
                            <StatusBadge label={`${item.quantity || 0} ${item.unitOfMeasure || 'EA'}`} tone="primary" />
                          </TableCell>
                          <TableCell align="right" sx={{ fontWeight: 750 }}>{formatMoney(item.unitPrice || 0, currency)}</TableCell>
                          <TableCell align="right" sx={{ fontWeight: 900, color: 'primary.main' }}>
                            {formatMoney((item.quantity || 0) * (item.unitPrice || 0), currency)}
                          </TableCell>
                        </TableRow>
                      );
                    })}
                    {items.length === 0 ? (
                      <TableRow>
                        <TableCell colSpan={6}>
                          <EmptyState title="No RFQ items" message="This RFQ does not include line items yet." />
                        </TableCell>
                      </TableRow>
                    ) : null}
                  </TableBody>
                </Table>
                <Stack direction="row" spacing={4} sx={{ justifyContent: 'flex-end', p: 2, bgcolor: 'action.hover', borderTop: '1px solid', borderColor: 'divider' }}>
                  <Box sx={{ textAlign: 'right' }}>
                    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 850, textTransform: 'uppercase' }}>Total Items</Typography>
                    <Typography sx={{ fontWeight: 900 }}>{items.length}</Typography>
                  </Box>
                  <Box sx={{ textAlign: 'right' }}>
                    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 850, textTransform: 'uppercase' }}>Grand Total</Typography>
                    <Typography variant="h6" sx={{ fontWeight: 950, color: 'primary.main' }}>{formatMoney(total)}</Typography>
                  </Box>
                </Stack>
              </Box>
            </InfoCard>
          </Stack>
        </Grid>

        <Grid size={{ xs: 12, lg: 3.5 }}>
          <Stack spacing={2.5} sx={{ position: { lg: 'sticky' }, top: { lg: 92 } }}>
            {rfq.leadId ? (
              <Paper
                sx={{
                  p: 2.5,
                  background: 'background.paper',
                }}
              >
                <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 1.5 }}>
                  <Box sx={{ width: 38, height: 38, borderRadius: 2, display: 'grid', placeItems: 'center', bgcolor: alpha(theme.palette.primary.main, 0.12), color: 'primary.main' }}>
                    <LinkIcon />
                  </Box>
                  <Box>
                    <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Connected Lead</Typography>
                    <Typography variant="caption" color="text.secondary">Lead #{rfq.leadId}</Typography>
                  </Box>
                </Stack>
                <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                  This RFQ was generated from a procurement lead and keeps the original context connected.
                </Typography>
                <Button fullWidth variant="outlined" onClick={() => navigate(`/procurement/leads/view/${rfq.leadId}`)}>
                  View Original Lead
                </Button>
              </Paper>
            ) : null}

            <TimelineCard
              title="Workflow History"
              subtitle="Recent RFQ lifecycle activity."
              items={[
                { title: 'RFQ Created', description: `by ${rfq.createdBy || 'System'}`, meta: formatDate(rfq.createdDate), icon: <EditIcon sx={{ fontSize: 15 }} /> },
                ...(rfq.modifiedBy ? [{ title: 'Last Modified', description: `by ${rfq.modifiedBy}`, meta: formatDate(rfq.modifiedDate), icon: <HistoryIcon sx={{ fontSize: 15 }} />, color: theme.palette.warning.main }] : []),
                ...(!isDraft ? [{ title: 'Approved & Sent', description: `by ${userData?.userName || 'System'}`, meta: rfq.rfqstatusValue || 'Approved', icon: <ApproveIcon sx={{ fontSize: 15 }} />, color: theme.palette.success.main }] : []),
              ]}
            />

            <AttachmentCard />
          </Stack>
        </Grid>
      </Grid>

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
            customerId,
          });
        }}
      />
    </Box>
  );
};

export default ViewRFQPage;
