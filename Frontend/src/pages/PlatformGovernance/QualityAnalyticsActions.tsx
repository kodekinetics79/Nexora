import { ButtonBase, Chip, Stack, Typography } from '@mui/material';
import CheckCircleOutlined from '@mui/icons-material/CheckCircleOutlined';
import ErrorOutlined from '@mui/icons-material/ErrorOutlined';
import type { QualityMetric } from '../../api/services/platformGovernanceService';

const displayValue = (metric: QualityMetric) => metric.value === null || metric.value === undefined
  ? 'Insufficient evidence' : `${metric.value.toLocaleString()}${metric.unit === '%' ? '%' : ` ${metric.unit}`}`;

interface QualityMetricCardProps {
  metric: QualityMetric;
  selected: boolean;
  onSelect: () => void;
}

export const QualityMetricCard = ({ metric, selected, onSelect }: QualityMetricCardProps) => (
  <ButtonBase
    type="button"
    onClick={onSelect}
    aria-label={`View evidence for ${metric.label}`}
    aria-pressed={selected}
    sx={{
      p: 2,
      width: '100%',
      display: 'block',
      boxSizing: 'border-box',
      textAlign: 'left',
      cursor: 'pointer',
      minHeight: 132,
      color: 'text.primary',
      font: 'inherit',
      border: '1px solid',
      borderColor: selected ? 'primary.main' : 'divider',
      bgcolor: 'background.paper',
      borderRadius: 1,
      '&:focus-visible': { outline: '3px solid', outlineColor: 'primary.main', outlineOffset: 2 },
    }}
  >
    <Stack direction="row" sx={{ justifyContent: 'space-between', gap: 1 }}>
      <Typography variant="body2" color="text.secondary">{metric.label}</Typography>
      {metric.evidenceStatus === 'Measured'
        ? <CheckCircleOutlined color="success" fontSize="small" />
        : <ErrorOutlined color="warning" fontSize="small" />}
    </Stack>
    <Typography variant="h6" sx={{ mt: 1, fontWeight: 750 }}>{displayValue(metric)}</Typography>
    <Typography variant="caption" color="text.secondary">{metric.numerator.toLocaleString()} / {metric.denominator.toLocaleString()} records</Typography>
  </ButtonBase>
);

interface QualityRecommendationButtonProps {
  title: string;
  priority: string;
  recommendation: string;
  evidence: string;
  onSelect: () => void;
}

export const QualityRecommendationButton = ({
  title, priority, recommendation, evidence, onSelect,
}: QualityRecommendationButtonProps) => (
  <ButtonBase
    type="button"
    onClick={onSelect}
    aria-label={`Review recommendation: ${title}`}
    sx={{
      width: '100%',
      display: 'block',
      p: 0,
      border: 0,
      bgcolor: 'transparent',
      color: 'inherit',
      font: 'inherit',
      textAlign: 'left',
      cursor: 'pointer',
      borderRadius: 1,
      '&:focus-visible': { outline: '3px solid', outlineColor: 'primary.main', outlineOffset: 2 },
    }}
  >
    <Stack direction="row" sx={{ gap: .75, alignItems: 'center' }}>
      <Chip size="small" label={priority} color={priority === 'Critical' ? 'error' : priority === 'High' ? 'warning' : 'default'} />
      <Typography variant="body2" sx={{ fontWeight: 700 }}>{title}</Typography>
    </Stack>
    <Typography variant="body2" sx={{ mt: .5 }}>{recommendation}</Typography>
    <Typography variant="caption" color="text.secondary">{evidence}</Typography>
  </ButtonBase>
);
