import { useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import {
  ContentCopy as CopyIcon,
  PersonAddAlt1 as AddIcon,
  Send as SendIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import ReasonDialog from '../../components/ReasonDialog';
import { ErrorState, LoadingState } from '../../components/States';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../../auth/permissions';
import type { CreateTenantUserInput, Tenant, TenantRole, TenantUser } from '../../types';

/**
 * The customer's own people, administered from the console.
 *
 * <p>Its own tab rather than another section on Profile &amp; access, for two reasons. The
 * existing "Founding administrator access" panel is about ONE link and its delivery receipts;
 * this is a roster with per-row authority changes, and folding a table with four dialogs into a
 * screen that already carries the company profile and the residency claim would bury both. And
 * the tab key lives in the URL, so "the customer says Layla cannot sign in" resolves to a link
 * an operator can paste into a ticket.</p>
 *
 * <p>The panel says out loud that this is the secondary path. The tenant's own Super
 * Administrator staffing their workspace is the primary one, and an operator who does not know
 * that will use this screen for work that belongs to the customer.</p>
 */

const ROLE_RANK_OWNER = 30;

const initialDraft = (): CreateTenantUserInput => ({
  email: '',
  firstName: '',
  lastName: '',
  roleId: '',
  activation: 'invite',
  password: '',
  reason: '',
});

type PendingAction =
  | { kind: 'deactivate' | 'reactivate' | 'resend' | 'role'; user: TenantUser };

export default function UsersTab({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();
  const [addOpen, setAddOpen] = useState(false);
  const [draft, setDraft] = useState<CreateTenantUserInput>(initialDraft);
  const [pending, setPending] = useState<PendingAction | null>(null);
  const [nextRoleId, setNextRoleId] = useState('');
  /** The one-time activation link, shown only when the provider did not transmit it. */
  const [recoveryLink, setRecoveryLink] = useState<string | null>(null);

  const users = useQuery({
    queryKey: platformKeys.tenantUsers(tenant.id),
    queryFn: () => platformApi.listTenantUsers(tenant.id),
  });

  const roles = useQuery({
    queryKey: platformKeys.tenantRoles(tenant.id),
    queryFn: () => platformApi.listTenantRoles(tenant.id),
  });

  const roleById = useMemo(
    () => new Map((roles.data ?? []).map((role) => [role.id, role])),
    [roles.data],
  );

  const refreshRoster = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantUsers(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantRoles(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantInvitations(tenant.id) });
  };

  const create = useMutation({
    mutationFn: () => platformApi.createTenantUser(tenant.id, {
      ...draft,
      email: draft.email.trim(),
      firstName: draft.firstName.trim(),
      lastName: draft.lastName.trim(),
      password: draft.activation === 'password' ? draft.password : null,
    }),
    onSuccess: (result) => {
      setAddOpen(false);
      setDraft(initialDraft());
      setRecoveryLink(result.activationUrl);
      refreshRoster();
      enqueueSnackbar(
        result.invitation === null
          ? 'The account was created with the password you set, and the act was audited'
          : result.emailDispatched
            ? 'The account was created and its activation email was accepted by the provider'
            : 'The account was created — email was not transmitted, use the one-time link shown above',
        { variant: result.invitation !== null && !result.emailDispatched ? 'warning' : 'success' },
      );
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The account could not be created'), { variant: 'error' }),
  });

  const changeStatus = useMutation({
    mutationFn: ({ user, kind, reason }: { user: TenantUser; kind: 'deactivate' | 'reactivate'; reason: string }) =>
      kind === 'deactivate'
        ? platformApi.deactivateTenantUser(tenant.id, user.id, reason)
        : platformApi.reactivateTenantUser(tenant.id, user.id, reason),
    onSuccess: (user) => {
      setPending(null);
      refreshRoster();
      enqueueSnackbar(
        user.isActive
          ? `${user.email} is back in service`
          : `${user.email} was taken out of service and any outstanding activation link was withdrawn`,
        { variant: 'success' },
      );
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The change was refused'), { variant: 'error' }),
  });

  const changeRole = useMutation({
    mutationFn: ({ user, reason }: { user: TenantUser; reason: string }) =>
      platformApi.changeTenantUserRole(tenant.id, user.id, { roleId: nextRoleId, reason }),
    onSuccess: (user) => {
      setPending(null);
      setNextRoleId('');
      refreshRoster();
      enqueueSnackbar(`${user.email} now holds ${user.roleName ?? 'a new role'}`, { variant: 'success' });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The role change was refused'), { variant: 'error' }),
  });

  const resend = useMutation({
    mutationFn: ({ user, reason }: { user: TenantUser; reason: string }) =>
      platformApi.resendTenantAdminInvitation(tenant.id, { userId: user.id, reason }),
    onSuccess: (result) => {
      setPending(null);
      setRecoveryLink(result.activationUrl);
      refreshRoster();
      enqueueSnackbar(
        result.emailDispatched
          ? 'A new activation email was accepted by the provider'
          : 'Email was not transmitted — use the one-time link shown above',
        { variant: result.emailDispatched ? 'success' : 'warning' },
      );
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The invitation could not be reissued'), { variant: 'error' }),
  });

  const copyRecoveryLink = async () => {
    if (!recoveryLink) return;
    try {
      await navigator.clipboard.writeText(recoveryLink);
      enqueueSnackbar('Activation link copied', { variant: 'success' });
    } catch {
      enqueueSnackbar('Copy failed — select the link and copy it manually', { variant: 'error' });
    }
  };

  const archived = tenant.status === 'archived';
  const grantableRoles = (roles.data ?? []).filter((role) => role.grantable);
  const draftReady =
    draft.email.trim().length > 3
    && draft.firstName.trim().length > 0
    && draft.lastName.trim().length > 0
    && draft.roleId !== ''
    && draft.reason.trim().length >= 5
    && (draft.activation !== 'password' || (draft.password ?? '').length >= 12);

  const openRoleDialog = (user: TenantUser) => {
    setNextRoleId(user.roleId ?? '');
    setPending({ kind: 'role', user });
  };

  return (
    <Stack spacing={2.5}>
      <PageSection
        title="People in this workspace"
        subtitle="The customer's own accounts, their roles and their activation state."
        actions={
          <Button
            variant="contained"
            startIcon={<AddIcon />}
            disabled={!permissions.canAdministerTenants || archived}
            title={permissions.canAdministerTenants ? undefined : REQUIRED_ROLE_COPY.tenantAdmin}
            onClick={() => setAddOpen(true)}
          >
            Add user
          </Button>
        }
      >
        <Stack spacing={2}>
          <Alert severity="info" sx={{ borderRadius: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>This is the secondary way to staff a tenant</AlertTitle>
            The customer&apos;s own Super Administrator adds their colleagues from inside the
            product, and that remains the intended path — they know who works there and we do not.
            Use this screen when somebody has to be in the workspace before the founding
            administrator has signed in, or when the customer cannot reach that screen at all.
            Every account created here is <strong>invited</strong>: the person receives a
            single-use link and chooses their own password, so nobody on this side ever holds a
            working credential for a customer&apos;s employee.
          </Alert>

          {recoveryLink && (
            <Alert severity="warning" sx={{ borderRadius: 2 }} onClose={() => setRecoveryLink(null)}>
              <AlertTitle sx={{ fontWeight: 800 }}>Email was not transmitted — copy this link now</AlertTitle>
              This link is shown once and only its hash is stored. Deliver it through a secure channel.
              <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 1 }}>
                <Box component="code" sx={{ flex: 1, wordBreak: 'break-all', p: 1, bgcolor: 'action.hover', borderRadius: 1 }}>
                  {recoveryLink}
                </Box>
                <Button startIcon={<CopyIcon />} onClick={copyRecoveryLink}>Copy</Button>
              </Stack>
            </Alert>
          )}

          {users.isLoading ? (
            <LoadingState label="Reading the roster…" minHeight={160} />
          ) : users.isError ? (
            <ErrorState
              message={platformErrorMessage(users.error, 'The roster could not be loaded.')}
              onRetry={() => users.refetch()}
              minHeight={160}
            />
          ) : (users.data ?? []).length === 0 ? (
            <Alert severity="warning">
              This tenant has no accounts at all. That is the shape of a workspace whose
              provisioning stopped before its founding administrator was created — check the
              Provisioning tab before adding anyone here.
            </Alert>
          ) : (
            <Box sx={{ overflowX: 'auto' }}>
              <Table size="small" aria-label="Tenant users">
                <TableHead>
                  <TableRow>
                    <TableCell sx={{ fontWeight: 800 }}>Person</TableCell>
                    <TableCell sx={{ fontWeight: 800 }}>Role</TableCell>
                    <TableCell sx={{ fontWeight: 800 }}>Status</TableCell>
                    <TableCell sx={{ fontWeight: 800 }}>Last sign-in</TableCell>
                    <TableCell sx={{ fontWeight: 800 }} align="right">Actions</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {(users.data ?? []).map((user) => (
                    <TableRow key={user.id} hover>
                      <TableCell>
                        <Typography sx={{ fontWeight: 700 }}>
                          {`${user.firstName} ${user.lastName}`.trim()}
                        </Typography>
                        <Typography variant="body2" color="text.secondary">{user.email}</Typography>
                      </TableCell>
                      <TableCell>
                        <Stack direction="row" spacing={0.75} alignItems="center" sx={{ flexWrap: 'wrap' }}>
                          <Typography variant="body2">{user.roleName ?? 'No role'}</Typography>
                          {/* The rank, not the label, is what decides authority — a role called
                              "Site Supervisor - Admin" tells an operator nothing on its own. */}
                          {user.roleRank !== null && (
                            <Chip
                              size="small"
                              label={user.roleRank >= ROLE_RANK_OWNER ? 'Owner rank' : `Rank ${user.roleRank}`}
                              color={user.roleRank >= ROLE_RANK_OWNER ? 'warning' : 'default'}
                            />
                          )}
                        </Stack>
                      </TableCell>
                      <TableCell>
                        <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap' }}>
                          <Chip
                            size="small"
                            label={user.isActive ? 'Active' : 'Inactive'}
                            color={user.isActive ? 'success' : 'default'}
                          />
                          {user.awaitingActivation && (
                            <Chip size="small" color="warning" label="Awaiting activation" />
                          )}
                          {user.invitation && (
                            <Chip size="small" variant="outlined" label={`Invite: ${user.invitation.status}`} />
                          )}
                        </Stack>
                        {!user.isActive && user.deactivatedAtUtc && (
                          <Typography variant="caption" color="text.secondary">
                            Deactivated {fmtDateTime(user.deactivatedAtUtc)}
                          </Typography>
                        )}
                      </TableCell>
                      <TableCell>
                        <Typography variant="body2" color="text.secondary">
                          {user.lastLogin ? fmtDateTime(user.lastLogin) : 'Never'}
                        </Typography>
                      </TableCell>
                      <TableCell align="right">
                        <Stack direction="row" spacing={1} justifyContent="flex-end" sx={{ flexWrap: 'wrap' }}>
                          {/* Reissue is offered whenever the person has never redeemed a link:
                              reactivating such an account gives it no credential, so a resend is
                              the repair and the button says so by being the one that is there. */}
                          {user.awaitingActivation && (
                            <Button
                              size="small"
                              startIcon={<SendIcon />}
                              disabled={!permissions.canAdministerTenants}
                              onClick={() => setPending({ kind: 'resend', user })}
                            >
                              Resend invite
                            </Button>
                          )}
                          <Button
                            size="small"
                            disabled={!permissions.isOwner || archived}
                            title={permissions.isOwner ? undefined : REQUIRED_ROLE_COPY.owner}
                            onClick={() => openRoleDialog(user)}
                          >
                            Change role
                          </Button>
                          {user.isActive ? (
                            <Button
                              size="small"
                              color="error"
                              disabled={!permissions.canAdministerTenants}
                              onClick={() => setPending({ kind: 'deactivate', user })}
                            >
                              Deactivate
                            </Button>
                          ) : (
                            <Button
                              size="small"
                              disabled={!permissions.canAdministerTenants || archived}
                              onClick={() => setPending({ kind: 'reactivate', user })}
                            >
                              Reactivate
                            </Button>
                          )}
                        </Stack>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </Box>
          )}
        </Stack>
      </PageSection>

      <Dialog open={addOpen} onClose={() => setAddOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Add a user to {tenant.name}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ mt: 1 }}>
            <TextField
              label="Email"
              type="email"
              value={draft.email}
              onChange={(event) => setDraft((d) => ({ ...d, email: event.target.value }))}
              helperText="One address maps to one account on one tenant, everywhere in the product."
              required
              fullWidth
            />
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                label="First name"
                value={draft.firstName}
                onChange={(event) => setDraft((d) => ({ ...d, firstName: event.target.value }))}
                required
                fullWidth
              />
              <TextField
                label="Last name"
                value={draft.lastName}
                onChange={(event) => setDraft((d) => ({ ...d, lastName: event.target.value }))}
                required
                fullWidth
              />
            </Stack>
            <TextField
              select
              label="Role"
              value={draft.roleId}
              onChange={(event) => setDraft((d) => ({ ...d, roleId: event.target.value }))}
              helperText={
                roles.isError
                  ? 'The tenant’s roles could not be read, so none can be assigned.'
                  : grantableRoles.length === 0 && !roles.isLoading
                    ? 'Your platform role cannot grant any of this tenant’s roles.'
                    : 'Read from the tenant’s own Setup_Master. Rank, not name, decides authority.'
              }
              required
              fullWidth
            >
              {(roles.data ?? []).map((role: TenantRole) => (
                <MenuItem key={role.id} value={role.id} disabled={!role.grantable}>
                  <Stack spacing={0.25}>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>
                      {role.name} · {role.rankLabel}
                    </Typography>
                    {!role.grantable && role.notGrantableReason && (
                      <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'normal' }}>
                        {role.notGrantableReason}
                      </Typography>
                    )}
                  </Stack>
                </MenuItem>
              ))}
            </TextField>

            {permissions.isOwner && (
              <TextField
                select
                label="How they get a password"
                value={draft.activation ?? 'invite'}
                onChange={(event) => setDraft((d) => ({
                  ...d, activation: event.target.value as 'invite' | 'password',
                }))}
                helperText="Inviting is the default and the right answer almost always."
                fullWidth
              >
                <MenuItem value="invite">Invite — they choose their own password</MenuItem>
                <MenuItem value="password">Set a password myself (Owner only, audited)</MenuItem>
              </TextField>
            )}

            {draft.activation === 'password' && (
              <>
                <Alert severity="warning" sx={{ borderRadius: 2 }}>
                  You will know this person&apos;s password and nothing forces them to change it.
                  Use this only when the customer&apos;s mail is genuinely blocked, and the audit
                  trail will record that an operator set the credential.
                </Alert>
                <TextField
                  label="Password"
                  type="password"
                  value={draft.password ?? ''}
                  onChange={(event) => setDraft((d) => ({ ...d, password: event.target.value }))}
                  helperText="At least 12 characters, mixing character classes."
                  fullWidth
                />
              </>
            )}

            <TextField
              label="Reason"
              value={draft.reason}
              onChange={(event) => setDraft((d) => ({ ...d, reason: event.target.value }))}
              multiline
              minRows={2}
              required
              helperText="Why the platform is creating this account rather than the customer. Written to the audit trail."
              fullWidth
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={() => setAddOpen(false)} color="inherit">Cancel</Button>
          <Button
            variant="contained"
            startIcon={create.isPending ? <CircularProgress size={16} /> : <AddIcon />}
            disabled={!draftReady || create.isPending}
            onClick={() => create.mutate()}
          >
            Create and invite
          </Button>
        </DialogActions>
      </Dialog>

      <ReasonDialog
        open={pending?.kind === 'deactivate'}
        title="Take this account out of service"
        confirmLabel="Deactivate"
        confirmColor="error"
        description={
          <>
            {pending?.user.email} loses access immediately, and any outstanding activation link
            for them is <strong>withdrawn in the same transaction</strong> — a live link would
            otherwise let its holder switch the account back on by redeeming it. The last active
            administrator cannot be deactivated at all.
          </>
        }
        busy={changeStatus.isPending}
        onClose={() => setPending(null)}
        onConfirm={(reason) =>
          pending && changeStatus.mutate({ user: pending.user, kind: 'deactivate', reason })}
      />

      <ReasonDialog
        open={pending?.kind === 'reactivate'}
        title="Return this account to service"
        confirmLabel="Reactivate"
        description={
          <>
            {pending?.user.email} can sign in again and occupies a seat against the tenant&apos;s
            plan from this moment.
            {pending?.user.awaitingActivation && (
              <> They have never redeemed an invitation, so they still hold no password anybody
                knows — send them a fresh invitation as well.</>
            )}
          </>
        }
        busy={changeStatus.isPending}
        onClose={() => setPending(null)}
        onConfirm={(reason) =>
          pending && changeStatus.mutate({ user: pending.user, kind: 'reactivate', reason })}
      />

      <ReasonDialog
        open={pending?.kind === 'resend'}
        title="Reissue this activation link"
        confirmLabel="Reissue & send"
        description={
          <>
            A fresh single-use link is mailed to {pending?.user.email} and the previous one is
            revoked atomically, so two working links never exist at once.
          </>
        }
        busy={resend.isPending}
        onClose={() => setPending(null)}
        onConfirm={(reason) => pending && resend.mutate({ user: pending.user, reason })}
      />

      <ReasonDialog
        open={pending?.kind === 'role'}
        title="Change this person's role"
        confirmLabel="Change role"
        confirmColor="warning"
        description={
          <>
            Reassigns what {pending?.user.email} may do inside their own workspace. Owner-ranked
            roles satisfy every permission check before a single grant is read, so moving somebody
            into one hands them the tenant.
          </>
        }
        extra={
          <TextField
            select
            label="New role"
            value={nextRoleId}
            onChange={(event) => setNextRoleId(event.target.value)}
            fullWidth
          >
            {(roles.data ?? []).map((role) => (
              <MenuItem key={role.id} value={role.id} disabled={!role.grantable}>
                {role.name} · {role.rankLabel}
              </MenuItem>
            ))}
          </TextField>
        }
        extraProblem={
          nextRoleId === ''
            ? 'Choose the role this person should hold.'
            : nextRoleId === pending?.user.roleId
              ? 'That is the role they already hold.'
              : roleById.get(nextRoleId)?.grantable === false
                ? roleById.get(nextRoleId)?.notGrantableReason ?? 'That role cannot be granted from here.'
                : null
        }
        busy={changeRole.isPending}
        onClose={() => { setPending(null); setNextRoleId(''); }}
        onConfirm={(reason) => pending && changeRole.mutate({ user: pending.user, reason })}
      />
    </Stack>
  );
}
