import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Box,
  Button,
  Chip,
  Dialog,
  DialogContent,
  DialogTitle,
  IconButton,
  List,
  ListItem,
  ListItemButton,
  ListItemText,
  Paper,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material';
import { ArrowForward as DrillDownIcon, Close as CloseIcon } from '@mui/icons-material';
import type { Release01KpiDTO, Release01KpiUnit } from '../../../api/services/dashboardService';

/**
 * One verified Release 01 KPI with its definition, its "insufficient data" honesty and the
 * records behind it. Moved here unchanged from the old dashboard page: the executive view keeps
 * the verified snapshot as its evidence row, beneath the glance.
 */
export const formatKpiValue = (kpi: Release01KpiDTO): string => {
  if (kpi.state === 'insufficient_data' || kpi.value === null) {
    return 'Insufficient data';
  }

  if (kpi.unit === 'percentage') return `${kpi.value.toLocaleString('en-US', { maximumFractionDigits: 1 })}%`;
  const formats: Record<Exclude<Release01KpiUnit, 'percentage'>, Intl.NumberFormatOptions> = {
    count: { maximumFractionDigits: 0 },
    currency: { maximumFractionDigits: 2 },
    hours: { maximumFractionDigits: 1 },
    score: { maximumFractionDigits: 1 },
    weighted_work: { maximumFractionDigits: 1 },
  };
  const formatted = new Intl.NumberFormat('en-US', formats[kpi.unit]).format(kpi.value);
  return kpi.unit === 'hours' ? `${formatted} h` : formatted;
};

export const drillDownRoute = (recordType: string, recordId: number): string | null => {
  if (recordType === 'lead') return `/procurement/leads/view/${recordId}`;
  if (recordType === 'rfq') return `/procurement/rfqs/view/${recordId}`;
  if (recordType === 'quote') return `/sales/quotes/view/${recordId}`;
  return null;
};

export default function KpiCard({ kpi, index = 0 }: { kpi: Release01KpiDTO; index?: number }) {
  const navigate = useNavigate();
  const [drillDownOpen, setDrillDownOpen] = useState(false);
  const drillDownRecords = kpi.drillDownIdentifiers;
  const canDrillDown = kpi.state === 'available' && drillDownRecords.length > 0;

  return (
    <Paper
      component="article"
      variant="outlined"
      className="nx-glass nx-enter"
      data-decorative-motion="true"
      style={{ animationDelay: `${Math.min(index, 8) * 30}ms` }}
      sx={{
        p: 2, minHeight: 160, borderRadius: 2, display: 'flex', flexDirection: 'column',
        transition: 'transform 180ms cubic-bezier(0.2, 0.7, 0.2, 1), box-shadow 180ms ease-out',
        '&:hover': { transform: 'translateY(-3px)', boxShadow: (theme) => `inset 0 1px 0 rgba(255,255,255,${theme.palette.mode === 'dark' ? 0.08 : 0.9}), 0 22px 44px -22px rgba(15,18,24,${theme.palette.mode === 'dark' ? 0.9 : 0.4})` },
        '@media (prefers-reduced-motion: reduce)': { transition: 'none', '&:hover': { transform: 'none' } },
      }}
    >
      <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start', justifyContent: 'space-between' }}>
        <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>
          {kpi.label}
        </Typography>
        <Chip
          size="small"
          label={kpi.state === 'available' ? 'Available' : 'Insufficient data'}
          color={kpi.state === 'available' ? 'success' : 'default'}
          variant="outlined"
        />
      </Stack>
      <Typography
        variant={kpi.state === 'available' ? 'h4' : 'h6'}
        sx={{ mt: 1.5, fontWeight: 900, fontVariantNumeric: 'tabular-nums', color: kpi.state === 'available' ? 'text.primary' : 'text.secondary' }}
      >
        {formatKpiValue(kpi)}
      </Typography>
      <Tooltip title={kpi.definition} placement="top-start">
        <Typography
          variant="body2"
          sx={{ mt: 1, color: 'text.secondary', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}
        >
          {kpi.definition}
        </Typography>
      </Tooltip>
      {kpi.state === 'insufficient_data' && kpi.insufficientDataReason && (
        <Typography variant="caption" sx={{ mt: 1, color: 'text.secondary' }}>
          {kpi.insufficientDataReason}
        </Typography>
      )}
      <Box sx={{ flexGrow: 1 }} />
      {canDrillDown && (
        <Button
          size="small"
          endIcon={<DrillDownIcon />}
          onClick={() => setDrillDownOpen(true)}
          sx={{ alignSelf: 'flex-start', mt: 1, px: 0 }}
        >
          View {kpi.drillDownIdentifiers.length} record{kpi.drillDownIdentifiers.length === 1 ? '' : 's'}
        </Button>
      )}
      <Dialog open={drillDownOpen} onClose={() => setDrillDownOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          {kpi.label} records
          <IconButton aria-label="Close drill-down" onClick={() => setDrillDownOpen(false)}>
            <CloseIcon />
          </IconButton>
        </DialogTitle>
        <DialogContent dividers>
          <List disablePadding>
            {drillDownRecords.map((record) => {
              const route = drillDownRoute(record.recordType.toLowerCase(), record.recordId);
              const content = (
                <ListItemText
                  primary={record.nexoraSerial}
                  secondary={`${record.recordType.toUpperCase()} #${record.recordId}${record.classification ? ` | ${record.classification}` : ''}`}
                />
              );
              return route ? (
                <ListItemButton key={`${record.recordType}-${record.recordId}`} onClick={() => navigate(route)}>
                  {content}<DrillDownIcon />
                </ListItemButton>
              ) : (
                <ListItem key={`${record.recordType}-${record.recordId}`}>
                  {content}
                  <Chip size="small" label="No detail route" variant="outlined" />
                </ListItem>
              );
            })}
          </List>
        </DialogContent>
      </Dialog>
    </Paper>
  );
}
