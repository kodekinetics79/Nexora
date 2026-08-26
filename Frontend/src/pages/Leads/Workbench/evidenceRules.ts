import type { LeadDecisionEvidenceDTO } from '../../../api/services/leadDecisionService';

export const inspectableEvidenceUrl = (evidence: LeadDecisionEvidenceDTO): string | null => {
  if (!evidence.sourceAvailable) return null;
  return evidence.downloadUrl?.trim() || evidence.contentUrl?.trim() || null;
};

export const evidenceRenderKey = (evidence: LeadDecisionEvidenceDTO, ordinal: number): string =>
  [evidence.occurrenceId, evidence.sourceDocumentId ?? 'metadata', evidence.kind, evidence.name, ordinal].join(':');
