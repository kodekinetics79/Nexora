import React from 'react';
import { Box, Button, LinearProgress, Stack, Typography, CircularProgress, Tooltip } from '@mui/material';
import {
  CheckCircle as DoneIcon,
  RadioButtonUnchecked as PendingIcon,
  ErrorOutlined as FailedIcon,
  PauseCircleOutlined as HeldIcon,
} from '@mui/icons-material';
import NextStepPanel from '../../components/common/NextStepPanel';
import type { BatchReconciliationDTO, BatchReconciliationItemDTO } from '../../api/services/leadService';

/**
 * What is happening to each uploaded document, in the words a rep uses, refreshed as the batch
 * polls. Derived only from the batch the page already holds: the security scan, the extraction
 * job, the reconciliation verdict and the customer read. No new fetches, no new logic.
 *
 * A blank page while the server reads a bid taught reps that the upload had failed. This keeps
 * them on the page: one bar for the batch, one line per document naming the step it is on, and a
 * clear "done" with the next thing to press.
 */

export const STAGES = ['Received', 'Safety check', 'Reading', 'Matching', 'Customer', 'Ready'] as const;
export type StageName = typeof STAGES[number];
export type StageState = 'done' | 'active' | 'pending' | 'failed' | 'held';

export interface DocumentProgress {
  /** Index into STAGES of the step the document is on. */
  step: number;
  state: StageState;
  /** The sentence for that step, present tense, from the rep's side. */
  sentence: string;
  /** Per-stage rendering state, length STAGES.length. */
  stages: StageState[];
}

const norm = (value: string | null | undefined) => (value ?? '').replaceAll('_', '').toLowerCase();

/** The one step a document is on right now, and how far it has come. */
export const documentProgress = (item: BatchReconciliationItemDTO): DocumentProgress => {
  const security = norm(item.securityStatus);
  const extraction = norm(item.extractionStatus);
  const classification = norm(item.classification);
  const customer = norm(item.customerResolutionStatus);
  const held = Boolean(item.recoverableSecurityHold);
  const finish = (step: number, state: StageState, sentence: string): DocumentProgress => {
    const stages = STAGES.map<StageState>((_, i) => (i < step ? 'done' : i === step ? state : 'pending'));
    if (state === 'done') for (let i = 0; i <= step; i += 1) stages[i] = 'done';
    return { step, state, sentence, stages };
  };

  if (held) return finish(1, 'held', 'Held: the safety scanner is offline. The file is stored safely and can be released with Retry.');
  if (security === 'quarantined' || security === 'rejected') return finish(1, 'failed', 'Did not pass the safety check. Nothing from this file was read.');
  if (classification === 'rejectedorunprocessable') return finish(2, 'failed', 'Could not be read. See the reason on the file below.');
  if (security === '' || security === 'pending') return finish(1, 'active', 'Checking the file is safe to open.');
  if (extraction === 'failed' || extraction === 'deadletter') return finish(2, 'failed', 'Reading stopped with an error. See the reason on the file below.');
  if (extraction === '' || extraction === 'pending' || extraction === 'leased') return finish(2, 'active', 'Queued to be read. Large bid lists take a few minutes.');
  if (extraction === 'extracting') return finish(2, 'active', 'Reading the document: headers, dates, lines and part numbers.');
  if (extraction === 'persisting') return finish(2, 'active', 'Saving what it read, line by line, with its evidence.');
  if (classification === 'pending') return finish(3, 'active', 'Comparing with inquiries you already have.');
  const verdict = classification === 'exactduplicate' ? 'Same document as one you already have.'
    : classification === 'revision' ? 'A new revision of an inquiry you already have.'
      : classification === 'possiblematchreviewrequired' ? 'Might be a repeat of an earlier inquiry; a person decides.'
        : 'A new inquiry.';
  if (customer.startsWith('automatched') || customer === 'humanconfirmed' || customer === 'confirmed') {
    return finish(5, 'done', `${verdict} Customer identified. Ready to decide.`);
  }
  if (customer === 'suggested' || customer === 'ambiguous') {
    return finish(5, 'done', `${verdict} The customer needs a look before deciding.`);
  }
  if (typeof item.leadId === 'number' && item.leadId > 0) {
    return finish(5, 'done', `${verdict} Customer not identified yet; set the client on the inquiry.`);
  }
  return finish(4, 'active', `${verdict} Identifying the customer.`);
};

const StageDots: React.FC<{ stages: StageState[] }> = ({ stages }) => (
  <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', flexWrap: 'wrap', rowGap: 0.25 }}>
    {stages.map((state, i) => {
      const label = STAGES[i];
      const icon = state === 'done' ? <DoneIcon sx={{ fontSize: 14, color: 'success.main' }} />
        : state === 'active' ? <CircularProgress size={11} thickness={6} />
          : state === 'failed' ? <FailedIcon sx={{ fontSize: 14, color: 'error.main' }} />
            : state === 'held' ? <HeldIcon sx={{ fontSize: 14, color: 'warning.main' }} />
              : <PendingIcon sx={{ fontSize: 12, color: 'text.disabled' }} />;
      return (
        <Tooltip key={label} title={`${label}: ${state}`} describeChild>
          <Stack direction="row" spacing={0.4} sx={{ alignItems: 'center', pr: 0.5 }} aria-label={`${label} ${state}`}>
            {icon}
            <Typography sx={{ fontSize: '0.68rem', lineHeight: 1.2, whiteSpace: 'nowrap', color: state === 'pending' ? 'text.disabled' : 'text.secondary', fontWeight: state === 'active' ? 700 : 500 }}>
              {label}
            </Typography>
          </Stack>
        </Tooltip>
      );
    })}
  </Stack>
);

