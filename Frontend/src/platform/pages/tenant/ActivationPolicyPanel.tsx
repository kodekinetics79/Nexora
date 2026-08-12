import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControl, InputLabel, MenuItem, Paper, Select, TextField, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import RoleGate from '../../components/RoleGate';
import { ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import { usePlatformPermissions } from '../../auth/usePlatformPermissions';
import { REQUIRED_ROLE_COPY } from '../../auth/permissions';
import type {
  ActivationControlDecision, ActivationControlDisposition, ActivationControlRemediation,
  ActivationRemediationAuthority, ActivationRemediationSurface,
  RecordActivationControlEvidenceInput, Tenant, TenantDeploymentProfile,
} from '../../types';
import { TENANT_DEPLOYMENT_PROFILES } from '../../types';
import { isAbsoluteHttpUrl, isActivationEvidenceValid, isSha256 } from './dataGovernanceValidation';
import ActivationResolverDialog, {
  isResolvableInline, type InlineResolverAction,
} from './ActivationResolvers';

const EVIDENCE_CONTROLS = new Set(['security.privileged-mfa-policy', 'integrations.mandatory']);

/**
 * The chip used to render `satisfied ? 'Pass' : 'Block'`, which is a different question from the
 * one the operator is asking. Under an approved DEMO profile the four deferrable controls are
 * unsatisfied and NOT blocking — the server says so in `disposition` — yet every one of them
 * showed a red "Block" indistinguishable from the ten that really do stop the activation. An
 * operator reading fourteen red rows, four of which are noise, has no way to tell which four.
 *
 * So the chip renders the disposition the server actually returned. `satisfied` is still the
 * strict answer and is still what the production-blocker list is built from; this is what the
 * failure MEANS on this profile.
 */
const DISPOSITION_COPY: Record<ActivationControlDisposition, { label: string; tone: 'success' | 'error' | 'warning' }> = {
  SATISFIED: { label: 'Pass', tone: 'success' },
  BLOCKING: { label: 'Blocking', tone: 'error' },
  DEFERRED: { label: 'Deferred', tone: 'warning' },
  EXTERNALLY_BLOCKED: { label: 'Externally blocked', tone: 'warning' },
};

/**
 * Who the operator would have to be, per authority the server will actually apply.
 *
 * Not `REQUIRED_ROLE_COPY.owner`: that sentence was written for tenant deletion and says the
 * action is irreversible. Registering a data boundary is versioned, audited and correctable, and
 * telling an operator it is irreversible is how they learn that the tooltip is boilerplate — the
 * next one they dismiss is the one that mattered.
 */
const AUTHORITY_COPY: Record<ActivationRemediationAuthority, string> = {
  Owner: 'Owner only. This control is satisfied from a screen that records a governed claim about '
    + 'where a customer\'s data lives, which support may operate around and may not restate.',
  Billing: REQUIRED_ROLE_COPY.billing,
  TenantAdmin: REQUIRED_ROLE_COPY.tenantAdmin,
  OwnerMfa: 'Owner only, and the server additionally requires an MFA-bound session: this records an '
    + 'attestation that an auditor will read as the platform\'s own word.',
};

/** Where each surface lives in the console. Tenant surfaces are tabs on this page. */
const SURFACE_TAB: Record<Exclude<ActivationRemediationSurface, 'platform.plans'>, string> = {
  'tenant.activation': 'activation',
  'tenant.profile-access': 'profile-access',
  'tenant.commercial': 'commercial',
  'tenant.data-storage': 'data-storage',
};

/**
 * The server refuses a profile change off PRODUCTION with a reason shorter than this, and says so
 * in prose. Mirroring the bound here turns that 400 into a disabled button, which is the difference
 * between "the form told me" and "the server told me after I filled it in".
 */
const MINIMUM_PROFILE_REASON_LENGTH = 15; // TenantsController.MinimumDataRegionReasonLength

const PROFILE_COPY: Record<TenantDeploymentProfile, string> = {
  PRODUCTION: 'Every activation control is a hard gate. Nothing is deferrable.',
  LOCAL_TEST: 'A developer machine. Infrastructure prerequisites that no laptop can stand up are deferred.',
  DEMO: 'A demonstration tenant with no customer data. Externally-supplied prerequisites are deferred, and stay recorded as production blockers.',
};
const localNow = () => {
  const now = new Date();
  return new Date(now.getTime() - now.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
};
const toUtc = (value: string) => value ? new Date(value).toISOString() : null;

interface EvidenceDraft {
  controlCode: string;
  disposition: 'approved' | 'deferred';
  evidenceReference: string;
  evidenceSha256: string;
  effectiveFrom: string;
  effectiveTo: string;
  reason: string;
}

const newEvidence = (controlCode: string): EvidenceDraft => ({
  controlCode,
  disposition: 'approved',
  evidenceReference: '',
  evidenceSha256: '',
  effectiveFrom: localNow(),
  effectiveTo: '',
  reason: '',
});

export default function ActivationPolicyPanel({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const permissions = usePlatformPermissions();
  const canChangeProfile = permissions.isOwner;
  const [evidence, setEvidence] = useState<EvidenceDraft | null>(null);
  const [resolver, setResolver] = useState<InlineResolverAction | null>(null);
  const [confirmActivation, setConfirmActivation] = useState(false);
  const [profileDraft, setProfileDraft] = useState<TenantDeploymentProfile | null>(null);
  const [profileReason, setProfileReason] = useState('');
  const decisionQuery = useQuery({
    queryKey: platformKeys.tenantActivationDecision(tenant.id),
    queryFn: () => platformApi.getTenantActivationDecision(tenant.id),
  });
  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantActivationDecision(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
  };
  const activateMutation = useMutation({
    mutationFn: () => platformApi.activateTenant(tenant.id),
    onSuccess: () => {
      setConfirmActivation(false);
      refresh();
      enqueueSnackbar('Tenant activated under the authoritative policy', { variant: 'success' });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Activation was refused'), { variant: 'error' }),
  });
  const evidenceMutation = useMutation({
    mutationFn: ({ controlCode, input }: { controlCode: string; input: RecordActivationControlEvidenceInput }) =>
      platformApi.recordTenantActivationEvidence(tenant.id, controlCode, input),
    onSuccess: () => {
      setEvidence(null);
      refresh();
      enqueueSnackbar('Activation evidence recorded', { variant: 'success' });
    },
    onError: (error) => enqueueSnackbar(platformErrorMessage(error, 'Evidence was refused'), { variant: 'error' }),
  });
  const profileMutation = useMutation({
    mutationFn: (profile: TenantDeploymentProfile) => platformApi.setTenantDeploymentProfile(tenant.id, {
      profile,
      // PRODUCTION needs no justification — tightening a gate never does — and the server
      // enforces that asymmetry, so send null rather than an empty string it would have to parse.
      reason: profile === 'PRODUCTION' ? null : profileReason.trim(),
    }),
    onSuccess: () => {
      setProfileDraft(null);
      setProfileReason('');
      refresh();
      enqueueSnackbar('Deployment profile recorded', { variant: 'success' });
    },
    onError: (error) => enqueueSnackbar(
      platformErrorMessage(error, 'The deployment profile change was refused'), { variant: 'error' }),
  });

  if (decisionQuery.isLoading) return <LoadingState label="Evaluating the authoritative activation policy…" />;
  if (decisionQuery.isError || !decisionQuery.data) {
    return <ErrorState message={platformErrorMessage(
      decisionQuery.error,
      'The activation policy could not be evaluated. Activation and evidence actions are unavailable.',
    )} onRetry={() => decisionQuery.refetch()} />;
  }

  const decision = decisionQuery.data;
  const evidenceValid = Boolean(evidence && isActivationEvidenceValid(evidence));
  const canActivate = tenant.status === 'provisioning' && decision.ready;

  /**
   * Whether this operator can take the remedy, decided by the same policy the server will apply.
   * OwnerMfa collapses to Owner here: the session's MFA binding is proven at request time by the
   * server, so treating an Owner as unable would hide a control they very likely can use — and
   * `RoleGate` disables rather than hides, so the operator who cannot is still told who can.
   */
  const authorityAllows = (authority: ActivationRemediationAuthority): boolean => {
    if (authority === 'Billing') return permissions.canAdministerBilling;
    if (authority === 'TenantAdmin') return permissions.canAdministerTenants;
    return permissions.isOwner;
  };

  /**
   * Take the remedy where it lives. Four of them are dialogs over the same endpoints the owning
   * tab calls; the rest open that tab, because assigning a plan or repricing the plan catalogue
   * moves money for tenants other than this one and belongs where that blast radius is visible.
   */
  const resolve = (control: ActivationControlDecision, remediation: ActivationControlRemediation) => {
    if (remediation.action === 'tenant.activation-evidence') {
      setEvidence(newEvidence(control.code));
      return;
    }
    if (isResolvableInline(remediation.action)) {
      setResolver(remediation.action);
      return;
    }
    if (remediation.surface === 'platform.plans') {
      navigate('/platform/plans');
      return;
    }
    // The tab lives in the URL on this page already, so navigating to it is a link an operator
    // can also paste into a ticket — which is what they do with it.
    navigate(`/platform/tenants/${tenant.id}?tab=${SURFACE_TAB[remediation.surface]}`);
  };

  const submitEvidence = () => {
    if (!evidence || !evidenceValid) return;
    evidenceMutation.mutate({
      controlCode: evidence.controlCode,
      input: {
        disposition: evidence.disposition,
        evidenceReference: evidence.evidenceReference.trim(),
        evidenceSha256: evidence.evidenceSha256.trim().toLowerCase(),
        effectiveFromUtc: toUtc(evidence.effectiveFrom)!,
        effectiveToUtc: toUtc(evidence.effectiveTo),
        reason: evidence.reason.trim(),
      },
    });
  };

  return <>
    <Paper sx={{ p: 3, borderRadius: 3 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
        <Box>
          <Typography variant="h6" sx={{ fontWeight: 800 }}>Authoritative tenant activation</Typography>
          <Typography variant="body2" color="text.secondary">
            Server-evaluated commercial, access, data, security and provisioning controls. Policy {decision.policyVersion}.
          </Typography>
        </Box>
        <Stack direction="row" spacing={1} alignItems="center">
          <SoftChip label={decision.ready ? 'Ready' : 'Blocked'} tone={decision.ready ? 'success' : 'error'} dot={false} />
          <Button variant="contained" disabled={!canActivate} onClick={() => setConfirmActivation(true)}>
            Activate tenant
          </Button>
        </Stack>
      </Stack>

      <Alert severity={decision.ready ? 'success' : 'warning'} sx={{ mt: 2 }}>
        <AlertTitle sx={{ fontWeight: 800 }}>{decision.ready ? 'All activation controls pass' : 'Activation blocked by server policy'}</AlertTitle>
        {decision.blockingControls.length === 0
          ? 'The decision is ready. The separate Owner action is still required.'
          : <Box component="ul" sx={{ m: 0, pl: 2.5 }}>{decision.blockingControls.map((code) => <li key={code}>{code}</li>)}</Box>}
      </Alert>
      {tenant.status !== 'provisioning' && (
        <Alert severity="info" sx={{ mt: 2 }}>Only a tenant in Provisioning can be activated. Current status: {tenant.status}.</Alert>
      )}
      {decision.warnings.map((warning) => <Alert severity="warning" sx={{ mt: 1 }} key={warning}>{warning}</Alert>)}

      {/*
        The deployment profile had a server endpoint, a request DTO, a frontend type and a client
        method — and no control anywhere in the console. The activation policy's own warning text
        told the operator to "set the profile through PUT /api/platform/tenants/{id}/deployment-
        profile", which is an instruction to open a terminal. Since the profile is what decides
        whether an externally-supplied prerequisite blocks activation or is recorded as deferred,
        a demo tenant had no reachable path to activation at all.

        Deliberately NOT a shortcut around the gate: a deferred control still reports as a
        production blocker, the reason is mandatory and audited, and the server refuses the change
        once the tenant has left Provisioning.
      */}
      <Paper variant="outlined" sx={{ p: 2, mt: 2 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={1.5}>
          <Box>
            <Stack direction="row" spacing={1} alignItems="center">
              <Typography sx={{ fontWeight: 800 }}>Deployment profile</Typography>
              <SoftChip label={decision.deploymentProfile} tone={decision.deploymentProfile === 'PRODUCTION' ? 'neutral' : 'warning'} />
            </Stack>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              {decision.deploymentProfileDetail || PROFILE_COPY[decision.deploymentProfile]}
            </Typography>
            {decision.deferredControls.length + decision.externallyBlockedControls.length > 0 && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                Deferred under this profile: {[...decision.deferredControls, ...decision.externallyBlockedControls].join(' · ')}
              </Typography>
            )}
          </Box>
          <RoleGate allowed={canChangeProfile} requirement="Only the platform Owner can change a deployment profile.">
            {(disabled) => (
              <Button
                variant="outlined"
                disabled={disabled || tenant.status !== 'provisioning'}
                onClick={() => { setProfileDraft(decision.deploymentProfile); setProfileReason(''); }}>
                Change profile
              </Button>
            )}
          </RoleGate>
        </Stack>
      </Paper>

      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ mt: 2 }}>
        <SoftChip label={`Commercial ${decision.commercialState}`} tone="neutral" dot={false} />
        <SoftChip label={`Access ${decision.accessState}`} tone="neutral" dot={false} />
        <SoftChip label={`Data ${decision.dataState}`} tone="neutral" dot={false} />
        <SoftChip label={`Legal hold ${decision.legalHoldState}`} tone="neutral" dot={false} />
      </Stack>

      <Stack spacing={1.25} sx={{ mt: 2 }}>
        {decision.controls.map((control) => <Paper key={control.code} variant="outlined" sx={{ p: 2 }}>
          <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={1.5}>
            <Box>
              <Stack direction="row" spacing={1} alignItems="center">
                <Typography sx={{ fontWeight: 800 }}>{control.code}</Typography>
                <SoftChip
                  label={DISPOSITION_COPY[control.disposition].label}
                  tone={DISPOSITION_COPY[control.disposition].tone}
                />
              </Stack>
              <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>{control.detail}</Typography>
              {/* What production needs, next to a control this profile is letting through. A
                  deferral is an activation on this profile and nothing else. */}
              {control.productionRequirement && control.disposition !== 'BLOCKING' && !control.satisfied && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                  Production still requires: {control.productionRequirement}
                </Typography>
              )}
              {control.remediation && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                  {control.remediation.hint}
                </Typography>
              )}
              {/*
                No button, and the reason said out loud. These four are consequences of system
                state or assertions that are not the operator's to make, and the platform
                deliberately offers nothing that would satisfy them from here — a button that
                cannot honestly change the fact would teach an operator that the other ten are
                decorative too.
              */}
              {!control.satisfied && !control.remediation && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                  No console action satisfies this control. It records a fact about the system or
                  about the customer, and the platform will not offer a button that asserts one on
                  their behalf.
                  {control.code === 'admin.first-activated' && (
                    <> The founding administrator has to redeem their invitation and sign in; the
                    link is reissued or withdrawn on Profile &amp; access.</>
                  )}
                </Typography>
              )}
              {control.evidenceReferences.length > 0 && (
                <Typography variant="caption" sx={{ overflowWrap: 'anywhere' }}>
                  Evidence: {control.evidenceReferences.join(' · ')}
                </Typography>
              )}
            </Box>
            {/*
              The remedy, gated on the authority the SERVER will apply rather than on a guess.
              Labelled with what it does rather than "Resolve": ten identical Resolve buttons down
              one column is the same dead end as the bare control codes this replaced.
              Deliberately absent on a satisfied control — there is nothing to fix, and an edit
              button beside a passing control is an invitation to change a customer's record for
              no reason.
            */}
            {control.remediation && !control.satisfied && (
              <RoleGate
                allowed={authorityAllows(control.remediation.requiredAuthority)}
                requirement={AUTHORITY_COPY[control.remediation.requiredAuthority]}
              >
                {(disabled) => (
                  <Button
                    variant="outlined"
                    disabled={disabled}
                    sx={{ flexShrink: 0, alignSelf: { md: 'flex-start' } }}
                    onClick={() => control.remediation && resolve(control, control.remediation)}
                  >
                    {control.remediation?.label}
                  </Button>
                )}
              </RoleGate>
            )}
            {/* The evidence form stays reachable on a SATISFIED attestation control — an
                attestation expires, and re-recording it before it does is the whole point — and
                as the fallback when a server has not sent a remediation for it at all. */}
            {EVIDENCE_CONTROLS.has(control.code) && (control.satisfied || !control.remediation) && (
              <Button variant="outlined" onClick={() => setEvidence(newEvidence(control.code))}>Record evidence</Button>
            )}
          </Stack>
        </Paper>)}
      </Stack>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 2 }}>
        Evaluated {fmtDateTime(decision.evaluatedAtUtc)}. A visible pass is not an activation; only the server transition changes tenant state.
      </Typography>
    </Paper>

    <Dialog open={confirmActivation} onClose={() => setConfirmActivation(false)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Activate {tenant.name}?</DialogTitle>
      <DialogContent dividers>
        <Alert severity="warning">This enables tenant access. The server will re-evaluate every control inside the activation transaction.</Alert>
      </DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => setConfirmActivation(false)}>Cancel</Button>
        <Button variant="contained" disabled={!canActivate || activateMutation.isPending} onClick={() => activateMutation.mutate()}>
          Activate under policy
        </Button>
      </DialogActions>
    </Dialog>

    <Dialog open={Boolean(profileDraft)} onClose={() => setProfileDraft(null)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Deployment profile for {tenant.name}</DialogTitle>
      <DialogContent dividers>{profileDraft && <Stack spacing={2}>
        <Alert severity="warning">
          A non-PRODUCTION profile defers prerequisites that this side cannot stand up; it does not satisfy
          them. Every deferred control keeps reporting as a production blocker, and this tenant cannot be
          certified production-ready while any of them is deferred.
        </Alert>
        <FormControl fullWidth>
          <InputLabel id="tenant-deployment-profile">Profile</InputLabel>
          <Select labelId="tenant-deployment-profile" label="Profile" value={profileDraft}
            onChange={(event) => setProfileDraft(event.target.value as TenantDeploymentProfile)}>
            {TENANT_DEPLOYMENT_PROFILES.map((profile) => (
              <MenuItem key={profile} value={profile}>{profile}</MenuItem>
            ))}
          </Select>
        </FormControl>
        <Typography variant="body2" color="text.secondary">{PROFILE_COPY[profileDraft]}</Typography>
        {profileDraft !== 'PRODUCTION' && (
          <TextField
            required multiline minRows={2} label="Reason" value={profileReason}
            error={Boolean(profileReason) && profileReason.trim().length < MINIMUM_PROFILE_REASON_LENGTH}
            helperText={`On what basis this tenant may defer a production control. At least ${MINIMUM_PROFILE_REASON_LENGTH} characters; recorded in the audit trail.`}
            onChange={(event) => setProfileReason(event.target.value)} />
        )}
      </Stack>}</DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => setProfileDraft(null)}>Cancel</Button>
        <Button
          variant="contained"
          disabled={
            !profileDraft || profileMutation.isPending
            || (profileDraft !== 'PRODUCTION' && profileReason.trim().length < MINIMUM_PROFILE_REASON_LENGTH)
          }
          onClick={() => profileDraft && profileMutation.mutate(profileDraft)}>
          Record profile
        </Button>
      </DialogActions>
    </Dialog>

    <Dialog open={Boolean(evidence)} onClose={() => setEvidence(null)} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Record activation control evidence</DialogTitle>
      <DialogContent dividers>{evidence && <Stack spacing={2}>
        <Alert severity="warning">Record a real, externally retained evidence artifact. This form does not verify the artifact or the underlying control.</Alert>
        <TextField label="Control" value={evidence.controlCode} disabled />
        <FormControl fullWidth>
          <InputLabel id="activation-evidence-disposition">Disposition</InputLabel>
          <Select labelId="activation-evidence-disposition" label="Disposition" value={evidence.disposition}
            onChange={(event) => setEvidence({ ...evidence, disposition: event.target.value as 'approved' | 'deferred' })}>
            <MenuItem value="approved">Approved</MenuItem>
            {evidence.controlCode === 'integrations.mandatory' && <MenuItem value="deferred">Deferred</MenuItem>}
          </Select>
        </FormControl>
        <TextField required label="Evidence URL" value={evidence.evidenceReference}
          error={Boolean(evidence.evidenceReference && !isAbsoluteHttpUrl(evidence.evidenceReference))}
          helperText="Absolute HTTP(S) reference to the governed evidence artifact."
          onChange={(event) => setEvidence({ ...evidence, evidenceReference: event.target.value })} />
        <TextField required label="Evidence SHA-256" value={evidence.evidenceSha256}
          error={Boolean(evidence.evidenceSha256 && !isSha256(evidence.evidenceSha256))}
          helperText="Exactly 64 hexadecimal characters."
          onChange={(event) => setEvidence({ ...evidence, evidenceSha256: event.target.value.trim() })} />
        <TextField required type="datetime-local" label="Effective from" value={evidence.effectiveFrom}
          slotProps={{ inputLabel: { shrink: true } }} onChange={(event) => setEvidence({ ...evidence, effectiveFrom: event.target.value })} />
        <TextField type="datetime-local" label="Effective until (optional)" value={evidence.effectiveTo}
          slotProps={{ inputLabel: { shrink: true } }} onChange={(event) => setEvidence({ ...evidence, effectiveTo: event.target.value })} />
        <TextField required multiline minRows={2} label="Approval reason" value={evidence.reason}
          onChange={(event) => setEvidence({ ...evidence, reason: event.target.value })} />
      </Stack>}</DialogContent>
      <DialogActions>
        <Button color="inherit" onClick={() => setEvidence(null)}>Cancel</Button>
        <Button variant="contained" disabled={!evidenceValid || evidenceMutation.isPending} onClick={submitEvidence}>Record immutable evidence</Button>
      </DialogActions>
    </Dialog>

    {/*
      The inline remedies. Each is the same HTTP call, under the same policy, audited under the
      same action as the edit the owning tab makes — no new privileged endpoint exists for any of
      them. `refresh` re-evaluates the policy afterwards so the operator sees the control turn on
      the screen they fixed it from, rather than having to go back and look.
    */}
    <ActivationResolverDialog
      tenant={tenant}
      action={resolver}
      onClose={() => setResolver(null)}
      onResolved={refresh}
    />
  </>;
}
