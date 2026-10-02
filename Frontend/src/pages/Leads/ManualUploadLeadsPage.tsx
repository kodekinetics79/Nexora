import React, { useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box, Typography, Paper, Button, Stack,
  CircularProgress, Alert, AlertTitle, IconButton, List,
  ListItem, ListItemIcon, ListItemText,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  CloudOff as OfflineIcon,
  Delete as DeleteIcon,
  Description as DocIcon,
  OpenInNew as OpenIcon,
  UploadFileOutlined as UploadIcon,
} from '@mui/icons-material';
import leadService, {
  readUploadPausedProblem,
  type GovernedUploadJobDTO,
} from '../../api/services/leadService';
import { useAuth } from '../../context/AuthContext';
import { useSnackbar } from 'notistack';
import { presentableErrorMessage } from '../../utils/apiErrors';
import {
  explainIntakeError,
  explainStoragePause,
  isRecoverableIntakeErrorCode,
} from '../../utils/intakeErrors';
import { ACCEPTED_FILE_TYPES, describeUnsupported, isSupportedFile } from './uploadFileTypes';

const MAX_FILE_BYTES = 25 * 1024 * 1024;
const MAX_BATCH_BYTES = 200 * 1024 * 1024;
const MAX_FILES = 50;
/**
 * Governed-upload outcomes that mean the document did not enter processing.
 *
 * `AwaitingSecurityScan` matters most and was missing: ExtractionController.cs:129-131 returns it
 * for every retryable inspection failure, so a scanner outage — the exact case that stranded the
 * owner's documents — used to look like a clean success and navigate the tray away.
 */
const STOPPED_OUTCOMES = ['Skipped', 'Rejected', 'Quarantined', 'Error', 'AwaitingSecurityScan'];

