import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  TextField,
  Typography,
} from '@mui/material';
import {
  ContentCopy as CopyIcon,
  Send as SendIcon,
  Save as SaveIcon,
  LinkOff as RevokeIcon,
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
import type { Tenant, UpdateTenantProfileInput } from '../../types';

const optional = (value: string): string | null => value.trim() || null;

/**
 * Why each of these cannot be edited here — the REASON, next to the field, not a blanket
 * "governed elsewhere".
 *
 * An operator told only that something is refused assumes a missing feature and asks for it to
 * be added. An operator told that the slug is stamped into every quote reference the customer
 * has ever issued stops asking, and — the part that matters — knows what would break if it were
 * granted. Each sentence names the thing downstream that depends on the value.
 */
const IMMUTABLE_FIELDS: Array<{ label: string; value: (tenant: Tenant) => string | null; why: string }> = [
  {
    label: 'Workspace slug',
    value: (tenant) => tenant.slug,
    why:
      'Permanently immutable. The slug became the business unit code, which became the lead '
      + 'reference prefix, which is stamped into the immutable master reference on every '
      + 'commercial case and printed on every quote this tenant has issued. Renaming it would '
      + 'orphan every reference already in the customer\'s own filing.',
  },
  {
    label: 'Base currency',
    value: (tenant) => tenant.baseCurrencyCode,
    why:
      'Permanently immutable. It is the base every stored conversion, unit cost and ledger '
      + 'balance was computed against. Changing the label without restating the history would '
      + 'silently reinterpret every figure the tenant has ever recorded.',
  },
  {
    label: 'Plan, billing mode and rate card',
    value: (tenant) => tenant.planCode,
    why:
      'Editable, on the Commercial tab, under the Billing policy rather than this one. What a '
      + 'customer is charged is a separation-of-duties boundary: a support administrator may '
      + 'operate a tenant and must not be able to move it onto a cheaper price list.',
  },
];

const initialProfile = (tenant: Tenant): UpdateTenantProfileInput => ({
  name: tenant.name,
  legalName: tenant.legalName,
  registrationNumber: tenant.registrationNumber,
  taxNumber: tenant.taxNumber,
  countryCode: tenant.countryCode,
  industry: tenant.industry,
  website: tenant.website,
  addressLine1: tenant.addressLine1,
  addressLine2: tenant.addressLine2,
  city: tenant.city,
  stateProvince: tenant.stateProvince,
  postalCode: tenant.postalCode,
  phone: tenant.phone,
  contactEmail: tenant.contactEmail,
  logoUrl: tenant.logoUrl,
  timeZoneId: tenant.timeZoneId,
  locale: tenant.locale,
  reason: '',
});

export default function ProfileAccessTab({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();
  const [profile, setProfile] = useState(() => initialProfile(tenant));
  const [resendReason, setResendReason] = useState('Customer did not receive the original activation email.');
  const [recoveryLink, setRecoveryLink] = useState<string | null>(null);
  const [regionOpen, setRegionOpen] = useState(false);
  /** Id of the outstanding invitation the operator is withdrawing, or null. */
  const [revokeTarget, setRevokeTarget] = useState<string | null>(null);
  const [region, setRegion] = useState(tenant.dataRegion ?? '');

  useEffect(() => setRegion(tenant.dataRegion ?? ''), [tenant]);

  const saveRegion = useMutation({
    mutationFn: (reason: string) =>
      platformApi.updateTenantDataRegion(tenant.id, { dataRegion: optional(region), reason }),
    onSuccess: (updated) => {
      queryClient.setQueryData(platformKeys.tenant(tenant.id), updated);
      queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
      setRegionOpen(false);
      enqueueSnackbar('Contractual data region corrected and audited', { variant: 'success' });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Data region change refused'), { variant: 'error' }),
  });

  useEffect(() => setProfile(initialProfile(tenant)), [tenant]);

  const invitations = useQuery({
    queryKey: platformKeys.tenantInvitations(tenant.id),
    queryFn: () => platformApi.listTenantAdminInvitations(tenant.id),
  });

  const saveProfile = useMutation({
    mutationFn: () => platformApi.updateTenantProfile(tenant.id, profile),
    onSuccess: (updated) => {
      queryClient.setQueryData(platformKeys.tenant(tenant.id), updated);
      queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
      queryClient.invalidateQueries({ queryKey: platformKeys.tenantOperations(tenant.id) });
      enqueueSnackbar('Tenant profile updated and audited', { variant: 'success' });
      setProfile(initialProfile(updated));
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Tenant profile update failed'), { variant: 'error' }),
  });

  const resend = useMutation({
    mutationFn: (userId: string | null) => platformApi.resendTenantAdminInvitation(tenant.id, {
      userId,
      reason: resendReason.trim(),
    }),
    onSuccess: (result) => {
      setRecoveryLink(result.activationUrl);
      queryClient.invalidateQueries({ queryKey: platformKeys.tenantInvitations(tenant.id) });
      enqueueSnackbar(
        result.emailDispatched
          ? 'A new activation email was accepted by the provider'
          : 'Email was not transmitted — use the one-time recovery link shown below',
        { variant: result.emailDispatched ? 'success' : 'warning' },
      );
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Invitation reissue failed'), { variant: 'error' }),
  });

  /**
   * WITHDRAW a link without minting another.
   *
   * Reissue was the only action this screen offered, and reissue always sends a fresh link
   * to the address ON FILE. So an invitation typed to the wrong address, or forwarded out of
   * the intended mailbox, could only ever be replaced with another one going to the same
   * place — there was no way at all to kill an outstanding link. The endpoint existed
   * (POST .../admin-invitations/{id}/revoke, TenantAdmin-gated, reason required); nothing
   * in the console called it.
   */
  const revoke = useMutation({
    mutationFn: ({ invitationId, reason }: { invitationId: string; reason: string }) =>
      platformApi.revokeTenantAdminInvitation(tenant.id, invitationId, reason),
    onSuccess: () => {
      setRevokeTarget(null);
      queryClient.invalidateQueries({ queryKey: platformKeys.tenantInvitations(tenant.id) });
      enqueueSnackbar('The activation link was withdrawn and can no longer be used', { variant: 'success' });
    },
    onError: (error) => enqueueSnackbar(
      platformErrorMessage(error, 'The invitation could not be withdrawn'), { variant: 'error' }),
  });

  const set = (key: keyof UpdateTenantProfileInput, value: string) =>
    setProfile((current) => ({ ...current, [key]: key === 'name' || key === 'reason' ? value : optional(value) }));

  const copyRecoveryLink = async () => {
    if (!recoveryLink) return;
    try {
      await navigator.clipboard.writeText(recoveryLink);
      enqueueSnackbar('Activation link copied', { variant: 'success' });
    } catch {
      enqueueSnackbar('Copy failed — select the link and copy it manually', { variant: 'error' });
    }
  };

  const fields: Array<[keyof UpdateTenantProfileInput, string, string?]> = [
    ['name', 'Trading name'],
    ['legalName', 'Legal name'],
    ['registrationNumber', 'Registration number'],
    ['taxNumber', 'Tax number'],
    ['countryCode', 'Country code', 'ISO alpha-2, e.g. US'],
    ['industry', 'Industry'],
    ['website', 'Website'],
    ['contactEmail', 'Company email'],
    ['phone', 'Company phone'],
    ['addressLine1', 'Address line 1'],
    ['addressLine2', 'Address line 2'],
    ['city', 'City'],
    ['stateProvince', 'State / province'],
    ['postalCode', 'Postal code'],
    ['logoUrl', 'Logo URL'],
    ['timeZoneId', 'Time zone', 'IANA, e.g. America/New_York'],
    ['locale', 'Locale', 'BCP-47, e.g. en-US'],
  ];

  return (
    <Stack spacing={2.5}>
      <PageSection title="Company profile" subtitle="Updates the tenant registry, primary business unit and quote identity together.">
        <Stack spacing={2}>
          <Alert severity="info" sx={{ borderRadius: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>What this form cannot change, and why</AlertTitle>
            <Stack spacing={1} sx={{ mt: 0.5 }}>
              {IMMUTABLE_FIELDS.map((field) => (
                <Box key={field.label}>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>
                    {field.label}
                    {field.value(tenant) ? ` — ${field.value(tenant)}` : ''}
                  </Typography>
                  <Typography variant="body2" color="text.secondary">{field.why}</Typography>
                </Box>
              ))}
            </Stack>
          </Alert>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}>
            {fields.map(([key, label, helper]) => (
              <TextField
                key={key}
                label={label}
                value={profile[key] ?? ''}
                onChange={(event) => set(key, event.target.value)}
                required={key === 'name'}
                helperText={helper}
                disabled={tenant.status === 'archived' || saveProfile.isPending}
                fullWidth
              />
            ))}
          </Box>
          <TextField
            label="Reason for change"
            value={profile.reason}
            onChange={(event) => set('reason', event.target.value)}
            required
            multiline
            minRows={2}
            helperText="Required and written to the platform audit trail."
            disabled={tenant.status === 'archived' || saveProfile.isPending}
          />
          {tenant.status === 'archived' && (
            <Alert severity="warning">Restore this archived tenant before editing its retained company identity.</Alert>
          )}
          <Box>
            <Button
              variant="contained"
              startIcon={saveProfile.isPending ? <CircularProgress size={16} /> : <SaveIcon />}
              disabled={tenant.status === 'archived' || saveProfile.isPending || profile.name.trim().length === 0 || profile.reason.trim().length < 3}
              onClick={() => saveProfile.mutate()}
            >
              Save audited changes
            </Button>
          </Box>
        </Stack>
      </PageSection>

      <PageSection
        title="Contractual data region"
        subtitle="Where this tenant's data is asserted to reside. Governed separately from the profile above."
      >
        <Stack spacing={2}>
          <Alert severity="info" sx={{ borderRadius: 2 }}>
            This is not a description of the customer, it is a claim about where their data
            physically is — and two controls read it. A data asset cannot be registered in a
            different region from this value, and tenant activation fails its residency control
            unless the verified database asset matches it. So it can be corrected only INTO
            agreement with the assets already registered: the assets are the evidence, this column
            is the claim about them. The server names any asset that disagrees and refuses rather
            than letting a residency control be satisfied by rewriting the claim instead of moving
            the data.
          </Alert>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} alignItems={{ sm: 'center' }}>
            <TextField
              label="Data region"
              value={region}
              onChange={(event) => setRegion(event.target.value)}
              helperText="Blank is accepted only while the tenant is still provisioning."
              disabled={!permissions.isOwner}
              sx={{ flex: 1 }}
            />
            <Button
              variant="outlined"
              disabled={!permissions.isOwner || optional(region) === (tenant.dataRegion ?? null)}
              onClick={() => setRegionOpen(true)}
            >
              Correct region
            </Button>
          </Stack>
          {!permissions.isOwner && (
            <Typography variant="body2" color="text.secondary">
              Owner-only. Residency is a contractual commitment made during the sale and a control
              an auditor reads; support may operate a tenant and may not restate where it lives.
            </Typography>
          )}
        </Stack>
      </PageSection>

      <ReasonDialog
        open={regionOpen}
        title="Correct the contractual data region"
        confirmLabel="Record the correction"
        confirmColor="warning"
        minReasonLength={15}
        description={
          <>
            Changes {tenant.name}&apos;s recorded region from{' '}
            <strong>{tenant.dataRegion ?? 'none'}</strong> to{' '}
            <strong>{optional(region) ?? 'none'}</strong>. State on whose instruction, and against
            which contract — this sentence is what an auditor reads when they ask why the residency
            claim moved.
          </>
        }
        busy={saveRegion.isPending}
        onClose={() => setRegionOpen(false)}
        onConfirm={(reason) => saveRegion.mutate(reason)}
      />

      <PageSection title="Founding administrator access" subtitle="Delivery receipts and governed reissue for single-use activation links.">
        <Stack spacing={2}>
          {recoveryLink && (
            <Alert severity="warning" sx={{ borderRadius: 2 }}>
              <AlertTitle sx={{ fontWeight: 800 }}>Email was not transmitted — copy this link now</AlertTitle>
              This newly issued link is shown once and only its hash is stored. Deliver it through a secure channel.
              <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 1 }}>
                <Box component="code" sx={{ flex: 1, wordBreak: 'break-all', p: 1, bgcolor: 'action.hover', borderRadius: 1 }}>
                  {recoveryLink}
                </Box>
                <Button startIcon={<CopyIcon />} onClick={copyRecoveryLink}>Copy</Button>
              </Stack>
            </Alert>
          )}

          <TextField
            label="Reason for reissue"
            value={resendReason}
            onChange={(event) => setResendReason(event.target.value)}
            required
            helperText="The previous live link is revoked atomically."
          />

          {invitations.isLoading ? (
            <LoadingState label="Reading invitations…" minHeight={140} />
          ) : invitations.isError ? (
            <ErrorState message={platformErrorMessage(invitations.error, 'Invitations could not be loaded.')} onRetry={() => invitations.refetch()} minHeight={140} />
          ) : (invitations.data ?? []).length === 0 ? (
            <Alert severity="warning">No founding-administrator invitation exists for this tenant.</Alert>
          ) : (
            <Stack spacing={1.5}>
              {(invitations.data ?? []).map((invitation) => (
                <Box key={invitation.id} sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 2, p: 2 }}>
                  <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} alignItems={{ md: 'center' }}>
                    <Box sx={{ flex: 1 }}>
                      <Stack direction="row" spacing={1} alignItems="center">
                        <Typography sx={{ fontWeight: 800 }}>{invitation.email}</Typography>
                        <Chip size="small" label={invitation.status} />
                      </Stack>
                      <Typography variant="body2" color="text.secondary">
                        Issued {fmtDateTime(invitation.issuedAtUtc)} · expires {fmtDateTime(invitation.expiresAtUtc)}
                      </Typography>
                      <Typography variant="body2" color={invitation.sendCount > 0 ? 'success.main' : 'error.main'} sx={{ fontWeight: 700 }}>
                        {/* sendCount and lastSentAtUtc are written by the same statement, so a
                            positive count with no timestamp cannot happen — but the type allows it,
                            and rendering "Invalid Date" beside a delivery claim is worse than
                            saying the timestamp is missing. */}
                        {invitation.sendCount > 0
                          ? `Provider accepted ${invitation.sendCount} send(s); last ${
                              invitation.lastSentAtUtc ? fmtDateTime(invitation.lastSentAtUtc) : 'time not recorded'
                            }`
                          : 'Never transmitted by an email provider'}
                      </Typography>
                    </Box>
                    {invitation.status !== 'Redeemed' && (
                      <Stack direction="row" spacing={1}>
                        <Button
                          variant="outlined"
                          startIcon={resend.isPending ? <CircularProgress size={16} /> : <SendIcon />}
                          disabled={resend.isPending || resendReason.trim().length < 3}
                          onClick={() => resend.mutate(invitation.userId)}
                        >
                          Reissue &amp; send
                        </Button>
                        {/* Only an OUTSTANDING link can be withdrawn. Expired and already-revoked
                            invitations ("Expired"/"Revoked"/"Redeemed") are listed for the record
                            and have nothing live to kill —
                            the endpoint answers 409 for both, so offering the button would be a
                            control that exists only to refuse. */}
                        {invitation.status === 'Pending' && (
                          <Button
                            variant="outlined"
                            color="error"
                            startIcon={<RevokeIcon />}
                            disabled={revoke.isPending}
                            onClick={() => setRevokeTarget(invitation.id)}
                          >
                            Withdraw
                          </Button>
                        )}
                      </Stack>
                    )}
                  </Stack>
                </Box>
              ))}
            </Stack>
          )}
          <Divider />
          <Typography variant="body2" color="text.secondary">
            This panel is about the FOUNDING administrator&apos;s link and its delivery receipts.
            Everyone else in the customer&apos;s workspace — their roles, whether they are in
            service, and adding another person — lives on the Users tab.
          </Typography>
          <Typography variant="body2" color="text.secondary">
            Configure and verify the real SMTP or SendGrid transport on Platform Settings → Email. A console provider records no delivery.
          </Typography>
        </Stack>
      </PageSection>

      <ReasonDialog
        open={revokeTarget !== null}
        title="Withdraw this activation link"
        confirmLabel="Withdraw the link"
        confirmColor="error"
        minReasonLength={3}
        description={
          <>
            The link stops working immediately and <strong>no replacement is sent</strong>. Use this
            when the invitation reached the wrong mailbox — reissuing would only send another one to
            the same address. The founding administrator will need a fresh invitation before they can
            set a password, so say what went wrong: this sentence is the audit record of why a
            customer&apos;s access was withdrawn.
          </>
        }
        busy={revoke.isPending}
        onClose={() => setRevokeTarget(null)}
        onConfirm={(reason) => revokeTarget && revoke.mutate({ invitationId: revokeTarget, reason })}
      />
    </Stack>
  );
}