export interface UploadProgressPanelProps {
  batch: BatchReconciliationDTO;
  onDecide: (leadId: number) => void;
  onOpenInquiries: () => void;
}

const UploadProgressPanel: React.FC<UploadProgressPanelProps> = ({ batch, onDecide, onOpenInquiries }) => {
  const progress = batch.items.map((item) => ({ item, progress: documentProgress(item) }));
  const notYetRecorded = Math.max(batch.filesReceived - batch.items.length, 0);
  const total = Math.max(batch.filesReceived, batch.items.length);
  const finished = progress.filter(({ progress: p }) => p.state === 'done' || p.state === 'failed').length;
  const stuck = progress.filter(({ progress: p }) => p.state === 'held').length;
  const running = total - finished - stuck;
  const percent = total === 0 ? 0
    : Math.round((progress.reduce((sum, { progress: p }) => sum + (p.state === 'done' ? STAGES.length : p.step), 0) / (total * STAGES.length)) * 100);
  const readyLeads = progress
    .filter(({ item, progress: p }) => p.state === 'done' && typeof item.leadId === 'number' && item.leadId > 0
      && !['exactduplicate'].includes(norm(item.classification)))
    .map(({ item }) => item.leadId as number);
  const failed = progress.filter(({ progress: p }) => p.state === 'failed').length;
  const complete = running === 0 && notYetRecorded === 0;

  const nextStep = complete
    ? readyLeads.length === 1
      ? { tone: 'success' as const, title: 'Done', sentence: `Your document is read${failed ? `, ${failed} could not be` : ''}. One inquiry is ready. Decide whether to quote it.`,
          action: <Button variant="contained" onClick={() => onDecide(readyLeads[0])} sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}>Decide</Button> }
      : readyLeads.length > 1
        ? { tone: 'success' as const, title: 'Done', sentence: `${readyLeads.length} inquiries are ready to decide${failed ? `; ${failed} could not be read` : ''}. Start with the first, or open the list.`,
            action: <Stack direction="row" spacing={1}><Button variant="contained" onClick={() => onDecide(readyLeads[0])} sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}>Decide the first</Button><Button variant="outlined" onClick={onOpenInquiries} sx={{ whiteSpace: 'nowrap' }}>Open inquiries</Button></Stack> }
        : stuck > 0
          ? { tone: 'warning' as const, title: 'Waiting on you', sentence: `${stuck} file${stuck === 1 ? ' is' : 's are'} held because the safety scanner is offline. Press Retry when it is back; nothing is lost.` }
          : failed > 0 && failed === total
            ? { tone: 'error' as const, title: 'Nothing could be read', sentence: 'Every file in this upload was refused or could not be read. The reasons are on each file below.' }
            : { tone: 'info' as const, title: 'Done', sentence: 'Nothing new to decide: every document was a repeat or a revision of an inquiry you already have.', action: <Button variant="outlined" onClick={onOpenInquiries} sx={{ whiteSpace: 'nowrap' }}>Open inquiries</Button> }
    : { tone: 'info' as const, title: `Reading your document${total === 1 ? '' : 's'}`,
        sentence: `${finished} of ${total} finished. Usually under a minute; a bid list with hundreds of lines takes a few. You can stay here, this page updates itself.` };

  return (
    <Box sx={{ mb: 2 }} data-testid="upload-progress">
      <NextStepPanel tone={nextStep.tone} title={nextStep.title} sentence={nextStep.sentence} action={nextStep.action} testId="upload-next-step">
        <LinearProgress
          variant={complete ? 'determinate' : (progress.length === 0 ? 'indeterminate' : 'determinate')}
          value={complete ? 100 : percent}
          aria-label="Upload progress"
          sx={{ height: 4, borderRadius: 2, mb: progress.length + notYetRecorded > 0 ? 1 : 0 }}
        />
        {/* One hairline-separated row per document, not a stack of nested cards. On a ten-file
            upload the card version pushed the counts and the file list off the screen entirely. */}
        <Box sx={{ '& > *': { borderTop: '1px solid', borderColor: 'divider' }, '& > *:first-of-type': { borderTop: 0 } }}>
          {progress.map(({ item, progress: p }) => (
            <Stack
              key={item.sourceDocumentOccurrenceId ?? item.occurrenceId}
              direction={{ xs: 'column', md: 'row' }}
              spacing={{ xs: 0.5, md: 2 }}
              sx={{ py: 0.85, justifyContent: 'space-between', alignItems: { md: 'center' } }}
            >
              <Box sx={{ minWidth: 0, flex: 1 }}>
                <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', lineHeight: 1.35, overflowWrap: 'anywhere' }}>
                  {item.fileName || `Document ${item.occurrenceId}`}
                </Typography>
                <Typography
                  sx={{ fontSize: '0.78rem', lineHeight: 1.4 }}
                  color={p.state === 'failed' ? 'error.main' : p.state === 'held' ? 'warning.main' : 'text.secondary'}
                >
                  {p.sentence}
                </Typography>
              </Box>
              <Box sx={{ flexShrink: 0 }}><StageDots stages={p.stages} /></Box>
            </Stack>
          ))}
          {Array.from({ length: notYetRecorded }, (_, i) => (
            <Stack key={`waiting-${i}`} direction="row" spacing={1} sx={{ py: 0.85, alignItems: 'center' }}>
              <CircularProgress size={11} thickness={6} />
              <Typography sx={{ fontSize: '0.78rem' }} color="text.secondary">
                {`Document ${batch.items.length + i + 1} of ${total}: received, waiting to be recorded.`}
              </Typography>
            </Stack>
          ))}
        </Box>
      </NextStepPanel>
    </Box>
  );
};

export default UploadProgressPanel;
