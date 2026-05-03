import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Typography,
  Paper,
  Button,
  Chip,
  IconButton,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Grid,
  FormControlLabel,
  Switch,
  TextField,
  CircularProgress,
  MenuItem,
  Avatar,
  Tooltip,
  Divider,
} from '@mui/material';
import {
  Add as AddIcon,
  Edit as EditIcon,
  VpnKey as KeyIcon,
  Person as PersonIcon,
} from '@mui/icons-material';
import { DataGrid, type GridColDef, type GridPaginationModel } from '@mui/x-data-grid';
import userService, { type UserDTO } from '../../../api/services/userService';
import { useAuth } from '../../../context/AuthContext';
import SearchField from '../../../components/common/SearchField';
import { handleApiError } from '../../../utils/errorHandler';
import { useSnackbar } from 'notistack';

const UsersPage: React.FC = () => {
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({
    pageSize: 10,
    page: 0,
  });

  const [search, setSearch] = useState('');
  const [filterRole, setFilterRole] = useState<number | 'all'>('all');
  const [filterActive, setFilterActive] = useState<boolean | 'all'>('all');

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [isPasswordModalOpen, setIsPasswordModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<UserDTO | null>(null);

  const [formData, setFormData] = useState<Partial<UserDTO>>({
    firstName: '',
    middleName: '',
    lastName: '',
    email: '',
    roleId: 0,
    teamId: undefined,
    timezone: '',
    region: '',
    managerId: undefined,
    userGroupId: undefined,
    isActive: true,
    buid: userData.businessUnitId || 1,
  });
  const [selectedImage, setSelectedImage] = useState<File | null>(null);
  const [imagePreview, setImagePreview] = useState<string | null>(null);
  const [newPassword, setNewPassword] = useState('');
  const [createPassword, setCreatePassword] = useState('');

  // Queries
  const { data: users, isLoading: loadingUsers } = useQuery({
    queryKey: ['users', paginationModel, search, filterRole, filterActive],
    queryFn: () => userService.getAll({
      businessUnitId: userData.businessUnitId || 1,
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      userName: search || undefined,
      roleId: filterRole === 'all' ? undefined : filterRole,
      isActive: filterActive === 'all' ? undefined : filterActive,
    }),
  });

  const { data: roles } = useQuery({
    queryKey: ['roles', userData.businessUnitId],
    queryFn: () => userService.getRoles(),
  });

  const { data: teams } = useQuery({
    queryKey: ['teams', userData.businessUnitId],
    queryFn: () => userService.getTeams(userData.businessUnitId || 1),
  });

  const { data: userGroups } = useQuery({
    queryKey: ['userGroups', userData.businessUnitId],
    queryFn: () => userService.getUserGroups(userData.businessUnitId || 1),
  });

  const { data: allUsers } = useQuery({
    queryKey: ['users-all-for-manager'],
    queryFn: () => userService.getAll({ businessUnitId: userData.businessUnitId || 1, pageSize: 500 }),
  });

  // Mutations
  const saveMutation = useMutation({
    mutationFn: (data: FormData) => selectedRecord ? userService.update(selectedRecord.id, data) : userService.create(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] });
      enqueueSnackbar(selectedRecord ? 'User updated successfully' : 'User created successfully', { variant: 'success' });
      setIsModalOpen(false);
      resetForm();
    },
    onError: (error: any) => handleApiError(error),
  });

  const passwordMutation = useMutation({
    mutationFn: (data: any) => userService.changePassword(selectedRecord!.id, data),
    onSuccess: () => {
      enqueueSnackbar('Password changed successfully', { variant: 'success' });
      setIsPasswordModalOpen(false);
      setNewPassword('');
    },
    onError: (error: any) => handleApiError(error),
  });

  const resetForm = () => {
    setFormData({
      firstName: '',
      middleName: '',
      lastName: '',
      email: '',
      roleId: 0,
      teamId: undefined,
      timezone: '',
      region: '',
      managerId: undefined,
      userGroupId: undefined,
      isActive: true,
      buid: userData.businessUnitId || 1,
    });
    setSelectedImage(null);
    setImagePreview(null);
    setCreatePassword('');
  };

  const handleEdit = (record: UserDTO) => {
    setSelectedRecord(record);
    setFormData(record);
    setImagePreview(record.imageUrl ? `${import.meta.env.VITE_API_URL}${record.imageUrl}` : null);
    setIsModalOpen(true);
  };

  const handleImageChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files && e.target.files[0]) {
      const file = e.target.files[0];
      setSelectedImage(file);
      setImagePreview(URL.createObjectURL(file));
    }
  };

  const handleSave = () => {
    const data = new FormData();
    Object.entries(formData).forEach(([key, value]) => {
      if (value !== undefined && value !== null) data.append(key, value.toString());
    });
    if (selectedImage) data.append('ImageFile', selectedImage);
    if (!selectedRecord) data.append('Password', createPassword || 'Welcome@123');
    saveMutation.mutate(data);
  };

  const columns: GridColDef[] = [
    {
      field: 'imageUrl',
      headerName: '',
      width: 60,
      sortable: false,
      renderCell: (params) => (
        <Avatar
          src={params.value ? `${import.meta.env.VITE_API_URL}${params.value}` : undefined}
          sx={{ width: 32, height: 32, my: 1 }}
        >
          {!params.value && <PersonIcon fontSize="small" />}
        </Avatar>
      ),
    },
    { field: 'firstName', headerName: 'First Name', flex: 1, minWidth: 120 },
    { field: 'lastName', headerName: 'Last Name', flex: 1, minWidth: 120 },
    { field: 'email', headerName: 'Email', flex: 1.5, minWidth: 200 },
    { field: 'roleName', headerName: 'Role', flex: 1, minWidth: 120 },
    {
      field: 'isActive',
      headerName: 'Status',
      flex: 0.8,
      minWidth: 100,
      renderCell: (params) => (
        <Chip
          label={params.value ? 'Active' : 'Inactive'}
          color={params.value ? 'success' : 'error'}
          size="small"
          variant="outlined"
        />
      ),
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 120,
      sortable: false,
      renderCell: (params) => (
        <Box>
          <Tooltip title="Edit User">
            <IconButton size="small" color="primary" onClick={() => handleEdit(params.row)}>
              <EditIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Change Password">
            <IconButton size="small" color="secondary" onClick={() => { setSelectedRecord(params.row); setIsPasswordModalOpen(true); }}>
              <KeyIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Box>
      ),
    },
  ];

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800 }}>User Management</Typography>
          <Typography variant="body2" color="text.secondary">Create and manage user accounts and system access</Typography>
        </Box>
        <Button variant="contained" startIcon={<AddIcon />} onClick={() => { setSelectedRecord(null); resetForm(); setIsModalOpen(true); }} sx={{ px: 3 }}>
          Add User
        </Button>
      </Box>

      <Paper sx={{ p: 1, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', backgroundColor: 'background.paper', borderRadius: 2 }}>
        <Grid container spacing={2} sx={{ width: '100%' }}>
          <Grid size={{ xs: 12, md: 4 }}>
            <SearchField width="100%" value={search} onChange={setSearch} placeholder="Search users..." />
          </Grid>
          <Grid size={{ xs: 12, md: 3 }}>
            <TextField select fullWidth size="small" label="Role" value={filterRole} onChange={(e) => setFilterRole(e.target.value as any)}>
              <MenuItem value="all">All Roles</MenuItem>
              {roles?.map(r => <MenuItem key={r.setupId} value={r.setupId}>{r.setupName}</MenuItem>)}
            </TextField>
          </Grid>
          <Grid size={{ xs: 12, md: 3 }}>
            <TextField select fullWidth size="small" label="Status" value={filterActive} onChange={(e) => setFilterActive(e.target.value as any)}>
              <MenuItem value="all">All Status</MenuItem>
              <MenuItem value="true">Active</MenuItem>
              <MenuItem value="false">Inactive</MenuItem>
            </TextField>
          </Grid>
        </Grid>
      </Paper>

      <Paper sx={{ height: 'calc(100vh - 220px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
        <DataGrid
          rows={users?.items || []}
          columns={columns}
          rowCount={users?.totalCount || 0}
          loading={loadingUsers}
          pageSizeOptions={[10, 25, 50]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
        />
      </Paper>

      {/* User Modal - Full Fields */}
      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} fullWidth maxWidth="md">
        <DialogTitle sx={{ fontWeight: 800 }}>{selectedRecord ? 'Edit User' : 'Create New User'}</DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Grid container spacing={3}>

            {/* Profile Image */}
            <Grid size={{ xs: 12, md: 3 }} sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 1.5 }}>
              <Avatar src={imagePreview || undefined} sx={{ width: 110, height: 110, border: '4px solid', borderColor: 'divider' }}>
                {!imagePreview && <PersonIcon sx={{ fontSize: 60 }} />}
              </Avatar>
              <Button variant="outlined" component="label" size="small">
                Upload Image
                <input type="file" hidden accept="image/*" onChange={handleImageChange} />
              </Button>
            </Grid>

            {/* Personal Info */}
            <Grid size={{ xs: 12, md: 9 }}>
              <Typography variant="overline" sx={{ fontWeight: 700, color: 'text.secondary', letterSpacing: 1 }}>Personal Information</Typography>
              <Grid container spacing={2} sx={{ mt: 0.5 }}>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth label="First Name" value={formData.firstName || ''} onChange={(e) => setFormData({ ...formData, firstName: e.target.value })} required />
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth label="Middle Name" value={formData.middleName || ''} onChange={(e) => setFormData({ ...formData, middleName: e.target.value })} />
                </Grid>
                <Grid size={{ xs: 12, sm: 4 }}>
                  <TextField fullWidth label="Last Name" value={formData.lastName || ''} onChange={(e) => setFormData({ ...formData, lastName: e.target.value })} required />
                </Grid>
                <Grid size={{ xs: 12 }}>
                  <TextField fullWidth label="Email Address" type="email" value={formData.email || ''} onChange={(e) => setFormData({ ...formData, email: e.target.value })} required />
                </Grid>
                {!selectedRecord && (
                  <Grid size={{ xs: 12 }}>
                    <TextField
                      fullWidth
                      label="Password"
                      type="password"
                      value={createPassword}
                      onChange={(e) => setCreatePassword(e.target.value)}
                      placeholder="Leave blank to use default (Welcome@123)"
                      helperText="Default password is Welcome@123 if left empty."
                    />
                  </Grid>
                )}
              </Grid>
            </Grid>

            {/* Divider - Roles & Access */}
            <Grid size={{ xs: 12 }}>
              <Divider />
              <Typography variant="overline" sx={{ fontWeight: 700, color: 'text.secondary', letterSpacing: 1, mt: 1.5, display: 'block' }}>Roles & Access</Typography>
            </Grid>

            <Grid size={{ xs: 12, sm: 6, md: 4 }}>
              <TextField select fullWidth label="Role" value={formData.roleId || ''} onChange={(e) => setFormData({ ...formData, roleId: Number(e.target.value) })} required>
                {roles?.map(r => <MenuItem key={r.setupId} value={r.setupId}>{r.setupName}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 6, md: 4 }}>
              <TextField select fullWidth label="Team" value={formData.teamId || ''} onChange={(e) => setFormData({ ...formData, teamId: Number(e.target.value) })}>
                <MenuItem value="">None</MenuItem>
                {teams?.map((t: any) => <MenuItem key={t.id} value={t.id}>{t.teamName}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 6, md: 4 }}>
              <TextField select fullWidth label="User Group" value={formData.userGroupId || ''} onChange={(e) => setFormData({ ...formData, userGroupId: Number(e.target.value) })}>
                <MenuItem value="">None</MenuItem>
                {userGroups?.map((g: any) => <MenuItem key={g.id} value={g.id}>{g.userGroupsName}</MenuItem>)}
              </TextField>
            </Grid>
            <Grid size={{ xs: 12, sm: 6, md: 4 }}>
              <TextField select fullWidth label="Manager" value={formData.managerId || ''} onChange={(e) => setFormData({ ...formData, managerId: Number(e.target.value) })}>
                <MenuItem value="">None</MenuItem>
                {allUsers?.items?.filter(u => u.id !== selectedRecord?.id).map(u => (
                  <MenuItem key={u.id} value={u.id}>{u.firstName} {u.lastName}</MenuItem>
                ))}
              </TextField>
            </Grid>

            {/* Divider - Location & Settings */}
            <Grid size={{ xs: 12 }}>
              <Divider />
              <Typography variant="overline" sx={{ fontWeight: 700, color: 'text.secondary', letterSpacing: 1, mt: 1.5, display: 'block' }}>Location & Settings</Typography>
            </Grid>

            <Grid size={{ xs: 12, sm: 6, md: 4 }}>
              <TextField fullWidth label="Timezone" value={formData.timezone || ''} onChange={(e) => setFormData({ ...formData, timezone: e.target.value })} placeholder="e.g. UTC, Asia/Karachi" />
            </Grid>
            <Grid size={{ xs: 12, sm: 6, md: 4 }}>
              <TextField fullWidth label="Region" value={formData.region || ''} onChange={(e) => setFormData({ ...formData, region: e.target.value })} placeholder="e.g. North America, Pakistan" />
            </Grid>
            <Grid size={{ xs: 12, sm: 6, md: 4 }} sx={{ display: 'flex', alignItems: 'center' }}>
              <FormControlLabel
                control={<Switch checked={!!formData.isActive} onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })} color="primary" />}
                label="Active Account"
              />
            </Grid>

          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit">Cancel</Button>
          <Button variant="contained" onClick={handleSave} disabled={saveMutation.isPending} sx={{ px: 4 }}>
            {saveMutation.isPending ? <CircularProgress size={24} /> : (selectedRecord ? 'Update User' : 'Create User')}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Password Modal */}
      <Dialog open={isPasswordModalOpen} onClose={() => setIsPasswordModalOpen(false)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>Change Password</DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>Changing password for <strong>{selectedRecord?.firstName} {selectedRecord?.lastName}</strong></Typography>
          <TextField fullWidth type="password" label="New Password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} />
        </DialogContent>
        <DialogActions sx={{ p: 3 }}>
          <Button onClick={() => setIsPasswordModalOpen(false)}>Cancel</Button>
          <Button variant="contained" color="secondary" onClick={() => passwordMutation.mutate({ newPassword })} disabled={passwordMutation.isPending || !newPassword}>
            {passwordMutation.isPending ? <CircularProgress size={24} /> : 'Update Password'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default UsersPage;
