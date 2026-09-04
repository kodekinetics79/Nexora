import React from 'react';
import {
  Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogTitle,
  Divider, Skeleton, Stack, Typography,
} from '@mui/material';
import {
  AttachFile as FileIcon, CheckCircle as OkIcon, RemoveCircleOutlined as SkippedIcon,
  ErrorOutlined as FailedIcon,
} from '@mui/icons-material';
import { useQuery } from '@tanstack/react-query';
import axios from 'axios';
import intakeRecordService, { type IntakeInventoryEntry } from '../../api/services/intakeRecordService';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import { formatDateSafe } from '../../utils/dates';
import statusLabel from '../../utils/statusLabels';

interface Props {
  leadId: number;
  open: boolean;
  onClose: () => void;
}

/**
 * "What did we actually receive?" — the smallest useful view of the canonical intake record.
 *
 * A rep looking at a lead could see the values the pipeline extracted and nothing about the
 * message they came from: which files arrived, which the intake door dropped and why, whether the
 * original email is still on file. That is the first question asked when a quantity looks wrong,
 * and answering it meant a database console.
 *
 * Deliberately a dialog on the lead and not a new screen: the record is only ever wanted while
 * looking at the lead it produced.
 */

/** The pipeline's derived status, in the words a salesperson would use. */
export const intakeOutcomeSentence = (finalStatus: string): string => {
  switch (finalStatus) {
    case 'Completed':
      return 'Everything on this message was read successfully.';
    case 'CompletedWithFailures':
      return 'This inquiry was created, but at least one file could not be read. Check the list below before quoting.';
    case 'NeedsReview':
      return 'This inquiry still needs someone to check the figures that were read.';
    case 'InProgress':
      return 'Still being read. Some files may not be listed yet.';
    case 'Rejected':
      return 'The message was received but judged not to be an inquiry.';
    case 'ProcessedNoLead':
      return 'The message was read and produced no inquiry.';
    case 'DeadLettered':
    case 'Failed':
      return 'The message could not be read. What is on this lead may be incomplete.';
    default:
      return 'Nexora has no recorded outcome for this message.';
  }
};

/** What became of one file, in a sentence — never a raw status word on its own. */
export const fileFateSentence = (entry: IntakeInventoryEntry): string => {
  if (entry.disposition === 'Skipped') {
    return entry.skippedReason?.trim()
      ? `Not read — ${entry.skippedReason.trim()}`
      : 'Not read. Nexora recorded no reason.';
  }
  if (entry.jobLastError?.trim()) return `Could not be read — ${entry.jobLastError.trim()}`;
  if (entry.resultLeadId != null) return 'Read, and it produced this inquiry.';
  if (entry.jobStatus) return `Read: ${statusLabel(entry.jobStatus).toLowerCase()}.`;
  return 'Read.';
};

const fateIcon = (entry: IntakeInventoryEntry) => {
  if (entry.disposition === 'Skipped') return <SkippedIcon sx={{ fontSize: 16, color: 'warning.main' }} />;
  if (entry.jobLastError?.trim()) return <FailedIcon sx={{ fontSize: 16, color: 'error.main' }} />;
  return <OkIcon sx={{ fontSize: 16, color: 'success.main' }} />;
};

