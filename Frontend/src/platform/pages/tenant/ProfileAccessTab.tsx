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
import { ContentCopy as CopyIcon, Save as SaveIcon, Send as SendIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import { ErrorState, LoadingState } from '../../components/States';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type { Tenant, UpdateTenantProfileInput } from '../../types';

const optional = (value: string): string | null => value.trim() || null;
const when = (value: string | null): string => value ? fmtDateTime(value) : 'never';

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
  const [profile, setProfile] = useState(() => initialProfile(tenant));
  const [resendReason, setResendReason] = useState('Customer did not receive the original activation email.');
  const [recoveryLink, setRecoveryLink] = useState<string | null>(null);

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
          : result.activationUrl
            ? 'Email was not transmitted — use the one-time recovery link shown below'
            : 'Email was not transmitted — an Owner must reissue it to receive the recovery link',
        { variant: result.emailDispatched ? 'success' : 'warning' },
      );
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Invitation reissue failed'), { variant: 'error' }),
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
          <Alert severity="info">
            Workspace slug, currency, residency and commercial terms use separate governed workflows and cannot be changed here.
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

      <PageSection title="Founding administrator access" subtitle="Delivery receipts and governed reissue for single-use activation links.">
        <Stack spacing={2}>
          {recoveryLink && (
            <Alert severity="warning">
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
                        {invitation.sendCount > 0
                          ? `Provider accepted ${invitation.sendCount} send(s); last ${when(invitation.lastSentAtUtc)}`
                          : 'Never transmitted by an email provider'}
                      </Typography>
                    </Box>
                    {invitation.status !== 'Redeemed' && (
                      <Button
                        variant="outlined"
                        startIcon={resend.isPending ? <CircularProgress size={16} /> : <SendIcon />}
                        disabled={resend.isPending || resendReason.trim().length < 3}
                        onClick={() => resend.mutate(invitation.userId)}
                      >
                        Reissue &amp; send
                      </Button>
                    )}
                  </Stack>
                </Box>
              ))}
            </Stack>
          )}
          <Divider />
          <Typography variant="body2" color="text.secondary">
            Configure and verify SMTP or SendGrid on Platform Settings → Email. A console provider records no delivery.
          </Typography>
        </Stack>
      </PageSection>
    </Stack>
  );
}
