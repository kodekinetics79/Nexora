import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, IconButton,
  Tooltip, Chip, Stack, Menu, MenuItem,
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
import leadService from '../../api/services/leadService';
import SearchField from '../../components/common/SearchField';
import { useAuth } from '../../context/AuthContext';
import { presentableErrorMessage } from '../../utils/apiErrors';

const AssignedLeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  // Filter toggle wired to the existing assignedToId param.
  const [myLeadsOnly, setMyLeadsOnly] = useState(false);
  // Inline 2-click reassign: click the assignee name, then pick the new name.
  const [quickAssign, setQuickAssign] = useState<{ el: HTMLElement, leadId: number, expectedAssigneeId: number } | null>(null);

  const isAdminOrManager = userData?.roleName?.toLowerCase().includes('admin') || userData?.roleName?.toLowerCase().includes('manager');

  const formatDate = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = new Date(dateStr);
    return isNaN(d.getTime()) ? '—' : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
  };

  const getUrgencyColor = (dateStr: string | null) => {
    if (!dateStr) return 'text.secondary';
    const deadline = new Date(dateStr);
    const now = new Date();
    const diffDays = Math.ceil((deadline.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
    if (diffDays < 3) return 'error.main';
    if (diffDays < 7) return 'warning.main';
    return 'success.main';
  };

  const { data, isLoading, refetch } = useQuery({
    queryKey: ['leads-assigned', paginationModel, search, myLeadsOnly],
    queryFn: () => leadService.getAssignedLeads({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      assignedToId: myLeadsOnly ? userData?.id : undefined,
    }),
  });

  const { data: users = [] } = useQuery({
    queryKey: ['assignment-users'],
    queryFn: () => leadService.getUsersForAssignment(userData?.businessUnitId || 0),
    enabled: !!userData?.businessUnitId && !!isAdminOrManager,
  });

  const reassignMutation = useMutation({
    mutationFn: (payload: { leadId: number; assignedToUserId: number; expectedAssigneeId: number }) => leadService.assignLead(payload),
    onSuccess: () => {
      enqueueSnackbar('Lead reassigned successfully!', { variant: 'success' });
      setQuickAssign(null);
      queryClient.invalidateQueries({ queryKey: ['leads-assigned'] });
      queryClient.invalidateQueries({ queryKey: ['leads-outstanding'] });
    },
    onError: (error: unknown) => enqueueSnackbar(
      presentableErrorMessage(error, 'The lead could not be reassigned. Its current owner is unchanged — try again.'),
      { variant: 'error' },
    ),
  });

  const columns: GridColDef[] = [
    {
      field: 'rfqno',
      headerName: t('rfq_management'),
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
      field: 'buyer',
      headerName: 'Buyer And Contact',
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
          {isAdminOrManager ? (
            // Click 1 of the 2-click reassign: opens the name list right here.
            <Tooltip title="Click to hand this lead to someone else">
              <Button
                size="small"
                color="inherit"
                endIcon={<ChangeIcon sx={{ fontSize: 14 }} />}
                onClick={(e) => setQuickAssign({
                  el: e.currentTarget,
                  leadId: p.row.id,
                  expectedAssigneeId: Number(p.row.assignedToId),
                })}
                sx={{ fontWeight: 800, fontSize: '0.8rem', py: 0.25, px: 0.75, justifyContent: 'flex-start', width: 'fit-content', textTransform: 'none' }}
              >
                <UserIcon sx={{ fontSize: 14, mr: 0.5, color: 'primary.main' }} />
                {p.row.assignedToFullName || 'Unassigned'}
              </Button>
            </Tooltip>
          ) : (
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <UserIcon sx={{ fontSize: 14, color: p.row.assignedToFullName ? 'primary.main' : 'error.main' }} />
              <Typography sx={{ fontWeight: 800, fontSize: '0.85rem', color: p.row.assignedToFullName ? 'text.primary' : 'error.main' }}>
                {p.row.assignedToFullName || 'Unassigned'}
              </Typography>
            </Stack>
          )}
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 700, color: 'text.disabled', textTransform: 'uppercase' }}>
            Since: {formatDate(p.row.assignedOn)}
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
          <Typography sx={{ fontSize: '0.8rem', fontWeight: 700 }}>{formatDate(p.row.recDate)}</Typography>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Accepted: {formatDate(p.row.acceptedDate)}</Typography>
        </Box>
      )
    },
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 120,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 900, color: getUrgencyColor(p.row.bidClosingDate) }}>
            {formatDate(p.row.bidClosingDate)}
          </Typography>
          <Typography variant="caption" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase' }}>Submission</Typography>
        </Box>
      )
    },
    {
      field: 'aiconfidence',
      headerName: 'AI Match',
      width: 140,
      renderCell: (p) => {
        const val = (p.row.aiconfidence || 0) * 100;
        const color = val > 80 ? 'success' : val > 50 ? 'warning' : 'error';
        return (
          <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%', width: '100%' }}>
            <Typography sx={{ fontSize: '0.7rem', fontWeight: 900, color: `${color}.main`, mb: 0.5 }}>
              {Math.round(val)}% Accurate
            </Typography>
            <Box sx={{ height: 4, width: '100%', bgcolor: 'action.hover', borderRadius: 2, overflow: 'hidden' }}>
              <Box sx={{ height: '100%', width: `${val}%`, bgcolor: `${color}.main` }} />
            </Box>
          </Box>
        );
      }
    },
    {
      field: 'itemCount',
      headerName: t('invoice_items'),
      width: 80,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', height: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800 }}>{p.row.itemCount || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'actions',
      headerName: 'Details',
      width: 100,
      sortable: false,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', height: '100%' }}>
          <Tooltip title="View Detailed Intelligence">
            <IconButton
              size="small"
              sx={{ color: 'primary.main', bgcolor: 'primary.lighter', '&:hover': { bgcolor: 'primary.light', color: 'white' } }}
              onClick={() => navigate(`/leads/view/${p.row.id}`)}
            >
              <ViewIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Box>
      ),
    },
  ];

  return (
    <Box sx={{ width: '100%', p: 2 }}>
      {/* Header */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>{t('assigned_leads')}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>Leads currently being processed by the sales team</Typography>
        </Box>
        <Button variant="outlined" startIcon={<RefreshIcon />} onClick={() => refetch()} size="small" sx={{ fontWeight: 800 }}>Refresh Dashboard</Button>
      </Box>

      {/* Search + filters */}
      <Paper sx={{ p: 2, mb: 2, borderRadius: 3, border: '1px solid', borderColor: 'divider', boxShadow: 'none', display: 'flex', alignItems: 'center', gap: 3, flexWrap: 'wrap' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Filter assigned leads..." />
        <FormControlLabel
          control={<Switch checked={myLeadsOnly} onChange={(e) => setMyLeadsOnly(e.target.checked)} size="small" />}
          label={<Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>My leads only</Typography>}
        />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 280px)', width: '100%', borderRadius: 3, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: '0 4px 20px rgba(0,0,0,0.03)' }}>
        <DataGrid
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalCount ?? 0}
          loading={isLoading}
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
        />
      </Paper>

      {/* Inline quick-reassign: click 2 of 2 — pick the new owner */}
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
          Hand this lead to
        </Typography>
        {users.length === 0 && (
          <MenuItem disabled sx={{ fontSize: '0.8rem' }}>No team members found</MenuItem>
        )}
        {users.map((u: any) => (
          <MenuItem
            key={u.id}
            disabled={reassignMutation.isPending}
            onClick={() => {
              if (quickAssign) reassignMutation.mutate({
                leadId: quickAssign.leadId,
                assignedToUserId: Number(u.id),
                expectedAssigneeId: quickAssign.expectedAssigneeId,
              });
            }}
            sx={{ fontSize: '0.8rem', fontWeight: 600, py: 1 }}
          >
            <UserIcon sx={{ fontSize: 16, mr: 1, color: 'primary.main' }} /> {u.fullName || u.userName}
          </MenuItem>
        ))}
      </Menu>
    </Box>
  );
};

export default AssignedLeadsPage;
