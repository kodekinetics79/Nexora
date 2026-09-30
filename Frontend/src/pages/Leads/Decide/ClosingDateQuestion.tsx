import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Alert, Button, CircularProgress, Stack, Typography } from '@mui/material';
import { EventOutlined as EventIcon } from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import leadService from '../../../api/services/leadService';
import leadDecisionService from '../../../api/services/leadDecisionService';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import { formatDeadline, formatDeadlineDay } from '../../../utils/dates';

export interface ClosingDateQuestionProps {
  leadId: number;
  /** False shows the question without the answer buttons. */
  canEdit?: boolean;
}

/**
 * "Closes 8 Sep or 9 Aug?" — asked once, on Decide, when the document's closing date could be read
 * two ways and nothing on the document said which. Extraction used to write this as a sentence in
 * the lead's remarks, which only the Excel export showed; the rep saw a confident "09 Aug 2026 ·
 * Closed 50 days ago" for a tender that closed on 8 September. One press answers it: the server
 * keeps the stated time, reads the received date the same way, and the question does not return.
 *
 * Shares the page's owner read (['lead-detail', id, 'owner']), so it costs no request of its own.
 */
const ClosingDateQuestion: React.FC<ClosingDateQuestionProps> = ({ leadId, canEdit = true }) => {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const leadQuery = useQuery({
    queryKey: ['lead-detail', leadId, 'owner'],
    queryFn: () => leadDecisionService.getOwner(leadId),
    enabled: leadId > 0,
    retry: false,
  });
  const question = leadQuery.data?.closingDateQuestion ?? null;

  const answer = useMutation({
    mutationFn: (reading: string) => leadService.confirmClosingDate(leadId, reading),
    onSuccess: async (lead) => {
      enqueueSnackbar(`Closing date set: ${formatDeadline(lead.bidClosingDate)}`, { variant: 'success' });
      queryClient.setQueryData(['lead-detail', leadId], lead);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['lead-detail', leadId] }),
        queryClient.invalidateQueries({ queryKey: ['lead-decision-workbench', leadId] }),
        queryClient.invalidateQueries({ queryKey: ['leads'] }),
      ]);
    },
    onError: (error: unknown) => {
      enqueueSnackbar(presentableErrorMessage(error, 'The closing date was not changed. Try again.'), { variant: 'error' });
    },
  });

  if (!question) return null;

  // Earlier date first, so the two buttons read like a calendar.
  const readings = [question.currentReading, question.otherReading]
    .sort((a, b) => a.localeCompare(b));

  return (
    <Alert
      severity="warning"
      icon={<EventIcon fontSize="inherit" />}
      role="region"
      aria-label="Closing date question"
      sx={{ mb: 1.25, alignItems: 'center' }}
      action={(
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          {answer.isPending ? <CircularProgress size={16} color="inherit" /> : null}
          {readings.map((reading) => (
            <Button
              key={reading}
              size="small"
              variant="outlined"
              color="inherit"
              disabled={!canEdit || answer.isPending}
              onClick={() => answer.mutate(reading)}
              sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}
            >
              {formatDeadlineDay(reading)}
            </Button>
          ))}
        </Stack>
      )}
    >
      <Typography component="span" sx={{ fontWeight: 800 }}>
        {`Closes ${formatDeadlineDay(readings[0])} or ${formatDeadlineDay(readings[1])}?`}
      </Typography>
      <Typography component="span" variant="body2" sx={{ ml: 1 }}>
        {`The document says ${question.documentText}.`}
      </Typography>
    </Alert>
  );
};

export default ClosingDateQuestion;
