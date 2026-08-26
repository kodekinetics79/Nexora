import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Checkbox, Chip, Dialog, DialogActions, DialogContent,
  DialogContentText, DialogTitle, Divider, FormControlLabel, Paper, Stack, Switch, Table,
  TableBody, TableCell, TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import {
  DeleteForeverOutlined, FactCheckOutlined, HistoryToggleOffOutlined, InventoryOutlined,
  SaveOutlined, ShieldOutlined, VerifiedUserOutlined,
} from '@mui/icons-material';
import {
  EVIDENCE_RETENTION_DEFAULT_DAYS, EVIDENCE_RETENTION_MAX_DAYS, EVIDENCE_RETENTION_MIN_DAYS,
  TENANT_DATA_CONFIRM_PHRASE,
  isEvidenceRetentionUnavailable, newIdempotencyKey, platformGovernanceService,
  type EvidenceRetentionExclusion, type EvidenceRetentionRunResult, type EvidenceRetentionSummary,
  type TenantDataCleanupResult, type TenantDataControlSummary,
} from '../../api/services/platformGovernanceService';
import { EmptyState, ErrorState, LoadingState } from '../../platform/components/States';
import { looksLikeTechnicalNoise, toPresentableError } from '../../utils/apiErrors';
import { useAuth } from '../../context/AuthContext';

/* ────────────────────────────────────────────────────────────────────────────
 * Storage & Retention — the tenant-facing control for reclaiming disk space.
 *
 * What this screen actually does, stated once so no copy below can drift from it:
 * it deletes STORED FILE BYTES. It does not, and cannot, delete evidence records — the database
 * refuses that outright. The document row, its SHA-256 fingerprint, filename, size and every
 * extracted field, lead, line item and audit event survive a purge. Lineage still answers
 * "this value came from document X, hash Y, purged on DATE under policy N by user Z".
 *
 * The one claim this screen must never make is that purging erases personal data. Buyer names and
 * email addresses were COPIED out of the file into field evidence, document regions and the lead
 * during extraction; deleting the original leaves every one of those copies in place. Saying
 * otherwise on an irreversible-action screen would be a false compliance answer, so the disclosure
 * says the opposite, in the confirmation the user cannot skip.
 * ──────────────────────────────────────────────────────────────────────────── */

const NOT_REPORTED = 'Not reported';

