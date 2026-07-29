import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Divider, Avatar, Dialog, DialogTitle, DialogContent,
  DialogActions, FormControl, InputLabel, MenuItem, Select, TextField,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  LocalShipping as SupplierIcon,
  Email as EmailIcon,
  LocationOn as LocationIcon,
  Payments as PaymentIcon,
  Tag as TagIcon,
} from '@mui/icons-material';
import supplierService from '../../api/services/supplierService';
import SupplierFormDialog from './SupplierFormDialog';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';

const readableStatus = (value?: string) => (value || 'UNKNOWN').replaceAll('_', ' ');

const InfoRow: React.FC<{ label: string; value: React.ReactNode }> = ({ label, value }) => (
  <Box sx={{ display: 'flex', gap: 2, py: 0.9, borderBottom: '1px solid', borderColor: 'divider', alignItems: 'center', '&:last-child': { border: 'none' } }}>
    <Typography component="span" sx={{ minWidth: 160, color: 'text.secondary', fontSize: '0.8rem', fontWeight: 600, flexShrink: 0 }}>
      {label}
    </Typography>
    <Box sx={{ fontSize: '0.875rem', fontWeight: 600 }}>
      {value ?? <Typography component="span" sx={{ color: '#9ca3af', fontSize: '0.875rem' }}>—</Typography>}
    </Box>
  </Box>
);

const Section: React.FC<{ title: string; icon: React.ReactNode; children: React.ReactNode }> = ({ title, icon, children }) => (
  <Box>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.08em', display: 'block', mb: 1.5 }}>
      {title}
    </Typography>
    <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
      <Box sx={{ px: 2, py: 1.5, display: 'flex', alignItems: 'center', gap: 1, bgcolor: 'action.hover', borderBottom: '1px solid', borderColor: 'divider' }}>
        <Box sx={{ color: 'primary.main', display: 'flex' }}>{icon}</Box>
        <Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>{title}</Typography>
      </Box>
      <Box sx={{ px: 2.5, py: 1.5 }}>{children}</Box>
    </Paper>
  </Box>
);

const SupplierDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { userData, hasPermission } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const [editOpen, setEditOpen] = useState(false);
  const [governanceOpen, setGovernanceOpen] = useState(false);
  const [governance, setGovernance] = useState({
    governanceStatus: 'UNVERIFIED', verificationStatus: 'UNKNOWN',
    complianceStatus: 'UNKNOWN', riskStatus: 'UNKNOWN',
    readinessStatus: 'REVIEW_REQUIRED', reason: '',
  });

  const { data: supplier, isLoading } = useQuery({
    queryKey: ['supplier-detail', Number(id)],
    queryFn: () => supplierService.getById(Number(id)),
    enabled: !!id,
  });

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (!supplier) return <Box sx={{ p: 4 }}><Typography>Supplier not found.</Typography></Box>;

  const apiBase = import.meta.env.VITE_API_BASE_URL ?? import.meta.env.VITE_API_URL ?? '';
  const canEdit = hasPermission('Suppliers', 'edit');
  const canGovern = userData.isManager === true && canEdit;
  const openGovernance = () => {
    setGovernance({
      governanceStatus: supplier.governanceStatus || 'UNVERIFIED',
      verificationStatus: supplier.verificationStatus || 'UNKNOWN',
      complianceStatus: supplier.complianceStatus || 'UNKNOWN',
      riskStatus: supplier.riskStatus || 'UNKNOWN',
      readinessStatus: supplier.readinessStatus || 'REVIEW_REQUIRED',
      reason: '',
    });
    setGovernanceOpen(true);
  };
  const governanceMutation = useMutation({
    mutationFn: () => supplierService.govern(supplier.id, {
      ...governance,
      expectedConcurrencyToken: supplier.concurrencyToken || '',
    }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['supplier-detail', Number(id)] });
      void queryClient.invalidateQueries({ queryKey: ['suppliers'] });
      setGovernanceOpen(false);
      enqueueSnackbar('Supplier governance decision recorded.', { variant: 'success' });
    },
    onError: () => enqueueSnackbar('Supplier governance decision was not recorded.', { variant: 'error' }),
  });

  return (
    <Box sx={{ p: 3, width: '100%' }}>
      {/* Top Bar */}
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
          <Button startIcon={<BackIcon />} onClick={() => navigate('/suppliers')} size="small" variant="outlined" sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            Back
          </Button>
          <Divider orientation="vertical" flexItem />
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <SupplierIcon sx={{ fontSize: 20, color: 'text.secondary' }} />
            <Typography sx={{ fontWeight: 700, fontSize: '0.9rem', color: 'text.secondary' }}>Suppliers /</Typography>
            <Typography sx={{ fontWeight: 800, fontSize: '0.9rem' }}>{supplier.name}</Typography>
          </Box>
        </Box>
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
          <Chip label={supplier.isActive ? 'Active' : 'Inactive'} color={supplier.isActive ? 'success' : 'default'} size="small" variant="outlined" />
          <Chip label={readableStatus(supplier.governanceStatus)} size="small" variant="outlined" />
          {canGovern && <Button variant="outlined" onClick={openGovernance} sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            Review Governance
          </Button>}
          {canEdit && <Button variant="contained" startIcon={<EditIcon />} onClick={() => setEditOpen(true)} disableElevation sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            Edit Supplier
          </Button>}
        </Box>
      </Box>

      {/* Header Card */}
      <Paper sx={{ p: 3, mb: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2.5 }}>
          <Avatar
            src={supplier.imageUrl ? `${apiBase}${supplier.imageUrl}` : undefined}
            sx={{ width: 64, height: 64, bgcolor: 'primary.main', fontSize: '1.5rem', fontWeight: 900 }}
          >
            {supplier.name?.[0]?.toUpperCase()}
          </Avatar>
          <Box sx={{ flex: 1 }}>
            <Typography variant="h5" sx={{ fontWeight: 900, mb: 0.3 }}>{supplier.name}</Typography>
            <Box sx={{ display: 'flex', gap: 1.5, flexWrap: 'wrap', alignItems: 'center' }}>
              {supplier.docId && (
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem', bgcolor: 'action.hover', px: 1, py: 0.2, borderRadius: 1, fontWeight: 700 }}>
                  {supplier.docId}
                </Typography>
              )}
              {supplier.contactEmail && (
                <Typography sx={{ fontSize: '0.85rem', color: 'text.secondary' }}>
                  {supplier.contactEmail}
                </Typography>
              )}
              {supplier.currencyName && <Chip label={supplier.currencyName} size="small" variant="outlined" />}
              {supplier.countryName && <Chip label={supplier.countryName} size="small" variant="outlined" />}
            </Box>
          </Box>
          {supplier.successRate != null && (
            <Box sx={{ textAlign: 'center', minWidth: 90 }}>
              <Typography variant="h4" sx={{ fontWeight: 900, color: supplier.successRate >= 70 ? 'success.main' : supplier.successRate >= 40 ? 'warning.main' : 'error.main', lineHeight: 1 }}>
                {supplier.successRate}%
              </Typography>
              <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600 }}>Success Rate</Typography>
            </Box>
          )}
        </Box>

        {/* Stats Strip */}
        <Divider sx={{ my: 2 }} />
        <Box sx={{ display: 'flex', gap: 0 }}>
          {[
            { label: 'Payment Terms', value: supplier.paymentTerms },
            { label: 'Avg Response Time', value: supplier.avgResponseTime != null ? `${supplier.avgResponseTime}h` : null },
            { label: 'Country', value: supplier.countryName },
            { label: 'Currency', value: supplier.currencyName },
          ].map(({ label, value }) => (
            <Box key={label} sx={{ flex: 1, textAlign: 'center', px: 2, borderRight: '1px solid', borderColor: 'divider', '&:last-child': { border: 'none' } }}>
              <Typography sx={{ fontSize: '0.68rem', color: 'text.secondary', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.06em', mb: 0.3 }}>{label}</Typography>
              <Typography sx={{ fontSize: '0.95rem', fontWeight: 800 }}>{value ?? '—'}</Typography>
            </Box>
          ))}
        </Box>
      </Paper>

      {/* Body */}
      <Grid container spacing={3}>
        <Grid size={{ xs: 12, md: 6 }}>
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
            <Section title="Contact" icon={<EmailIcon sx={{ fontSize: 16 }} />}>
              <InfoRow label="Email" value={supplier.contactEmail} />
              <InfoRow label="Payment Terms" value={supplier.paymentTerms} />
              <InfoRow label="Avg Response Time" value={supplier.avgResponseTime != null ? `${supplier.avgResponseTime} hours` : null} />
            </Section>

            <Section title="Tags & Notes" icon={<TagIcon sx={{ fontSize: 16 }} />}>
              <InfoRow label="Tags" value={supplier.tags} />
              <InfoRow label="Comments" value={
                <Typography sx={{ fontSize: '0.875rem', color: 'text.secondary', lineHeight: 1.7 }}>
                  {supplier.comments || '—'}
                </Typography>
              } />
            </Section>
          </Box>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Box sx={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
            <Section title="Address" icon={<LocationIcon sx={{ fontSize: 16 }} />}>
              <InfoRow label="Address Line 1" value={supplier.addressLine1} />
              <InfoRow label="Address Line 2" value={supplier.addressLine2} />
              <InfoRow label="City" value={supplier.cityName} />
              <InfoRow label="Country" value={supplier.countryName} />
              <InfoRow label="Postal Code" value={supplier.postalCode} />
            </Section>

            <Section title="Financial" icon={<PaymentIcon sx={{ fontSize: 16 }} />}>
              <InfoRow label="Currency" value={supplier.currencyName} />
              <InfoRow label="Success Rate" value={supplier.successRate != null ? `${supplier.successRate}%` : null} />
            </Section>

            <Section title="Governance" icon={<SupplierIcon sx={{ fontSize: 16 }} />}>
              <InfoRow label="Approval" value={readableStatus(supplier.governanceStatus)} />
              <InfoRow label="Verification" value={readableStatus(supplier.verificationStatus)} />
              <InfoRow label="Compliance" value={readableStatus(supplier.complianceStatus)} />
              <InfoRow label="Risk" value={readableStatus(supplier.riskStatus)} />
              <InfoRow label="RFQ readiness" value={readableStatus(supplier.readinessStatus)} />
              <InfoRow label="Reviewed by" value={supplier.governanceReviewedBy} />
            </Section>
          </Box>
        </Grid>
      </Grid>

      {canEdit && <SupplierFormDialog open={editOpen} onClose={() => setEditOpen(false)} supplierId={Number(id)} />}
      <Dialog open={governanceOpen} onClose={() => setGovernanceOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle>Supplier Governance Review</DialogTitle>
        <DialogContent dividers sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 2 }}>
          {[
            ['governanceStatus', 'Approval', ['UNVERIFIED', 'REVIEW_REQUIRED', 'PROVISIONAL', 'APPROVED', 'PREFERRED', 'RESTRICTED', 'BLOCKED', 'INACTIVE']],
            ['verificationStatus', 'Verification', ['UNKNOWN', 'PENDING', 'VERIFIED', 'FAILED', 'EXPIRED']],
            ['complianceStatus', 'Compliance', ['UNKNOWN', 'PENDING', 'CLEARED', 'RESTRICTED', 'BLOCKED', 'FAILED']],
            ['riskStatus', 'Risk', ['UNKNOWN', 'LOW', 'MEDIUM', 'HIGH', 'BLOCKED']],
            ['readinessStatus', 'RFQ readiness', ['REVIEW_REQUIRED', 'READY', 'RESTRICTED', 'BLOCKED']],
          ].map(([field, label, options]) => (
            <FormControl key={field as string} size="small" fullWidth>
              <InputLabel>{label as string}</InputLabel>
              <Select label={label as string} value={governance[field as keyof typeof governance]}
                onChange={(event) => setGovernance(current => ({ ...current, [field as string]: event.target.value }))}>
                {(options as string[]).map(option => <MenuItem key={option} value={option}>{readableStatus(option)}</MenuItem>)}
              </Select>
            </FormControl>
          ))}
          <TextField label="Decision reason" required multiline minRows={3} value={governance.reason}
            onChange={(event) => setGovernance(current => ({ ...current, reason: event.target.value }))}
            sx={{ gridColumn: '1 / -1' }} />
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setGovernanceOpen(false)}>Cancel</Button>
          <Button variant="contained" onClick={() => governanceMutation.mutate()}
            disabled={!governance.reason.trim() || !supplier.concurrencyToken || governanceMutation.isPending}>
            Record Decision
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default SupplierDetailPage;
