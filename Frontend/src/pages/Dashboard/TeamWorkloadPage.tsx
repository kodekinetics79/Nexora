import { keepPreviousData, useQuery } from '@tanstack/react-query';
import {
  Box,
  Chip,
  Paper,
  Skeleton,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
  Button,
} from '@mui/material';
import {
  Groups as TeamIcon,
  Inbox as InboxIcon,
} from '@mui/icons-material';
import dashboardService, { type TeamWorkloadRowDTO } from '../../api/services/dashboardService';
import { useNavigate } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';

/**
 * WP-B1: manager view of who is carrying what right now. One row per rep plus
 * a highlighted "Unassigned" bucket. Server-side this is managers/admins only
 * (403 for everyone else) — the page degrades to a plain-language explainer.
 * Zero-training language: "waiting too long", not "SLA breach".
 */
export default function TeamWorkloadPage() {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  const canOpenRouting = hasPermission('Leads');
  const canOpenRepRecords = hasPermission('Users');
  const workload = useQuery({
    queryKey: ['dashboard', 'team-workload'],
    queryFn: dashboardService.getTeamWorkload,
    refetchInterval: 60_000,
    placeholderData: keepPreviousData,
    retry: (failureCount, error: any) =>
      error?.response?.status !== 403 && failureCount < 2,
  });

  const isForbidden = (workload.error as any)?.response?.status === 403;
  const rows: TeamWorkloadRowDTO[] = workload.data?.rows ?? [];
  const reps = rows.filter((r) => !r.isUnassignedBucket);
  const unassigned = rows.find((r) => r.isUnassignedBucket);
  const staleDays = workload.data?.staleQuoteDays ?? 7;

  return (
    <Box sx={{ maxWidth: 1100, mx: 'auto', p: { xs: 1, md: 2 } }}>
      {/* Header */}
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
        <TeamIcon color="primary" />
        <Typography variant="h5" sx={{ fontWeight: 900 }}>
          Team workload
        </Typography>
      </Stack>
      <Typography variant="body2" sx={{ color: 'text.secondary', mb: 2.5 }}>
        Who is carrying what right now — open leads, deadlines already passed, and quotes still
        waiting on a customer answer.
      </Typography>

      {isForbidden ? (
        <Paper variant="outlined" sx={{ p: 4, borderRadius: 3, textAlign: 'center' }}>
          <Typography variant="h6" sx={{ fontWeight: 700, mb: 1 }}>
            This page is for managers
          </Typography>
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            Team workload shows everyone&apos;s assignments, so it is only available to managers
            and administrators.
          </Typography>
        </Paper>
      ) : workload.isLoading ? (
        <Stack spacing={1}>
          {Array.from({ length: 5 }, (_, i) => (
            <Skeleton key={i} variant="rounded" height={48} sx={{ borderRadius: 2 }} />
          ))}
        </Stack>
      ) : workload.isError ? (
        <Paper variant="outlined" sx={{ p: 4, borderRadius: 3, textAlign: 'center' }}>
          <Typography variant="body1" sx={{ color: 'text.secondary', mb: 1.5 }}>We couldn&apos;t load the team right now.</Typography>
          <Button onClick={() => void workload.refetch()}>Retry</Button>
        </Paper>
      ) : (
        <TableContainer component={Paper} variant="outlined" sx={{ borderRadius: 3 }}>
          <Table size="small" aria-label="Team workload by rep">
            <TableHead>
              <TableRow sx={{ '& th': { fontWeight: 800, whiteSpace: 'nowrap' } }}>
                <TableCell>Team member</TableCell>
                <TableCell align="right">Open leads</TableCell>
                <TableCell align="right">Deadline passed</TableCell>
                <TableCell align="right">Quotes sent</TableCell>
                <TableCell align="right">
                  <Tooltip title={`Sent quotes with no customer answer for over ${staleDays} days`}>
                    <span>Waiting too long</span>
                  </Tooltip>
                </TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {/* Unassigned bucket first — it is the row a manager must act on. */}
              {unassigned && (
                <TableRow
                  sx={{
                    bgcolor: (t) =>
                      t.palette.mode === 'dark' ? 'rgba(245, 158, 11, 0.10)' : 'rgba(245, 158, 11, 0.08)',
                    '& td': { borderBottomWidth: 2 },
                  }}
                >
                  <TableCell>
                    <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                      <InboxIcon fontSize="small" sx={{ color: 'warning.main' }} />
                      <Box>
                        {canOpenRouting ? <Button color="inherit" sx={{ p: 0, minWidth: 0, justifyContent: 'flex-start', fontWeight: 800 }} onClick={() => navigate('/sales/routing')}>Unassigned</Button> : <Typography variant="body2" sx={{ fontWeight: 800 }}>Unassigned</Typography>}
                        <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                          Work nobody owns yet — assign these first
                        </Typography>
                      </Box>
                    </Stack>
                  </TableCell>
                  <CountCell value={unassigned.openLeads} bold />
                  <OverdueCell value={unassigned.overdueLeads} />
                  <CountCell value={unassigned.sentQuotes} bold />
                  <StaleCell value={unassigned.staleQuotes} />
                </TableRow>
              )}

              {reps.map((rep) => (
                <TableRow key={rep.userId} hover>
                  <TableCell>
                    {canOpenRepRecords ? <Button color="inherit" sx={{ p: 0, minWidth: 0, justifyContent: 'flex-start', fontWeight: 600 }} onClick={() => navigate(`/sales/reps/${rep.userId}`)}>{rep.name}</Button> : <Typography variant="body2" sx={{ fontWeight: 600 }}>{rep.name}</Typography>}
                    {rep.email && (
                      <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                        {rep.email}
                      </Typography>
                    )}
                  </TableCell>
                  <CountCell value={rep.openLeads} />
                  <OverdueCell value={rep.overdueLeads} />
                  <CountCell value={rep.sentQuotes} />
                  <StaleCell value={rep.staleQuotes} />
                </TableRow>
              ))}

              {reps.length === 0 && !unassigned && (
                <TableRow>
                  <TableCell colSpan={5} align="center" sx={{ py: 4 }}>
                    <Typography variant="body2" sx={{ color: 'text.secondary' }}>
                      No team members found for your business unit yet.
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
            </TableBody>
          </Table>
        </TableContainer>
      )}
    </Box>
  );
}

/** Plain count; quiet dash for zero so busy cells stand out. */
function CountCell({ value, bold = false }: { value: number; bold?: boolean }) {
  return (
    <TableCell align="right">
      {value > 0 ? (
        <Typography variant="body2" sx={{ fontWeight: bold ? 800 : 600 }}>
          {value}
        </Typography>
      ) : (
        <Typography variant="body2" sx={{ color: 'text.disabled' }}>
          —
        </Typography>
      )}
    </TableCell>
  );
}

/** Red chip when a rep has leads whose closing date already passed. */
function OverdueCell({ value }: { value: number }) {
  return (
    <TableCell align="right">
      {value > 0 ? (
        <Chip
          size="small"
          color="error"
          label={`${value} overdue`}
          sx={{ fontWeight: 700 }}
        />
      ) : (
        <Typography variant="body2" sx={{ color: 'text.disabled' }}>
          —
        </Typography>
      )}
    </TableCell>
  );
}

/** Amber chip for quotes the customer has not answered past the threshold. */
function StaleCell({ value }: { value: number }) {
  return (
    <TableCell align="right">
      {value > 0 ? (
        <Chip
          size="small"
          color="warning"
          label={`${value} waiting`}
          sx={{ fontWeight: 700 }}
        />
      ) : (
        <Typography variant="body2" sx={{ color: 'text.disabled' }}>
          —
        </Typography>
      )}
    </TableCell>
  );
}
