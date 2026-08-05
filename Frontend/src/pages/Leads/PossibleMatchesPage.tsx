import { useQuery } from '@tanstack/react-query';
import { Alert, Box, Button, Chip, CircularProgress, Paper, Stack, Typography } from '@mui/material';
import { FactCheck as ReviewIcon } from '@mui/icons-material';
import { useNavigate } from 'react-router-dom';
import dayjs from 'dayjs';
import leadService from '../../api/services/leadService';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';

export default function PossibleMatchesPage() {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: ['lead-possible-matches'],
    queryFn: leadService.getPossibleMatches,
  });

  return (
    <Box sx={{ p: { xs: 1.5, md: 3 }, minWidth: 0 }}>
      <Typography variant="h5" sx={{ fontWeight: 900, mb: 0.5 }}>Possible Matches</Typography>
      <Typography color="text.secondary" sx={{ mb: 3 }}>
        Review uncertain inquiry identity before any Lead, RFQ, Quote, workload, or KPI is duplicated.
      </Typography>

      {query.isLoading && <Stack sx={{ alignItems: 'center', py: 6 }}><CircularProgress /></Stack>}
      {query.isError && (
        <ApiErrorNotice
          error={query.error}
          fallbackMessage="Possible matches could not be loaded. Nothing was changed — try again."
          onRetry={() => query.refetch()}
        />
      )}
      {query.data?.length === 0 && <Alert severity="success">No possible matches are awaiting review.</Alert>}

      <Stack spacing={1.5}>
        {query.data?.map((item) => {
          const candidate = item.matchCandidates[0];
          return (
            <Paper key={item.occurrenceId} variant="outlined" sx={{ p: 2, borderRadius: 1 }}>
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between' }}>
                <Box sx={{ minWidth: 0 }}>
                  <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
                    <Typography sx={{ fontWeight: 800 }}>{item.fileName || `Occurrence ${item.occurrenceId}`}</Typography>
                    <Chip size="small" color="warning" label={`${Math.round(item.confidence * 100)}% match`} />
                  </Stack>
                  <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
                    Received {dayjs(item.ingestedAtUtc).format('DD MMM YYYY, HH:mm')}
                  </Typography>
                  <Typography variant="body2" sx={{ mt: 1 }}>
                    Candidate {candidate?.nexoraSerial || 'identity unavailable'}
                    {candidate?.customerRfqReference ? ` | RFQ ${candidate.customerRfqReference}` : ''}
                  </Typography>
                </Box>
                <Button
                  variant="contained"
                  startIcon={<ReviewIcon />}
                  onClick={() => navigate(`/procurement/leads/ingestion/${item.batchId}`)}
                >
                  Review evidence
                </Button>
              </Stack>
            </Paper>
          );
        })}
      </Stack>
    </Box>
  );
}
