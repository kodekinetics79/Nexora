import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Alert, Box, Typography, Paper, Button, IconButton,
  Tooltip, Chip, Stack,
  FormControlLabel, Switch,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  Layers as ItemsIcon,
  Person as UserIcon,
  ExpandMore as ChangeIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import leadService, { type AcceptedLeadResponseDTO } from '../../api/services/leadService';
import commercialRoutingService, {
  LEAD_OWNERSHIP_ACTION, type RoutingOwnerOption,
} from '../../api/services/commercialRoutingService';
import {
  OwnerPickerMenu, AssignReasonDialog, assignmentNeedsReason, useOwnerOptions,
} from './LeadOwnerPicker';
import { ownershipAuthority } from './LeadOwnerControl';
import SearchField from '../../components/common/SearchField';
import { useAuth } from '../../context/AuthContext';
import { presentableErrorMessage } from '../../utils/apiErrors';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import { gridEmptyOverlay } from '../../components/common/gridOverlays';
import ViewTabs from '../../components/layout/ViewTabs';
import { formatDateSafe } from '../../utils/dates';
import ClientCell from './ClientCell';
import ResolveClientDialog from './ResolveClientDialog';

const AssignedLeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const canEditLeads = hasPermission('Leads', 'edit');
  // One resolve dialog for the whole grid (never one per row).
  const [resolveLead, setResolveLead] = useState<AcceptedLeadResponseDTO | null>(null);
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  // Filter toggle wired to the existing assignedToId param.
  const [myLeadsOnly, setMyLeadsOnly] = useState(false);
  // Inline 2-click reassign: click the assignee name, then pick the new name.
  const [quickAssign, setQuickAssign] = useState<{ el: HTMLElement, lead: AcceptedLeadResponseDTO } | null>(null);
  const [reasonPrompt, setReasonPrompt] = useState<{ owner: RoutingOwnerOption, lead: AcceptedLeadResponseDTO } | null>(null);

  const isAdminOrManager = userData?.isManager === true || userData?.isSuperAdmin === true;

  const getUrgencyColor = (dateStr: string | null) => {
    if (!dateStr) return 'text.secondary';
    const deadline = new Date(dateStr);
    const now = new Date();
    const diffDays = Math.ceil((deadline.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
    if (diffDays < 3) return 'error.main';
    if (diffDays < 7) return 'warning.main';
    return 'success.main';
  };

  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['leads-assigned', paginationModel, search, myLeadsOnly],
    queryFn: () => leadService.getAssignedLeads({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      assignedToId: myLeadsOnly ? userData?.id : undefined,
    }),
  });

  /**
   * This queue posted to `POST /api/UnAssignedLead/assign`, which is manager-only, from a screen
   * any Leads:Edit user can open. The reassign control was therefore hidden from every rep with
   * no explanation, and a rep could not even HAND BACK an inquiry that was their own — something
   * `PUT /api/commercial-routing/leads/{id}/owner` allows, because it resolves the caller's rank
   * against the lead's CURRENT owner rather than from an attribute.
   */
  const ownerOptions = useOwnerOptions(canEditLeads);
  const myOwnerOption = React.useMemo(
    () => (ownerOptions.data ?? []).find((option) => option.userId === userData?.id) ?? null,
    [ownerOptions.data, userData?.id],
  );
  const iCanTakeLeads = myOwnerOption?.isAvailable === true;
  const whyICannotTakeLeads = canEditLeads && !ownerOptions.isLoading && !ownerOptions.isError && !iCanTakeLeads
    ? (myOwnerOption?.eligibilityReason?.trim()
      || 'You do not have a Sales Rep profile yet, so inquiries cannot be routed to you. Ask an administrator to add one under Sales > Rep directory.')
    : null;

  const reassignMutation = useMutation({
    mutationFn: ({ owner, lead, reason }: { owner: RoutingOwnerOption; lead: AcceptedLeadResponseDTO; reason?: string }) => {
      const identity = `lead-owner-${lead.id}-${crypto.randomUUID()}`;
      return commercialRoutingService.changeLeadOwner(lead.id, {
        action: LEAD_OWNERSHIP_ACTION.Assign,
        assignedToUserId: owner.userId,
        expectedAssignmentVersion: lead.assignmentVersion ?? 1,
        idempotencyKey: identity,
        correlationId: identity,
        comment: reason ?? null,
      });
    },
    onSuccess: (_result, { owner }) => {
      enqueueSnackbar(`Assigned to ${owner.name}.`, { variant: 'success' });
      setQuickAssign(null);
      setReasonPrompt(null);
      queryClient.invalidateQueries({ queryKey: ['leads-assigned'] });
      queryClient.invalidateQueries({ queryKey: ['leads-outstanding'] });
      queryClient.invalidateQueries({ queryKey: ['leads'] });
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The owner could not be changed. This inquiry still belongs to whoever held it before.'),
      { variant: 'error' },
    ),
  });

  const assignTo = React.useCallback((owner: RoutingOwnerOption, lead: AcceptedLeadResponseDTO) => {
    if (assignmentNeedsReason(lead.assignedToId, owner.userId)) {
      setQuickAssign(null);
      setReasonPrompt({ owner, lead });
      return;
    }
    reassignMutation.mutate({ owner, lead });
  }, [reassignMutation]);

  // Memoised because DataGrid takes a component TYPE here: rebuilding the factory on every
  // render would hand it a new type each time and remount the overlay for no reason.
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: myLeadsOnly ? 'No leads are assigned to you' : 'No leads are assigned yet',
    message: 'An assigned Lead stays here until a full no-bid closes it or approved Bid lines are promoted into a formal RFQ.',
    icon: <ItemsIcon sx={{ fontSize: 48 }} />,
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/leads/outstanding')} sx={{ fontWeight: 700 }}>
        See unassigned inquiries
      </Button>
    ),
    filtered: Boolean(search),
    filteredMessage: 'No assigned lead matches this search. Clear it to see the whole queue.',
    filteredAction: (
      <Button variant="outlined" onClick={() => setSearch('')} sx={{ fontWeight: 700 }}>
        Clear the search
      </Button>
    ),
  }), [search, myLeadsOnly, navigate]);

  const columns: GridColDef[] = [
    {
      field: 'rfqno',
      headerName: t('rfq_number'),
      width: 200,
      renderCell: (p) => (
        <Box sx={{ py: 1.5 }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace', letterSpacing: '-0.02em' }}>
            {p.row.rfqno || 'NO RFQ #'}
          </Typography>
          {p.row.rfqtype && (
            <Chip
              label={p.row.rfqtype}
              size="small"
              sx={{ height: 16, fontSize: '0.6rem', fontWeight: 900, bgcolor: 'primary.lighter', color: 'primary.dark', mt: 0.5 }}
            />
          )}
        </Box>
      )
    },
    {
      // Which CLIENT the enquiry came from — placed before the buyer person so
      // the organisation reads first.
      field: 'client',
      headerName: 'Client',
      width: 200,
      sortable: false,
      filterable: false,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%' }}>
          <ClientCell lead={p.row} canEdit={canEditLeads} onResolve={() => setResolveLead(p.row)} />
        </Box>
      ),
    },
    {
      field: 'buyer',
      headerName: 'Buyer contact',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => (
        <Box sx={{ py: 1.5 }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.primary', mb: 0.2 }}>
            {p.row.buyersName || 'Unknown Buyer'}
          </Typography>
          <Typography variant="caption" sx={{ color: 'text.secondary', fontSize: '0.7rem', display: 'flex', alignItems: 'center', gap: 0.5 }}>
            <EmailIcon sx={{ fontSize: 12 }} /> {p.row.clientemail}
          </Typography>
          {(p.row.buyersName?.toLowerCase().includes('aramco') || p.row.buyersName?.toLowerCase().includes('sec')) && (
            <Chip
              label="KEY ACCOUNT"
              size="small"
              sx={{ height: 14, fontSize: '0.55rem', fontWeight: 900, bgcolor: 'error.main', color: 'white', mt: 0.5, borderRadius: 1 }}
            />
          )}
        </Box>
      )
    },
    {
      field: 'assignee',
      headerName: 'Assigned Specialist',
      width: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          {!p.row.assignedToId && p.row.isUnassignedOverdue && (
            <Chip
              label={`Unassigned ${p.row.unassignedHours ?? 0}h`}
              size="small"
              sx={{ height: 18, fontSize: '0.6rem', fontWeight: 900, bgcolor: 'error.main', color: 'white', mb: 0.5, borderRadius: 1, width: 'fit-content' }}
            />
          )}
          {(() => {
            /* Who may move THIS inquiry, by the server's own rule: take work that is nobody's,
               put down work that is yours, and only a manager moves anyone else's. The control is
               offered only where that rule says yes; where it says no, the reason is printed
               instead of a 403 arriving after the click. */
            const authority = ownershipAuthority(p.row.assignedToId ?? null, userData?.id ?? null, isAdminOrManager);
            const canOpenPicker = canEditLeads
              && (authority.canGiveItToSomeoneElse || (authority.canTakeIt && iCanTakeLeads));
            if (canOpenPicker) {
              return (
                <Tooltip title={authority.canGiveItToSomeoneElse
                  ? 'Click to hand this inquiry to someone else'
                  : 'Click to take this inquiry'}
                >
                  <Button
                    size="small"
                    color="inherit"
                    /* A cell button whose only label is a person's name announces as that name
                       and nothing else. This says what pressing it does. */
                    aria-label={authority.canGiveItToSomeoneElse
                      ? `Change the owner of ${p.row.rfqno || `inquiry ${p.row.id}`}`
                      : `Take ${p.row.rfqno || `inquiry ${p.row.id}`}`}
                    endIcon={<ChangeIcon sx={{ fontSize: 14 }} />}
                    disabled={reassignMutation.isPending}
                    onClick={(e) => setQuickAssign({ el: e.currentTarget, lead: p.row })}
                    sx={{ fontWeight: 800, fontSize: '0.8rem', py: 0.25, px: 0.75, justifyContent: 'flex-start', width: 'fit-content', textTransform: 'none' }}
                  >
                    <UserIcon sx={{ fontSize: 14, mr: 0.5, color: 'primary.main' }} />
                    {p.row.assignedToFullName || 'Unassigned'}
                  </Button>
                </Tooltip>
              );
            }
            return (
              <Box>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  <UserIcon sx={{ fontSize: 14, color: p.row.assignedToFullName ? 'primary.main' : 'error.main' }} />
                  <Typography sx={{ fontWeight: 800, fontSize: '0.85rem', color: p.row.assignedToFullName ? 'text.primary' : 'error.main' }}>
                    {p.row.assignedToFullName || 'Unassigned'}
                  </Typography>
                </Stack>
                {canEditLeads && p.row.assignedToId != null && p.row.assignedToId !== userData?.id && (
                  <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', whiteSpace: 'normal', lineHeight: 1.3 }}>
                    Only a manager can move it.
                  </Typography>
                )}
              </Box>
            );
          })()}
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 700, color: 'text.disabled', textTransform: 'uppercase' }}>
            Since: {formatDateSafe(p.row.assignedOn)}
          </Typography>
        </Box>
      )
    },
    {
      field: 'recDate',
      headerName: t('date'),
      width: 120,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>{formatDateSafe(p.row.recDate)}</Typography>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Accepted: {formatDateSafe(p.row.acceptedDate)}</Typography>
        </Box>
      )
    },
    {
      // FR-RFQ-04. TWO dates, labelled, in one cell: when the bid must be back and when the
      // buyer wants the goods. The second used to be captured and shown to nobody, which is
      // how a trader commits a lead time against the wrong date. "Not stated" is printed
      // when the document gave none, so an absent requirement never reads as no requirement.
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 150,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 900, color: getUrgencyColor(p.row.bidClosingDate) }}>
            {formatDateSafe(p.row.bidClosingDate)}
          </Typography>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Submission</Typography>
          <Typography sx={{ fontSize: '0.8rem', fontWeight: 700, color: p.row.requiredDeliveryDate ? 'text.primary' : 'text.disabled' }}>
            {p.row.requiredDeliveryDate ? formatDateSafe(p.row.requiredDeliveryDate) : 'Not stated'}
          </Typography>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Buyer delivery</Typography>
        </Box>
      )
    },
    // An "N% Accurate" column used to sit here, driven by Lead.Aiconfidence.
    // Nexora has never measured extraction accuracy — there is no labelled
    // corpus — so no accuracy figure is shown anywhere in the product.
    {
      field: 'itemCount',
      headerName: t('line_count'),
      width: 90,
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', justifyContent: 'flex-end', height: '100%', width: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800, fontVariantNumeric: 'tabular-nums' }}>{p.row.itemCount || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'actions',
      headerName: 'Next action',
      width: 240,
      sortable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', height: '100%' }}>
          <Tooltip title="View canonical Lead">
            <IconButton
              aria-label={`View canonical Lead ${p.row.rfqno || p.row.id}`}
              size="small"
              sx={{ color: 'primary.main', bgcolor: 'primary.lighter', '&:hover': { bgcolor: 'primary.light', color: 'white' } }}
              onClick={() => navigate(`/procurement/leads/view/${p.row.id}`)}
            >
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Button
            size="small"
            variant="contained"
            aria-label={`Open decision workbench for ${p.row.rfqno || `Lead ${p.row.id}`}`}
            onClick={() => navigate(`/procurement/leads/${p.row.id}/workbench`)}
            sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}
          >
            Decision workbench
          </Button>
        </Stack>
      ),
    },
  ];

  return (
    <Box sx={{ width: '100%', p: 2 }}>
      {/* Header */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>{t('assigned_leads')}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>Assigned Leads waiting for fit, participation or approved-line promotion</Typography>
        </Box>
        <Button variant="outlined" startIcon={<RefreshIcon />} onClick={() => refetch()} size="small" sx={{ fontWeight: 800 }}>Refresh Dashboard</Button>
      </Box>

      <ViewTabs primaryKey="leads" ariaLabel="Inquiry views" />

      {/* Search + filters */}
      <Paper sx={{ p: 2, mb: 2, borderRadius: 3, border: '1px solid', borderColor: 'divider', boxShadow: 'none', display: 'flex', alignItems: 'center', gap: 3, flexWrap: 'wrap' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Filter assigned leads..." />
        <FormControlLabel
          control={<Switch checked={myLeadsOnly} onChange={(e) => setMyLeadsOnly(e.target.checked)} size="small" />}
          label={<Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>My leads only</Typography>}
        />
      </Paper>

      {/* A control that is absent for a reason must say the reason. */}
      {whyICannotTakeLeads && !isAdminOrManager && (
        <Alert severity="info" sx={{ mb: 2, borderRadius: 2 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>
            You cannot take inquiries yourself yet.
          </Typography>
          <Typography variant="body2">{whyICannotTakeLeads}</Typography>
        </Alert>
      )}
      {canEditLeads && ownerOptions.isError && (
        <Alert severity="error" sx={{ mb: 2, borderRadius: 2 }}>
          We couldn&apos;t check who can take these inquiries, so the owner controls are hidden.
          Nothing has changed — reload the page to try again.
        </Alert>
      )}

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 280px)', width: '100%', borderRadius: 3, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: '0 4px 20px rgba(0,0,0,0.03)' }}>
        {/* See OutstandingLeadsPage: a failed request must not render as an empty pipeline. */}
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Box sx={{ maxWidth: 520 }}>
              <ApiErrorNotice
                error={error}
                fallbackMessage="We couldn't load assigned leads. No empty result has been assumed."
                onRetry={() => refetch()}
              />
            </Box>
          </Box>
        ) : <DataGrid
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalCount ?? 0}
          loading={isLoading}
          slots={{ noRowsOverlay }}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(r) => r.id}
          rowHeight={95}
          sx={{
            border: 'none',
            '& .MuiDataGrid-columnHeaders': {
              bgcolor: 'action.hover',
              borderBottom: '1px solid',
              borderColor: 'divider',
            },
            '& .MuiDataGrid-cell': {
              borderBottom: '1px solid',
              borderColor: 'action.hover',
            }
          }}
        />}
      </Paper>

      {/* Client resolution — one dialog for the grid, driven by the client cell */}
      <ResolveClientDialog
        open={resolveLead !== null}
        leadId={resolveLead?.id ?? null}
        lead={resolveLead}
        onClose={() => setResolveLead(null)}
        onResolved={() => queryClient.invalidateQueries({ queryKey: ['leads-assigned'] })}
      />

      {/* Click 2 of 2: the one owner picker every leads screen opens. Each name carries the
          routing engine's verdict, so a name that cannot take the inquiry is greyed with its
          reason rather than accepted and then refused. */}
      <OwnerPickerMenu
        anchorEl={quickAssign?.el ?? null}
        open={Boolean(quickAssign)}
        onClose={() => setQuickAssign(null)}
        onPick={(owner) => { if (quickAssign) assignTo(owner, quickAssign.lead); }}
        busy={reassignMutation.isPending}
        heading="Hand this inquiry to"
        currentOwnerId={quickAssign?.lead.assignedToId ?? null}
      />

      <AssignReasonDialog
        open={Boolean(reasonPrompt)}
        ownerName={reasonPrompt?.owner.name ?? ''}
        currentOwnerName={reasonPrompt?.lead.assignedToFullName}
        leadCount={1}
        busy={reassignMutation.isPending}
        onCancel={() => setReasonPrompt(null)}
        onConfirm={(reason) => {
          if (reasonPrompt) reassignMutation.mutate({ owner: reasonPrompt.owner, lead: reasonPrompt.lead, reason });
        }}
      />
    </Box>
  );
};

export default AssignedLeadsPage;
