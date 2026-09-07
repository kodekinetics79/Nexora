import { useState } from 'react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Alert, AlertTitle, Box, Button, Link, MenuItem, TextField, Typography } from '@mui/material';
import { useSnackbar } from 'notistack';
import Stack from './Flex';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import type { PlatformDataBoundaryManifest } from '../types';

/**
 * How this deployment answers "where does our customers' data live" — asked once, of an Owner, in
 * words an operator can act on.
 *
 * <p><b>The defect this replaces.</b> The residency control needed a provider reference, a region
 * and a versioned backup policy, and it asked the operator for all three: four opaque fields in a
 * dialog, per tenant, or four environment variables on the API service. Neither is answerable by
 * the person who meets it. Nobody onboarding a customer knows the Neon endpoint id, and "set an
 * environment variable" needs a deploy and a dashboard — it is the same demand wearing a different
 * hat, which is what made the first fix for this only half a fix.</p>
 *
 * <p><b>What changed.</b> The server is holding an open connection to the database in question, so
 * it reads its own address and shows it here. Two of the three facts are then a confirmation, not
 * a memory test. The third — how long the provider keeps backups — genuinely cannot be observed
 * over SQL, so it is one plain question with the common answer preselected and a sentence saying
 * where to check it.</p>
 *
 * <p><b>What is deliberately still a human act.</b> Pressing the button. Nothing is registered
 * against any tenant until an Owner confirms, the confirmation is audited against their account,
 * and the record says whether the values were observed-and-confirmed or typed. The machine
 * proposes; a person disposes; the audit trail says which did what.</p>
 */
export interface DeploymentDatabasePanelProps {
  manifest: PlatformDataBoundaryManifest;
  /** Called after the server accepted it, so the caller can re-read whatever it shows. */
  onRecorded?: () => void;
  /** Rendered inside a dialog: drop the outer heading, keep the controls. */
  dense?: boolean;
}

/**
 * The answers a provider actually gives, in the words a provider's own console uses. Free text is
 * still available under "Something else" — this list is a shortcut, not a closed world.
 */
const BACKUP_POLICIES: { value: string; label: string }[] = [
  { value: 'pitr-7d', label: 'Point-in-time recovery, last 7 days' },
  { value: 'pitr-14d', label: 'Point-in-time recovery, last 14 days' },
  { value: 'pitr-30d', label: 'Point-in-time recovery, last 30 days' },
  { value: 'daily-snapshot-7d', label: 'Daily snapshots, kept 7 days' },
  { value: 'daily-snapshot-30d', label: 'Daily snapshots, kept 30 days' },
];

const CUSTOM = '__custom';
const opaque = (value: string) => value.trim().length > 0 && !/[\s@=?]|:\/\//.test(value);

