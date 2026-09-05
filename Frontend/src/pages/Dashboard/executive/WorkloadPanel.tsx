import { Alert, Box, Button, Paper, Skeleton, Stack, Typography, useTheme } from '@mui/material';
import { ArrowForwardRounded as GoIcon } from '@mui/icons-material';
import { Bar, BarChart, CartesianGrid, Cell, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts';
import type { TeamWorkloadDTO } from '../../../api/services/dashboardService';

/**
 * Who is carrying what, as bars a manager reads across the room: open leads and the deadlines
 * already passed, per person, with the work nobody owns drawn first and in amber — it is the row
 * a manager must act on. The full table, with quotes sent and "waiting too long", is one click
 * away on Team workload.
 *
 * Server-side this is managers and administrators only. Anyone else who reaches this screen sees
 * nothing here rather than an error: the panel is theirs to lack, not a fault.
 */
export interface WorkloadPanelProps {
  data?: TeamWorkloadDTO;
  loading?: boolean;
  forbidden?: boolean;
  error?: boolean;
  onOpen?: () => void;
}

export default function WorkloadPanel({ data, loading, forbidden, error, onOpen }: WorkloadPanelProps) {
  const theme = useTheme();
  const dark = theme.palette.mode === 'dark';
  if (forbidden) return null;
  const rows = (data?.rows ?? [])
    .slice()
    .sort((a, b) => Number(b.isUnassignedBucket) - Number(a.isUnassignedBucket) || (b.openLeads + b.overdueLeads) - (a.openLeads + a.overdueLeads))
    .slice(0, 8)
    .map((r) => ({ ...r, name: r.isUnassignedBucket ? 'Unassigned' : (r.name.length > 18 ? `${r.name.slice(0, 17)}…` : r.name) }));
  const grid = dark ? 'rgba(163,169,181,0.16)' : 'rgba(42,47,58,0.12)';
  const axis = theme.palette.text.secondary;
  const graphite = dark ? '#7d8597' : '#3a4050';

  return (
    <Paper variant="outlined" className="nx-glass" sx={{ p: { xs: 1.5, sm: 2 }, borderRadius: 3, height: '100%' }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 1 }}>
        <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>Who is carrying what</Typography>
        {onOpen && (
          <Button size="small" endIcon={<GoIcon />} onClick={onOpen} sx={{ fontWeight: 700 }}>
            Team workload
          </Button>
        )}
      </Stack>
      {loading ? (
        <Skeleton variant="rounded" height={220} sx={{ mt: 1.5, borderRadius: 2 }} />
      ) : error || !data ? (
        <Alert severity="warning" sx={{ mt: 1.5 }}>The team's workload could not be loaded right now.</Alert>
      ) : rows.length === 0 ? (
        <Alert severity="info" sx={{ mt: 1.5 }}>No open work is assigned or waiting.</Alert>
      ) : (
        <Box sx={{ mt: 1, height: 232 }} aria-label="Open and overdue leads per team member">
          <ResponsiveContainer width="100%" height="100%">
            <BarChart data={rows} layout="vertical" margin={{ top: 4, right: 12, left: 8, bottom: 0 }} barCategoryGap={8}>
              <CartesianGrid strokeDasharray="3 3" stroke={grid} horizontal={false} />
              <XAxis type="number" stroke={axis} fontSize={11} tickLine={false} axisLine={false} allowDecimals={false} />
              <YAxis type="category" dataKey="name" stroke={axis} fontSize={12} tickLine={false} axisLine={false} width={124} />
              <Tooltip
                cursor={{ fill: dark ? 'rgba(255,255,255,0.04)' : 'rgba(42,47,58,0.06)' }}
                contentStyle={{
                  background: dark ? '#1b1f26' : '#ffffff', border: `1px solid ${dark ? 'rgba(170,176,190,0.24)' : 'rgba(95,102,115,0.2)'}`,
                  borderRadius: 10, color: theme.palette.text.primary, fontSize: 12,
                }}
              />
              <Legend wrapperStyle={{ fontSize: 12 }} />
              <Bar dataKey="openLeads" name="Open leads" stackId="w" radius={[0, 0, 0, 0]} maxBarSize={22}>
                {rows.map((r) => <Cell key={`o-${r.name}`} fill={r.isUnassignedBucket ? '#f59e0b' : graphite} />)}
              </Bar>
              <Bar dataKey="overdueLeads" name="Deadline passed" stackId="w" radius={[0, 6, 6, 0]} maxBarSize={22}>
                {rows.map((r) => <Cell key={`d-${r.name}`} fill={r.isUnassignedBucket ? '#b45309' : '#c9931a'} />)}
              </Bar>
            </BarChart>
          </ResponsiveContainer>
        </Box>
      )}
    </Paper>
  );
}
