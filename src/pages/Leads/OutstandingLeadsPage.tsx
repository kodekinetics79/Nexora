import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, IconButton,
  Stack, Dialog, DialogTitle, DialogContent,
  DialogActions, TextField, MenuItem, Chip,
  Menu,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  AssignmentInd as AssignIcon,
  Visibility as ViewIcon,
  Refresh as RefreshIcon,
  Email as EmailIcon,
  Layers as ItemsIcon,
  Person as UserIcon,
  MoreHoriz as MenuIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import SearchField from '../../components/common/SearchField';
import { useSnackbar } from 'notistack';
import { useAuth } from '../../context/AuthContext';

const OutstandingLeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 10, page: 0 });
  const [search, setSearch] = useState('');
  const [menuAnchor, setMenuAnchor] = useState<{ el: HTMLElement, row: any } | null>(null);

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

  // Assignment Dialog State
  const [assignDialogOpen, setAssignDialogOpen] = useState(false);
  const [selectedAcceptedLeadId, setSelectedAcceptedLeadId] = useState<number | null>(null);
  const [assignToUserId, setAssignToUserId] = useState<string>('');
  const [assignComment, setAssignComment] = useState('');

  const isAdminOrManager = userData?.roleName?.toLowerCase().includes('admin') || userData?.roleName?.toLowerCase().includes('manager');

  const { data, isLoading, refetch } = useQuery({
    queryKey: ['leads-outstanding', paginationModel, search],
    queryFn: () => leadService.getOutstandingLeads({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      assignedToId: isAdminOrManager ? undefined : userData?.id,
      excludeAssigned: true, // Backend logic now handles this correctly with assignedToId
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
      setAssignToUserId('');
      setAssignComment('');
      queryClient.invalidateQueries({ queryKey: ['leads-outstanding'] });
    },
    onError: (err: any) => enqueueSnackbar(err.response?.data?.error || 'Failed to assign lead', { variant: 'error' }),
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

  const columns: GridColDef[] = [
    {
      field: 'rfqno',
      headerName: t('rfq_management'),
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
      field: 'buyer',
      headerName: 'Buyer And Contact',
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
      width: 180,
      renderCell: (p) => (
        <Box sx={{ display: 'flex', flexDirection: 'column', justifyContent: 'center', height: '100%' }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <UserIcon sx={{ fontSize: 14, color: p.row.assignedToFullName ? 'primary.main' : 'error.main' }} />
            <Typography sx={{ fontWeight: 800, fontSize: '0.85rem', color: p.row.assignedToFullName ? 'text.primary' : 'error.main' }}>
              {p.row.assignedToFullName || 'Unassigned'}
            </Typography>
          </Stack>
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
      field: 'itemCount',
      headerName: t('invoice_items'),
      width: 80,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', height: '100%' }}>
          <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
          <Typography sx={{ fontSize: '0.85rem', fontWeight: 800 }}>{p.value || 0}</Typography>
        </Stack>
      )
    },
    {
      field: 'source_confidence',
      headerName: 'Source & AI Match',
      width: 160,
      renderCell: (p) => {
        const val = (p.row.aiconfidence || 0) * 100;
        const color = val > 80 ? 'success' : val > 50 ? 'warning' : 'error';
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
            <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.3 }}>
              <Typography sx={{ fontSize: '0.7rem', fontWeight: 900, color: `${color}.main` }}>
                {Math.round(val)}% Match
              </Typography>
            </Stack>
            <Box sx={{ height: 4, width: '100%', bgcolor: 'action.hover', borderRadius: 2, overflow: 'hidden' }}>
              <Box sx={{ height: '100%', width: `${val}%`, bgcolor: `${color}.main` }} />
            </Box>
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

      {/* Search */}
      <Paper sx={{ p: 2, mb: 2, borderRadius: 3, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="400px" value={search} onChange={setSearch} placeholder="Filter outstanding leads..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ width: '100%', borderRadius: 3, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: '0 4px 20px rgba(0,0,0,0.03)' }}>
        <DataGrid
          autoHeight
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
              {users.map((u: any) => (
                <MenuItem key={u.id} value={u.id}>{u.fullName || u.userName}</MenuItem>
              ))}
            </TextField>
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
        <MenuItem
          onClick={() => {
            const id = menuAnchor!.row.id;
            setMenuAnchor(null);
            handleAssignClick(id);
          }}
          sx={{ fontSize: '0.8rem', fontWeight: 600, py: 1 }}
        >
          <AssignIcon sx={{ fontSize: 16, mr: 1, color: 'warning.main' }} /> Assign
        </MenuItem>
      </Menu>
    </Box >
  );
};

export default OutstandingLeadsPage;