/** Human byte sizes. `null` (field absent from the response) never becomes "0 B". */
export const formatBytes = (bytes: number | null | undefined): string => {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes) || bytes < 0) return NOT_REPORTED;
  if (bytes < 1024) return `${bytes} B`;
  const units = ['KB', 'MB', 'GB', 'TB'] as const;
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${units[unit]}`;
};

const formatCount = (count: number | null | undefined): string =>
  count === null || count === undefined || !Number.isFinite(count)
    ? NOT_REPORTED
    : count.toLocaleString();

/**
 * Eligibility reason codes, in the tenant's language. A number smaller than expected is only
 * defensible if the user can see WHY each document was held back.
 */
const EXCLUSION_COPY: Readonly<Record<string, string>> = {
  LEGAL_HOLD: 'Under legal hold. Release the hold before its file can be deleted.',
  // No DELETION_REVIEW_PENDING / DELETION_REQUESTED entries: that review had no approver
  // anywhere, and the copy told the reader a decision was coming that never could.
  WITHIN_RETENTION_WINDOW: 'Still inside the retention window you set.',
  NOT_YET_ELIGIBLE: 'Still inside the retention window you set.',
  PROCESSING_INCOMPLETE: 'Extraction has not finished, so the file is still needed.',
  PROCESSING_NOT_COMPLETED: 'Extraction has not finished, so the file is still needed.',
  REVIEW_REQUIRED: 'Waiting on human review of the extracted data.',
  EXTRACTION_FAILED: 'Extraction failed. The file is kept so it can be retried.',
  EXTRACTION_JOB_NOT_SUCCEEDED: 'Its extraction job can still be retried, which needs the file.',
  DEAD_LETTER: 'A failed job is still replayable, which needs the file.',
  INTAKE_NOT_TERMINAL: 'Intake is still in progress for this document.',
  SECURITY_PENDING: 'The security scan has not completed.',
  QUARANTINED: 'Quarantined. The stored bytes are the malware evidence.',
  OPEN_COMMERCIAL_RECORD: 'On an open commercial case — an RFQ, quote or order is still live.',
  OPEN_HUMAN_ACTION: 'An open action item still points at this document.',
  STATUTORY_RETENTION: 'Classified as an invoice, purchase order or contract. Commercial and tax law require these to be kept for years, so they are never auto-purged.',
  COMMERCIAL_DOCUMENT_TYPE: 'Classified as an invoice, purchase order or contract. Commercial and tax law require these to be kept for years, so they are never auto-purged.',
  INQUIRY_NOT_RESOLVED: 'Its inquiry is still a draft or awaiting review.',
  ALREADY_PURGED: 'Its stored file was already purged. The record and lineage remain.',
  BYTES_ALREADY_ABSENT: 'No stored file to delete — this record is already reconciled.',
};

/**
 * Renders a reason code as product copy. Unknown codes are de-cased into a readable phrase;
 * anything carrying operator signal is suppressed via the shared error-presentation gate rather
 * than pasted onto the screen.
 */
export const describeExclusionReason = (reason: string | null | undefined): string => {
  const raw = (reason ?? '').trim();
  if (!raw) return 'Held back by a retention rule this deployment did not name.';
  const mapped = EXCLUSION_COPY[raw.toUpperCase().replace(/[\s-]+/g, '_')];
  if (mapped) return mapped;
  if (raw.length > 200 || looksLikeTechnicalNoise(raw)) {
    return 'Held back by a retention rule that cannot be shown here.';
  }
  if (/^[A-Za-z0-9_.-]+$/.test(raw)) {
    const words = raw.replace(/[_.-]+/g, ' ').trim().toLowerCase();
    return `${words.charAt(0).toUpperCase()}${words.slice(1)}.`;
  }
  return raw;
};

const CONFIRM_PHRASE = TENANT_DATA_CONFIRM_PHRASE;

interface TileProps { label: string; value: string; caption: string; icon: React.ReactNode }

function Tile({ label, value, caption, icon }: TileProps) {
  return (
    <Paper variant="outlined" component="div" sx={{ p: 2, height: '100%' }}>
      <Stack direction="row" sx={{ gap: 1, alignItems: 'center', color: 'text.secondary', mb: 0.5 }}>
        {icon}
        <Typography variant="caption" sx={{ fontWeight: 700, letterSpacing: 0.4 }}>
          {label}
        </Typography>
      </Stack>
      <Typography variant="h5" sx={{ fontWeight: 800, m: 0, lineHeight: 1.2 }}>
        {value}
      </Typography>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
        {caption}
      </Typography>
    </Paper>
  );
}

function ExclusionTable({ rows }: { rows: EvidenceRetentionExclusion[] }) {
  return (
    <TableContainer>
      <Table size="small" aria-label="Documents excluded from this purge and the reason for each">
        <TableHead>
          <TableRow>
            <TableCell scope="col">Document</TableCell>
            <TableCell scope="col">Why it is kept</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {rows.map((row, index) => (
            <TableRow key={`${row.documentId ?? 'unknown'}-${index}`}>
              <TableCell>
                <Typography variant="body2" sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>
                  {row.fileName ?? (row.documentId !== null ? `Document ${row.documentId}` : 'Unidentified document')}
                </Typography>
                {row.fileName && row.documentId !== null && (
                  <Typography variant="caption" color="text.secondary">Document {row.documentId}</Typography>
                )}
              </TableCell>
              <TableCell>{describeExclusionReason(row.reason)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

/* ────────────────────────────────────────────────────────────────────────────
 * "Clear out what produced nothing" — the selection surface.
 *
 * Three rows, a count, a size. That is deliberately the whole thing. Date range is the wrong
 * granularity for this data (the four "Nexora outbound email test" messages arrived the same
 * afternoon as forty real ones, so no cutoff separates them) and per-record ticking across two
 * hundred records is a screen nobody finishes. OUTCOME is the axis a business owner can judge at
 * a glance — and, not coincidentally, the only axis whose deletion cannot break a link, because a
 * record with no downstream artefact has nothing pointing at it.
 *
 * Every word on these rows comes from the server as finished copy. No code, enum, table name or
 * state value is rendered here, and the fallbacks in the service layer are words too: the person
 * reading this owns the business, and must never have to learn our vocabulary to understand his
 * own mail.
 * ──────────────────────────────────────────────────────────────────────────── */

interface BucketRowProps {
  bucket: TenantDataControlSummary['buckets'][number];
  checked: boolean;
  readOnly: boolean;
  onToggle: (code: string, checked: boolean) => void;
}

function BucketRow({ bucket, checked, readOnly, onToggle }: BucketRowProps) {
  const empty = bucket.count === 0;
  return (
    <Paper
      variant="outlined"
      sx={{ p: 2, opacity: bucket.canClear ? 1 : 0.85 }}
    >
      <Stack direction="row" sx={{ gap: 1.5, alignItems: 'flex-start' }}>
        <Checkbox
          checked={checked}
          disabled={readOnly || !bucket.canClear}
          onChange={(event) => onToggle(bucket.code, event.target.checked)}
          slotProps={{ input: { 'aria-label': `Include: ${bucket.title}` } }}
          sx={{ mt: -0.5 }}
        />
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Stack
            direction={{ xs: 'column', sm: 'row' }}
            sx={{ gap: 1, alignItems: { sm: 'baseline' }, justifyContent: 'space-between' }}
          >
            <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 750 }}>
              {bucket.title}
            </Typography>
            <Typography variant="body2" sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}>
              {formatCount(bucket.count)} · {formatBytes(bucket.bytes)}
            </Typography>
          </Stack>
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
            {bucket.detail}
          </Typography>
          {/* A control that cannot work says why, in words, right where it is disabled — never a
              greyed-out box the owner has to raise a ticket to understand. */}
          {!bucket.canClear && bucket.blockedReason && (
            <Typography
              variant="caption"
              color={empty ? 'text.secondary' : 'warning.main'}
              sx={{ display: 'block', mt: 0.75, fontWeight: 600 }}
            >
              {bucket.blockedReason}
            </Typography>
          )}
        </Box>
      </Stack>
    </Paper>
  );
}

/* The reassurance panel. Not an error list — the answer to "what will you never touch?", asked
   before the button is pressed rather than reported after it. */
function KeptPanel({ lines, summary }: { lines: TenantDataControlSummary['kept']; summary: string | null }) {
  const shown = lines.filter((line) => line.count === null || line.count > 0);
  return (
    <Paper variant="outlined" sx={{ p: 2.5 }}>
      <Stack direction="row" sx={{ gap: 1, alignItems: 'center', mb: 1 }}>
        <VerifiedUserOutlined fontSize="small" color="success" />
        <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 750 }}>
          Never deleted, whatever you choose
        </Typography>
      </Stack>
      {summary && (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>{summary}</Typography>
      )}
      {shown.length === 0 ? (
        <Typography variant="body2" color="text.secondary">
          Nothing is being held back right now. When something must be kept, it is listed here with
          the reason.
        </Typography>
      ) : (
        <Box component="ul" sx={{ pl: 0, m: 0, listStyle: 'none' }}>
          {shown.map((line) => (
            <Box
              component="li"
              key={line.title}
              sx={{ display: 'flex', gap: 2, alignItems: 'baseline', py: 0.75,
                borderTop: '1px solid', borderColor: 'divider', '&:first-of-type': { borderTop: 'none' } }}
            >
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>{line.title}</Typography>
                {line.detail && (
                  <Typography variant="caption" color="text.secondary">{line.detail}</Typography>
                )}
              </Box>
              <Typography variant="body2" sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}>
                {formatCount(line.count)}
              </Typography>
            </Box>
          ))}
        </Box>
      )}
    </Paper>
  );
}

/* What the sweep found and deliberately left alone. Reported rather than dropped: a sweep that
   quietly skips what it cannot prove safe is indistinguishable from one that had nothing to do,
   and those bytes are still on the bill. */
function RefusalList({ rows }: { rows: TenantDataCleanupResult['refused'] }) {
  return (
    <Alert severity="warning">
      <AlertTitle sx={{ fontWeight: 800 }}>Left alone on purpose</AlertTitle>
      <Box component="ul" sx={{ pl: 2.5, m: 0, '& li': { mb: 0.5 } }}>
        {rows.map((row, index) => (
          <Typography component="li" variant="body2" key={`${row.what ?? 'item'}-${index}`}>
            <strong>{row.what ?? 'One item'}</strong>
            {row.why ? ` — ${row.why}` : ''}
          </Typography>
        ))}
      </Box>
    </Alert>
  );
}

export default function StorageRetentionPage() {
  const client = useQueryClient();
  const { userData } = useAuth();
  // Stored-file deletion is an irreversible tenant-owner decision. The API remains the authority;
  // this client gate keeps readers from being offered controls their role cannot use.
  const canManageRetention = userData.isSuperAdmin === true;

  const summary = useQuery<EvidenceRetentionSummary>({
    queryKey: ['evidence-retention'],
    queryFn: platformGovernanceService.getEvidenceRetention,
    retry: false,
  });

  const [retentionDays, setRetentionDays] = useState<string>(String(EVIDENCE_RETENTION_DEFAULT_DAYS));
  const [isEnabled, setIsEnabled] = useState(false);
  const [policyReason, setPolicyReason] = useState('');
  const [preview, setPreview] = useState<EvidenceRetentionRunResult | null>(null);
  const [receipt, setReceipt] = useState<EvidenceRetentionRunResult | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  /**
   * The figures the user is being asked to confirm, frozen at the moment the dialog opens. Reading
   * them live from `preview` would let the numbers in an irreversible-action dialog change — or
   * blank out to "Not reported" mid-close, once the preview is cleared on success.
   */
  const [confirmTarget, setConfirmTarget] = useState<
    { documents: number; bytes: number | null; excluded: number | null } | null
  >(null);
  const [confirmText, setConfirmText] = useState('');
  const [purgeReason, setPurgeReason] = useState('');
  const [purgeKey, setPurgeKey] = useState<string | null>(null);

  /* ── "Clear out what produced nothing" ─────────────────────────────────── */
  const tenantData = useQuery<TenantDataControlSummary>({
    queryKey: ['tenant-data-control'],
    queryFn: platformGovernanceService.getTenantDataControl,
    retry: false,
  });
  const [chosen, setChosen] = useState<string[]>([]);
  const [cleanupReason, setCleanupReason] = useState('');
  const [cleanupPreview, setCleanupPreview] = useState<TenantDataCleanupResult | null>(null);
  const [cleanupReceipt, setCleanupReceipt] = useState<TenantDataCleanupResult | null>(null);
  const [cleanupConfirmOpen, setCleanupConfirmOpen] = useState(false);
  const [cleanupConfirmText, setCleanupConfirmText] = useState('');
  const [cleanupKey, setCleanupKey] = useState<string | null>(null);
  /** Frozen when the dialog opens: the figures in an irreversible dialog must not move. */
  const [cleanupTarget, setCleanupTarget] = useState<
    { messages: number | null; files: number | null; bytes: number | null } | null
  >(null);

  // The saved policy is the source of truth for the draft; a concurrent save elsewhere reloads it.
  useEffect(() => {
    const policy = summary.data?.policy;
    if (!policy) return;
    setRetentionDays(String(policy.retentionDays ?? EVIDENCE_RETENTION_DEFAULT_DAYS));
    setIsEnabled(policy.isEnabled ?? false);
  }, [summary.data]);

  // The server owns the bounds; the local constants are only the fallback when it does not say.
  const minDays = summary.data?.policy.minimumRetentionDays ?? EVIDENCE_RETENTION_MIN_DAYS;
  const maxDays = summary.data?.policy.maximumRetentionDays ?? EVIDENCE_RETENTION_MAX_DAYS;
  const parsedDays = Number(retentionDays);
  const daysError = !Number.isInteger(parsedDays) || parsedDays < minDays || parsedDays > maxDays
    ? `Enter a whole number of days between ${minDays} and ${maxDays}.`
    : null;

  const savePolicy = useMutation({
    mutationFn: () => platformGovernanceService.updateEvidenceRetentionPolicy({
      retentionDays: parsedDays,
      isEnabled,
      reason: policyReason.trim(),
    }),
    onSuccess: async () => {
      setPolicyReason('');
      // A new policy changes what is eligible; an estimate taken under the old one is stale.
      setPreview(null);
      await client.invalidateQueries({ queryKey: ['evidence-retention'] });
    },
  });

  const dryRun = useMutation({
    mutationFn: () => platformGovernanceService.runEvidenceRetentionPurge({
      dryRun: true,
      reason: purgeReason.trim() || 'Tenant preview of reclaimable storage.',
      idempotencyKey: newIdempotencyKey(),
    }),
    onSuccess: (result) => {
      setPreview(result);
      setReceipt(null);
    },
  });

  const executePurge = useMutation({
    mutationFn: () => platformGovernanceService.runEvidenceRetentionPurge({
      dryRun: false,
      reason: purgeReason.trim(),
      previewToken: preview?.previewToken ?? undefined,
      // Reused across retries of THIS confirmed purge so a lost response cannot delete twice.
      idempotencyKey: purgeKey ?? newIdempotencyKey(),
    }),
    onSuccess: async (result) => {
      setReceipt(result);
      setPreview(null);
      setConfirmOpen(false);
      setConfirmText('');
      setPurgeKey(null);
      setPurgeReason('');
      await client.invalidateQueries({ queryKey: ['evidence-retention'] });
    },
  });

  const toggleBucket = (code: string, include: boolean) => {
    setChosen((current) => (include
      ? current.includes(code) ? current : [...current, code]
      : current.filter((x) => x !== code)));
    // A preview taken over a different selection is not this selection's preview.
    setCleanupPreview(null);
  };

  const cleanupDryRun = useMutation({
    mutationFn: () => platformGovernanceService.runTenantDataCleanup({
      buckets: chosen,
      dryRun: true,
      reason: cleanupReason.trim() || 'Preview of what would be cleared.',
      idempotencyKey: newIdempotencyKey(),
    }),
    onSuccess: (result) => {
      setCleanupPreview(result);
      setCleanupReceipt(null);
    },
  });

  const runCleanup = useMutation({
    mutationFn: () => platformGovernanceService.runTenantDataCleanup({
      buckets: chosen,
      dryRun: false,
      reason: cleanupReason.trim(),
      confirmation: CONFIRM_PHRASE,
      // Reused across retries of THIS confirmed run so a lost response cannot delete twice.
      idempotencyKey: cleanupKey ?? newIdempotencyKey(),
    }),
    onSuccess: async (result) => {
      setCleanupReceipt(result);
      setCleanupPreview(null);
      setCleanupConfirmOpen(false);
      setCleanupConfirmText('');
      setCleanupKey(null);
      setCleanupReason('');
      setChosen([]);
      await Promise.all([
        client.invalidateQueries({ queryKey: ['tenant-data-control'] }),
        client.invalidateQueries({ queryKey: ['evidence-retention'] }),
      ]);
    },
  });

  const buckets = tenantData.data?.buckets ?? [];
  const cleanupReasonGiven = cleanupReason.trim().length > 0;
  const cleanupPreviewShown = cleanupPreview !== null;
  const cleanupWouldRemove = (cleanupPreview?.messagesCleared ?? 0) + (cleanupPreview?.filesDeleted ?? 0);
  const canRunCleanup = canManageRetention && cleanupPreviewShown && cleanupWouldRemove > 0
    && cleanupReasonGiven && chosen.length > 0;
  const cleanupPhraseTyped = cleanupConfirmText.trim() === CONFIRM_PHRASE;

  const openCleanupConfirm = () => {
    setCleanupTarget({
      messages: cleanupPreview?.messagesCleared ?? null,
      files: cleanupPreview?.filesDeleted ?? null,
      bytes: cleanupPreview?.bytesReclaimed ?? null,
    });
    setCleanupKey(newIdempotencyKey());
    setCleanupConfirmText('');
    setCleanupConfirmOpen(true);
  };
  const closeCleanupConfirm = () => {
    if (runCleanup.isPending) return;
    setCleanupConfirmOpen(false);
    setCleanupConfirmText('');
    setCleanupKey(null);
    runCleanup.reset();
  };

  const eligible = preview?.eligible ?? null;
  const previewShown = preview !== null;
  const hasSomethingToDelete = previewShown && eligible !== null && eligible > 0;
  const confirmPhraseTyped = confirmText.trim() === CONFIRM_PHRASE;
  const purgeReasonGiven = purgeReason.trim().length > 0;
  /** The saved switch records consent for a manually confirmed purge; it starts no scheduler. */
  const policyOptedIn = summary.data?.policy.isEnabled === true;
  const hasSignedPreview = preview?.previewToken != null;
  const canDelete = canManageRetention && hasSomethingToDelete && hasSignedPreview
    && purgeReasonGiven && policyOptedIn;

  const excluded = useMemo(() => preview?.skipped ?? [], [preview]);

  const openConfirm = () => {
    if (eligible === null) return;
    setConfirmTarget({
      documents: eligible,
      bytes: preview?.bytesReclaimed ?? null,
      excluded: preview?.skippedReported ? excluded.length : null,
    });
    setPurgeKey(newIdempotencyKey());
    setConfirmText('');
    setConfirmOpen(true);
  };
  const closeConfirm = () => {
    if (executePurge.isPending) return;
    setConfirmOpen(false);
    setConfirmText('');
    setPurgeKey(null);
    executePurge.reset();
  };

  if (summary.isLoading) {
    return (
      <Box sx={{ maxWidth: 1200, mx: 'auto', p: { xs: 2, md: 3 } }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800, mb: 2 }}>Storage &amp; Retention</Typography>
        <LoadingState label="Loading your storage figures…" />
      </Box>
    );
  }

  if (summary.isError) {
    const unavailable = isEvidenceRetentionUnavailable(summary.error);
    return (
      <Box sx={{ maxWidth: 1200, mx: 'auto', p: { xs: 2, md: 3 } }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800, mb: 2 }}>Storage &amp; Retention</Typography>
        {unavailable ? (
          <EmptyState
            title="Storage controls are not available on this deployment yet"
            message="Nothing is being deleted, and nothing is at risk. Storage retention needs the matching server release before figures can be shown or files can be reclaimed."
            icon={<InventoryOutlined sx={{ fontSize: 44 }} />}
            action={<Button variant="outlined" onClick={() => void summary.refetch()}>Check again</Button>}
          />
        ) : (
          <ErrorState
            message={toPresentableError(summary.error, {
              fallbackMessage: 'Your storage figures could not be loaded. Nothing was changed or deleted.',
            }).message}
            onRetry={() => void summary.refetch()}
          />
        )}
      </Box>
    );
  }

  const storage = summary.data?.storage;
  const policy = summary.data?.policy;

  return (
    <Box sx={{ maxWidth: 1200, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Box sx={{ mb: 3 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800 }}>Storage &amp; Retention</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 760, mt: 0.5 }}>
          See how much space your uploaded documents use, decide how long Nexora keeps the original
          files, and reclaim space once the details have been extracted from them.
        </Typography>
      </Box>

      {!canManageRetention && (
        <Alert severity="info" sx={{ mb: 3 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Read-only storage view</AlertTitle>
          You can review storage usage, retention settings and protected records. Only a tenant
          super administrator can change the policy, run deletion previews or permanently remove
          stored files.
        </Alert>
      )}

      {/* ── Storage figures ─────────────────────────────────────────────────── */}
      <Box component="section" aria-labelledby="storage-usage-heading" sx={{ mb: 3 }}>
        <Typography id="storage-usage-heading" variant="h6" component="h2" sx={{ fontWeight: 750, mb: 1.5 }}>
          What your documents are using
        </Typography>
        <Box
          sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr', lg: 'repeat(4, 1fr)' }, gap: 2 }}
        >
          <Tile
            icon={<InventoryOutlined fontSize="small" />}
            label="Stored files"
            value={formatBytes(storage?.usedBytes)}
            caption="Space taken by the original uploaded documents."
          />
          <Tile
            icon={<HistoryToggleOffOutlined fontSize="small" />}
            label="Reclaimable now"
            value={formatBytes(storage?.reclaimableBytes)}
            caption={storage?.reclaimableDocumentCount != null
              ? `Across ${formatCount(storage.reclaimableDocumentCount)} documents under today's policy.`
              : "What today's policy would free. Preview before deleting."}
          />
          <Tile
            icon={<FactCheckOutlined fontSize="small" />}
            label="Documents held"
            value={formatCount(storage?.documentCount)}
            caption="Documents whose original file is still stored."
          />
          <Tile
            icon={<ShieldOutlined fontSize="small" />}
            label="Already reclaimed"
            value={formatCount(storage?.purgedCount)}
            caption="Files deleted previously. Their records and lineage remain."
          />
        </Box>
        {summary.data && summary.data.missingFields.length > 0 && (
          <Alert severity="info" sx={{ mt: 2 }}>
            Some figures are shown as “{NOT_REPORTED}” because this deployment did not return them.
            They are not zero — they are unknown, and the preview below is the reliable number.
          </Alert>
        )}
      </Box>

      {/* ── Clear out what produced nothing ─────────────────────────────────── */}
      <Box component="section" aria-labelledby="clear-nothing-heading" sx={{ mb: 3 }}>
        <Typography id="clear-nothing-heading" variant="h6" component="h2" sx={{ fontWeight: 750, mb: 0.5 }}>
          Clear out what produced nothing
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 760, mb: 1.5 }}>
          Mail and files that never turned into anything — no inquiry, no lead, no document. Tick
          what you want gone, preview it, then decide. Nothing here touches your invoices, orders
          or live deals.
        </Typography>

        {tenantData.isLoading && <LoadingState label="Working out what can be cleared…" />}

        {tenantData.isError && (
          isEvidenceRetentionUnavailable(tenantData.error) ? (
            <Alert severity="info">
              This part of the screen needs a newer server release. Nothing is being deleted and
              nothing is at risk — the retention policy below still works.
            </Alert>
          ) : (
            <ErrorState
              message={toPresentableError(tenantData.error, {
                fallbackMessage: 'We could not work out what can be cleared. Nothing was changed or deleted.',
              }).message}
              onRetry={() => void tenantData.refetch()}
            />
          )
        )}

        {tenantData.isSuccess && !tenantData.data.bucketsReported && (
          <Alert severity="warning">
            This deployment did not report what can be cleared, so nothing is offered here. That is
            not the same as "you have nothing to clear".
          </Alert>
        )}

        {tenantData.isSuccess && tenantData.data.bucketsReported && (
          <Stack sx={{ gap: 2 }}>
            <Stack sx={{ gap: 1.5 }}>
              {buckets.map((bucket) => (
                <BucketRow
                  key={bucket.code}
                  bucket={bucket}
                  checked={chosen.includes(bucket.code)}
                  readOnly={!canManageRetention}
                  onToggle={toggleBucket}
                />
              ))}
              {buckets.length === 0 && (
                <Paper variant="outlined" sx={{ p: 3, textAlign: 'center' }}>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>
                    There is nothing to clear right now
                  </Typography>
                  <Typography variant="body2" color="text.secondary">
                    Every message and file you hold is attached to something. Check back later.
                  </Typography>
                </Paper>
              )}
            </Stack>

            <KeptPanel lines={tenantData.data.kept} summary={tenantData.data.keptSummary} />

            <TextField
              label="Reason for clearing this"
              value={cleanupReason}
              onChange={(event) => setCleanupReason(event.target.value)}
              multiline
              minRows={2}
              required
              helperText="Required before anything is deleted. Recorded permanently in your audit trail."
              disabled={!canManageRetention}
              sx={{ maxWidth: 560 }}
            />

            <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 1.5 }}>
              <Button
                variant="contained"
                startIcon={<FactCheckOutlined />}
                disabled={!canManageRetention || chosen.length === 0 || cleanupDryRun.isPending}
                onClick={() => cleanupDryRun.mutate()}
              >
                {cleanupDryRun.isPending ? 'Checking…' : 'Preview what would be removed'}
              </Button>
              <Button
                variant="outlined"
                color="error"
                startIcon={<DeleteForeverOutlined />}
                disabled={!canRunCleanup}
                onClick={openCleanupConfirm}
              >
                Remove them permanently
              </Button>
            </Stack>
            {chosen.length === 0 && (
              <Typography variant="caption" color="text.secondary">
                Tick at least one group above to preview it.
              </Typography>
            )}
            {chosen.length > 0 && !cleanupPreviewShown && (
              <Typography variant="caption" color="text.secondary">
                Removing stays switched off until you have previewed it.
              </Typography>
            )}
            {cleanupPreviewShown && cleanupWouldRemove > 0 && !cleanupReasonGiven && (
              <Typography variant="caption" color="text.secondary">
                Enter a reason to switch on permanent removal.
              </Typography>
            )}

            {cleanupDryRun.isError && (
              <Alert severity="error">
                {toPresentableError(cleanupDryRun.error, {
                  fallbackMessage: 'The preview could not be produced. Nothing was deleted.',
                }).message}
              </Alert>
            )}

            {/* Numbers first, detail on demand. */}
            {cleanupPreview && (
              <Box role="status" aria-live="polite">
                <Divider sx={{ mb: 2 }} />
                <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 750, mb: 1 }}>
                  Preview — nothing has been deleted
                </Typography>
                <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap', mb: 1.5 }}>
                  <Chip color="error" variant="outlined"
                    label={`${formatCount(cleanupPreview.messagesCleared)} messages would be cleared`} />
                  <Chip color="error" variant="outlined"
                    label={`${formatCount(cleanupPreview.filesDeleted)} leftover files would be deleted`} />
                  <Chip variant="outlined"
                    label={`${formatBytes(cleanupPreview.bytesReclaimed)} would be freed`} />
                </Stack>
                {cleanupWouldRemove === 0 && (
                  <Alert severity="info" sx={{ mb: 1.5 }}>
                    Nothing in what you ticked can be removed right now.
                  </Alert>
                )}
                {cleanupPreview.refused.length > 0 && <RefusalList rows={cleanupPreview.refused} />}
              </Box>
            )}

            {cleanupReceipt && (
              <Box role="status" aria-live="polite">
                <Alert severity="success">
                  <AlertTitle sx={{ fontWeight: 800 }}>
                    {cleanupReceipt.idempotentReplay ? 'Already done' : 'Cleared'}
                  </AlertTitle>
                  {cleanupReceipt.idempotentReplay
                    ? 'This exact request had already been carried out, so nothing further was deleted.'
                    : 'The records that these arrived — who sent them and when — are kept in full.'}
                  <Typography variant="body2" sx={{ mt: 1, fontWeight: 700 }}>
                    {formatCount(cleanupReceipt.messagesCleared)} messages ·{' '}
                    {formatCount(cleanupReceipt.filesDeleted)} files ·{' '}
                    {formatBytes(cleanupReceipt.bytesReclaimed)} freed
                  </Typography>
                </Alert>
                {cleanupReceipt.refused.length > 0 && (
                  <Box sx={{ mt: 1.5 }}><RefusalList rows={cleanupReceipt.refused} /></Box>
                )}
              </Box>
            )}
          </Stack>
        )}
      </Box>

      {/* ── What a purge does / does not do ─────────────────────────────────── */}
      <Box component="section" aria-labelledby="what-purging-heading" sx={{ mb: 3 }}>
        <Typography id="what-purging-heading" variant="h6" component="h2" sx={{ fontWeight: 750, mb: 1.5 }}>
          What reclaiming space removes — and what it never touches
        </Typography>
        <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', md: '1fr 1fr' }, gap: 2 }}>
          <Paper variant="outlined" sx={{ p: 2.5 }}>
            <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 750, mb: 1 }}>
              Deleted permanently
            </Typography>
            <Box component="ul" sx={{ pl: 2.5, m: 0, '& li': { mb: 0.75 } }}>
              <Typography component="li" variant="body2">
                The original uploaded file — the PDF, spreadsheet or email attachment itself.
              </Typography>
              <Typography component="li" variant="body2">
                You will no longer be able to open, download, re-read or re-extract it, and you
                cannot produce the original to a customer, auditor or court.
              </Typography>
              <Typography component="li" variant="body2">
                Nexora keeps no backup of these files. Deletion cannot be undone.
              </Typography>
            </Box>
          </Paper>
          <Paper variant="outlined" sx={{ p: 2.5 }}>
            <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 750, mb: 1 }}>
              Kept, permanently
            </Typography>
            <Box component="ul" sx={{ pl: 2.5, m: 0, '& li': { mb: 0.75 } }}>
              <Typography component="li" variant="body2">
                The document record: filename, size, upload date and its SHA-256 fingerprint.
              </Typography>
              <Typography component="li" variant="body2">
                Everything extracted from it — every field, its confidence, its page and position.
              </Typography>
              <Typography component="li" variant="body2">
                The linked lead, its line items, and every RFQ, quote and order that followed.
              </Typography>
              <Typography component="li" variant="body2">
                Your audit trail. If the same file is ever sent to you again, Nexora can still prove
                it is the same file.
              </Typography>
            </Box>
          </Paper>
        </Box>
        <Alert severity="warning" sx={{ mt: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>This does not erase personal data</AlertTitle>
          Buyer names and email addresses read out of these documents were copied into your leads
          and extraction records during processing. Deleting the original files leaves those copies
          in place. To remove a specific person's details, edit or delete the lead that holds them —
          this control is about storage, not erasure.
        </Alert>
      </Box>

      {/* ── Retention policy ────────────────────────────────────────────────── */}
      <Box component="section" aria-labelledby="retention-policy-heading" sx={{ mb: 3 }}>
        <Typography id="retention-policy-heading" variant="h6" component="h2" sx={{ fontWeight: 750, mb: 1.5 }}>
          Retention policy
        </Typography>
        <Paper variant="outlined" sx={{ p: 2.5 }}>
          <Stack sx={{ gap: 2.5 }}>
            <FormControlLabel
              control={(
                <Switch
                  checked={isEnabled}
                  onChange={(_event, checked) => setIsEnabled(checked)}
                  disabled={!canManageRetention}
                  slotProps={{ input: { 'aria-describedby': 'retention-enabled-help' } }}
                />
              )}
              label="Allow permanent deletion once files pass the retention period"
            />
            <Typography id="retention-enabled-help" variant="body2" color="text.secondary" sx={{ mt: -1.5, ml: 6 }}>
              Off by default. Turning this on records consent and enables a super administrator to
              run a separate preview and confirmed deletion below. It does not start an automatic
              deletion schedule in this build.
            </Typography>

            <TextField
              label="Keep original files for (days after extraction finishes)"
              type="number"
              value={retentionDays}
              onChange={(event) => setRetentionDays(event.target.value)}
              disabled={!canManageRetention}
              error={daysError !== null}
              helperText={daysError
                ?? `${EVIDENCE_RETENTION_DEFAULT_DAYS} days is the compliance-approved default: long enough to re-read or re-extract a document during a dispute, short enough to satisfy UAE and KSA data-protection rules that personal data is not kept longer than it is needed.`}
              slotProps={{
                htmlInput: {
                  min: minDays,
                  max: maxDays,
                  step: 1,
                },
              }}
              sx={{ maxWidth: 560 }}
            />

            <TextField
              label="Reason for this change"
              value={policyReason}
              onChange={(event) => setPolicyReason(event.target.value)}
              multiline
              minRows={2}
              required
              helperText="Recorded in your audit trail alongside who changed it and when."
              disabled={!canManageRetention}
              sx={{ maxWidth: 560 }}
            />

            {savePolicy.isError && (
              <Alert severity="error">
                {toPresentableError(savePolicy.error, {
                  fallbackMessage: 'The retention policy was not saved. Nothing was changed or deleted.',
                }).message}
              </Alert>
            )}
            {savePolicy.isSuccess && (
              <Alert severity="success" role="status">
                Retention policy saved. Any earlier preview was discarded — run a new one before deleting.
              </Alert>
            )}

            <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 1.5, alignItems: { sm: 'center' } }}>
              <Button
                variant="contained"
                startIcon={<SaveOutlined />}
                disabled={!canManageRetention || daysError !== null || !policyReason.trim() || savePolicy.isPending}
                onClick={() => savePolicy.mutate()}
              >
                {savePolicy.isPending ? 'Saving…' : 'Save retention policy'}
              </Button>
              {policy?.updatedOn && (
                <Typography variant="caption" color="text.secondary">
                  Last changed {new Date(policy.updatedOn).toLocaleString()}
                  {policy.version != null ? ` · version ${policy.version}` : ''}
                </Typography>
              )}
            </Stack>
          </Stack>
        </Paper>
      </Box>

      {/* ── Reclaim space ───────────────────────────────────────────────────── */}
      <Box component="section" aria-labelledby="reclaim-heading" sx={{ mb: 2 }}>
        <Typography id="reclaim-heading" variant="h6" component="h2" sx={{ fontWeight: 750, mb: 0.5 }}>
          Delete older documents you no longer need to keep
        </Typography>
        <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 760, mb: 1.5 }}>
          This one is about age: it deletes the original files of documents that have passed the
          retention period you set above. Use the section further up instead if you just want to
          clear out mail and files that never turned into anything.
        </Typography>
        <Paper variant="outlined" sx={{ p: 2.5 }}>
          <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: 760 }}>
            Always start with a preview. It scans your documents against the retention policy and
            reports exactly how many files would be deleted, how much space that frees, and which
            documents are held back and why. Nothing is deleted by a preview.
          </Typography>

          <TextField
            label="Reason for reclaiming space"
            value={purgeReason}
            onChange={(event) => setPurgeReason(event.target.value)}
            multiline
            minRows={2}
            required
            helperText="Required before anything is deleted. Recorded permanently in your audit trail."
            disabled={!canManageRetention}
            sx={{ maxWidth: 560, mb: 2 }}
          />

          <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 1.5 }}>
            <Button
              variant="contained"
              startIcon={<FactCheckOutlined />}
              disabled={!canManageRetention || dryRun.isPending}
              onClick={() => dryRun.mutate()}
            >
              {dryRun.isPending ? 'Checking…' : 'Preview what would be deleted'}
            </Button>
            <Button
              variant="outlined"
              color="error"
              startIcon={<DeleteForeverOutlined />}
              disabled={!canDelete}
              onClick={openConfirm}
            >
              Delete stored files permanently
            </Button>
          </Stack>
          {!previewShown && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Deleting stays disabled until a preview has been run.
            </Typography>
          )}
          {previewShown && !hasSignedPreview && (
            <Alert severity="warning" sx={{ mt: 2 }}>
              This server did not sign the preview, so permanent deletion remains disabled. Nothing
              is at risk. Deploy the matching retention backend and run the preview again.
            </Alert>
          )}
          {previewShown && !purgeReasonGiven && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Enter a reason to enable permanent deletion.
            </Typography>
          )}
          {previewShown && !policyOptedIn && (
            <Alert severity="info" sx={{ mt: 2 }}>
              Permanent deletion is opt-in. Turn on “Allow permanent deletion” above and save the
              policy first — that saved policy is your recorded consent to irreversible deletion.
              Previews stay available either way.
            </Alert>
          )}

          {dryRun.isError && (
            <Alert severity="error" sx={{ mt: 2 }}>
              {toPresentableError(dryRun.error, {
                fallbackMessage: 'The preview could not be produced. Nothing was deleted.',
              }).message}
            </Alert>
          )}

          {/* The preview result. Announced politely — it is the number the user acts on. */}
          {preview && (
            <Box role="status" aria-live="polite" sx={{ mt: 2.5 }}>
              <Divider sx={{ mb: 2 }} />
              <Typography variant="subtitle1" component="h3" sx={{ fontWeight: 750, mb: 1.5 }}>
                Preview — nothing has been deleted
              </Typography>
              <Stack direction="row" sx={{ gap: 1, flexWrap: 'wrap', mb: 2 }}>
                <Chip color="error" variant="outlined" label={`${formatCount(preview.eligible)} documents would be deleted`} />
                <Chip variant="outlined" label={`${formatBytes(preview.bytesReclaimed)} would be freed`} />
                <Chip variant="outlined" label={`${formatCount(preview.scanned)} documents checked`} />
                <Chip variant="outlined" label={preview.skippedReported
                  ? `${formatCount(excluded.length)} kept back`
                  : 'Exclusions not reported'} />
              </Stack>
              {preview.previewExpiresOn && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
                  This protected preview expires at {new Date(preview.previewExpiresOn).toLocaleString()}.
                  If it expires or any protection status changes, Nexora deletes nothing and asks
                  you to preview again.
                </Typography>
              )}

              {eligible === 0 && (
                <Alert severity="info" sx={{ mb: 2 }}>
                  No document is currently eligible. Every stored file is either still inside the
                  retention period or held back for one of the reasons below.
                </Alert>
              )}

              {/* The server's own wording, rendered verbatim: one source of truth for the claim. */}
              {preview.disclosure && (
                <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
                  {preview.disclosure}
                </Typography>
              )}

              <Typography variant="subtitle2" component="h4" sx={{ fontWeight: 750, mb: 1 }}>
                Kept back from this purge
              </Typography>
              {!preview.skippedReported ? (
                <Alert severity="warning">
                  This deployment did not report which documents were excluded, so the list below
                  cannot be shown. The count above is the only figure to rely on.
                </Alert>
              ) : excluded.length === 0 ? (
                <Typography variant="body2" color="text.secondary">
                  Nothing was held back — every document scanned is eligible under your policy.
                </Typography>
              ) : (
                <ExclusionTable rows={excluded} />
              )}
            </Box>
          )}

          {receipt && (
            <Box role="status" aria-live="polite" sx={{ mt: 2.5 }}>
              <Alert severity="success">
                <AlertTitle sx={{ fontWeight: 800 }}>
                  {receipt.idempotentReplay ? 'Already completed' : 'Stored files deleted'}
                </AlertTitle>
                {receipt.idempotentReplay
                  ? 'This exact request had already been carried out, so nothing further was deleted. The figures below are from the original run.'
                  : 'Their records, fingerprints and everything extracted from them remain in full.'}
                <Typography variant="body2" sx={{ mt: 1, fontWeight: 700 }}>
                  {formatCount(receipt.purged)} documents · {formatBytes(receipt.bytesReclaimed)} freed
                  {receipt.legacyCopiesDeleted != null && receipt.legacyCopiesDeleted > 0
                    ? ` · ${formatCount(receipt.legacyCopiesDeleted)} older duplicate copies also removed`
                    : ''}
                </Typography>
                {receipt.disclosure && (
                  <Typography variant="body2" sx={{ mt: 1 }}>{receipt.disclosure}</Typography>
                )}
              </Alert>
              {receipt.legacyCopiesUnresolved != null && receipt.legacyCopiesUnresolved > 0 && (
                <Alert severity="warning" sx={{ mt: 1.5 }}>
                  {formatCount(receipt.legacyCopiesUnresolved)} older copies of these files could not
                  be matched with certainty and were deliberately left in place rather than deleted
                  on a guess. They still take up space — report this to support so they can be
                  reconciled.
                </Alert>
              )}
            </Box>
          )}
        </Paper>
      </Box>

      {/* ── Clear-out confirmation ──────────────────────────────────────────── */}
      <Dialog
        open={cleanupConfirmOpen}
        onClose={closeCleanupConfirm}
        fullWidth
        maxWidth="sm"
        aria-labelledby="confirm-cleanup-title"
        aria-describedby="confirm-cleanup-description"
      >
        <DialogTitle id="confirm-cleanup-title" sx={{ fontWeight: 800 }}>
          Remove {formatCount(cleanupTarget?.messages)} messages and {formatCount(cleanupTarget?.files)} files
        </DialogTitle>
        <DialogContent>
          <DialogContentText id="confirm-cleanup-description" component="div">
            <Typography variant="body2" sx={{ fontWeight: 700, mb: 1.5 }}>
              This frees {formatBytes(cleanupTarget?.bytes)}. It cannot be undone — Nexora keeps no
              backup of these.
            </Typography>
            <Typography variant="body2" sx={{ mb: 1.5 }}>
              <strong>What goes:</strong> the stored copy of each message, and leftover files
              nothing points to. You will no longer be able to open or re-read the original message.
            </Typography>
            <Typography variant="body2" sx={{ mb: 1.5 }}>
              <strong>What stays:</strong> the record that each message arrived — who sent it, when,
              what the subject was, and what we decided about it. Nothing on an invoice, order,
              live deal or legal hold is included.
            </Typography>
          </DialogContentText>

          {runCleanup.isError && (
            <Alert severity="error" sx={{ mb: 2 }}>
              {toPresentableError(runCleanup.error, {
                fallbackMessage: 'The removal did not complete. Preview again to see where things stand before retrying.',
              }).message}
            </Alert>
          )}

          <TextField
            label={`Type ${CONFIRM_PHRASE} to confirm`}
            value={cleanupConfirmText}
            onChange={(event) => setCleanupConfirmText(event.target.value)}
            fullWidth
            autoComplete="off"
            helperText={`Type ${CONFIRM_PHRASE} in capitals. This is the last step.`}
          />
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={closeCleanupConfirm} disabled={runCleanup.isPending}>Keep everything</Button>
          <Button
            variant="contained"
            color="error"
            startIcon={<DeleteForeverOutlined />}
            disabled={!cleanupPhraseTyped || !canRunCleanup || runCleanup.isPending}
            onClick={() => runCleanup.mutate()}
          >
            {runCleanup.isPending ? 'Removing…' : 'Remove them'}
          </Button>
        </DialogActions>
      </Dialog>

      {/* ── Irreversible-action confirmation ────────────────────────────────── */}
      <Dialog
        open={confirmOpen}
        onClose={closeConfirm}
        fullWidth
        maxWidth="sm"
        aria-labelledby="confirm-purge-title"
        aria-describedby="confirm-purge-description"
      >
        <DialogTitle id="confirm-purge-title" sx={{ fontWeight: 800 }}>
          Delete {formatCount(confirmTarget?.documents)} stored documents permanently
        </DialogTitle>
        <DialogContent>
          <DialogContentText id="confirm-purge-description" component="div">
            <Typography variant="body2" sx={{ fontWeight: 700, mb: 1.5 }}>
              This frees {formatBytes(confirmTarget?.bytes)}. This cannot be undone.
              Nexora keeps no backup of these files.
            </Typography>
            <Typography variant="body2" sx={{ mb: 1.5 }}>
              <strong>What is deleted:</strong> the original uploaded files. You will no longer be
              able to open, download, re-read or re-extract them, and you cannot produce the
              original to a customer, auditor or court.
            </Typography>
            <Typography variant="body2" sx={{ mb: 1.5 }}>
              <strong>What is kept forever:</strong> the document record — filename, size, SHA-256
              fingerprint and upload date — and everything extracted from it: every field, its
              confidence, its page and position, and the linked lead, RFQ, quote and order. Your
              audit trail stays complete.
            </Typography>
            <Typography variant="body2" sx={{ mb: 1.5 }}>
              <strong>What is excluded:</strong>{' '}
              {confirmTarget?.excluded != null
                ? `${formatCount(confirmTarget.excluded)} documents are held back — under legal hold, still in review, on open commercial cases, or classified as invoices, purchase orders or contracts. They are listed on the page behind this dialog.`
                : 'documents under legal hold, still in review, on open commercial cases, or classified as invoices, purchase orders or contracts are never included.'}
            </Typography>
          </DialogContentText>

          <Alert severity="warning" sx={{ mb: 2 }}>
            <AlertTitle sx={{ fontWeight: 800 }}>This does not erase personal data</AlertTitle>
            Buyer names and email addresses extracted from these documents remain in your leads and
            evidence records. To remove a specific person's details, edit or delete the lead that
            holds them.
          </Alert>

          {executePurge.isError && (
            <Alert severity="error" sx={{ mb: 2 }}>
              {toPresentableError(executePurge.error, {
                fallbackMessage: 'The deletion did not complete. Re-run the preview to see the current position before trying again.',
              }).message}
            </Alert>
          )}

          <TextField
            label={`Type ${CONFIRM_PHRASE} to confirm`}
            value={confirmText}
            onChange={(event) => setConfirmText(event.target.value)}
            fullWidth
            autoComplete="off"
            helperText={`Type ${CONFIRM_PHRASE} in capitals. This is the last step before the files are gone.`}
          />
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={closeConfirm} disabled={executePurge.isPending}>Keep my files</Button>
          <Button
            variant="contained"
            color="error"
            startIcon={<DeleteForeverOutlined />}
            disabled={!confirmPhraseTyped || !canDelete || executePurge.isPending}
            onClick={() => executePurge.mutate()}
          >
            {executePurge.isPending ? 'Deleting…' : `Delete ${formatCount(confirmTarget?.documents)} documents`}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
