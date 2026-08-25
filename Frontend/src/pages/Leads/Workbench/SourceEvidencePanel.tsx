import React from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  Divider,
  Paper,
  Stack,
  Typography,
} from '@mui/material';
import {
  Description as DocumentIcon,
  EmailOutlined as EmailIcon,
  OpenInNew as OpenIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import type { LeadDecisionWorkbenchDTO } from '../../../api/services/leadDecisionService';
import { openAuthenticatedFile } from '../../../utils/authenticatedFile';
import { formatDateSafe } from '../../../utils/dates';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { inspectableEvidenceUrl } from './evidenceRules';

const SourceEvidencePanel: React.FC<{ workbench: LeadDecisionWorkbenchDTO; compact?: boolean }> = ({ workbench, compact = false }) => {
  const { enqueueSnackbar } = useSnackbar();
  const inspectable = workbench.evidence.filter((item) => inspectableEvidenceUrl(item)).length;

  const openEvidence = async (path: string) => {
    try {
      await openAuthenticatedFile(path);
    } catch (error: unknown) {
      enqueueSnackbar(presentableErrorMessage(error, 'The source document could not be opened.'), { variant: 'error' });
    }
  };

  return (
    <Paper
      variant="outlined"
      component="section"
      aria-labelledby="source-evidence-heading"
      sx={{ p: compact ? 2 : 2.5, borderRadius: 2, minWidth: 0, height: '100%' }}
    >
      <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between', gap: 1, mb: 1.5 }}>
        <Box>
          <Typography id="source-evidence-heading" variant="h6" sx={{ fontWeight: 900 }}>Source evidence</Typography>
          <Typography variant="caption" color="text.secondary">Durable occurrence and retained customer documents</Typography>
        </Box>
        <Chip size="small" label={`${inspectable}/${workbench.evidence.length} inspectable`} color={workbench.evidence.length > 0 && inspectable === workbench.evidence.length ? 'success' : 'warning'} variant="outlined" />
      </Stack>

      {workbench.evidence.length === 0 ? <Alert severity="error">No durable source evidence is linked to this Lead revision.</Alert> : null}
      {workbench.evidence.length > 0 && inspectable === 0 ? (
        <Alert severity="error" sx={{ mb: 1.5 }}>
          Source metadata is retained, but no evidence content can be opened. Validation and RFQ promotion must remain blocked.
        </Alert>
      ) : null}
      {workbench.verificationStatus === 'SOURCE_UNAVAILABLE' ? (
        <Alert severity="error" sx={{ mb: 1.5 }}>The authoritative source is unavailable. Validation and RFQ promotion are blocked.</Alert>
      ) : null}

      <Stack spacing={1.25}>
        <Paper variant="outlined" sx={{ p: 1.5, borderRadius: 2, bgcolor: 'action.hover' }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1 }}>
            <EmailIcon color="primary" fontSize="small" />
            <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>Email occurrence</Typography>
          </Stack>
          <Typography variant="body2" sx={{ fontWeight: 800, overflowWrap: 'anywhere' }}>{workbench.emailSubject || 'Subject not captured'}</Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', overflowWrap: 'anywhere' }}>{workbench.senderEmail || 'Sender not captured'}</Typography>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{formatDateSafe(workbench.receivedAtUtc ?? null)}</Typography>
          <Typography variant="caption" color="text.disabled" sx={{ display: 'block', mt: 0.5, fontFamily: 'monospace', overflowWrap: 'anywhere' }}>{workbench.emailMessageId || 'Message-ID not captured'}</Typography>
        </Paper>

        {workbench.evidence.map((item) => {
          const sourceUrl = inspectableEvidenceUrl(item);
          const canInspect = Boolean(sourceUrl);
          return (
          <Paper key={`${item.occurrenceId}-${item.name}`} variant="outlined" sx={{ p: 1.5, borderRadius: 2 }}>
            <Stack direction="row" spacing={1.25} sx={{ alignItems: 'flex-start' }}>
              <DocumentIcon color={canInspect ? 'primary' : 'disabled'} fontSize="small" />
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography variant="body2" sx={{ fontWeight: 800, overflowWrap: 'anywhere' }}>{item.name}</Typography>
                <Stack direction="row" spacing={0.75} sx={{ mt: 0.5, alignItems: 'center', flexWrap: 'wrap' }}>
                  <Chip size="small" label={canInspect ? item.status : 'CONTENT UNAVAILABLE'} color={canInspect ? 'default' : 'error'} variant="outlined" />
                  <Typography variant="caption" color="text.secondary">Occurrence #{item.occurrenceId}</Typography>
                </Stack>
                {item.detail ? <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.75 }}>{item.detail}</Typography> : null}
                {!canInspect ? (
                  <Typography variant="caption" color="error.main" sx={{ display: 'block', mt: 0.75, fontWeight: 700 }}>
                    Retained occurrence metadata only — no download or content URL was supplied.
                  </Typography>
                ) : null}
              </Box>
              {canInspect ? (
                <Button size="small" startIcon={<OpenIcon />} onClick={() => openEvidence(sourceUrl!)} aria-label={`Open source ${item.name}`}>
                  Open
                </Button>
              ) : null}
            </Stack>
          </Paper>
          );
        })}
      </Stack>

      {workbench.sourceCoverage ? (
        <>
          <Divider sx={{ my: 1.5 }} />
          <Typography variant="caption" color="text.secondary">
            Source coverage: {workbench.sourceCoverage.coveredLines} of {workbench.sourceCoverage.totalLines} lines have linked evidence.
            {inspectable === 0 ? ' Coverage records do not make the retained content inspectable.' : ''}
          </Typography>
        </>
      ) : null}
    </Paper>
  );
};

export default SourceEvidencePanel;
