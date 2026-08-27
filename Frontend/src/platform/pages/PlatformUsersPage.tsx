import { useState } from 'react';
import Stack from '../components/Flex';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  IconButton,
  MenuItem,
  Paper,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import { DataGrid, type GridColDef } from '@mui/x-data-grid';
import {
  Add as AddIcon,
  BlockOutlined as DeactivateIcon,
  CheckCircleOutlined as ReactivateIcon,
  LockResetOutlined as PasswordIcon,
  ManageAccountsOutlined as RoleIcon,
  PhonelinkLockOutlined as MfaIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import {
  PLATFORM_OPERATOR_ROLES,
  type PlatformOperator,
  type PlatformOperatorRole,
} from '../types';
import PageHeader from '../components/PageHeader';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../auth/permissions';
import { SoftChip } from '../components/StatusChip';
import { ErrorState } from '../components/States';
import { fmtDateTime, fmtRelative } from '../components/format';

type DialogState =
  | { kind: 'create' }
  | { kind: 'role'; user: PlatformOperator }
  | { kind: 'password'; user: PlatformOperator }
  | { kind: 'mfa'; user: PlatformOperator }
  | { kind: 'deactivate'; user: PlatformOperator }
  | null;

const emptyCreate = {
  email: '',
  password: '',
  role: 'ReadOnlyOps' as PlatformOperatorRole,
  displayName: '',
};

const ROLE_TONE: Record<string, 'success' | 'info' | 'warning' | 'neutral'> = {
  Owner: 'success',
  SupportAdmin: 'info',
  BillingAdmin: 'warning',
  ReadOnlyOps: 'neutral',
};

export default function PlatformUsersPage() {
  const queryClient = useQueryClient();
  const permissions = usePlatformPermissions();
  const { enqueueSnackbar } = useSnackbar();

  const [dialog, setDialog] = useState<DialogState>(null);
  const [createForm, setCreateForm] = useState(emptyCreate);
  const [role, setRole] = useState<PlatformOperatorRole>('ReadOnlyOps');
  const [newPassword, setNewPassword] = useState('');

  const { data: users, isLoading, isError, refetch } = useQuery({
    queryKey: platformKeys.platformUsers(),
    queryFn: () => platformApi.listPlatformUsers(),
    enabled: permissions.isOwner,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: platformKeys.platformUsers() });

  const createMutation = useMutation({
    mutationFn: () =>
      platformApi.createPlatformUser({
        email: createForm.email.trim(),
        password: createForm.password,
        role: createForm.role,
        displayName: createForm.displayName.trim() || undefined,
      }),
    onSuccess: (user) => {
      enqueueSnackbar(`Platform user ${user.email} created`, { variant: 'success' });
      setDialog(null);
      setCreateForm(emptyCreate);
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Failed to create the platform user'), { variant: 'error' }),
  });

  const roleMutation = useMutation({
    mutationFn: (user: PlatformOperator) => platformApi.changePlatformUserRole(user.id, role),
    onSuccess: (user) => {
      enqueueSnackbar(`${user.email} is now ${user.platformRole}`, { variant: 'success' });
      setDialog(null);
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Role change failed'), { variant: 'error' }),
  });

  const activationMutation = useMutation({
    mutationFn: (user: PlatformOperator) =>
      user.isActive ? platformApi.deactivatePlatformUser(user.id) : platformApi.reactivatePlatformUser(user.id),
    onSuccess: (user) => {
      enqueueSnackbar(`${user.email} ${user.isActive ? 'reactivated' : 'deactivated'}`, { variant: 'success' });
      setDialog(null);
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Activation change failed'), { variant: 'error' }),
  });

  const passwordMutation = useMutation({
    mutationFn: (user: PlatformOperator) => platformApi.resetPlatformUserPassword(user.id, newPassword),
    onSuccess: (user) => {
      enqueueSnackbar(`Password reset for ${user.email}`, { variant: 'success' });
      setDialog(null);
      setNewPassword('');
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Password reset failed'), { variant: 'error' }),
  });

  const mfaResetMutation = useMutation({
    mutationFn: (user: PlatformOperator) => platformApi.resetPlatformUserMfa(user.id),
    onSuccess: () => {
      // The endpoint returns no body, so the console has no evidence of what was revoked. It
      // used to assert "All target sessions were revoked" as fact — a claim about a security
      // outcome that nothing on the wire supported.
      enqueueSnackbar('MFA reset. That operator must enroll a new authenticator before signing in again.', { variant: 'success' });
      setDialog(null);
      // Was the only mutation on this page that did not invalidate, so the grid kept showing
      // the pre-reset row and the reset looked like it had not happened.
      invalidate();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'MFA reset failed'), { variant: 'error' }),
  });

  const columns: GridColDef<PlatformOperator>[] = [
    {
      field: 'email',
      headerName: 'Operator',
      flex: 1.4,
      minWidth: 220,
      renderCell: (p) => (
        <Box sx={{ lineHeight: 1.2 }}>
          <Typography variant="body2" sx={{ fontWeight: 700 }}>{p.row.displayName ?? p.row.email}</Typography>
          <Typography variant="caption" color="text.secondary">{p.row.email}</Typography>
        </Box>
      ),
    },
    {
      field: 'platformRole',
      headerName: 'Role',
      width: 150,
      renderCell: (p) => (
        <SoftChip label={p.row.platformRole} tone={ROLE_TONE[p.row.platformRole] ?? 'neutral'} dot={false} />
      ),
    },
    {
      field: 'isActive',
      headerName: 'Status',
      width: 120,
      renderCell: (p) => (
        <SoftChip label={p.row.isActive ? 'active' : 'inactive'} tone={p.row.isActive ? 'success' : 'error'} />
      ),
    },
    {
      field: 'lastLogin',
      headerName: 'Last login',
      width: 130,
      renderCell: (p) => (
        <Typography variant="caption" color="text.secondary">{fmtRelative(p.row.lastLogin)}</Typography>
      ),
    },
    {
      field: 'createdOn',
      headerName: 'Created',
      width: 170,
      renderCell: (p) => (
        <Typography variant="caption" color="text.secondary">{fmtDateTime(p.row.createdOn)}</Typography>
      ),
    },
    {
      field: 'actions',
      headerName: 'Actions',
      width: 200,
      sortable: false,
      filterable: false,
      renderCell: (p) => (
        <Stack direction="row" spacing={0.5}>
          <Tooltip title="Change role">
            <IconButton
              size="small"
              color="primary"
              onClick={() => {
                const parsed = PLATFORM_OPERATOR_ROLES.find((r) => r === p.row.platformRole);
                setRole(parsed ?? 'ReadOnlyOps');
                setDialog({ kind: 'role', user: p.row });
              }}
            >
              <RoleIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Reset password">
            <IconButton
              size="small"
              onClick={() => {
                setNewPassword('');
                setDialog({ kind: 'password', user: p.row });
              }}
            >
              <PasswordIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title="Reset lost MFA">
            <IconButton size="small" onClick={() => setDialog({ kind: 'mfa', user: p.row })}>
              <MfaIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Tooltip title={p.row.isActive ? 'Deactivate' : 'Reactivate'}>
            <IconButton
              size="small"
              color={p.row.isActive ? 'error' : 'success'}
              onClick={() =>
                p.row.isActive
                  ? setDialog({ kind: 'deactivate', user: p.row })
                  : activationMutation.mutate(p.row)}
            >
              {p.row.isActive ? <DeactivateIcon fontSize="small" /> : <ReactivateIcon fontSize="small" />}
            </IconButton>
          </Tooltip>
        </Stack>
      ),
    },
  ];

  const createValid =
    /.+@.+\..+/.test(createForm.email.trim()) && createForm.password.length >= 12;

  if (!permissions.isOwner) {
    // Every endpoint behind this screen — list included — carries Platform.Owner. The
    // operator registry is where authority itself is granted, so seeing it is a privilege.
    return (
      <Box>
        <PageHeader title="Platform Users" subtitle="Control-plane operator accounts." />
        <Alert severity="info" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Owner only</AlertTitle>
          {REQUIRED_ROLE_COPY.owner} This registry is where platform authority is granted and revoked, so it is
          not readable by the tiers it governs.
        </Alert>
      </Box>
    );
  }

  return (
    <Box>
      <PageHeader
        title="Platform Users"
        subtitle="Control-plane operator accounts. Owner-only: the platform always retains at least one active Owner."
        actions={
          <Button variant="contained" startIcon={<AddIcon />} onClick={() => { setCreateForm(emptyCreate); setDialog({ kind: 'create' }); }} sx={{ fontWeight: 700 }}>
            New Platform User
          </Button>
        }
      />

      <Paper sx={{ borderRadius: 3, overflow: 'hidden', height: 'calc(100vh - 260px)', minHeight: 420 }}>
        {isError ? (
          <ErrorState message="The platform user registry did not respond." onRetry={() => refetch()} />
        ) : (
          <DataGrid
            rows={users ?? []}
            columns={columns}
            loading={isLoading}
            getRowId={(r) => r.id}
            disableRowSelectionOnClick
            rowHeight={60}
            initialState={{ pagination: { paginationModel: { pageSize: 25, page: 0 } } }}
            pageSizeOptions={[25, 50]}
            sx={{
              border: 'none',
              '& .MuiDataGrid-columnHeaders': { bgcolor: 'action.hover' },
              '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
            }}
          />
        )}
      </Paper>

      {/* Create dialog */}
      <Dialog open={dialog?.kind === 'create'} onClose={() => setDialog(null)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Create platform user</DialogTitle>
        <DialogContent dividers>
          <Stack spacing={2.5} sx={{ mt: 0.5 }}>
            <TextField
              label="Email"
              type="email"
              value={createForm.email}
              onChange={(e) => setCreateForm({ ...createForm, email: e.target.value })}
              fullWidth
              required
            />
            <TextField
              label="Display name"
              value={createForm.displayName}
              onChange={(e) => setCreateForm({ ...createForm, displayName: e.target.value })}
              fullWidth
            />
            <TextField
              label="Password"
              type="password"
              value={createForm.password}
              onChange={(e) => setCreateForm({ ...createForm, password: e.target.value })}
              helperText="At least 12 characters."
              fullWidth
              required
            />
            <TextField
              label="Platform role"
              select
              value={createForm.role}
              onChange={(e) => setCreateForm({ ...createForm, role: e.target.value as PlatformOperatorRole })}
              fullWidth
            >
              {PLATFORM_OPERATOR_ROLES.map((r) => (
                <MenuItem key={r} value={r}>{r}</MenuItem>
              ))}
            </TextField>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setDialog(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            onClick={() => createMutation.mutate()}
            disabled={!createValid || createMutation.isPending}
            sx={{ fontWeight: 700, px: 3 }}
          >
            {createMutation.isPending ? 'Creating…' : 'Create'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Change role dialog */}
      <Dialog open={dialog?.kind === 'role'} onClose={() => setDialog(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Change role</DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ mb: 2 }}>
            {dialog?.kind === 'role' && (
              <>Change the platform role for <strong>{dialog.user.email}</strong>. The last active Owner cannot be demoted.</>
            )}
          </DialogContentText>
          <TextField
            select
            fullWidth
            label="Platform role"
            value={role}
            onChange={(e) => setRole(e.target.value as PlatformOperatorRole)}
          >
            {PLATFORM_OPERATOR_ROLES.map((r) => (
              <MenuItem key={r} value={r}>{r}</MenuItem>
            ))}
          </TextField>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setDialog(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            onClick={() => dialog?.kind === 'role' && roleMutation.mutate(dialog.user)}
            disabled={roleMutation.isPending || (dialog?.kind === 'role' && dialog.user.platformRole === role)}
            sx={{ fontWeight: 700 }}
          >
            {roleMutation.isPending ? 'Saving…' : 'Change role'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Reset password dialog */}
      <Dialog open={dialog?.kind === 'password'} onClose={() => setDialog(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Reset password</DialogTitle>
        <DialogContent>
          <DialogContentText sx={{ mb: 2 }}>
            {dialog?.kind === 'password' && (
              <>Set a new password for <strong>{dialog.user.email}</strong>. The reset is written to the audit trail (the password itself is not).</>
            )}
          </DialogContentText>
          <TextField
            fullWidth
            required
            type="password"
            label="New password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            helperText="At least 12 characters."
          />
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setDialog(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            onClick={() => dialog?.kind === 'password' && passwordMutation.mutate(dialog.user)}
            disabled={passwordMutation.isPending || newPassword.length < 12}
            sx={{ fontWeight: 700 }}
          >
            {passwordMutation.isPending ? 'Resetting…' : 'Reset password'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Break-glass MFA recovery dialog */}
      <Dialog open={dialog?.kind === 'mfa'} onClose={() => setDialog(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Reset lost MFA</DialogTitle>
        <DialogContent>
          <DialogContentText>
            {dialog?.kind === 'mfa' && (
              <>Reset MFA for <strong>{dialog.user.email}</strong>? All of their platform sessions,
                recovery codes and outstanding challenges will be revoked. They must enroll MFA again.
                A different Owner must perform this recovery.</>
            )}
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setDialog(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            color="error"
            onClick={() => dialog?.kind === 'mfa' && mfaResetMutation.mutate(dialog.user)}
            disabled={mfaResetMutation.isPending}
            sx={{ fontWeight: 700 }}
          >
            {mfaResetMutation.isPending ? 'Resetting…' : 'Reset MFA and revoke sessions'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* Deactivate confirm dialog */}
      <Dialog open={dialog?.kind === 'deactivate'} onClose={() => setDialog(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Deactivate platform user</DialogTitle>
        <DialogContent>
          <DialogContentText>
            {dialog?.kind === 'deactivate' && (
              <>Deactivate <strong>{dialog.user.email}</strong>? They can no longer sign in to the platform console. You cannot deactivate yourself, and the last active Owner is protected.</>
            )}
          </DialogContentText>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setDialog(null)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            color="error"
            onClick={() => dialog?.kind === 'deactivate' && activationMutation.mutate(dialog.user)}
            disabled={activationMutation.isPending}
            sx={{ fontWeight: 700 }}
          >
            {activationMutation.isPending ? 'Working…' : 'Deactivate'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
