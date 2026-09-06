import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  Paper, TextField, Typography,
} from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from '../../components/Flex';
import { ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type {
  PlatformDataBoundary, RegisterTenantDataAssetInput, Tenant, TenantDataAsset, VerifyTenantDataAssetInput,
} from '../../types';
import DataRecoveryPanel from './DataRecoveryPanel';

const LOGICAL_KEY = 'postgresql.primary' as const;
const ASSET_TYPE = 'PostgreSqlTenantScope' as const;
const CLASSIFICATION = 'CustomerData' as const;
const DISPOSITION = 'BackupRetainedUntilExpiryThenDestroy' as const;
const opaque = (value: string) => value.trim().length > 0 && !/[\s@=?]|:\/\//.test(value);

/**
 * Seeded from the deployment's declaration when there is one — the manual form is then a way to
 * EDIT what the platform already knows, not a memory test. The reason stays blank either way:
 * that one is the operator's own words and nothing may suggest it.
 */
const blankRegister = (
  tenant: Tenant, declared: PlatformDataBoundary | null,
): RegisterTenantDataAssetInput => ({
  logicalKey: LOGICAL_KEY,
  opaqueProviderReference: declared?.opaqueProviderReference ?? '',
  region: tenant.dataRegion ?? declared?.region ?? '',
  classification: CLASSIFICATION,
  disposition: DISPOSITION,
  backupPolicyReference: declared?.backupPolicyReference ?? '',
  backupPolicyVersion: declared?.backupPolicyVersion ?? 1,
  reason: '',
});

const blankVerify = (asset: TenantDataAsset): VerifyTenantDataAssetInput => ({
  expectedVersion: asset.version,
  observedBusinessUnitId: '',
  observedRegion: asset.region,
  evidenceReference: '',
  evidenceSha256: '',
  reason: '',
});

export default function DataStorageTab({ tenant }: { tenant: Tenant }) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const [register, setRegister] = useState<RegisterTenantDataAssetInput | null>(null);
  const [verify, setVerify] = useState<{ asset: TenantDataAsset; input: VerifyTenantDataAssetInput } | null>(null);
  const assetsQuery = useQuery({
    queryKey: platformKeys.tenantDataAssets(tenant.id),
    queryFn: () => platformApi.listTenantDataAssets(tenant.id),
  });
  const decisionQuery = useQuery({
    queryKey: platformKeys.tenantActivationDataDecision(tenant.id),
    queryFn: () => platformApi.getTenantActivationDataDecision(tenant.id),
  });
  // What the DEPLOYMENT declares its own database to be. Where it has said, the two forms below
  // are asking an operator to retype configuration the server is already holding.
  const manifestQuery = useQuery({
    queryKey: platformKeys.platformDataBoundaries(),
    queryFn: () => platformApi.getPlatformDataBoundaries(),
  });
  const invalidate = () => {
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantDataAssets(tenant.id) });
    queryClient.invalidateQueries({ queryKey: platformKeys.tenantActivationDataDecision(tenant.id) });
  };
  const onError = (fallback: string) => (error: unknown) =>
    enqueueSnackbar(platformErrorMessage(error, fallback), { variant: 'error' });
  const registerMutation = useMutation({
    mutationFn: (input: RegisterTenantDataAssetInput) => platformApi.registerTenantDataAsset(tenant.id, input),
    onSuccess: () => { setRegister(null); invalidate(); enqueueSnackbar('PostgreSQL tenant scope registered', { variant: 'success' }); },
    onError: onError('Registration was refused'),
  });
  const verifyMutation = useMutation({
    mutationFn: ({ asset, input }: NonNullable<typeof verify>) => platformApi.verifyTenantDataAsset(tenant.id, asset.id, input),
    onSuccess: () => { setVerify(null); invalidate(); enqueueSnackbar('PostgreSQL tenant scope verified', { variant: 'success' }); },
    onError: onError('Verification was refused'),
  });
  const applyMutation = useMutation({
    mutationFn: () => platformApi.applyPlatformDataBoundaries(tenant.id),
    onSuccess: (result) => {
      invalidate();
      // A tenant with no contractual region has one recorded by this action, and the header and
      // Profile & access both read it off the tenant row.
      queryClient.invalidateQueries({ queryKey: platformKeys.tenant(tenant.id) });
      enqueueSnackbar(
        result.decision.dataGateReady
          ? 'Data boundaries registered and verified from the platform configuration'
          : 'Boundaries registered, but the data gate is still blocked',
        { variant: result.decision.dataGateReady ? 'success' : 'warning' },
      );
    },
    onError: onError('The registration was refused'),
  });

  if (assetsQuery.isLoading || decisionQuery.isLoading || manifestQuery.isLoading) return <LoadingState label="Reading the tenant data boundary…" />;
  if (assetsQuery.isError || decisionQuery.isError || !assetsQuery.data || !decisionQuery.data) {
    const error = assetsQuery.error ?? decisionQuery.error;
    return (
      <ErrorState
        message={platformErrorMessage(error, 'The data boundary could not be established. No data-asset action is available.')}
        onRetry={() => { assetsQuery.refetch(); decisionQuery.refetch(); }}
      />
    );
  }

  const primary = assetsQuery.data.find(
    (asset) => asset.logicalKey === LOGICAL_KEY && asset.assetType === ASSET_TYPE,
  ) ?? null;
  const otherAssets = assetsQuery.data.filter((asset) => asset.id !== primary?.id);
  const inconsistent = assetsQuery.data.filter((asset) => asset.logicalKey === LOGICAL_KEY).length > 1;
  const decision = decisionQuery.data;
  const declared = manifestQuery.data?.primaryPostgreSqlScope ?? null;
  const manifestDefect = (manifestQuery.data?.defects ?? [])
    .find((defect) => defect.assetType === ASSET_TYPE)?.reason ?? null;
  const registerValid = Boolean(register
    && opaque(register.opaqueProviderReference)
    && register.region.trim()
    && opaque(register.backupPolicyReference)
    && Number.isInteger(register.backupPolicyVersion)
    && register.backupPolicyVersion > 0
    && register.reason.trim());
  const verifyValid = Boolean(verify
    && /^\d+$/.test(verify.input.observedBusinessUnitId)
    && Number(verify.input.observedBusinessUnitId) > 0
    && verify.input.observedRegion.trim()
    && opaque(verify.input.evidenceReference)
    && /^[a-fA-F0-9]{64}$/.test(verify.input.evidenceSha256)
    && verify.input.reason.trim());

  return (
    <Stack spacing={2.5}>
      {/*
        The activation panel moved to the Lifecycle tab, where activation is looked for. It is not
        rendered in both places on purpose: two Activate buttons on two tabs is two ways to fire the
        same one-way transition, and the second one is always the one nobody tested.
        What stays here is the data control the panel DEPENDS on — "data.residency-isolation" is
        satisfied from this screen, and the decision below still says so.
      */}
      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
          <Box>
            <Typography variant="h6" sx={{ fontWeight: 800 }}>Activation data decision</Typography>
            <Typography variant="body2" color="text.secondary">Server-authoritative readiness for this tenant’s primary data boundary.</Typography>
          </Box>
          <SoftChip label={decision.decision} tone={decision.dataGateReady ? 'success' : 'error'} dot={false} />
        </Stack>
        <Alert severity={decision.dataGateReady ? 'success' : 'warning'} sx={{ mt: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>{decision.dataGateReady ? 'Data gate ready' : 'Activation blocked'}</AlertTitle>
          {decision.blockers.length === 0 ? 'No data-gate blockers were returned.' : (
            <Box component="ul" sx={{ m: 0, pl: 2.5 }}>{decision.blockers.map((blocker) => <li key={blocker}>{blocker}</li>)}</Box>
          )}
        </Alert>
        <Alert severity="info" sx={{ mt: 2 }}><AlertTitle sx={{ fontWeight: 800 }}>Decision boundary</AlertTitle>{decision.boundary}</Alert>
      </Paper>

      {inconsistent && (
        <Alert severity="error"><AlertTitle sx={{ fontWeight: 800 }}>Conflicting primary boundary</AlertTitle>
          The server returned more than one asset for the primary PostgreSQL key. Mutations are disabled; investigate the registry directly.
        </Alert>
      )}

      <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Stack direction={{ xs: 'column', md: 'row' }} justifyContent="space-between" spacing={2}>
          <Box>
            <Typography variant="h6" sx={{ fontWeight: 800 }}>Primary PostgreSQL tenant scope</Typography>
            <Typography variant="body2" color="text.secondary">Logical key {LOGICAL_KEY}. This inventory stores opaque references, never credentials.</Typography>
          </Box>
          {!primary || primary.status !== 'Verified' ? (
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1} sx={{ flexShrink: 0 }}>
              {declared && (
                <Button
                  variant="contained"
                  disabled={inconsistent || applyMutation.isPending}
                  onClick={() => applyMutation.mutate()}
                >
                  {applyMutation.isPending
                    ? 'Working…'
                    : primary ? 'Verify from this deployment' : 'Register from this deployment'}
                </Button>
              )}
              <Button
                variant={declared ? 'text' : 'contained'}
                color={declared ? 'inherit' : 'primary'}
                disabled={inconsistent || applyMutation.isPending}
                onClick={() => (primary
                  ? setVerify({ asset: primary, input: blankVerify(primary) })
                  : setRegister(blankRegister(tenant, declared)))}
              >
                {primary ? 'Verify by hand' : declared ? 'Register by hand' : 'Register boundary'}
              </Button>
            </Stack>
          ) : null}
        </Stack>
        {declared ? (
          <Alert severity="info" sx={{ mt: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>This deployment declares its own database</AlertTitle>
            <code>{declared.opaqueProviderReference}</code> · {declared.region} · backup policy{' '}
            <code>{declared.backupPolicyReference}</code> v{declared.backupPolicyVersion}. Registering
            from it writes exactly that and verifies the scope against the running database — a probe
            that disagrees refuses the whole action rather than registering half of it.
          </Alert>
        ) : (
          <Alert severity="warning" sx={{ mt: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>This deployment has not declared its own database</AlertTitle>
            {manifestDefect ?? (
              <>Set <code>Platform__DataBoundaries__PostgreSqlTenantScope__OpaqueProviderReference</code>,{' '}
                <code>__Region</code>, <code>__BackupPolicyReference</code> and <code>__BackupPolicyVersion</code>{' '}
                on the API service and every tenant registers and verifies itself. Until then the forms
                below are the only way, once per tenant.</>
            )}
          </Alert>
        )}
        {!primary ? <Alert severity="warning" sx={{ mt: 2 }}>No primary PostgreSQL tenant-scope asset is registered.</Alert> : (
          <Stack spacing={1.25} sx={{ mt: 2 }}>
            <Row label="Status"><SoftChip label={primary.status} tone={primary.status === 'Verified' ? 'success' : 'warning'} /></Row>
            <Row label="Provider reference"><code>{primary.opaqueProviderReference}</code></Row>
            <Row label="Region">{primary.region}</Row>
            <Row label="Classification / disposition">{primary.classification} · {primary.disposition}</Row>
            <Row label="Backup policy"><code>{primary.backupPolicyReference}</code> · version {primary.backupPolicyVersion}</Row>
            <Row label="Verified scope">{primary.verifiedBusinessUnitId ?? 'Not verified'}</Row>
            <Row label="Verification evidence">{primary.verificationEvidenceReference ?? 'Not verified'}</Row>
            <Row label="Evidence SHA-256"><code>{primary.verificationEvidenceSha256 ?? 'Not verified'}</code></Row>
            <Row label="Verified by">{primary.verifiedOn ? `${primary.verifiedBy ?? 'Unknown'} · ${fmtDateTime(primary.verifiedOn)}` : 'Not verified'}</Row>
          </Stack>
        )}
      </Paper>

      {otherAssets.length > 0 && <Paper sx={{ p: 3, borderRadius: 3 }}>
        <Typography variant="h6" sx={{ fontWeight: 800 }}>Other registered data boundaries</Typography>
        <Typography variant="body2" color="text.secondary">
          External and derived-data boundaries participate independently in recovery and deletion certification.
        </Typography>
        <Stack spacing={1.25} sx={{ mt: 2 }}>{otherAssets.map((asset) => <Paper variant="outlined" sx={{ p: 2 }} key={asset.id}>
          <Row label={asset.logicalKey}><SoftChip label={asset.status} tone={asset.status === 'Verified' ? 'success' : 'warning'} /></Row>
          <Row label="Type / classification">{asset.assetType} · {asset.classification}</Row>
          <Row label="Provider / region"><code>{asset.opaqueProviderReference}</code> · {asset.region}</Row>
          <Row label="Disposition">{asset.disposition}</Row>
          <Row label="Backup policy"><code>{asset.backupPolicyReference}</code> · version {asset.backupPolicyVersion}</Row>
        </Paper>)}</Stack>
      </Paper>}

      <DataRecoveryPanel tenant={tenant} assets={assetsQuery.data} />

      <Dialog open={Boolean(register)} onClose={() => setRegister(null)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Register PostgreSQL tenant scope</DialogTitle>
        <DialogContent dividers>{register && <Stack spacing={2}>
          <Alert severity="info">Fixed contract: {ASSET_TYPE} · {CLASSIFICATION} · {DISPOSITION}.</Alert>
          <TextField required label="Opaque provider reference" value={register.opaqueProviderReference} helperText="Identifier only—no URL, connection string, credential, whitespace, @, =, or ?." onChange={(e) => setRegister({ ...register, opaqueProviderReference: e.target.value })} error={Boolean(register.opaqueProviderReference && !opaque(register.opaqueProviderReference))} />
          <TextField required label="Data region" value={register.region} onChange={(e) => setRegister({ ...register, region: e.target.value })} helperText={tenant.dataRegion ? `Must match contractual region ${tenant.dataRegion}.` : 'The contractual tenant region must also exist server-side.'} />
          <TextField required label="Opaque backup policy reference" value={register.backupPolicyReference} onChange={(e) => setRegister({ ...register, backupPolicyReference: e.target.value })} error={Boolean(register.backupPolicyReference && !opaque(register.backupPolicyReference))} />
          <TextField required type="number" label="Backup policy version" value={register.backupPolicyVersion} slotProps={{ htmlInput: { min: 1, step: 1 } }} onChange={(e) => setRegister({ ...register, backupPolicyVersion: Number(e.target.value) })} />
          <TextField required multiline minRows={2} label="Registration reason" value={register.reason} onChange={(e) => setRegister({ ...register, reason: e.target.value })} />
        </Stack>}</DialogContent>
        <DialogActions><Button color="inherit" onClick={() => setRegister(null)}>Cancel</Button><Button variant="contained" disabled={!registerValid || registerMutation.isPending} onClick={() => register && registerMutation.mutate(register)}>Register</Button></DialogActions>
      </Dialog>

      <Dialog open={Boolean(verify)} onClose={() => setVerify(null)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Verify PostgreSQL tenant scope</DialogTitle>
        <DialogContent dividers>{verify && <Stack spacing={2}>
          <Alert severity="warning">Record only evidence from a completed external probe. This form does not connect to PostgreSQL or grant access.</Alert>
          <TextField required label="Observed business-unit ID" value={verify.input.observedBusinessUnitId} onChange={(e) => setVerify({ ...verify, input: { ...verify.input, observedBusinessUnitId: e.target.value.replace(/\D/g, '') } })} />
          <TextField required label="Observed region" value={verify.input.observedRegion} onChange={(e) => setVerify({ ...verify, input: { ...verify.input, observedRegion: e.target.value } })} helperText={`Must match registered region ${verify.asset.region}.`} />
          <TextField required label="Opaque evidence reference" value={verify.input.evidenceReference} error={Boolean(verify.input.evidenceReference && !opaque(verify.input.evidenceReference))} onChange={(e) => setVerify({ ...verify, input: { ...verify.input, evidenceReference: e.target.value } })} />
          <TextField required label="Evidence SHA-256" value={verify.input.evidenceSha256} error={Boolean(verify.input.evidenceSha256 && !/^[a-fA-F0-9]{64}$/.test(verify.input.evidenceSha256))} onChange={(e) => setVerify({ ...verify, input: { ...verify.input, evidenceSha256: e.target.value.trim() } })} helperText="Exactly 64 hexadecimal characters." />
          <TextField required multiline minRows={2} label="Verification reason" value={verify.input.reason} onChange={(e) => setVerify({ ...verify, input: { ...verify.input, reason: e.target.value } })} />
        </Stack>}</DialogContent>
        <DialogActions><Button color="inherit" onClick={() => setVerify(null)}>Cancel</Button><Button variant="contained" disabled={!verifyValid || verifyMutation.isPending} onClick={() => verify && verifyMutation.mutate(verify)}>Verify evidence</Button></DialogActions>
      </Dialog>
    </Stack>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" spacing={1}>
    <Typography variant="body2" color="text.secondary">{label}</Typography>
    <Box sx={{ fontWeight: 700, fontSize: 14, textAlign: { sm: 'right' }, overflowWrap: 'anywhere', maxWidth: 650 }}>{children}</Box>
  </Stack>;
}
