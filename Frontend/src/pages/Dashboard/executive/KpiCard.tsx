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
  Tooltip,
  Typography,
} from '@mui/material';
import { ArrowForward as DrillDownIcon, Close as CloseIcon } from '@mui/icons-material';
import type { Release01KpiDTO, Release01KpiUnit } from '../../../api/services/dashboardService';
import { neuRaised, neuSlab } from '../glance/neumorphic';

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

  const records = `${drillDownRecords.length} record${drillDownRecords.length === 1 ? '' : 's'}`;

  /**
   * A slim tile: the label, the figure, and — when the KPI can open its rows — the way to them.
   * The definition sits behind the tile's tooltip rather than under the figure, because this row
   * sits above the bands and has to cost one line of the screen, not a third of it.
   */
  return (
    <Paper
      component="article"
      variant="outlined"
      className="nx-neu nx-enter"
      data-decorative-motion="true"
      aria-label={`${kpi.label}: ${formatKpiValue(kpi)}. ${kpi.definition}`}
      style={{ animationDelay: `${Math.min(index, 8) * 30}ms` }}
      sx={(theme) => ({
        ...neuSlab(theme.palette.mode, 6),
        px: 2, py: 1, borderRadius: 3.5, minWidth: 0,
        display: 'flex', alignItems: 'center', gap: 1.5,
        transition: 'box-shadow 180ms ease-out',
        '&:hover': { boxShadow: neuRaised(theme.palette.mode, 8) },
        '@media (prefers-reduced-motion: reduce)': { transition: 'none' },
      })}
    >
      <Tooltip
        title={kpi.state === 'insufficient_data' && kpi.insufficientDataReason
          ? `${kpi.definition} ${kpi.insufficientDataReason}`
          : kpi.definition}
        placement="bottom-start"
      >
        <Box sx={{ minWidth: 0, flexGrow: 1 }}>
          <Typography
            variant="caption"
            sx={{ display: 'block', fontWeight: 700, color: 'text.secondary', lineHeight: 1.2, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}
          >
            {kpi.label}
          </Typography>
          <Typography
            sx={{
              fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontSize: kpi.state === 'available' ? 22 : 15,
              fontWeight: 900, lineHeight: 1.15, fontVariantNumeric: 'tabular-nums',
              color: kpi.state === 'available' ? 'text.primary' : 'text.secondary',
            }}
          >
            {formatKpiValue(kpi)}
          </Typography>
        </Box>
      </Tooltip>
      {canDrillDown && (
        <Button
          size="small"
          endIcon={<DrillDownIcon />}
          onClick={() => setDrillDownOpen(true)}
          aria-label={`View ${records} behind ${kpi.label}`}
          sx={{ flexShrink: 0, px: 0.75, minWidth: 0, whiteSpace: 'nowrap' }}
        >
          {records}
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
