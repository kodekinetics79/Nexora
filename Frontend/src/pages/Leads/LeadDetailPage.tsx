import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Stack, Table, TableHead, TableContainer,
  TableRow, TableCell, TableBody, IconButton,
  Breadcrumbs, Link, Dialog, DialogTitle, DialogContent,
  DialogActions, Alert, AlertTitle,
} from '@mui/material';
import {
  Description as FileIcon,
  Download as DownloadIcon,
  NavigateNext as NextIcon,
  RuleOutlined as DecisionIcon,
  OpenInNew as WorkspaceIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import LateIngestedBadge from './LateIngestedBadge';
import ClientIdentityPanel from './ClientIdentityPanel';
import ResolveClientDialog from './ResolveClientDialog';
import { clientDisplayName } from './ClientCell';
import { parseDateSafe, formatDateSafe } from '../../utils/dates';
import { downloadAuthenticatedFile } from '../../utils/authenticatedFile';
import { useAuth } from '../../context/AuthContext';
import LeadRevisionTimeline from './LeadRevisionTimeline';
import LeadOwnerControl from './LeadOwnerControl';
import LeadDecisionActions from './LeadDecisionActions';

import { toast } from 'react-hot-toast';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { routingDecisionSentence } from '../../utils/routingDecisionReasons';

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

const LeadDetailPage: React.FC = () => {
  const { hasPermission } = useAuth();
  const { t } = useTranslation();
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const downloadAttachment = async (attachmentId: number, fileName: string) => {
    try {
      await downloadAuthenticatedFile(`/api/File/attachment/${attachmentId}`, fileName);
    } catch {
      toast.error('Could not download this attachment');
    }
  };

  const { data: lead, isLoading, isError, refetch } = useQuery({
    queryKey: ['lead-detail', Number(id)],
    queryFn: () => leadService.getById(Number(id)),
    enabled: !!id,
  });

  // Second entry point to client resolution, from the Customer field in General
  // Information. Shares the query cache with ClientIdentityPanel's own dialog.
  const [resolveClientOpen, setResolveClientOpen] = React.useState(false);

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
    onError: (error: unknown) => {
      toast.error(presentableErrorMessage(error, 'The duplicate flag could not be updated. Nothing was changed — try again.'));
    },
  });

  // Sentinel-safe formatting (dates before 2000 render as "—", never "01 Jan 1").
  const formatDate = (dateStr: string | null) => formatDateSafe(dateStr);

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (isError) return <Box sx={{ p: 4 }}><Alert severity="error" action={<Button color="inherit" onClick={() => refetch()}>Retry</Button>}>We couldn't load this lead.</Alert></Box>;
  if (!lead) return <Box sx={{ p: 4 }}><Typography>Lead not found.</Typography></Box>;

  // Review state, from facts the platform records — not from a model score.
  const awaitingReview = (lead.headerRemarks ?? '').startsWith('[NEEDS REVIEW]');
  // `reviewVersion` proves only that review activity was recorded. It does not identify the
  // reviewer, scope or source coverage, so it must not be presented as “Checked by a person”.
  // The Decision Workbench uses the persisted verification status/actor/time contract.
  const reviewActivityRecorded = !awaitingReview && (lead.reviewVersion ?? 0) > 0;
  const decisionClosed = ['CONVERTED_TO_RFQ', 'DISQUALIFIED', 'LOST', 'CANCELLED', 'COMPLETED', 'DUPLICATED']
    .includes((lead.leadStatusCode ?? '').trim().toUpperCase());

  return (
    <Box sx={{ p: { xs: 1, sm: 2, md: 3 }, maxWidth: 1800, mx: 'auto', minWidth: 0 }}>
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

      <Box sx={{ display: 'flex', flexDirection: { xs: 'column', md: 'row' }, gap: 2, justifyContent: 'space-between', alignItems: { xs: 'stretch', md: 'flex-start' }, mb: 3 }}>
        <Box sx={{ minWidth: 0 }}>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5, flexWrap: 'wrap' }}>
            <Typography variant="h5" sx={{ fontWeight: 950, color: 'text.primary', overflowWrap: 'anywhere' }}>
              {lead.rfqno}
            </Typography>
            <DeadlineChip bidClosingDate={lead.bidClosingDate} />
            {/* Audit fairness: this lead entered Nexora after its deadline, so it
                is excluded from response-time / aging performance metrics. */}
            <LateIngestedBadge
              lateIngested={lead.lateIngested}
              ingestedOn={lead.ingestedOn || lead.ingestedAtUtc || lead.createdDate}
              dueDate={lead.bidClosingDate || lead.subDate}
            />
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            {t('lead_detail_analysis') || 'Lead Details Analysis Engine'}
          </Typography>
          {(lead.nexoraSerial || lead.commercialCaseReference) && (
            <Chip
              label={`Nexora Serial: ${lead.nexoraSerial || lead.commercialCaseReference}`}
              size="small"
              variant="outlined"
              sx={{ mt: 1, fontWeight: 900, fontFamily: 'monospace', width: 'fit-content' }}
            />
          )}
          {/* The client organisation is the primary identity on a lead, so this
              panel is ALWAYS rendered — resolved, suggested or unresolved — and
              sits directly under the Nexora Serial. It replaces a chip row that
              appeared only when a customer happened to be linked, which is
              exactly the case that never occurred in production. */}
          <ClientIdentityPanel
            lead={lead}
            canEdit={hasPermission('Leads', 'edit')}
            onChanged={() => queryClient.invalidateQueries({ queryKey: ['lead-detail', Number(id)] })}
          />
          <Box sx={{ mt: 1.5 }}>
            <LeadOwnerControl
              leadId={lead.id}
              assignedToId={lead.assignedToId}
              assignedToName={lead.assignedToFullName}
              assignmentMethod={lead.assignmentMethod}
              assignmentVersion={lead.assignmentVersion ?? 1}
              canEdit={hasPermission('Leads', 'edit')}
            />
          </Box>
        </Box>
        <Stack direction="row" spacing={1.5} sx={{ flexWrap: 'wrap', gap: 1, justifyContent: { xs: 'flex-start', md: 'flex-end' } }}>
          <LeadDecisionActions
            leadId={lead.id}
            reviewVersion={lead.reviewVersion ?? 1}
            canEdit={hasPermission('Leads', 'edit')}
          />
          {lead.commercialCaseId && (
            <Button
              variant="outlined"
              startIcon={<WorkspaceIcon />}
              size="small"
              onClick={() => navigate(`/commercial-cases/${lead.commercialCaseId}`)}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
            >
              Workspace
            </Button>
          )}
          <Button
            variant="contained"
            startIcon={<DecisionIcon />}
            size="small"
            onClick={() => navigate(`/procurement/leads/${lead.id}/workbench`)}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            {decisionClosed ? 'View decision record' : 'Open decision workbench'}
          </Button>
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

      {/* The former AMBIGUOUS-only alert lived here. It is gone on purpose:
          ambiguity is one of three client states, all of them now rendered by
          ClientIdentityPanel above with the candidates and a one-click resolve —
          instead of a banner that only fired for one status and sent the rep to
          an unrelated screen. */}

      <Grid container spacing={3}>
        {/* Left Column: General Information */}
        <Grid size={{ xs: 12, lg: 9 }} component="div">
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', height: '100%' }}>
            <SectionTitle title={t('general_information') || 'General Information'} />
            <Grid container spacing={2} component="div">
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Customer RFQ Reference" value={lead.rfqno} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Buyer Name" value={lead.buyersName} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Client Email" value={lead.clientemail} /></Grid>

              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Received" value={formatDate(lead.recDate)} /></Grid>
              {/* FR-RFQ-04. The Gregorian deadline, with the Hijri rendering of the SAME
                  date directly beneath it. Saudi government tenders publish closing dates
                  in Hijri; a Hijri date read as a Gregorian one loses the bid, so the two
                  are shown together and the cross-check is a glance rather than a
                  conversion someone has to do in their head. */}
              <Grid size={{ xs: 12, md: 4 }} component="div">
                <DataField label="Bid Close" value={formatDate(lead.bidClosingDate)} />
                {lead.bidClosingDateHijri && (
                  <Typography
                    variant="caption"
                    sx={{ display: 'block', mt: -1.2, mb: 1.5, fontFamily: 'monospace', color: 'text.secondary', fontWeight: 700 }}
                  >
                    {lead.bidClosingDateHijri} H
                  </Typography>
                )}
              </Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Source" value={lead.leadSource} /></Grid>

              {lead.leadSource === 'Email' && (
                <>
                  <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Email Sender" value={lead.emailSender || lead.clientemail} /></Grid>
                  <Grid size={{ xs: 12, md: 8 }} component="div"><DataField label="Email Subject" value={lead.emailSubject ?? null} /></Grid>
                  <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Email Received" value={lead.emailReceivedAtUtc ? formatDate(lead.emailReceivedAtUtc) : '—'} /></Grid>
                  <Grid size={{ xs: 12, md: 8 }} component="div"><DataField label="RFC Message-ID" value={lead.emailMessageId ?? null} boldValue={false} /></Grid>
                </>
              )}

              {/* FR-RFQ-04. What the BUYER asked for. It sits next to the bid deadline
                  because the two are constantly confused and they drive different
                  decisions: the deadline governs when we must answer, this governs what
                  we may promise. "Not stated" is a real answer and is shown as one. */}
              <Grid size={{ xs: 12, md: 4 }} component="div">
                <DataField
                  label="Required Delivery (buyer)"
                  value={lead.requiredDeliveryDate ? formatDate(lead.requiredDeliveryDate) : 'Not stated on the document'}
                />
              </Grid>
              {/* FR-RFQ-03. The standing agreement this inquiry is called off against. */}
              <Grid size={{ xs: 12, md: 4 }} component="div">
                <DataField
                  label="Agreement Reference"
                  value={lead.agreementReference || 'None stated'}
                />
              </Grid>

              {/* Audit-grade ingestion timestamp (earliest source received_on;
                  legacy pipeline timestamp / createdDate as display fallbacks),
                  deliberately distinct from the business dates above. */}
              <Grid size={{ xs: 12, md: 4 }} component="div">
                <DataField
                  label="Nexora Ingestion Date"
                  value={`Ingested ${formatDate(lead.ingestedOn || lead.ingestedAtUtc || lead.createdDate || null)}`}
                />
                <LateIngestedBadge
                  lateIngested={lead.lateIngested}
                  ingestedOn={lead.ingestedOn || lead.ingestedAtUtc || lead.createdDate}
                  dueDate={lead.bidClosingDate || lead.subDate}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Current Revision" value={`Revision ${lead.currentRevisionNumber || 1}`} /></Grid>
              {/* No dead-end "Unresolved" string here any more: when there is no
                  client, this field offers the way to set one. The full evidence
                  and the machine's suggestions live in ClientIdentityPanel. */}
              <Grid size={{ xs: 12, md: 4 }} component="div">
                {lead.customerId ? (
                  <DataField label="Customer" value={clientDisplayName(lead)} />
                ) : (
                  <Box sx={{ mb: 1.5 }}>
                    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.2, fontSize: '0.65rem' }}>
                      Customer
                    </Typography>
                    <Button
                      size="small"
                      variant="outlined"
                      onClick={() => setResolveClientOpen(true)}
                      disabled={!hasPermission('Leads', 'edit')}
                      sx={{ fontWeight: 800, textTransform: 'none', mt: 0.25 }}
                    >
                      Set client
                    </Button>
                  </Box>
                )}
              </Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Account Owner" value={lead.accountOwnerName || 'Unassigned'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Opportunity Owner" value={lead.assignedToFullName || 'Unassigned'} /></Grid>
              {lead.assignmentReason && <Grid size={{ xs: 12, md: 8 }} component="div"><DataField label="Assignment reason" value={routingDecisionSentence(lead.assignmentReason)} /></Grid>}

              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="RFQ Type" value={lead.rfqtype ?? 'N/A'} /></Grid>
              <Grid size={{ xs: 12, md: 4 }} component="div"><DataField label="Opportunity No" value={lead.opportunityNo ?? 'N/A'} /></Grid>
              {/* Replaces the "N% Match" meter. That number was Lead.Aiconfidence,
                  which is not a measured accuracy: on the structured path it is a
                  literal written per cell, on the model path it is the model's own
                  self-report. What a user can act on is whether a person has
                  checked this document yet, which is a fact we record. */}
              <Grid size={{ xs: 12, md: 4 }} component="div">
                <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.5, fontSize: '0.65rem' }}>Extraction review</Typography>
                {awaitingReview ? (
                  <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                    <Chip size="small" color="warning" variant="outlined" label="Awaiting review" sx={{ fontWeight: 800, fontSize: '0.7rem' }} />
                    <Button
                      size="small"
                      onClick={() => navigate(`/procurement/extraction/review/${lead.id}`)}
                      sx={{ fontWeight: 800, textTransform: 'none' }}
                    >
                      Open review
                    </Button>
                  </Stack>
                ) : (
                  <Chip
                    size="small"
                    color="default"
                    variant="outlined"
                    label={reviewActivityRecorded ? 'Review activity recorded' : 'Not flagged for review'}
                    sx={{ fontWeight: 800, fontSize: '0.7rem' }}
                  />
                )}
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
                  <IconButton
                    size="small"
                    aria-label={`Download ${file.fileName}`}
                    onClick={() => downloadAttachment(file.id, file.fileName)}
                  >
                    <DownloadIcon sx={{ fontSize: 16 }} />
                  </IconButton>
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
              <TableContainer sx={{ overflowX: 'auto' }}>
              <Table size="small" sx={{ minWidth: 760 }}>
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
                        {/* A quantity the document never stated is shown as a gap, like the
                            price beside it. Rendering null gave a blank cell that read as a
                            loading state; rendering 0 would read as a demand for nothing. */}
                        <Typography sx={{ fontWeight: 800, fontSize: '0.85rem' }}>
                          {item.quantity && item.quantity > 0 ? item.quantity : 'Not stated'}
                        </Typography>
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
              </TableContainer>
            </Paper>
          </Box>
        </Grid>
      </Grid>

      <LeadRevisionTimeline leadId={lead.id} />

      <ResolveClientDialog
        open={resolveClientOpen}
        leadId={lead.id}
        lead={lead}
        // What the message actually said about the buying organisation, so creating a client
        // starts from the enquiry rather than from whatever the operator retyped into search.
        // Only these two become field values; the evidence snippet and buyer name are shown as
        // context so the operator can judge whether the extracted name is the right one.
        prefill={{
          name: lead.customerCompanyNameExtracted,
          email: lead.clientemail,
          contactName: lead.buyersName,
          evidence: lead.customerCompanyEvidence,
        }}
        onClose={() => setResolveClientOpen(false)}
        onResolved={() => queryClient.invalidateQueries({ queryKey: ['lead-detail', Number(id)] })}
      />

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

    </Box>
  );
};

export default LeadDetailPage;