const LeadIntakeRecordDialog: React.FC<Props> = ({ leadId, open, onClose }) => {
  const record = useQuery({
    queryKey: ['intake-record', leadId],
    queryFn: () => intakeRecordService.getByLead(leadId),
    enabled: open,
    retry: false,
  });

  /**
   * A 403 here is a fact about the tenant's plan and a 404 is "this lead did not come from a
   * mailbox" — neither is an outage, and neither must render as one.
   */
  const status = axios.isAxiosError(record.error) ? record.error.response?.status : undefined;
  const notEntitled = status === 403;
  const noRecord = status === 404;
  const data = record.data;
  const attachments = (data?.inventory ?? []).filter((entry) => entry.kind !== 'Body');

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 900 }}>What we received</DialogTitle>
      <DialogContent dividers>
        {record.isLoading && <Skeleton variant="rounded" height={180} />}

        {notEntitled && (
          <Alert severity="info" sx={{ borderRadius: 2 }}>
            Reading inquiries out of a mailbox is not switched on for your company, so there is no
            received message behind this inquiry to show.
          </Alert>
        )}

        {noRecord && (
          <Alert severity="info" sx={{ borderRadius: 2 }}>
            This inquiry did not arrive by email — it was entered or uploaded here — so there is no
            received message to show.
          </Alert>
        )}

        {record.isError && !notEntitled && !noRecord && (
          <ApiErrorNotice
            error={record.error}
            fallbackMessage="We couldn't load what was received. Nothing about this inquiry has changed."
            onRetry={() => record.refetch()}
          />
        )}

        {data && (
          <Stack spacing={2}>
            <Alert
              severity={
                data.finalStatus === 'Completed' ? 'success'
                  : data.finalStatus === 'InProgress' ? 'info'
                    : 'warning'
              }
              sx={{ borderRadius: 2 }}
            >
              {intakeOutcomeSentence(data.finalStatus)}
            </Alert>

            <Box>
              <Typography variant="caption" sx={{ fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase', display: 'block' }}>
                The message
              </Typography>
              <Typography sx={{ fontWeight: 800 }}>{data.message.subject?.trim() || 'No subject'}</Typography>
              <Typography variant="body2" color="text.secondary">
                From {data.message.from?.trim() || 'an unrecorded sender'}
                {' · received '}{formatDateSafe(data.sourceEmail.receivedOn)}
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Into the {data.sourceEmail.mailbox || 'unnamed'} mailbox
              </Typography>
              {!data.sourceEmail.rawEmailAvailable && (
                <Typography variant="body2" sx={{ color: 'warning.main', fontWeight: 700, mt: 0.5 }}>
                  The original email is no longer stored, so it cannot be re-read.
                </Typography>
              )}
            </Box>

            <Divider />

            <Box>
              <Typography variant="caption" sx={{ fontWeight: 900, color: 'text.disabled', textTransform: 'uppercase', display: 'block', mb: 0.5 }}>
                Files that arrived ({attachments.length})
              </Typography>
              {attachments.length === 0 && (
                <Typography variant="body2" color="text.secondary">
                  No files were attached. Everything on this inquiry was read from the message
                  itself.
                </Typography>
              )}
              <Stack spacing={1}>
                {attachments.map((entry, index) => (
                  <Stack
                    key={`${entry.fileName}-${index}`}
                    direction="row"
                    spacing={1}
                    sx={{ alignItems: 'flex-start' }}
                  >
                    {fateIcon(entry)}
                    <Box sx={{ minWidth: 0 }}>
                      <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', overflowWrap: 'anywhere' }}>
                        <FileIcon sx={{ fontSize: 13, mr: 0.5, verticalAlign: 'text-bottom' }} />
                        {entry.fileName}
                      </Typography>
                      <Typography variant="body2" color="text.secondary">
                        {fileFateSentence(entry)}
                      </Typography>
                    </Box>
                  </Stack>
                ))}
              </Stack>
            </Box>

            {data.otherLeadIds.length > 0 && (
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                This one message produced {data.otherLeadIds.length + 1} inquiries in total.
                <Stack direction="row" spacing={0.5} sx={{ mt: 1, flexWrap: 'wrap', gap: 0.5 }}>
                  {data.otherLeadIds.map((id) => (
                    <Chip key={id} size="small" label={`Inquiry ${id}`} variant="outlined" />
                  ))}
                </Stack>
              </Alert>
            )}
          </Stack>
        )}
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button onClick={onClose} sx={{ fontWeight: 800 }}>Close</Button>
      </DialogActions>
    </Dialog>
  );
};

export default LeadIntakeRecordDialog;
