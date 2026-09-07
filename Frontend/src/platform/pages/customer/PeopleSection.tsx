import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Card, CardContent, Chip, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, MenuItem, Table, TableBody, TableCell, TableRow, TextField, Tooltip,
  Typography,
} from '@mui/material';
import { PersonAddOutlined as AddIcon } from '@mui/icons-material';
import Stack from '../../components/Flex';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type { TenantUser } from '../../types';

/**
 * PEOPLE — who at the customer can sign in, on the customer page rather than a tab of its own.
 *
 * The Users tab this replaces put a destructive control in every row: "Deactivate", error-red,
 * last in a flex row of small buttons, repeated per person. The only thing distinguishing one
 * person's Deactivate from another's was vertical position in a dense table. Here the roster is a
 * read surface and the two acts that matter — invite somebody, take somebody out of service —
 * are deliberate, named, and ask why.
 *
 * It also answers the question the old tab could not: deactivating also withdraws an outstanding
 * activation link, which the old screen mentioned in prose while showing nothing about which rows
 * had one. The invite state is now on the row.
 */
export default function PeopleSection({
  tenantId, canAdminister, isOwner,
}: {
  tenantId: string;
  canAdminister: boolean;
  isOwner: boolean;
}) {
  const queryClient = useQueryClient();
  const [adding, setAdding] = useState(false);
  const [deactivating, setDeactivating] = useState<TenantUser | null>(null);
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [draft, setDraft] = useState({ firstName: '', lastName: '', email: '', roleId: '' });

  const users = useQuery({
    queryKey: platformKeys.tenantUsers(tenantId),
    queryFn: () => platformApi.listTenantUsers(tenantId),
  });
  const roles = useQuery({
    queryKey: platformKeys.tenantRoles(tenantId),
    queryFn: () => platformApi.listTenantRoles(tenantId),
    enabled: adding,
  });

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantUsers(tenantId) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantConfiguration(tenantId) });
  };

  const create = useMutation({
    mutationFn: () => platformApi.createTenantUser(tenantId, {
      email: draft.email.trim(),
      firstName: draft.firstName.trim(),
      lastName: draft.lastName.trim(),
      roleId: draft.roleId,
      // Always an invitation. The console never sets a password on somebody else's behalf —
      // a credential an operator knows is a credential the customer does not solely control.
      activation: 'invite',
      password: null,
      reason: reason.trim(),
    }),
    onSuccess: () => {
      setAdding(false); setReason(''); setError(null);
      setDraft({ firstName: '', lastName: '', email: '', roleId: '' });
      refresh();
    },
    onError: (e) => setError(platformErrorMessage(e, 'The person could not be added.')),
  });

  const deactivate = useMutation({
    mutationFn: (user: TenantUser) =>
      platformApi.deactivateTenantUser(tenantId, user.id, reason.trim()),
    onSuccess: () => { setDeactivating(null); setReason(''); setError(null); refresh(); },
    onError: (e) => setError(platformErrorMessage(e, 'That person was not deactivated.')),
  });

  const reactivate = useMutation({
    mutationFn: (user: TenantUser) =>
      platformApi.reactivateTenantUser(tenantId, user.id, 'Reactivated from the customer screen.'),
    onSuccess: refresh,
    onError: (e) => setError(platformErrorMessage(e, 'That person was not reactivated.')),
  });

  const rows = users.data ?? [];
  const awaiting = rows.filter((u) => u.invitation?.status === 'Pending').length;
  const active = rows.filter((u) => u.isActive && u.invitation?.status !== 'Pending').length;

  return (
    <Card variant="outlined">
      <CardContent>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <Typography variant="h6" sx={{ fontWeight: 700, flex: 1 }}>
            People
            <Typography component="span" variant="body2" color="text.secondary" sx={{ ml: 1 }}>
              {awaiting > 0
                ? `${active} signed in, ${awaiting} still to accept`
                : `${active} of ${rows.length} active`}
            </Typography>
          </Typography>
          {canAdminister && (
            <Button size="small" startIcon={<AddIcon />} onClick={() => { setAdding(true); setError(null); }}>
              Invite someone
            </Button>
          )}
        </Stack>

        <Divider sx={{ my: 1.5 }} />

        {error && <Alert severity="error" sx={{ mb: 1.5 }} onClose={() => setError(null)}>{error}</Alert>}

        {rows.length === 0 ? (
          <Typography variant="body2" color="text.secondary">
            Nobody has been added yet. The founding administrator is created with the workspace.
          </Typography>
        ) : (
          <Table size="small">
            <TableBody>
              {rows.map((user) => (
                <TableRow key={user.id} sx={{ '&:last-child td': { border: 0 } }}>
                  <TableCell sx={{ pl: 0 }}>
                    <Typography variant="body2" sx={{ fontWeight: 600 }}>
                      {user.firstName} {user.lastName}
                    </Typography>
                    <Typography variant="caption" color="text.secondary">{user.email}</Typography>
                  </TableCell>
                  <TableCell>
                    <Typography variant="body2">{user.roleName ?? '—'}</Typography>
                  </TableCell>
                  <TableCell>
                    {user.invitation?.status === 'Pending' ? (
                      // The old tab said in prose that deactivating withdraws an outstanding
                      // link, while showing nothing about who had one. Now the row says so.
                      <Tooltip describeChild title="They have not signed in yet. Taking them out of service withdraws this link.">
                        <Chip size="small" label="Awaiting first sign-in" color="warning" variant="outlined" />
                      </Tooltip>
                    ) : !user.isActive ? (
                      <Chip size="small" label="Taken out of service" variant="outlined" />
                    ) : (
                      <Chip size="small" label="Active" color="success" variant="outlined" />
                    )}
                  </TableCell>
                  <TableCell>
                    <Typography variant="caption" color="text.secondary">
                      {user.lastLogin ? `Last in ${new Date(user.lastLogin).toLocaleDateString()}` : 'Never signed in'}
                    </Typography>
                  </TableCell>
                  <TableCell align="right" sx={{ pr: 0 }}>
                    {canAdminister && (user.isActive || user.invitation?.status === 'Pending' ? (
                      <Button
                        size="small" color="inherit"
                        onClick={() => { setDeactivating(user); setReason(''); setError(null); }}
                      >
                        Take out of service
                      </Button>
                    ) : (
                      <Button size="small" onClick={() => reactivate.mutate(user)}>Restore</Button>
                    ))}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </CardContent>

      <Dialog open={adding} onClose={() => !create.isPending && setAdding(false)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>Invite someone at this customer</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ mt: 0.5 }}>
            <Stack direction="row" spacing={2}>
              <TextField label="First name" fullWidth value={draft.firstName}
                onChange={(e) => setDraft({ ...draft, firstName: e.target.value })} />
              <TextField label="Last name" fullWidth value={draft.lastName}
                onChange={(e) => setDraft({ ...draft, lastName: e.target.value })} />
            </Stack>
            <TextField label="Work email" type="email" value={draft.email}
              onChange={(e) => setDraft({ ...draft, email: e.target.value })} />
            <TextField
              select label="What they can do" value={draft.roleId}
              onChange={(e) => setDraft({ ...draft, roleId: e.target.value })}
              helperText={roles.isError ? 'The roles could not be read.' : ' '}
            >
              {(roles.data ?? []).map((role) => (
                <MenuItem key={role.id} value={role.id} disabled={!role.grantable}>
                  {role.name}{!role.grantable ? ' — you cannot grant this' : ''}
                </MenuItem>
              ))}
            </TextField>
            <TextField label="Why" multiline minRows={2} value={reason}
              onChange={(e) => setReason(e.target.value)}
              helperText="Recorded on the audit trail against your operator account." />
            <Alert severity="info">
              They get an activation link by email. No password is set on their behalf.
            </Alert>
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={() => setAdding(false)} disabled={create.isPending}>Cancel</Button>
          <Button
            variant="contained"
            disabled={
              create.isPending || draft.roleId === '' || reason.trim().length < 5
              || draft.firstName.trim() === '' || draft.lastName.trim() === ''
              || !/^[^@\s]+@[^@\s.]+\.[^@\s]+$/.test(draft.email.trim())
            }
            onClick={() => create.mutate()}
          >
            {create.isPending ? 'Inviting…' : 'Send the invitation'}
          </Button>
        </DialogActions>
      </Dialog>

      <Dialog open={deactivating !== null} onClose={() => !deactivate.isPending && setDeactivating(null)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 800 }}>
          Take {deactivating?.firstName} {deactivating?.lastName} out of service?
        </DialogTitle>
        <DialogContent>
          <Alert severity="warning" sx={{ mb: 2 }}>
            They stop being able to sign in immediately
            {deactivating?.invitation?.status === 'Pending'
              ? ', and their outstanding activation link is withdrawn — restoring them later does not bring it back.'
              : '. Their account is kept, and you can restore them.'}
          </Alert>
          <TextField
            label="Why" fullWidth multiline minRows={2} value={reason}
            onChange={(e) => setReason(e.target.value)}
            helperText="At least five characters, recorded on the audit trail."
          />
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={() => setDeactivating(null)} disabled={deactivate.isPending}>Cancel</Button>
          <Button
            color="error" variant="contained"
            disabled={deactivate.isPending || reason.trim().length < 5}
            onClick={() => deactivating && deactivate.mutate(deactivating)}
          >
            {deactivate.isPending ? 'Working…' : 'Take out of service'}
          </Button>
        </DialogActions>
      </Dialog>

      {!isOwner && (
        <Box sx={{ px: 2, pb: 2 }}>
          <Typography variant="caption" color="text.disabled">
            Changing somebody's role is an Owner action and is done from Advanced.
          </Typography>
        </Box>
      )}
    </Card>
  );
}
