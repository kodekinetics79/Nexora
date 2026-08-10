import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import dayjs from 'dayjs';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  IconButton,
  MenuItem,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  Add as AddIcon,
  Delete as DeleteIcon,
  Download as DownloadIcon,
  Edit as EditIcon,
} from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import reportingService, {
  type ReportCadence,
  type ReportFormat,
  type ReportSubscriptionDTO,
  type UpsertReportSubscriptionRequest,
} from '../../../api/services/reportingService';

const DAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

const emptyForm = (reportKey: string): UpsertReportSubscriptionRequest => ({
  id: null,
  reportKey,
  cadence: 'WEEKLY',
  format: 'PDF',
  hourUtc: 6,
  // Sunday: the first working day in KSA, so a weekly report does not land inside the
  // Friday-Saturday weekend where nobody can act on it.
  dayOfWeek: 0,
  dayOfMonth: 1,
  windowDays: 7,
  recipients: '',
  isActive: true,
});

const scheduleLabel = (row: ReportSubscriptionDTO): string => {
  const at = `${String(row.hourUtc).padStart(2, '0')}:00 UTC`;
  if (row.cadence === 'DAILY') return `Every day at ${at}`;
  if (row.cadence === 'MONTHLY') return `Day ${row.dayOfMonth} of each month at ${at}`;
  return `Every ${DAY_NAMES[row.dayOfWeek] ?? 'Sunday'} at ${at}`;
};

const outcomeChip = (row: ReportSubscriptionDTO) => {
  if (!row.lastRunOutcome) return <Chip size="small" variant="outlined" label="Never run" />;
  if (row.lastRunOutcome === 'DELIVERED') return <Chip size="small" color="success" label="Delivered" />;
  if (row.lastRunOutcome === 'FAILED') return <Chip size="small" color="error" label="Failed" />;
  return <Chip size="small" color="warning" variant="outlined" label="Nothing to report" />;
};

/**
 * FR-DSH-06 — daily, weekly or monthly email delivery of PDF/Excel reports.
 *
 * Three things on this screen are deliberate:
 *  - **Last run and its detail are columns, not a log.** A schedule that stopped delivering is
 *    otherwise invisible until somebody notices a report they never received.
 *  - **"Not scheduled" is shown as itself.** A paused subscription has no next run at all; showing
 *    a stale past date would read as overdue.
 *  - **Download runs the same builder the schedule uses**, so a schedule can be checked now rather
 *    than a week from now.
 */