const ManualUploadLeadsPage: React.FC = () => {
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [uploading, setUploading] = useState(false);
  const [selectionError, setSelectionError] = useState<string | null>(null);
  // Retained after an all-failed upload so the tray survives and the batch stays reachable.
  const [failedJobs, setFailedJobs] = useState<GovernedUploadJobDTO[]>([]);
  const [batchId, setBatchId] = useState<string | null>(null);
  /**
   * The storage refusal (503 evidence_storage_unavailable). Held separately from `failedJobs`
   * because it is not a verdict about any file — it is the store failing — and offering a retry per
   * file would repeat the 2026-08-12 defect. `accepted` is the server's count of documents that
   * genuinely reached durable storage before it failed, which can be more than zero.
   */
  const [storagePaused, setStoragePaused] = useState<{ isConfigurationFault: boolean; accepted: number } | null>(null);
  const canCreateLeads = hasPermission('Leads', 'create');
  const heldByScanner = failedJobs.some((job) => isRecoverableIntakeErrorCode(job.errorCode));
  const storageExplanation = storagePaused === null
    ? null
    : explainStoragePause(storagePaused.isConfigurationFault);
  // Only a misconfigured store is guaranteed to refuse the next attempt. An unreachable one may
  // already be back, and disabling the button on it would dress a thirty-second blip as something
  // only an administrator can fix.
  const storagePausedBlocksRetry = storagePaused?.isConfigurationFault === true;

  const uploadMutation = useMutation({
    mutationFn: (fd: FormData) => leadService.uploadGoverned(fd),
    onSuccess: (result) => {
      const stopped = result.jobs.filter((job) => STOPPED_OUTCOMES.includes(job.outcome));
      const everyFileFailed = result.jobs.length > 0 && stopped.length === result.jobs.length;

      if (!result.batchId) {
        setSelectionError('The server did not return a batch reference. Your selected files have been kept so you can try again.');
        return;
      }

      setBatchId(result.batchId);

      // An all-failed upload used to navigate straight to the batch page, clearing the tray and
      // forcing the user to re-select every file to retry. Stay put and keep the files: the batch
      // is still reachable through the link below, and retrying is one click.
      if (everyFileFailed) {
        const held = stopped.filter((job) => isRecoverableIntakeErrorCode(job.errorCode));
        setFailedJobs(stopped);
        enqueueSnackbar(
          held.length > 0
            ? 'Malware scanning is offline. Your files are held safely — retry when you are ready.'
            : `No document could be processed. Your ${result.jobs.length === 1 ? 'file has' : 'files have'} been kept so you can retry.`,
          { variant: 'warning' },
        );
        return;
      }

      setFailedJobs([]);
      enqueueSnackbar(
        stopped.length === 0
          ? 'Documents queued for ingestion and reconciliation.'
          : `${stopped.length} document${stopped.length === 1 ? '' : 's'} need attention. Opened the batch outcomes.`,
        { variant: stopped.length === 0 ? 'success' : 'warning' },
      );
      setFiles([]);
      navigate(`/procurement/leads/ingestion/${encodeURIComponent(result.batchId)}`);
    },
    onError: (error: unknown) => {
      /*
        The batch-wide storage refusal arrives here rather than in onSuccess, because the server
        answers 503 instead of accepting files it cannot durably store. Its generic sibling copy
        below — "your files were not sent — try again" — is true but useless for this one: retrying
        is the single thing that cannot work while the store is unwritable.
      */
      const paused = readUploadPausedProblem(error);
      if (paused) {
        /*
          `jobs` carries every row the batch decided before the outage — quarantined, rejected and
          held files as well as stored ones — so its length is not a count of accepted work. Reading
          it as one reported a malware quarantine as "stored and processing normally", in the banner
          written to end exactly that kind of falsehood. The server sends its own `accepted` count;
          the filter is the fallback for a payload that predates it.
        */
        const rows = paused.jobs ?? [];
        const stopped = rows.filter((job) => STOPPED_OUTCOMES.includes(job.outcome));
        setStoragePaused({
          isConfigurationFault: paused.isConfigurationFault === true,
          accepted: paused.accepted ?? rows.length - stopped.length,
        });
        // Files the batch had already REFUSED keep their tray rows and their real outcomes.
        // Clearing them would hide a malware verdict behind a storage outage.
        setFailedJobs(stopped);
        setBatchId(paused.batchId ?? null);
        enqueueSnackbar('Uploads are paused — document storage is unavailable. Your files have been kept.',
          { variant: 'error' });
        return;
      }

      enqueueSnackbar(
        presentableErrorMessage(error, 'The upload could not be completed. Your files were not sent — try again.'),
        { variant: 'error' },
      );
    },
    onSettled: () => setUploading(false),
  });

  const addFiles = (incoming: File[]) => {
    if (uploading || !canCreateLeads) return;
    const combined = [...files, ...incoming];
    const unsupported = incoming.filter((file) => !isSupportedFile(file.name));
    const oversized = incoming.filter((file) => file.size > MAX_FILE_BYTES);
    const batchTooLarge = combined.reduce((total, file) => total + file.size, 0) > MAX_BATCH_BYTES;
    if (unsupported.length > 0 || oversized.length > 0 || combined.length > MAX_FILES || batchTooLarge) {
      const reasons = [
        describeUnsupported(unsupported.map((file) => file.name)),
        oversized.length > 0 ? `${oversized.length} file${oversized.length === 1 ? '' : 's'} over 25 MB` : null,
        combined.length > MAX_FILES ? `a maximum of ${MAX_FILES} files per batch` : null,
        batchTooLarge ? 'the batch is over 200 MB' : null,
      ].filter(Boolean);
      setSelectionError(`Selection stopped: ${reasons.join(', ')}.`);
      return;
    }

    setSelectionError(null);
    setFailedJobs([]);
    setStoragePaused(null);
    setFiles(combined);
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (!uploading && canCreateLeads && e.target.files) addFiles(Array.from(e.target.files));
    e.target.value = '';
  };

  const removeFile = (index: number) => {
    if (uploading || !canCreateLeads) return;
    setFiles(prev => prev.filter((_, i) => i !== index));
  };

  const handleUpload = () => {
    if (files.length === 0 || uploading || !canCreateLeads || storagePausedBlocksRetry) return;
    setUploading(true);
    setFailedJobs([]);
    setStoragePaused(null);
    const fd = new FormData();
    files.forEach(f => fd.append('files', f));
    uploadMutation.mutate(fd);
  };

  return (
    <Box sx={{ p: { xs: 1, sm: 2 }, maxWidth: 1440, mx: 'auto' }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        sx={{ alignItems: { xs: 'flex-start', sm: 'center' }, justifyContent: 'space-between', gap: 1.5, mb: 2 }}
      >
        <Box>
          <Typography variant="h4" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.02em', lineHeight: 1.1 }}>
            Upload documents
          </Typography>
          <Typography color="text.secondary" sx={{ mt: 0.75 }}>
            Add customer RFQs to Leads · PDF, Word, Excel, CSV, email, web pages or images · up to 25 MB each
          </Typography>
        </Box>
        <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate('/procurement/leads/all')} sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}>
          Back to Leads
        </Button>
      </Stack>

      <Paper variant="outlined" sx={{ borderRadius: 3, overflow: 'hidden' }}>
        <Box sx={{ p: { xs: 2, sm: 3 } }}>
          {/* Upload Area */}
          <Paper
            role="button"
            tabIndex={uploading || !canCreateLeads ? -1 : 0}
            aria-label="Select RFQ documents"
            aria-disabled={uploading || !canCreateLeads}
            onClick={() => {
              if (!uploading && canCreateLeads) inputRef.current?.click();
            }}
            onKeyDown={(event) => {
              if (!uploading && canCreateLeads && (event.key === 'Enter' || event.key === ' ')) {
                event.preventDefault();
                inputRef.current?.click();
              }
            }}
            onDragOver={(event) => event.preventDefault()}
            onDrop={(event) => {
              event.preventDefault();
              if (!uploading && canCreateLeads) addFiles(Array.from(event.dataTransfer.files));
            }}
            sx={{
              py: 6,
              px: 3,
              display: 'flex',
              flexDirection: 'column',
              alignItems: 'center',
              justifyContent: 'center',
              border: '1.5px dashed',
              borderColor: 'divider',
              borderRadius: 3,
              bgcolor: 'action.hover',
              boxShadow: 'none',
              cursor: uploading || !canCreateLeads ? 'not-allowed' : 'pointer',
              opacity: uploading || !canCreateLeads ? 0.65 : 1,
              transition: 'border-color 150ms ease, background-color 150ms ease',
              '&:hover': { borderColor: 'primary.main' },
              mb: 3
            }}
          >
            <input ref={inputRef} type="file" multiple hidden accept={ACCEPTED_FILE_TYPES} onChange={handleFileChange} disabled={uploading || !canCreateLeads} />
            <UploadIcon sx={{ fontSize: 40, color: 'text.secondary', mb: 1.5 }} />
            <Typography sx={{ fontWeight: 700, fontSize: '1.05rem' }}>
              Drop files here or click to choose
            </Typography>
          </Paper>

          {selectionError && <Alert severity="warning" sx={{ mb: 3 }}>{selectionError}</Alert>}

          {/*
            Document storage is unwritable, so nothing further was accepted. ONE banner, not one row
            per file: every remaining file failed for the same reason, and the reason is ours to fix
            rather than the operator's. The files stay in the tray below — losing a selection to an
            outage the operator did not cause would be a second insult.

            `explainStoragePause` supplies the next action, which differs by fault: a misconfigured
            store will refuse the next attempt identically and the Queue button stays withheld, while
            an unreachable one may already be back and retrying is honest.
          */}
          {storagePaused && storageExplanation && (
            <Alert
              severity="error"
              icon={<OfflineIcon fontSize="inherit" />}
              sx={{ mb: 3, borderLeft: 4, borderColor: 'error.main' }}
              action={storagePausedBlocksRetry ? (
                <Button
                  color="inherit"
                  size="small"
                  onClick={() => setStoragePaused(null)}
                >
                  Storage restored? Try again
                </Button>
              ) : undefined}
            >
              <AlertTitle sx={{ fontWeight: 800 }}>{storageExplanation.title}</AlertTitle>
              <Typography variant="body2" sx={{ mb: 1 }}>
                {storageExplanation.whatHappened}
              </Typography>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>
                {storageExplanation.nextAction}
              </Typography>
              {/*
                Work that really was stored before the store failed is still running and will surface
                as leads. Saying so is the difference between an honest refusal and one the operator
                will be contradicted by later.
              */}
              {storagePaused.accepted > 0 && (
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                  {storagePaused.accepted} file
                  {storagePaused.accepted === 1 ? ' was' : 's were'} stored before storage stopped
                  responding and {storagePaused.accepted === 1 ? 'is' : 'are'} processing normally.
                </Typography>
              )}
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                Your {files.length === 1 ? 'file is' : 'files are'} still selected below.
              </Typography>
            </Alert>
          )}

          {/*
            All-failed upload. The files stay in the tray below, so the Upload button
            retries the exact same selection in one click — no re-picking.
          */}
          {failedJobs.length > 0 && (
            <Alert
              severity="warning"
              icon={heldByScanner ? <OfflineIcon fontSize="inherit" /> : undefined}
              sx={{ mb: 3, borderLeft: 4, borderColor: 'warning.main' }}
              action={batchId ? (
                <Button
                  color="inherit"
                  size="small"
                  endIcon={<OpenIcon />}
                  onClick={() => navigate(`/procurement/leads/ingestion/${encodeURIComponent(batchId)}`)}
                >
                  View batch
                </Button>
              ) : undefined}
            >
              <AlertTitle sx={{ fontWeight: 800 }}>
                {heldByScanner
                  ? 'Malware scanning is offline'
                  /*
                    "No document was processed" is only true when this tray IS the whole outcome.
                    Alongside a storage pause these are the files the batch had already decided
                    before the store failed, and others may have been accepted — so the heading
                    counts them instead of speaking for the batch.
                  */
                  : storagePaused
                    ? `${failedJobs.length} file${failedJobs.length === 1 ? '' : 's'} stopped before storage failed`
                    : `No document was processed (${failedJobs.length} file${failedJobs.length === 1 ? '' : 's'})`}
              </AlertTitle>
              <Typography variant="body2" sx={{ mb: 1 }}>
                {heldByScanner
                  ? 'Your files are held safely and will process automatically when scanning recovers. They are still selected below — press Retry to send them again now.'
                  : storagePaused
                    ? 'These files were stopped on their own merits, not by the storage outage above.'
                    : 'Your files are still selected below, so you can fix and retry without choosing them again.'}
              </Typography>
              <Stack spacing={0.75}>
                {failedJobs.map((job) => {
                  /*
                    `job.reason` is the backend's own account of why THIS file stopped
                    (ExtractionController returns `reason = ex.Inspection.Reason`). It was being
                    received, typed and thrown away, so a macro-enabled workbook was explained to
                    the owner as "the file is damaged — re-export it or send it as a PDF". The
                    reason outranks our per-code guess; `explainIntakeError` applies the shared
                    presentability gate before rendering any of it.
                  */
                  const explanation = explainIntakeError(job.errorCode, job.reason);
                  return (
                    <Box key={`${job.jobId}:${job.fileName}`}>
                      <Typography variant="body2" sx={{ fontWeight: 700, overflowWrap: 'anywhere' }}>
                        {job.fileName}
                      </Typography>
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                        {explanation.whatHappened}
                      </Typography>
                      <Typography variant="caption" sx={{ display: 'block', fontWeight: 700 }}>
                        {explanation.nextAction}
                      </Typography>
                    </Box>
                  );
                })}
              </Stack>
            </Alert>
          )}

          {/* File Queue Preview */}
          {files.length > 0 && (
            <List sx={{ mb: 3, border: '1px solid', borderColor: 'divider', borderRadius: 2 }}>
              {files.map((file, i) => (
                <ListItem
                  key={i}
                  divider={i < files.length - 1}
                  secondaryAction={
                    <IconButton
                      edge="end"
                      aria-label={`Remove ${file.name}`}
                      onClick={() => removeFile(i)}
                      disabled={uploading || !canCreateLeads}
                    >
                      <DeleteIcon fontSize="small" color="error" />
                    </IconButton>
                  }
                >
                  <ListItemIcon><DocIcon color="primary" /></ListItemIcon>
                  <ListItemText
                    primary={file.name}
                    secondary={`${(file.size / 1024).toFixed(1)} KB`}
                    slotProps={{ primary: { sx: { fontWeight: 600, fontSize: '0.875rem' } } }}
                  />
                </ListItem>
              ))}
            </List>
          )}

          <Stack direction="row" sx={{ justifyContent: 'flex-end' }}>
            <Button
              variant="contained"
              // Disabled only for a MISCONFIGURED store, and then deliberately rather than
              // relabelled: the banner above says retrying cannot work, and an enabled "Retry 4
              // files" underneath it would be the same contradiction the incident shipped. The
              // banner's own action clears it. An unreachable store leaves the button live —
              // waiting out a blip is the user's call, not ours to forbid.
              disabled={files.length === 0 || uploading || !canCreateLeads || storagePausedBlocksRetry}
              onClick={handleUpload}
              sx={{ fontWeight: 700, minWidth: 160 }}
              startIcon={uploading ? <CircularProgress size={18} color="inherit" /> : undefined}
            >
              {uploading
                ? 'Uploading…'
                : storagePausedBlocksRetry
                  ? 'Uploads paused — document storage is unavailable'
                  : failedJobs.length > 0 || storagePaused
                    ? `Retry ${files.length} file${files.length === 1 ? '' : 's'}`
                    : files.length > 0
                      ? `Upload ${files.length} file${files.length === 1 ? '' : 's'}`
                      : 'Upload'}
            </Button>
          </Stack>
        </Box>
      </Paper>
    </Box>
  );
};

export default ManualUploadLeadsPage;
