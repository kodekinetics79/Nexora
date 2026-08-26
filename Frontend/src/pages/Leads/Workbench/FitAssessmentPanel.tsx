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
import FormHelperText from '@mui/material/FormHelperText';
import { SaveOutlined as SaveIcon } from '@mui/icons-material';
import type {
  FitAssessmentDTO,
  FitCriterionDTO,
  FitCriterionDecision,
  OverallFitDecision,
  SaveFitAssessmentRequest,
} from '../../../api/services/leadDecisionService';
import {
  fitAssessmentFormComplete,
  initialOverallFitDecision,
  type OverallFitDraftDecision,
} from './fitAssessmentDraftState';
import FeatureHelp from '../../../components/common/FeatureHelp';

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
  const [overallDecision, setOverallDecision] = React.useState<OverallFitDraftDecision>(initialOverallFitDecision(assessment));
  const [rationale, setRationale] = React.useState(assessment?.rationale ?? '');
  const [criteria, setCriteria] = React.useState<FitCriterionDTO[]>(assessment?.criteria ?? []);

  React.useEffect(() => {
    setOverallDecision(initialOverallFitDecision(assessment));
    setRationale(assessment?.rationale ?? '');
    setCriteria(assessment?.criteria ?? []);
  }, [assessment]);

  const updateCriterion = (code: string, patch: Partial<FitCriterionDTO>) => {
    setCriteria((current) => current.map((criterion) => criterion.code === code ? { ...criterion, ...patch } : criterion));
  };

  const unknownCount = criteria.filter((criterion) => criterion.decision === 'UNKNOWN').length;
  const hasCriteria = criteria.length > 0;
  const persisted = (assessment?.version ?? 0) > 0;
  const canSave = fitAssessmentFormComplete(overallDecision, criteria, rationale) && !saving && !readOnly;

  return (
    <Paper
      component="section"
      aria-labelledby="fit-assessment-heading"
      aria-describedby="fit-assessment-description fit-assessment-requirements"
      variant="outlined"
      sx={{ p: 2.5, borderRadius: 2 }}
    >
      <Stack direction={{ xs: 'column', sm: 'row' }} sx={{ justifyContent: 'space-between', alignItems: { xs: 'stretch', sm: 'flex-start' }, gap: 2, mb: 2 }}>
        <Box>
          <Stack direction="row" spacing={0.25} sx={{ alignItems: 'center' }}>
            <Typography id="fit-assessment-heading" component="h2" variant="h6" sx={{ fontWeight: 900 }}>Fit assessment</Typography>
            <FeatureHelp
              label="fit assessment"
              description="A governed human decision about whether this opportunity fits your commercial rules. Nexora records the decision and evidence; it does not invent or automatically approve a fit score."
            />
          </Stack>
          <Typography id="fit-assessment-description" variant="body2" color="text.secondary">
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

      <Typography id="fit-assessment-requirements" variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
        Assess every criterion, explain each Concern in at least 5 characters, choose an overall decision, and enter a rationale of at least 5 characters.
      </Typography>

      <Stack spacing={1.5}>
        {criteria.map((criterion) => {
          const headingId = `fit-${criterion.code}-heading`;
          const descriptionId = `fit-${criterion.code}-description`;
          const assessmentLabelId = `fit-${criterion.code}-label`;
          const assessmentHelpId = `fit-${criterion.code}-assessment-help`;
          const noteHelpId = `fit-${criterion.code}-note-help`;
          const concernNoteMissing = criterion.decision === 'CONCERN' && (criterion.note?.trim().length ?? 0) < 5;

          return (
          <Paper
            key={criterion.code}
            component="section"
            role="group"
            aria-labelledby={headingId}
            aria-describedby={criterion.description ? descriptionId : undefined}
            variant="outlined"
            sx={{ p: 1.5, borderRadius: 2 }}
          >
            <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} sx={{ alignItems: { xs: 'stretch', md: 'flex-start' } }}>
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography id={headingId} component="h3" variant="subtitle2" sx={{ fontWeight: 900 }}>{criterion.label}</Typography>
                {criterion.description ? <Typography id={descriptionId} variant="caption" color="text.secondary">{criterion.description}</Typography> : null}
              </Box>
              <FormControl required size="small" sx={{ minWidth: 170 }} disabled={readOnly}>
                <InputLabel id={assessmentLabelId}>Assessment</InputLabel>
                <Select
                  labelId={`${headingId} ${assessmentLabelId}`}
                  label="Assessment"
                  value={criterion.decision}
                  onChange={(event) => updateCriterion(criterion.code, { decision: event.target.value as FitCriterionDecision })}
                  inputProps={{ 'aria-describedby': assessmentHelpId }}
                >
                  {CRITERION_OPTIONS.map((option) => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
                </Select>
                <FormHelperText id={assessmentHelpId}>Choose Pass, Concern, or Not applicable before saving.</FormHelperText>
              </FormControl>
              <TextField
                size="small"
                label="Evidence or note"
                value={criterion.note ?? ''}
                onChange={(event) => updateCriterion(criterion.code, { note: event.target.value.slice(0, 500) })}
                disabled={readOnly}
                error={concernNoteMissing}
                helperText={concernNoteMissing ? 'Required for Concern; enter at least 5 characters.' : 'Optional supporting evidence, up to 500 characters.'}
                slotProps={{
                  htmlInput: {
                    'aria-label': `${criterion.label} evidence or note`,
                    'aria-describedby': noteHelpId,
                  },
                  formHelperText: { id: noteHelpId },
                }}
                sx={{ flex: 1.2 }}
              />
            </Stack>
          </Paper>
          );
        })}
      </Stack>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ mt: 2, alignItems: 'flex-start' }}>
        <FormControl required sx={{ minWidth: 220 }} disabled={readOnly}>
          <InputLabel id="overall-fit-label" shrink>Overall decision</InputLabel>
          <Select
            labelId="overall-fit-label"
            label="Overall decision"
            value={overallDecision}
            onChange={(event) => setOverallDecision(event.target.value as OverallFitDecision)}
            displayEmpty
            renderValue={(value) => value
              ? OVERALL_OPTIONS.find((option) => option.value === value)?.label ?? value
              : 'Select an overall decision'}
            inputProps={{ 'aria-describedby': 'overall-fit-help' }}
          >
            <MenuItem value="" disabled>Select an overall decision</MenuItem>
            {OVERALL_OPTIONS.map((option) => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
          </Select>
          <FormHelperText id="overall-fit-help">No overall decision is assumed.</FormHelperText>
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
          error={rationale.length > 0 && rationale.trim().length < 5}
          helperText={`At least 5 characters required · ${rationale.length}/1000${unknownCount > 0 ? ` · ${unknownCount === 1 ? '1 criterion remains' : `${unknownCount} criteria remain`} unknown` : ''}`}
        />
      </Stack>

      {!readOnly ? (
        <Stack direction="row" sx={{ justifyContent: 'flex-end', mt: 2 }}>
          <Button
            variant="contained"
            startIcon={<SaveIcon />}
            disabled={!canSave}
            onClick={() => {
              if (overallDecision === '') return;
              onSave({
                expectedLeadRevisionId: leadRevisionId,
                expectedDecisionVersion: decisionVersion,
                expectedFitVersion: persisted ? assessment!.version : undefined,
                overallDecision,
                rationale: rationale.trim(),
                criteria: criteria.map((criterion) => ({ code: criterion.code, decision: criterion.decision, note: criterion.note?.trim() || undefined })),
              });
            }}
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
