import type {
  FitAssessmentDTO,
  FitCriterionDTO,
  OverallFitDecision,
} from '../../../api/services/leadDecisionService';
import { fitAssessmentDraftComplete } from './workbenchRules';

export type OverallFitDraftDecision = OverallFitDecision | '';

export const initialOverallFitDecision = (
  assessment?: FitAssessmentDTO | null,
): OverallFitDraftDecision => (assessment?.version ?? 0) > 0 ? assessment!.overallDecision : '';

export const fitAssessmentFormComplete = (
  overallDecision: OverallFitDraftDecision,
  criteria: FitCriterionDTO[],
  rationale: string,
): boolean => overallDecision !== '' && fitAssessmentDraftComplete(criteria, rationale);
