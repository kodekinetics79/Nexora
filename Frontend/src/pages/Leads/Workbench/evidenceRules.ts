import type { LeadDecisionEvidenceDTO } from '../../../api/services/leadDecisionService';

export const inspectableEvidenceUrl = (evidence: LeadDecisionEvidenceDTO): string | null => {
  if (!evidence.sourceAvailable) return null;
  return evidence.downloadUrl?.trim() || evidence.contentUrl?.trim() || null;
};
