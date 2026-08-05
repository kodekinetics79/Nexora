import { useEffect, useMemo, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, AlertTitle, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogContentText,
  DialogTitle, Divider, FormControlLabel, Paper, Stack, Switch, Table, TableBody, TableCell,
  TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import {
  DeleteForeverOutlined, FactCheckOutlined, HistoryToggleOffOutlined, InventoryOutlined,
  SaveOutlined, ShieldOutlined,
} from '@mui/icons-material';
import {
  EVIDENCE_RETENTION_DEFAULT_DAYS, EVIDENCE_RETENTION_MAX_DAYS, EVIDENCE_RETENTION_MIN_DAYS,
  isEvidenceRetentionUnavailable, newIdempotencyKey, platformGovernanceService,
  type EvidenceRetentionExclusion, type EvidenceRetentionRunResult, type EvidenceRetentionSummary,
} from '../../api/services/platformGovernanceService';
import { EmptyState, ErrorState, LoadingState } from '../../platform/components/States';
import { looksLikeTechnicalNoise, toPresentableError } from '../../utils/apiErrors';

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
  DELETION_REVIEW_PENDING: 'A deletion review is open and has not been decided.',
  DELETION_REQUESTED: 'A deletion review is open and has not been decided.',
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

const CONFIRM_PHRASE = 'DELETE';

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

export default function StorageRetentionPage() {
  const client = useQueryClient();

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

  const eligible = preview?.eligible ?? null;
  const previewShown = preview !== null;
  const hasSomethingToDelete = previewShown && eligible !== null && eligible > 0;
  const confirmPhraseTyped = confirmText.trim() === CONFIRM_PHRASE;
  const purgeReasonGiven = purgeReason.trim().length > 0;
  /**
   * The server refuses a real purge until a policy has been SAVED with automatic deletion on —
   * irreversible deletion is opt-in twice over. Blocking here turns a guaranteed 409 into an
   * instruction the user can act on.
   */
  const policyOptedIn = summary.data?.policy.isEnabled === true;
  const canDelete = hasSomethingToDelete && purgeReasonGiven && policyOptedIn;

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
          in place. To erase personal data, raise a Data Subject Request instead — this control is
          about storage, not erasure.
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
                  slotProps={{ input: { 'aria-describedby': 'retention-enabled-help' } }}
                />
              )}
              label="Delete stored files automatically once they pass the retention period"
            />
            <Typography id="retention-enabled-help" variant="body2" color="text.secondary" sx={{ mt: -1.5, ml: 6 }}>
              Off by default. While this is off, nothing is deleted at all — not on a schedule and
              not by hand. Previews still work. Turning this on and saving is the recorded consent
              that unlocks permanent deletion, so leave it off until you mean it.
            </Typography>

            <TextField
              label="Keep original files for (days after extraction finishes)"
              type="number"
              value={retentionDays}
              onChange={(event) => setRetentionDays(event.target.value)}
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
                disabled={daysError !== null || !policyReason.trim() || savePolicy.isPending}
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
        <Typography id="reclaim-heading" variant="h6" component="h2" sx={{ fontWeight: 750, mb: 1.5 }}>
          Reclaim space now
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
            sx={{ maxWidth: 560, mb: 2 }}
          />

          <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ gap: 1.5 }}>
            <Button
              variant="contained"
              startIcon={<FactCheckOutlined />}
              disabled={dryRun.isPending}
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
          {previewShown && !purgeReasonGiven && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Enter a reason to enable permanent deletion.
            </Typography>
          )}
          {previewShown && !policyOptedIn && (
            <Alert severity="info" sx={{ mt: 2 }}>
              Permanent deletion is opt-in. Turn on “Delete stored files automatically” above and
              save the policy first — that saved policy is your recorded consent to irreversible
              deletion. Previews stay available either way.
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
            evidence records. To erase those, use a Data Subject Request.
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
