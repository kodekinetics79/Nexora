import { describe, expect, it } from 'vitest';
import type { LeadDecisionEvidenceDTO } from '../../../api/services/leadDecisionService';
import { evidenceRenderKey, inspectableEvidenceUrl } from './evidenceRules';

const evidence = (over: Partial<LeadDecisionEvidenceDTO> = {}): LeadDecisionEvidenceDTO => ({
  occurrenceId: 1,
  kind: 'ATTACHMENT',
  name: 'request.pdf',
  status: 'RETAINED',
  sourceAvailable: true,
  ...over,
});

describe('inspectableEvidenceUrl', () => {
  it('does not treat retained metadata as inspectable without a content URL', () => {
    expect(inspectableEvidenceUrl(evidence())).toBeNull();
  });

  it('accepts either a download URL or content URL only when the source is available', () => {
    expect(inspectableEvidenceUrl(evidence({ downloadUrl: '/api/source/1' }))).toBe('/api/source/1');
    expect(inspectableEvidenceUrl(evidence({ contentUrl: '/api/content/1' }))).toBe('/api/content/1');
    expect(inspectableEvidenceUrl(evidence({ sourceAvailable: false, downloadUrl: '/api/source/1' }))).toBeNull();
  });
});

describe('evidenceRenderKey', () => {
  it('keeps same-named evidence rows distinct across documents and repeated projections', () => {
    const first = evidence({ occurrenceId: 18, sourceDocumentId: 41, name: 'request.csv' });
    const second = evidence({ occurrenceId: 18, sourceDocumentId: 42, name: 'request.csv' });

    expect(evidenceRenderKey(first, 0)).not.toBe(evidenceRenderKey(second, 1));
    expect(evidenceRenderKey(first, 0)).not.toBe(evidenceRenderKey(first, 1));
  });
});