const ScheduledReportsPage: React.FC = () => {
  const queryClient = useQueryClient();
  const [editing, setEditing] = React.useState<UpsertReportSubscriptionRequest | null>(null);

  const catalog = useQuery({ queryKey: ['reporting', 'catalog'], queryFn: reportingService.getCatalog });
  const subscriptions = useQuery({
    queryKey: ['reporting', 'subscriptions'],
    queryFn: reportingService.listSubscriptions,
  });

  const save = useMutation({
    mutationFn: (body: UpsertReportSubscriptionRequest) => reportingService.upsertSubscription(body),
    onSuccess: () => {
      toast.success('Report schedule saved');
      setEditing(null);
      queryClient.invalidateQueries({ queryKey: ['reporting', 'subscriptions'] });
    },
    // The server's message reaches the user verbatim. The client invents no copy that could
    // contradict a validation rule it does not own.
    onError: (error: any) =>
      toast.error(error?.response?.data?.message ?? 'The report schedule could not be saved.'),
  });

  const remove = useMutation({
    mutationFn: (id: number) => reportingService.deleteSubscription(id),
    onSuccess: () => {
      toast.success('Report schedule removed');
      queryClient.invalidateQueries({ queryKey: ['reporting', 'subscriptions'] });
    },
    onError: (error: any) =>
      toast.error(error?.response?.data?.message ?? 'The report schedule could not be removed.'),
  });

  const reportKeys = catalog.data?.reportKeys ?? [];
  const rows = subscriptions.data ?? [];

  const titleFor = (key: string) => rows.find((r) => r.reportKey === key)?.reportTitle ?? key;

  return (
    <Box sx={{ maxWidth: 1200, mx: 'auto', p: { xs: 1, sm: 2, md: 3 } }}>
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={1}
        sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' }, mb: 2 }}
      >
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 900 }}>Scheduled reports</Typography>
          <Typography variant="body2" color="text.secondary">
            Email delivery of dashboard reports on a daily, weekly or monthly schedule. Reports are
            English-only in this release.
          </Typography>
        </Box>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          disabled={reportKeys.length === 0}
          onClick={() => setEditing(emptyForm(reportKeys[0]))}
        >
          New schedule
        </Button>
      </Stack>

      {subscriptions.isLoading ? (
        <Box sx={{ minHeight: 200, display: 'grid', placeItems: 'center' }}><CircularProgress /></Box>
      ) : rows.length === 0 ? (
        <Alert severity="info">
          No report is scheduled. Nothing is being emailed to anyone in this business unit.
        </Alert>
      ) : (
        <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>Report</TableCell>
                <TableCell>Schedule</TableCell>
                <TableCell>Covers</TableCell>
                <TableCell>Format</TableCell>
                <TableCell>Recipients</TableCell>
                <TableCell>Next run</TableCell>
                <TableCell>Last run</TableCell>
                <TableCell align="right">Actions</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {rows.map((row) => (
                <TableRow key={row.id} hover>
                  <TableCell sx={{ fontWeight: 700 }}>{row.reportTitle}</TableCell>
                  <TableCell>{scheduleLabel(row)}</TableCell>
                  <TableCell>{row.windowDays} day(s)</TableCell>
                  <TableCell>{row.format}</TableCell>
                  <TableCell>
                    <Tooltip title={row.recipients.join(', ')}>
                      <span>{row.recipients.length} address(es)</span>
                    </Tooltip>
                  </TableCell>
                  <TableCell>
                    {!row.isActive
                      ? <Chip size="small" variant="outlined" label="Paused" />
                      : row.nextRunOn
                        ? dayjs(row.nextRunOn).format('DD MMM YYYY HH:mm')
                        : <Chip size="small" color="warning" variant="outlined" label="Not scheduled" />}
                  </TableCell>
                  <TableCell>
                    <Stack spacing={0.25}>
                      {outcomeChip(row)}
                      {row.lastRunOn && (
                        <Typography variant="caption" color="text.secondary">
                          {dayjs(row.lastRunOn).format('DD MMM YYYY HH:mm')}
                        </Typography>
                      )}
                      {row.lastRunDetail && (
                        <Typography variant="caption" color="text.secondary">
                          {row.lastRunDetail}
                        </Typography>
                      )}
                    </Stack>
                  </TableCell>
                  <TableCell align="right">
                    <Tooltip title="Download this report now">
                      <IconButton
                        size="small"
                        onClick={() =>
                          reportingService
                            .download(row.reportKey, row.format, row.windowDays)
                            .catch(() => toast.error('The report could not be produced.'))
                        }
                      >
                        <DownloadIcon fontSize="small" />
                      </IconButton>
                    </Tooltip>
                    <IconButton
                      size="small"
                      onClick={() =>
                        setEditing({
                          id: row.id,
                          reportKey: row.reportKey,
                          cadence: row.cadence,
                          format: row.format,
                          hourUtc: row.hourUtc,
                          dayOfWeek: row.dayOfWeek,
                          dayOfMonth: row.dayOfMonth,
                          windowDays: row.windowDays,
                          recipients: row.recipients.join('; '),
                          isActive: row.isActive,
                        })
                      }
                    >
                      <EditIcon fontSize="small" />
                    </IconButton>
                    <IconButton size="small" color="error" onClick={() => remove.mutate(row.id)}>
                      <DeleteIcon fontSize="small" />
                    </IconButton>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </TableContainer>
      )}

      <Dialog open={editing !== null} onClose={() => setEditing(null)} fullWidth maxWidth="sm">
        <DialogTitle>{editing?.id ? 'Edit report schedule' : 'New report schedule'}</DialogTitle>
        <DialogContent>
          {editing && (
            <Stack spacing={2} sx={{ mt: 1 }}>
              <TextField
                select
                label="Report"
                value={editing.reportKey}
                onChange={(e) => setEditing({ ...editing, reportKey: e.target.value })}
                fullWidth
              >
                {reportKeys.map((key) => (
                  <MenuItem key={key} value={key}>{titleFor(key)}</MenuItem>
                ))}
              </TextField>

              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                <TextField
                  select
                  label="How often"
                  value={editing.cadence}
                  onChange={(e) => setEditing({ ...editing, cadence: e.target.value as ReportCadence })}
                  fullWidth
                >
                  {(catalog.data?.cadences ?? []).map((c) => (
                    <MenuItem key={c} value={c}>{c.charAt(0) + c.slice(1).toLowerCase()}</MenuItem>
                  ))}
                </TextField>
                <TextField
                  select
                  label="Format"
                  value={editing.format}
                  onChange={(e) => setEditing({ ...editing, format: e.target.value as ReportFormat })}
                  fullWidth
                >
                  {(catalog.data?.formats ?? []).map((f) => (
                    <MenuItem key={f} value={f}>{f === 'PDF' ? 'PDF' : 'Excel'}</MenuItem>
                  ))}
                </TextField>
              </Stack>

              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
                <TextField
                  type="number"
                  label="Hour (UTC)"
                  value={editing.hourUtc}
                  onChange={(e) => setEditing({ ...editing, hourUtc: Number(e.target.value) })}
                  slotProps={{ htmlInput: { min: 0, max: 23 } }}
                  helperText="Stated in UTC so it does not shift"
                  fullWidth
                />
                {editing.cadence === 'WEEKLY' && (
                  <TextField
                    select
                    label="Day of week"
                    value={editing.dayOfWeek}
                    onChange={(e) => setEditing({ ...editing, dayOfWeek: Number(e.target.value) })}
                    fullWidth
                  >
                    {DAY_NAMES.map((name, index) => (
                      <MenuItem key={name} value={index}>{name}</MenuItem>
                    ))}
                  </TextField>
                )}
                {editing.cadence === 'MONTHLY' && (
                  <TextField
                    type="number"
                    label="Day of month"
                    value={editing.dayOfMonth}
                    onChange={(e) => setEditing({ ...editing, dayOfMonth: Number(e.target.value) })}
                    slotProps={{ htmlInput: { min: 1, max: catalog.data?.maximumDayOfMonth ?? 28 } }}
                    helperText={`1 to ${catalog.data?.maximumDayOfMonth ?? 28} — later days do not exist in every month`}
                    fullWidth
                  />
                )}
              </Stack>

              <TextField
                type="number"
                label="Days of history each report covers"
                value={editing.windowDays}
                onChange={(e) => setEditing({ ...editing, windowDays: Number(e.target.value) })}
                slotProps={{ htmlInput: { min: 1, max: catalog.data?.maximumWindowDays ?? 732 } }}
                helperText="Independent of how often it is sent"
                fullWidth
              />

              <TextField
                label="Recipients"
                value={editing.recipients}
                onChange={(e) => setEditing({ ...editing, recipients: e.target.value })}
                helperText="Email addresses separated by semicolons. At least one is required."
                multiline
                minRows={2}
                fullWidth
              />

              <TextField
                select
                label="Status"
                value={editing.isActive ? 'active' : 'paused'}
                onChange={(e) => setEditing({ ...editing, isActive: e.target.value === 'active' })}
                helperText="A paused schedule has no next run at all; it is not queued up."
                fullWidth
              >
                <MenuItem value="active">Active</MenuItem>
                <MenuItem value="paused">Paused</MenuItem>
              </TextField>
            </Stack>
          )}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setEditing(null)}>Cancel</Button>
          <Button
            variant="contained"
            disabled={save.isPending}
            onClick={() => editing && save.mutate(editing)}
          >
            Save
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default ScheduledReportsPage;