export default function DeploymentDatabasePanel({ manifest, onRecorded, dense }: DeploymentDatabasePanelProps) {
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const observed = manifest.observation;

  const [policy, setPolicy] = useState<string>(BACKUP_POLICIES[0].value);
  const [customPolicy, setCustomPolicy] = useState('');
  // Only used when the host said nothing about itself. Seeded from whatever the server DID manage
  // to read, so a half-readable host is still half a head start.
  const [provider, setProvider] = useState(observed.opaqueProviderReference ?? '');
  const [region, setRegion] = useState(observed.region ?? '');

  const backupPolicyReference = policy === CUSTOM ? customPolicy.trim() : policy;

  const record = useMutation({
    mutationFn: () => platformApi.recordPlatformDataBoundary({
      // Omitted on the observed path: the server re-reads its own connection and records the
      // result as observed-and-confirmed, so what is stored is what the process saw at that
      // instant rather than what a form carried back to it.
      opaqueProviderReference: observed.isUsable ? null : provider.trim(),
      region: observed.isUsable ? null : region.trim(),
      backupPolicyReference,
      backupPolicyVersion: 1,
      reason: observed.isUsable ? null : 'Recorded from the operator console for this deployment.',
    }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: platformKeys.platformDataBoundaries() });
      enqueueSnackbar('This deployment’s database is recorded — every tenant can now register itself', { variant: 'success' });
      onRecorded?.();
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The database could not be recorded'), { variant: 'error' }),
  });

  const problem = !backupPolicyReference
    ? 'Choose how long your database provider keeps backups.'
    : !opaque(backupPolicyReference)
      ? 'The backup policy name must be an identifier — no spaces, @, = or ?.'
      : !observed.isUsable && !opaque(provider)
        ? 'A provider reference is required, as an identifier — no URL, credential, @, = or ?.'
        : !observed.isUsable && !region.trim()
          ? 'A region is required.'
          : null;

  return (
    <Stack spacing={1.5}>
      {!dense && (
        <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>This deployment’s database</Typography>
      )}

      {observed.isUsable ? (
        <Alert severity="info" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Nexora read its own database</AlertTitle>
          <Box component="dl" sx={{ m: 0, display: 'grid', gridTemplateColumns: 'auto 1fr', columnGap: 1.5, rowGap: 0.25 }}>
            {observed.providerName && (<><dt>Provider</dt><dd style={{ margin: 0 }}><strong>{observed.providerName}</strong></dd></>)}
            <dt>Database</dt><dd style={{ margin: 0 }}><code>{observed.opaqueProviderReference}</code></dd>
            <dt>Region</dt><dd style={{ margin: 0 }}><strong>{observed.region}</strong></dd>
          </Box>
          <Typography variant="caption" sx={{ display: 'block', mt: 0.75 }}>{observed.basis}</Typography>
        </Alert>
      ) : (
        <Alert severity="warning" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Nexora could not read its own database name</AlertTitle>
          {observed.basis}
        </Alert>
      )}

      {!observed.isUsable && (
        <>
          <TextField
            fullWidth required size="small" label="A name for this database"
            value={provider} onChange={(event) => setProvider(event.target.value)}
            error={Boolean(provider && !opaque(provider))}
            helperText="Any stable identifier your team would recognise — never a connection string or password."
          />
          <TextField
            fullWidth required size="small" label="Where it is hosted"
            value={region} onChange={(event) => setRegion(event.target.value)}
            helperText="The hosting region, for example us-east-1. Every tenant is recorded as living here."
          />
        </>
      )}

      <TextField
        select fullWidth size="small" label="How long backups are kept"
        value={policy} onChange={(event) => setPolicy(event.target.value)}
        helperText={
          <>
            What your database provider actually keeps — check its console if you are not sure.
            {observed.providerName === 'Neon' && ' Neon’s paid plans keep 7 days by default.'}
          </>
        }
      >
        {BACKUP_POLICIES.map((option) => (
          <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>
        ))}
        <MenuItem value={CUSTOM}>Something else…</MenuItem>
      </TextField>

      {policy === CUSTOM && (
        <TextField
          fullWidth required size="small" label="Name of your backup policy"
          value={customPolicy} onChange={(event) => setCustomPolicy(event.target.value)}
          error={Boolean(customPolicy && !opaque(customPolicy))}
          helperText="As your provider names it, for example pitr-3d. No spaces."
        />
      )}

      <Typography variant="caption" color="text.secondary">
        Recorded once for this whole deployment, against your account, in the platform audit trail.
        Every tenant is then registered and verified from it — you will not be asked again.
      </Typography>

      {problem && <Alert role="alert" severity="error" sx={{ borderRadius: 2 }}>{problem}</Alert>}

      <Box>
        <Button
          variant="contained"
          disabled={Boolean(problem) || record.isPending}
          onClick={() => record.mutate()}
          sx={{ fontWeight: 700 }}
        >
          {record.isPending ? 'Recording…' : observed.isUsable ? 'Use this for every tenant' : 'Record this for every tenant'}
        </Button>
      </Box>

      {manifest.source === 'none' && (
        <Typography variant="caption" color="text.secondary">
          Prefer to set it with the rest of your infrastructure? <code>{manifest.configurationKey}</code> in the
          service configuration does the same job — see{' '}
          <Link href="https://github.com/kodekinetics79/Nexora/blob/main/DEPLOYMENT.md" target="_blank" rel="noreferrer">
            DEPLOYMENT.md
          </Link>. What is recorded here wins, so it can always be corrected from this screen.
        </Typography>
      )}
    </Stack>
  );
}
