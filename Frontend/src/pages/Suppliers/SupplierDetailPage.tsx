import React, { useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Divider, Avatar, Dialog, DialogTitle, DialogContent, Alert,
  DialogActions, FormControl, InputLabel, MenuItem, Select, TextField, Collapse,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  LocalShipping as SupplierIcon,
  Email as EmailIcon,
  LocationOn as LocationIcon,
  Payments as PaymentIcon,
  Tag as TagIcon,
  History as HistoryIcon,
  ExpandMore as ExpandMoreIcon,
  ExpandLess as ExpandLessIcon,
} from '@mui/icons-material';
import supplierService, { supplierTierLabel } from '../../api/services/supplierService';
import {
  APPROVE_FOR_RFQS_LABEL, RFQ_READY_RULE, approveForRfqs, approveForRfqsReason, rfqReadinessGaps,
  type GovernanceDecision,
} from './supplierRfqReadiness';
import SupplierFormDialog from './SupplierFormDialog';
import ChangeHistoryPanel from '../../components/common/ChangeHistoryPanel';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';
import { statusLabel } from '../../utils/statusLabels';


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
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [governance, setGovernance] = useState<GovernanceDecision & { reason: string }>({
    governanceStatus: 'UNVERIFIED', verificationStatus: 'UNKNOWN',
    complianceStatus: 'UNKNOWN', riskStatus: 'UNKNOWN',
    readinessStatus: 'REVIEW_REQUIRED', reason: '',
  });

  const { data: supplier, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['supplier-detail', Number(id)],
    queryFn: () => supplierService.getById(Number(id)),
    enabled: !!id,
  });

  // Every hook runs on every render, BEFORE the loading/error/not-found returns below. The
  // mutation used to be declared after them, so the first render (loading) registered one
  // hook fewer than the second (loaded) and React threw #310 — the whole supplier page
  // "stopped working" the moment its data arrived, on every open.
  // Both doors — the one-click preset and the Advanced verdicts — pass their payload in, so the
  // preset never has to round-trip through state before it can be recorded.
  const governanceMutation = useMutation({
    mutationFn: (decision: GovernanceDecision & { reason: string }) => {
      if (!supplier) throw new Error('The supplier has not loaded yet.');
      return supplierService.govern(supplier.id, {
        ...decision,
        expectedConcurrencyToken: supplier.concurrencyToken || '',
      });
    },
    onSuccess: (_result, decision) => {
      void queryClient.invalidateQueries({ queryKey: ['supplier-detail', Number(id)] });
      void queryClient.invalidateQueries({ queryKey: ['suppliers'] });
      setGovernanceOpen(false);
      enqueueSnackbar(rfqReadinessGaps(decision).length === 0
        ? `${supplier?.name ?? 'This supplier'} can now be asked for quotes.`
        : 'Supplier approval decision recorded.', { variant: 'success' });
    },
    onError: (mutationError: any) => enqueueSnackbar(
      mutationError?.response?.data?.detail || mutationError?.response?.data || 'Supplier governance decision was not recorded.',
      { variant: 'error' },
    ),
  });

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (isError) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button onClick={() => refetch()}>Retry</Button>}>
    {(error as any)?.response?.data?.detail || (error as Error)?.message || 'The Supplier could not be loaded.'}
  </Alert></Box>;
  if (!supplier) return <Box sx={{ p: 4 }}><Typography>Supplier not found.</Typography></Box>;

  const canEdit = hasPermission('Suppliers', 'edit');
  const canGovern = userData.isManager === true && canEdit;
  const recorded: GovernanceDecision = {
    governanceStatus: supplier.governanceStatus || 'UNVERIFIED',
    verificationStatus: supplier.verificationStatus || 'UNKNOWN',
    complianceStatus: supplier.complianceStatus || 'UNKNOWN',
    riskStatus: supplier.riskStatus || 'UNKNOWN',
    readinessStatus: supplier.readinessStatus || 'REVIEW_REQUIRED',
  };
  // The preset is the door most managers want: it is offered unless the supplier can already be
  // asked, or its risk verdict is High/Blocked (never lowered quietly), or the record has no
  // concurrency token to write against. In those cases Advanced is the only door, so it opens.
  const alreadyAskable = rfqReadinessGaps(recorded).length === 0;
  const preset = approveForRfqs(recorded);
  const presetOffered = preset !== null && !alreadyAskable && Boolean(supplier.concurrencyToken);
  const actor = userData.userName || userData.email;
  const draftGaps = rfqReadinessGaps(governance);
  const canRecordAdvanced = Boolean(governance.reason.trim()) && Boolean(supplier.concurrencyToken) && !governanceMutation.isPending;
  const openGovernance = () => {
    setGovernance({ ...recorded, reason: '' });
    setAdvancedOpen(!presetOffered);
    setGovernanceOpen(true);
  };

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
          <Chip label={statusLabel(supplier.governanceStatus)} size="small" variant="outlined" />
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
        </Box>

        {/* Stats Strip */}
        <Divider sx={{ my: 2 }} />
        <Box sx={{ display: 'flex', gap: 0 }}>
          {[
            { label: 'Payment Terms', value: supplier.paymentTerms },
            { label: 'RFQ Readiness', value: statusLabel(supplier.readinessStatus) },
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
              {/* Absence is the load-bearing case: without a registration number this supplier's
                  input VAT cannot be treated as recoverable, and capturing its quotes will be
                  refused while the tenant recovers input tax. Say so here rather than let it
                  surface first as an error mid-capture. */}
              <InfoRow label="VAT Registration" value={supplier.taxRegistrationNumber
                ? supplier.taxRegistrationNumber
                : <Typography sx={{ fontSize: '0.875rem', color: 'warning.main', fontWeight: 600 }}>
                    Not captured — input VAT cannot be reclaimed against this supplier
                  </Typography>} />
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
              {/* Tier sits here, with the commercial terms, and deliberately NOT beside the
                  governance verdicts below. It says who you choose to buy from first; it does not
                  say whether this supplier is approved, and it never blocks a dispatch. */}
              <InfoRow label="Tier" value={supplierTierLabel(supplier.tier)} />
              {/* Blank is not zero. An uncaptured credit term has to read as uncaptured, because
                  payment terms can carry weight in the supplier comparison and a supplier with no
                  number is not the same as one that demands payment on the day. */}
              <InfoRow label="Credit days" value={supplier.creditDays === null || supplier.creditDays === undefined
                ? <Typography component="span" sx={{ fontSize: '0.875rem', color: 'text.secondary' }}>Not captured</Typography>
                : `${supplier.creditDays} days`} />
              <InfoRow label="Governance reviewed" value={supplier.governanceReviewedOn ? new Date(supplier.governanceReviewedOn).toLocaleString() : null} />
            </Section>

            <Section title="Governance" icon={<SupplierIcon sx={{ fontSize: 16 }} />}>
              <InfoRow label="Approval" value={statusLabel(supplier.governanceStatus)} />
              <InfoRow label="Verification" value={statusLabel(supplier.verificationStatus)} />
              <InfoRow label="Compliance" value={statusLabel(supplier.complianceStatus)} />
              <InfoRow label="Risk" value={statusLabel(supplier.riskStatus)} />
              <InfoRow label="RFQ readiness" value={statusLabel(supplier.readinessStatus)} />
              <InfoRow label="Reviewed by" value={supplier.governanceReviewedBy} />
            </Section>
          </Box>
        </Grid>

        {/* FR-MDM-05 · the record's own before/after trail. Suppliers carried the endpoint and the
            descriptor already; only Customers and Products ever showed it. Tier and credit days are
            master-data fields a buyer acts on, so "who changed this, from what, and why" has to be
            answerable on the screen that displays them. */}
        <Grid size={{ xs: 12 }}>
          <Section title="Change history" icon={<HistoryIcon sx={{ fontSize: 16 }} />}>
            <ChangeHistoryPanel
              entityType="Supplier"
              entityId={Number(id)}
              emptyMessage="No changes have been recorded for this supplier since audit capture began."
            />
          </Section>
        </Grid>
      </Grid>

      {canEdit && <SupplierFormDialog open={editOpen} onClose={() => setEditOpen(false)} supplierId={Number(id)} />}
      {/* One sentence says the rule; one button records the combination that satisfies it. The five
          verdict selects — 8 x 5 x 6 x 5 x 4 values with no hint which mix is askable — sit under
          Advanced for restricting, blocking, or a risk verdict the preset will not touch. */}
      <Dialog open={governanceOpen} onClose={() => setGovernanceOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Approve {supplier.name} for RFQs</DialogTitle>
        <DialogContent dividers>
          <Typography variant="body2" sx={{ mb: 2, textWrap: 'pretty' }}>{RFQ_READY_RULE}</Typography>
          {alreadyAskable && (
            <Alert severity="success" sx={{ mb: 2 }}>
              This supplier can already be asked for quotes. Change a verdict under Advanced only to restrict or block it.
            </Alert>
          )}
          {preset === null && (
            <Alert severity="warning" sx={{ mb: 2 }}>
              Risk is {statusLabel(supplier.riskStatus)}, so this supplier cannot be marked Ready. Lower the risk verdict under Advanced first; the one-click approval never does that for you.
            </Alert>
          )}
          {presetOffered && preset && (
            <Paper variant="outlined" sx={{ p: 2, mb: 2, display: 'flex', gap: 2, alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', bgcolor: 'action.hover' }}>
              <Box sx={{ minWidth: 0, flex: 1 }}>
                <Typography sx={{ fontWeight: 700 }}>One click does it</Typography>
                <Typography variant="body2" color="text.secondary">
                  Records {statusLabel(preset.governanceStatus)} · Verified · Compliance cleared · Risk {statusLabel(preset.riskStatus)} · Ready for RFQs,
                  with the reason "{approveForRfqsReason(actor)}" on the change history.
                </Typography>
              </Box>
              <Button variant="contained" disableElevation sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}
                onClick={() => governanceMutation.mutate({ ...preset, reason: approveForRfqsReason(actor) })}
                disabled={governanceMutation.isPending}>
                {governanceMutation.isPending ? 'Approving…' : APPROVE_FOR_RFQS_LABEL}
              </Button>
            </Paper>
          )}
          <Button size="small" onClick={() => setAdvancedOpen((open) => !open)} aria-expanded={advancedOpen}
            startIcon={advancedOpen ? <ExpandLessIcon /> : <ExpandMoreIcon />} sx={{ textTransform: 'none', fontWeight: 700 }}>
            Advanced: set each verdict
          </Button>
          <Collapse in={advancedOpen} unmountOnExit>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr' }, gap: 2, mt: 1.5 }}>
              {[
                ['governanceStatus', 'Approval', ['UNVERIFIED', 'REVIEW_REQUIRED', 'PROVISIONAL', 'APPROVED', 'PREFERRED', 'RESTRICTED', 'BLOCKED', 'INACTIVE']],
                ['verificationStatus', 'Verification', ['UNKNOWN', 'PENDING', 'VERIFIED', 'FAILED', 'EXPIRED']],
                ['complianceStatus', 'Compliance', ['UNKNOWN', 'PENDING', 'CLEARED', 'RESTRICTED', 'BLOCKED', 'FAILED']],
                ['riskStatus', 'Risk', ['UNKNOWN', 'LOW', 'MEDIUM', 'HIGH', 'BLOCKED']],
                ['readinessStatus', 'RFQ readiness', ['REVIEW_REQUIRED', 'READY', 'RESTRICTED', 'BLOCKED']],
              ].map(([field, label, options]) => (
                <FormControl key={field as string} size="small" fullWidth>
                  <InputLabel id={`governance-${field as string}-label`}>{label as string}</InputLabel>
                  <Select labelId={`governance-${field as string}-label`} label={label as string}
                    value={governance[field as keyof GovernanceDecision]}
                    onChange={(event) => setGovernance(current => ({ ...current, [field as string]: event.target.value }))}>
                    {(options as string[]).map(option => <MenuItem key={option} value={option}>{statusLabel(option)}</MenuItem>)}
                  </Select>
                </FormControl>
              ))}
              <TextField label="Decision reason" required multiline minRows={3} value={governance.reason}
                onChange={(event) => setGovernance(current => ({ ...current, reason: event.target.value }))}
                sx={{ gridColumn: '1 / -1' }} />
            </Box>
            <Typography variant="body2" sx={{ mt: 1.5, fontWeight: 600, color: draftGaps.length === 0 ? 'success.main' : 'warning.main' }}>
              {draftGaps.length === 0
                ? 'With these verdicts this supplier can be asked for quotes.'
                : `With these verdicts this supplier still cannot be asked: ${draftGaps.join(', ')}.`}
            </Typography>
          </Collapse>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setGovernanceOpen(false)}>Cancel</Button>
          {advancedOpen && (
            <Button variant={!presetOffered && canRecordAdvanced ? 'contained' : 'outlined'} disableElevation
              onClick={() => governanceMutation.mutate(governance)} disabled={!canRecordAdvanced}>
              Record decision
            </Button>
          )}
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default SupplierDetailPage;
