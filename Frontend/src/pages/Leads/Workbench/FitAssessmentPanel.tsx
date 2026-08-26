import React from 'react';
import {
  Alert,
  Box,
  Button,
  Chip,
  FormControl,
  InputLabel,
  MenuItem,
  Paper,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material';
import { SaveOutlined as SaveIcon } from '@mui/icons-material';
import type {
  FitAssessmentDTO,
  FitCriterionDTO,
  FitCriterionDecision,
  OverallFitDecision,
  SaveFitAssessmentRequest,
} from '../../../api/services/leadDecisionService';
import { fitAssessmentDraftComplete } from './workbenchRules';

const CRITERION_OPTIONS: Array<{ value: FitCriterionDecision; label: string }> = [
  { value: 'PASS', label: 'Pass' },
  { value: 'CONCERN', label: 'Concern' },
  { value: 'UNKNOWN', label: 'Unknown' },
  { value: 'NOT_APPLICABLE', label: 'Not applicable' },
];

const OVERALL_OPTIONS: Array<{ value: OverallFitDecision; label: string }> = [
  { value: 'FIT', label: 'Fit to bid' },
  { value: 'CONDITIONAL', label: 'Conditional fit' },
  { value: 'NOT_FIT', label: 'Not fit' },
];

interface FitAssessmentPanelProps {
  assessment?: FitAssessmentDTO | null;
  leadRevisionId: number;
  decisionVersion: number;
  saving: boolean;
  readOnly?: boolean;
  onSave: (request: SaveFitAssessmentRequest) => void;
}

const FitAssessmentPanel: React.FC<FitAssessmentPanelProps> = ({
  assessment,
  leadRevisionId,
  decisionVersion,
  saving,
  readOnly = false,
  onSave,
}) => {
  const [overallDecision, setOverallDecision] = React.useState<OverallFitDecision>(assessment?.overallDecision ?? 'CONDITIONAL');
  const [rationale, setRationale] = React.useState(assessment?.rationale ?? '');
  const [criteria, setCriteria] = React.useState<FitCriterionDTO[]>(assessment?.criteria ?? []);

  React.useEffect(() => {
    setOverallDecision(assessment?.overallDecision ?? 'CONDITIONAL');
    setRationale(assessment?.rationale ?? '');
    setCriteria(assessment?.criteria ?? []);
  }, [assessment]);

  const updateCriterion = (code: string, patch: Partial<FitCriterionDTO>) => {
    setCriteria((current) => current.map((criterion) => criterion.code === code ? { ...criterion, ...patch } : criterion));
  };

  const unknownCount = criteria.filter((criterion) => criterion.decision === 'UNKNOWN').length;
  const hasCriteria = criteria.length > 0;
  const persisted = (assessment?.version ?? 0) > 0;
  const canSave = fitAssessmentDraftComplete(criteria, rationale) && !saving && !readOnly;

  return (
    <Paper component="section" aria-labelledby="fit-assessment-heading" variant="outlined" sx={{ p: 2.5, borderRadius: 2 }}>
      <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'flex-start' }, gap: 2, mb: 2 }}>
        <Box>
          <Typography id="fit-assessment-heading" variant="h6" sx={{ fontWeight: 900 }}>Fit assessment</Typography>
          <Typography variant="body2" color="text.secondary">
            A person records the commercial fit against governed criteria. Nexora does not calculate or invent a fit score.
          </Typography>
        </Box>
        {persisted ? <Chip label={`Saved version ${assessment!.version}`} color="success" variant="outlined" /> : <Chip label="Not saved" color="warning" variant="outlined" />}
      </Stack>

      {!hasCriteria ? (
        <Alert severity="error" sx={{ mb: 2 }}>
          No governed fit criteria are available. Promotion stays blocked until the business unit configures them.
        </Alert>
      ) : null}

      <Stack spacing={1.5}>
        {criteria.map((criterion) => (
          <Paper key={criterion.code} variant="outlined" sx={{ p: 1.5, borderRadius: 2 }}>
            <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} sx={{ alignItems: { xs: 'stretch', md: 'flex-start' } }}>
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 900 }}>{criterion.label}</Typography>
                {criterion.description ? <Typography variant="caption" color="text.secondary">{criterion.description}</Typography> : null}
              </Box>
              <FormControl size="small" sx={{ minWidth: 170 }} disabled={readOnly}>
                <InputLabel id={`fit-${criterion.code}-label`}>Assessment</InputLabel>
                <Select
                  labelId={`fit-${criterion.code}-label`}
                  label="Assessment"
                  value={criterion.decision}
                  onChange={(event) => updateCriterion(criterion.code, { decision: event.target.value as FitCriterionDecision })}
                >
                  {CRITERION_OPTIONS.map((option) => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
                </Select>
              </FormControl>
              <TextField
                size="small"
                label="Evidence or note"
                value={criterion.note ?? ''}
                onChange={(event) => updateCriterion(criterion.code, { note: event.target.value.slice(0, 500) })}
                disabled={readOnly}
                sx={{ flex: 1.2 }}
              />
            </Stack>
          </Paper>
        ))}
      </Stack>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ mt: 2, alignItems: 'flex-start' }}>
        <FormControl sx={{ minWidth: 220 }} disabled={readOnly}>
          <InputLabel id="overall-fit-label">Overall decision</InputLabel>
          <Select
            labelId="overall-fit-label"
            label="Overall decision"
            value={overallDecision}
            onChange={(event) => setOverallDecision(event.target.value as OverallFitDecision)}
          >
            {OVERALL_OPTIONS.map((option) => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
          </Select>
        </FormControl>
        <TextField
          fullWidth
          required
          label="Assessment rationale"
          value={rationale}
          onChange={(event) => setRationale(event.target.value.slice(0, 1000))}
          multiline
          minRows={2}
          disabled={readOnly}
          helperText={`${rationale.length}/1000${unknownCount > 0 ? ` · ${unknownCount === 1 ? '1 criterion remains' : `${unknownCount} criteria remain`} unknown` : ''}`}
        />
      </Stack>

      {!readOnly ? (
        <Stack direction="row" sx={{ justifyContent: 'flex-end', mt: 2 }}>
          <Button
            variant="contained"
            startIcon={<SaveIcon />}
            disabled={!canSave}
            onClick={() => onSave({
              expectedLeadRevisionId: leadRevisionId,
              expectedDecisionVersion: decisionVersion,
              expectedFitVersion: persisted ? assessment!.version : undefined,
              overallDecision,
              rationale: rationale.trim(),
              criteria: criteria.map((criterion) => ({ code: criterion.code, decision: criterion.decision, note: criterion.note?.trim() || undefined })),
            })}
            sx={{ fontWeight: 800 }}
          >
            {saving ? 'Saving assessment…' : 'Save fit assessment'}
          </Button>
        </Stack>
      ) : null}
    </Paper>
  );
};

export default FitAssessmentPanel;
