import { describe, expect, it } from 'vitest';
import type { ExtractionDeadLetter } from '../../api/services/operationalReadinessService';
import { canRetryExtractionDeadLetter } from './TenantAdminOperationsPage';

const item = (blocksReadiness: boolean): ExtractionDeadLetter => ({
  jobId: 1, batchId: 'batch', sourceDocumentOccurrenceId: 2, fileName: 'rfq.doc',
  sourceType: 'Email', attempts: 3, maxAttempts: 3, failureCategory: 'EVIDENCE_MISSING',
  createdOn: '2026-08-29T00:00:00Z', updatedOn: '2026-08-29T00:00:00Z',
  resolution: blocksReadiness ? 'Open' : 'SourceObjectUnavailable', blocksReadiness,
});

describe('tenant extraction recovery actions', () => {
  it('keeps terminal source-loss evidence visible without offering another retry', () => {
    expect(canRetryExtractionDeadLetter(item(false))).toBe(false);
    expect(canRetryExtractionDeadLetter(item(true))).toBe(true);
  });
});
