import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Typography,
  Paper,
  CircularProgress,
  MenuItem,
  Checkbox,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Button,
} from '@mui/material';

import rolePermissionService from '../../../api/services/rolePermissionService';
import userService from '../../../api/services/userService';
import moduleService from '../../../api/services/moduleService';
import SearchField from '../../../components/common/SearchField';
import { useSnackbar } from 'notistack';

const RolesPermissionsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  const [selectedRoleId, setSelectedRoleId] = useState<number | ''>('');
  const [search, setSearch] = useState('');

  // Queries
  // Queries - ignoring BUID for roles as they are global
  const { data: roles } = useQuery({
    queryKey: ['roles-global'],
    queryFn: () => userService.getRoles(),
  });

  const { data: modules } = useQuery({
    queryKey: ['modules'],
    queryFn: () => moduleService.getAll({ pageSize: 100 }),
  });

  const { data: permissions, isLoading: loadingPermissions } = useQuery({
    queryKey: ['permissions-global', selectedRoleId],
    queryFn: () => rolePermissionService.getAll({
      businessUnitId: 1, // Globally ignoring BUID
      roleId: selectedRoleId === '' ? undefined : selectedRoleId,
      pageSize: 1000,
    }),
    enabled: !!selectedRoleId,
  });

  // Filtered Modules from Database
  const filteredModules = (modules?.items || []).filter(m =>
    m.moduleName.toLowerCase().includes(search.toLowerCase())
  );

  // Mutations
  const updatePermissionMutation = useMutation({
    mutationFn: (permission: any) =>
      permission.id
        ? rolePermissionService.update(permission.id, permission)
        : rolePermissionService.create(permission),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['permissions-global'] });
    },
  });

  const handleToggle = async (module: any, field: 'canCreate' | 'canEdit' | 'canDelete') => {
    const moduleId = module.id;

    const existing = permissions?.items.find(p => p.moduleId === moduleId);
    const payload = existing
      ? { ...existing, [field]: !existing[field] }
      : {
        roleId: selectedRoleId,
        moduleId,
        businessUnitId: 1, // Global
        canCreate: field === 'canCreate',
        canEdit: field === 'canEdit',
        canDelete: field === 'canDelete',
      };

    updatePermissionMutation.mutate(payload);
  };

  const handleBulkToggleColumn = async (field: 'canCreate' | 'canEdit' | 'canDelete', checked: boolean) => {
    if (!selectedRoleId) return;

    // This is a simplified version - in a real app, you might want a bulk API endpoint
    enqueueSnackbar(`Applying permissions to ${filteredModules.length} modules...`, { variant: 'info' });

    for (const module of filteredModules) {
      const moduleId = module.id;

      const existing = permissions?.items.find(p => p.moduleId === moduleId);
      if (existing && existing[field] === checked) continue;

      const payload: any = existing
        ? { ...existing, [field]: checked }
        : {
          roleId: selectedRoleId,
          moduleId,
          businessUnitId: 1,
          canCreate: field === 'canCreate' ? checked : false,
          canEdit: field === 'canEdit' ? checked : false,
          canDelete: field === 'canDelete' ? checked : false
        };

      await updatePermissionMutation.mutateAsync(payload);
    }

    queryClient.invalidateQueries({ queryKey: ['permissions-global'] });
    queryClient.invalidateQueries({ queryKey: ['modules'] });
    enqueueSnackbar('Column permissions updated', { variant: 'success' });
  };

  const handleBulkToggleRow = async (module: any, checked: boolean, skipInvalidate = false) => {
    if (!selectedRoleId) return;

    const moduleId = module.id;

    const payload: any = {
      roleId: selectedRoleId,
      moduleId,
      businessUnitId: 1,
      canCreate: checked,
      canEdit: checked,
      canDelete: checked
    };

    const existing = permissions?.items.find(p => p.moduleId === moduleId);
    if (existing) {
      if (existing.canCreate === checked && existing.canEdit === checked && existing.canDelete === checked) return;
      payload.id = existing.id;
    }

    if (skipInvalidate) {
      await updatePermissionMutation.mutateAsync(payload);
    } else {
      updatePermissionMutation.mutate(payload);
    }
  };

  const handleAllRowsToggle = async (checked: boolean) => {
    enqueueSnackbar(`Updating ${filteredModules.length} modules...`, { variant: 'info' });
    for (const m of filteredModules) {
      await handleBulkToggleRow(m, checked, true);
    }
    queryClient.invalidateQueries({ queryKey: ['permissions-global'] });
    queryClient.invalidateQueries({ queryKey: ['modules'] });
    enqueueSnackbar('All modules updated', { variant: 'success' });
  };

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800 }}>Roles & Permissions</Typography>
          <Typography variant="body2" color="text.secondary">Configure module-level access for system roles</Typography>
        </Box>
      </Box>

      <Paper sx={{ p: 2, mb: 2, display: 'flex', gap: 3, alignItems: 'center', borderRadius: 2, justifyContent: 'space-between' }}>
        <Box sx={{ display: 'flex', gap: 3, alignItems: 'center' }}>
          <TextField
            select
            label="Select Role"
            value={selectedRoleId}
            onChange={(e) => setSelectedRoleId(Number(e.target.value))}
            sx={{ minWidth: 250 }}
            size="small"
          >
            {roles?.map(r => <MenuItem key={r.setupId} value={r.setupId}>{r.setupName}</MenuItem>)}
          </TextField>
          {selectedRoleId && (
            <Box sx={{ display: 'flex', gap: 1 }}>
              <Button
                variant="outlined"
                size="small"
                color="success"
                onClick={() => handleAllRowsToggle(true)}
                disabled={loadingPermissions}
              >
                Grant Full Access
              </Button>
              <Button
                variant="outlined"
                size="small"
                color="error"
                onClick={() => handleAllRowsToggle(false)}
                disabled={loadingPermissions}
              >
                Revoke All Access
              </Button>
            </Box>
          )}
        </Box>
        <SearchField value={search} onChange={setSearch} placeholder="Search modules..." width={300} />
      </Paper>

      <TableContainer component={Paper} sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
        <Table size="small">
          <TableHead>
            <TableRow sx={{ backgroundColor: 'action.hover' }}>
              <TableCell sx={{ fontWeight: 700, py: 1.5 }}>
                <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                  Module Name
                  {selectedRoleId && (
                    <Checkbox
                      size="small"
                      title="Toggle All Rows"
                      onChange={(e) => handleAllRowsToggle(e.target.checked)}
                    />
                  )}
                </Box>
              </TableCell>
              <TableCell align="center" sx={{ fontWeight: 700 }}>
                <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
                  Can Create
                  {selectedRoleId && (
                    <Checkbox
                      size="small"
                      onChange={(e) => handleBulkToggleColumn('canCreate', e.target.checked)}
                    />
                  )}
                </Box>
              </TableCell>
              <TableCell align="center" sx={{ fontWeight: 700 }}>
                <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
                  Can Edit
                  {selectedRoleId && (
                    <Checkbox
                      size="small"
                      onChange={(e) => handleBulkToggleColumn('canEdit', e.target.checked)}
                    />
                  )}
                </Box>
              </TableCell>
              <TableCell align="center" sx={{ fontWeight: 700 }}>
                <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center' }}>
                  Can Delete
                  {selectedRoleId && (
                    <Checkbox
                      size="small"
                      onChange={(e) => handleBulkToggleColumn('canDelete', e.target.checked)}
                    />
                  )}
                </Box>
              </TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {!selectedRoleId ? (
              <TableRow>
                <TableCell colSpan={4} align="center" sx={{ py: 8, color: 'text.secondary' }}>
                  Please select a role above to view and configure permissions
                </TableCell>
              </TableRow>
            ) : loadingPermissions ? (
              <TableRow>
                <TableCell colSpan={4} align="center" sx={{ py: 8 }}><CircularProgress size={24} /></TableCell>
              </TableRow>
            ) : filteredModules.map(m => {
              const p = permissions?.items.find(perm => perm.moduleId === m.id);
              const isAllRowChecked = p?.canCreate && p?.canEdit && p?.canDelete;

              const canCreate = p?.canCreate || false;
              const canEdit = p?.canEdit || false;
              const canDelete = p?.canDelete || false;

              return (
                <TableRow key={m.id} hover>
                  <TableCell sx={{ fontWeight: 600 }}>
                    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                      <Checkbox
                        size="small"
                        checked={!!isAllRowChecked}
                        onChange={(e) => handleBulkToggleRow(m, e.target.checked)}
                      />
                      <Box>
                        {m.moduleName}
                      </Box>
                    </Box>
                  </TableCell>
                  <TableCell align="center">
                    <Checkbox
                      size="small"
                      checked={canCreate}
                      onChange={() => handleToggle(m, 'canCreate')}
                    />
                  </TableCell>
                  <TableCell align="center">
                    <Checkbox
                      size="small"
                      checked={canEdit}
                      onChange={() => handleToggle(m, 'canEdit')}
                    />
                  </TableCell>
                  <TableCell align="center">
                    <Checkbox
                      size="small"
                      checked={canDelete}
                      onChange={() => handleToggle(m, 'canDelete')}
                    />
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </TableContainer>
    </Box>
  );
};

export default RolesPermissionsPage;
