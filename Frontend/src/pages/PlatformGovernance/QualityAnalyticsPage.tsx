import { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import {
  Alert, Box, Chip, CircularProgress, FormControl, InputLabel, MenuItem, Paper, Select,
  Stack, Tab, Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Tabs,
  Typography,
} from '@mui/material';
import { InsightsOutlined } from '@mui/icons-material';
import ArtifactStudioPage from './ArtifactStudioPage';
import { platformGovernanceService } from '../../api/services/platformGovernanceService';
import { QualityMetricCard, QualityRecommendationButton } from './QualityAnalyticsActions';

export default function QualityAnalyticsPage() {
  const [tab, setTab] = useState(0);
  const [windowDays, setWindowDays] = useState(30);
  const [drilldown, setDrilldown] = useState<string | undefined>();
  const quality = useQuery({
    queryKey: ['quality-analytics', windowDays, drilldown],
    queryFn: () => platformGovernanceService.getQualityAnalytics(windowDays, drilldown),
    enabled: tab === 0,
  });
  const selectedMetric = useMemo(() => quality.data?.metrics.find((metric) =>
    metric.drilldownKey === drilldown), [quality.data, drilldown]);

  return (
    <Box sx={{ maxWidth: 1600, mx: 'auto', p: { xs: 2, md: 3 } }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ justifyContent: 'space-between', gap: 2, mb: 2 }}>
        <Box><Typography variant="h5" sx={{ fontWeight: 750 }}>Quality Analytics Center</Typography>
          <Typography variant="body2" color="text.secondary">Reconciled document and workflow quality with record-level evidence.</Typography></Box>
        {tab === 0 && <FormControl size="small" sx={{ minWidth: 170 }}><InputLabel>Cohort</InputLabel>
          <Select label="Cohort" value={windowDays} onChange={(event) => setWindowDays(Number(event.target.value))}>
            <MenuItem value={7}>Last 7 days</MenuItem><MenuItem value={30}>Last 30 days</MenuItem>
            <MenuItem value={90}>Last 90 days</MenuItem><MenuItem value={365}>Last 365 days</MenuItem>
          </Select></FormControl>}
      </Stack>
      <Tabs value={tab} onChange={(_, value) => setTab(value)} sx={{ mb: 2 }}>
        <Tab label="Measured Quality" /><Tab label="Metric Definitions" />
      </Tabs>
      {tab === 1 ? <ArtifactStudioPage title="Quality Metric Definitions"
        subtitle="Version, test, publish and roll back cohort thresholds and metric controls."
        types={['QualityMetricSet']} /> : (
        <>
          {quality.isLoading && <Box sx={{ py: 10, textAlign: 'center' }}><CircularProgress /></Box>}
          {quality.isError && <Alert severity="error">Quality analytics could not be reconciled from the tenant evidence ledger.</Alert>}
          {quality.data && <>
            <Alert severity="info" sx={{ mb: 2 }}>{quality.data.accuracyLimitation}</Alert>
            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: 'repeat(2, 1fr)', xl: 'repeat(4, 1fr)' }, gap: 1.5, mb: 2 }}>
              {quality.data.metrics.map((metric) => <QualityMetricCard
                key={metric.key}
                metric={metric}
                selected={drilldown === metric.drilldownKey}
                onSelect={() => setDrilldown(metric.drilldownKey)}
              />)}
            </Box>
            {selectedMetric && <Alert severity="success" icon={<InsightsOutlined />} sx={{ mb: 2 }}>
              <strong>{selectedMetric.label}:</strong> {selectedMetric.definition}
            </Alert>}

            <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', lg: 'minmax(0, 1.35fr) minmax(320px, .65fr)' }, gap: 2, mb: 2 }}>
              <TableContainer component={Paper} variant="outlined">
                <Table size="small"><TableHead><TableRow><TableCell>Evidence record</TableCell><TableCell>Ingested</TableCell><TableCell>Intake</TableCell><TableCell>Path</TableCell><TableCell>Cost status</TableCell></TableRow></TableHead>
                  <TableBody>{quality.data.records.map((record) => <TableRow key={record.occurrenceId}>
                    <TableCell><Typography variant="body2" sx={{ fontWeight: 700 }}>{record.fileName}</Typography><Typography variant="caption" color="text.secondary">Occurrence {record.occurrenceId}</Typography></TableCell>
                    <TableCell>{new Date(record.ingestedOn).toLocaleString()}</TableCell>
                    <TableCell><Chip size="small" label={record.intakeStatus} /></TableCell>
                    <TableCell>{record.processingPath}</TableCell><TableCell>{record.costStatus}</TableCell>
                  </TableRow>)}
                  {!quality.data.records.length && <TableRow><TableCell colSpan={5}><Box sx={{ py: 6, textAlign: 'center' }}><Typography sx={{ fontWeight: 700 }}>No records in this drill-down</Typography><Typography variant="body2" color="text.secondary">The selected cohort has no qualifying evidence.</Typography></Box></TableCell></TableRow>}
                  </TableBody></Table>
              </TableContainer>
              <Stack sx={{ gap: 2 }}>
                <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="subtitle1" sx={{ fontWeight: 750, mb: 1 }}>Evidence-based recommendations</Typography>
                  <Stack sx={{ gap: 1.5 }}>{quality.data.recommendations.map((item) => <QualityRecommendationButton
                    key={item.title}
                    title={item.title}
                    priority={item.priority}
                    recommendation={item.recommendation}
                    evidence={item.evidence}
                    onSelect={() => setDrilldown(item.drilldownKey)}
                  />)}</Stack>
                </Paper>
                <Paper variant="outlined" sx={{ p: 2 }}><Typography variant="subtitle1" sx={{ fontWeight: 750, mb: 1 }}>Leading exception causes</Typography>
                  {quality.data.exceptionCauses.map((cause) => <Stack key={`${cause.category}-${cause.code}`} direction="row" sx={{ justifyContent: 'space-between', gap: 1, py: .5 }}><Typography variant="body2">{cause.code}</Typography><Chip size="small" label={cause.count} /></Stack>)}
                  {!quality.data.exceptionCauses.length && <Typography variant="body2" color="text.secondary">No classified exceptions in this cohort.</Typography>}
                </Paper>
              </Stack>
            </Box>
            <Typography variant="caption" color="text.secondary">Cohort {new Date(quality.data.from).toLocaleString()} to {new Date(quality.data.to).toLocaleString()} · {quality.data.definitionVersion}</Typography>
          </>}
        </>
      )}
    </Box>
  );
}
