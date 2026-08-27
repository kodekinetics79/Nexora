import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, IconButton,
  Stack, Dialog, DialogTitle, DialogContent,
  DialogActions, TextField, MenuItem, Chip,
  Menu, FormControlLabel, Switch, Alert,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import { gridEmptyOverlay } from '../../components/common/gridOverlays';
import { formatDateSafe } from '../../utils/dates';
import {
  AssignmentInd as AssignIcon,
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  Layers as ItemsIcon,
  Person as UserIcon,
  MoreHoriz as MenuIcon,
} from '@mui/icons-material';
import leadService, { assignabilityNote, type AcceptedLeadResponseDTO } from '../../api/services/leadService';
import SearchField from '../../components/common/SearchField';
import ViewTabs from '../../components/layout/ViewTabs';
import { useSnackbar } from 'notistack';
import { useAuth } from '../../context/AuthContext';
import { presentableErrorMessage } from '../../utils/apiErrors';
import ClientCell from './ClientCell';
import ResolveClientDialog from './ResolveClientDialog';

const OutstandingLeadsPage: React.FC = () => {
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
  const [menuAnchor, setMenuAnchor] = useState<{ el: HTMLElement, row: any } | null>(null);
  // Filter toggle wired to the existing excludeAssigned param: on = only Leads
  // still waiting for an owner; off = every accepted Lead not yet promoted to an RFQ.
  const [unassignedOnly, setUnassignedOnly] = useState(true);
  // Inline 2-click assign: click "Assign to..." on a row, then pick a name.
  const [quickAssign, setQuickAssign] = useState<{ el: HTMLElement, leadId: number } | null>(null);

  const getUrgencyColor = (dateStr: string | null) => {
    if (!dateStr) return 'text.secondary';
    const deadline = new Date(dateStr);
    const now = new Date();
    const diffDays = Math.ceil((deadline.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
    if (diffDays < 3) return 'error.main';
    if (diffDays < 7) return 'warning.main';
    return 'success.main';
  };

  // Assignment Dialog State
  const [assignDialogOpen, setAssignDialogOpen] = useState(false);
  const [selectedAcceptedLeadId, setSelectedAcceptedLeadId] = useState<number | null>(null);
  const [assignToUserId, setAssignToUserId] = useState<string>('');
  const [assignComment, setAssignComment] = useState('');

  const isAdminOrManager = userData?.isManager === true || userData?.isSuperAdmin === true;

  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['leads-outstanding', paginationModel, search, unassignedOnly],
    queryFn: () => leadService.getOutstandingLeads({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      assignedToId: isAdminOrManager ? undefined : userData?.id,
      excludeAssigned: unassignedOnly, // Backend logic now handles this correctly with assignedToId
    }),
  });

  const { data: users = [] } = useQuery({
    queryKey: ['assignment-users'],
    queryFn: () => leadService.getUsersForAssignment(userData?.businessUnitId || 0),
    enabled: !!userData.businessUnitId,
  });

  const assignMutation = useMutation({
    mutationFn: (data: { leadId: number; assignedToUserId: number; comment?: string }) => leadService.assignLead(data),
    onSuccess: () => {
      enqueueSnackbar('Lead assigned successfully!', { variant: 'success' });
      setAssignDialogOpen(false);
      setQuickAssign(null);
      setAssignToUserId('');
      setAssignComment('');
      queryClient.invalidateQueries({ queryKey: ['leads-outstanding'] });
      queryClient.invalidateQueries({ queryKey: ['leads-assigned'] });
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The lead could not be assigned. Its current owner is unchanged — try again.'),
      { variant: 'error' },
    ),
  });

  const handleAssignClick = (id: number) => {
    setSelectedAcceptedLeadId(id);
    setAssignDialogOpen(true);
  };


  const handleConfirmAssign = () => {
    if (selectedAcceptedLeadId && assignToUserId) {
      assignMutation.mutate({
        leadId: selectedAcceptedLeadId,
        assignedToUserId: Number(assignToUserId),
        comment: assignComment || undefined,
      });
    }
  };

  // Memoised because DataGrid takes a component TYPE here: rebuilding the factory on every
  // render would hand it a new type each time and remount the overlay for no reason.
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: unassignedOnly ? 'Every accepted lead has an owner' : 'No outstanding leads',
    message: 'Accepted Leads appear here until assigned. Fit and participation then happen in the Decision Workbench; only approved Bid lines are promoted into a formal RFQ.',
    icon: <ItemsIcon sx={{ fontSize: 48 }} />,
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/leads/all')} sx={{ fontWeight: 700 }}>
        See all inquiries
      </Button>
    ),
    filtered: Boolean(search),
    filteredMessage: 'No outstanding lead matches this search. Clear it to see the whole queue.',
    filteredAction: (
      <Button variant="outlined" onClick={() => setSearch('')} sx={{ fontWeight: 700 }}>
        Clear the search
      </Button>
    ),
  }), [search, unassignedOnly, navigate]);

  const columns: GridColDef[] = [
    {
      field: 'rfqno',
      headerName: t('rfq_number'),
      width: 200,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
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
      minWidth: 280,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
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
      renderCell: (p) => {
        const isUnassigned = !p.row.assignedToId;
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
            {isUnassigned && p.row.isUnassignedOverdue && (
              <Chip
                label={`Unassigned ${p.row.unassignedHours ?? 0}h`}
                size="small"
                sx={{ height: 18, fontSize: '0.6rem', fontWeight: 900, bgcolor: 'error.main', color: 'white', mb: 0.5, borderRadius: 1, width: 'fit-content' }}
              />
            )}
            {isUnassigned && isAdminOrManager ? (
              // Click 1 of the 2-click assign: opens the name list right here.
              <Button
                size="small"
                variant="outlined"
                startIcon={<AssignIcon sx={{ fontSize: 14 }} />}
                onClick={(e) => setQuickAssign({ el: e.currentTarget, leadId: p.row.id })}
                sx={{ fontWeight: 800, fontSize: '0.7rem', py: 0.25, px: 1, borderRadius: 1.5, width: 'fit-content', textTransform: 'none' }}
              >
                Assign to...
              </Button>
            ) : (
              <>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  <UserIcon sx={{ fontSize: 14, color: p.row.assignedToFullName && !isUnassigned ? 'primary.main' : 'error.main' }} />
                  <Typography sx={{ fontWeight: 800, fontSize: '0.85rem', color: !isUnassigned ? 'text.primary' : 'error.main' }}>
                    {!isUnassigned ? p.row.assignedToFullName : 'Unassigned'}
                  </Typography>
                </Stack>
                {!isUnassigned && (
                  <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 700, color: 'text.disabled', textTransform: 'uppercase' }}>
                    Since: {formatDateSafe(p.row.assignedOn)}
                  </Typography>
                )}
              </>
            )}
          </Box>
        );
      }
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
    {
      field: 'itemCount',
      headerName: t('line_count'),
      width: 90,
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', justifyContent: 'flex-end', height: '100%', width: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800, fontVariantNumeric: 'tabular-nums' }}>{p.value || 0}</Typography>
        </Stack>
      )
    },
    {
      // The "% Match" meter that used to sit beside this chip rendered
      // Lead.Aiconfidence, which is not a measured accuracy — on the structured
      // path it is a literal written per cell, on the model path it is the
      // model's own self-report. It is gone; the source alone is a fact.
      field: 'source_confidence',
      headerName: 'Source',
      width: 120,
      renderCell: (p) => {
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%', width: '100%' }}>
            <Chip
              label={p.row.leadSource}
              size="small"
              sx={{
                fontWeight: 900,
                fontSize: '0.65rem',
                height: 18,
                mb: 0.5,
                borderRadius: 1,
                color: 'white !important',
                bgcolor: p.row.leadSource === 'Email' ? 'primary.main' : 'secondary.main',
                width: 'fit-content'
              }}
            />
          </Box>
        );
      }
    },
    {
      field: 'actions',
      headerName: t('actions'),
      width: 80,
      sortable: false,
      renderCell: (p) => (
        <IconButton
          size="small"
          onClick={(e) => setMenuAnchor({ el: e.currentTarget, row: p.row })}
        >
          <MenuIcon />
        </IconButton>
      ),
    },
  ];

  return (
    <Box sx={{ width: '100%', p: 2 }}>
      {/* Header */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>{t('outstanding_leads')}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>Accepted leads waiting for team assignment</Typography>
        </Box>
        <Button variant="outlined" startIcon={<RefreshIcon />} onClick={() => refetch()} size="small" sx={{ fontWeight: 800 }}>Refresh Dashboard</Button>
      </Box>

      {/* The lead queues, as one level of tabs. This page used to be reachable only by URL — it
          had a route and no rail entry at all. */}
      <ViewTabs primaryKey="leads" ariaLabel="Inquiry views" />

      {/* Search + filters */}
      <Paper sx={{ p: 2, mb: 2, borderRadius: 3, border: '1px solid', borderColor: 'divider', boxShadow: 'none', display: 'flex', alignItems: 'center', gap: 3, flexWrap: 'wrap' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Filter outstanding leads..." />
        <FormControlLabel
          control={<Switch checked={unassignedOnly} onChange={(e) => setUnassignedOnly(e.target.checked)} size="small" />}
          label={<Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>Waiting for an owner only</Typography>}
        />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 280px)', width: '100%', borderRadius: 3, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: '0 4px 20px rgba(0,0,0,0.03)' }}>
        {/* A failed request used to fall through `data?.items ?? []` into MUI's bare "No rows",
            which a salesperson reads as "no outstanding leads" — a statement about the pipeline,
            not about the network. An empty result is never assumed from a failure. */}
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Box sx={{ maxWidth: 520 }}>
              <ApiErrorNotice
                error={error}
                fallbackMessage="We couldn't load outstanding leads. No empty result has been assumed."
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
        onResolved={() => queryClient.invalidateQueries({ queryKey: ['leads-outstanding'] })}
      />

      {/* Assignment Dialog */}
      <Dialog open={assignDialogOpen} onClose={() => setAssignDialogOpen(false)} fullWidth>
        <DialogTitle sx={{ fontWeight: 900, textTransform: 'uppercase', fontSize: '1rem', letterSpacing: '0.05em' }}>Assign Lead</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={3} sx={{ mt: 1 }}>
            <TextField
              select
              fullWidth
              label="Assign To User"
              value={assignToUserId}
              onChange={(e) => setAssignToUserId(e.target.value)}
              sx={{ '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
            >
              {users.map((u) => (
                <MenuItem key={u.id} value={u.id} disabled={!u.isEligibleForAssignment}>
                  {u.fullName} &mdash; {assignabilityNote(u)}
                </MenuItem>
              ))}
            </TextField>
            {users.length > 0 && !users.some((u) => u.isEligibleForAssignment) && (
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                Nobody in this business unit can currently receive a lead. Governed routing only
                accepts a user who has an effective Sales Rep profile with capacity left, and there
                is no such user right now. Give someone a profile in Sales &gt; Rep directory.
              </Alert>
            )}
            <TextField
              fullWidth
              multiline
              rows={3}
              label="Assignment Comment (Optional)"
              value={assignComment}
              onChange={(e) => setAssignComment(e.target.value)}
              placeholder="Add any instructions for the assignee..."
              sx={{ '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2.5 }}>
          <Button onClick={() => setAssignDialogOpen(false)} color="inherit" sx={{ fontWeight: 800 }}>Cancel</Button>
          <Button
            variant="contained"
            onClick={handleConfirmAssign}
            disabled={!assignToUserId || assignMutation.isPending}
            sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
          >
            Confirm Assignment
          </Button>
        </DialogActions>
      </Dialog>

      {/* Inline quick-assign: click 2 of 2 — pick a name and it's assigned */}
      <Menu
        anchorEl={quickAssign?.el}
        open={Boolean(quickAssign)}
        onClose={() => setQuickAssign(null)}
        slotProps={{
          paper: {
            sx: { borderRadius: 2, boxShadow: '0 8px 32px rgba(0,0,0,0.1)', border: '1px solid', borderColor: 'divider', minWidth: 200, maxHeight: 320 }
          }
        }}
      >
        <Typography sx={{ px: 2, py: 0.75, fontSize: '0.65rem', fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase' }}>
          Pick a person
        </Typography>
        {users.length === 0 && (
          <MenuItem disabled sx={{ fontSize: '0.8rem' }}>No team members found</MenuItem>
        )}
        {users.map((u) => (
          <MenuItem
            key={u.id}
            disabled={assignMutation.isPending || !u.isEligibleForAssignment}
            onClick={() => {
              if (quickAssign) assignMutation.mutate({ leadId: quickAssign.leadId, assignedToUserId: Number(u.id) });
            }}
            sx={{ fontSize: '0.8rem', fontWeight: 600, py: 1, display: 'block' }}
          >
            <Box sx={{ display: 'flex', alignItems: 'center' }}>
              <UserIcon sx={{ fontSize: 16, mr: 1, color: 'primary.main' }} /> {u.fullName}
            </Box>
            <Typography variant="caption" sx={{ display: 'block', pl: 3, color: 'text.secondary', whiteSpace: 'normal' }}>
              {assignabilityNote(u)}
            </Typography>
          </MenuItem>
        ))}
      </Menu>

      {/* Actions Menu */}
      <Menu
        anchorEl={menuAnchor?.el}
        open={Boolean(menuAnchor)}
        onClose={() => setMenuAnchor(null)}
        slotProps={{
          paper: {
            sx: { borderRadius: 2, boxShadow: '0 8px 32px rgba(0,0,0,0.1)', border: '1px solid', borderColor: 'divider', minWidth: 120 }
          }
        }}
      >

        <MenuItem
          onClick={() => {
            const row = menuAnchor?.row;
            if (row) {
              setMenuAnchor(null);
              navigate(`/procurement/leads/view/${row.id}`);
            }
          }}
          sx={{ fontSize: '0.8rem', fontWeight: 600, py: 1 }}
        >
          <ViewIcon sx={{ fontSize: 16, mr: 1, color: 'primary.main' }} /> View
        </MenuItem>
        {isAdminOrManager && (
          <MenuItem
            onClick={() => {
              const id = menuAnchor!.row.id;
              setMenuAnchor(null);
              handleAssignClick(id);
            }}
            sx={{ fontSize: '0.8rem', fontWeight: 600, py: 1 }}
          >
            <AssignIcon sx={{ fontSize: 16, mr: 1, color: 'warning.main' }} /> Assign with a note
          </MenuItem>
        )}
      </Menu>
    </Box >
  );
};

export default OutstandingLeadsPage;
