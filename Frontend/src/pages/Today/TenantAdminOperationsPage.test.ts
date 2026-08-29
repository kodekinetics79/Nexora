import { describe, expect, it } from 'vitest';
import type { ExtractionDeadLetter } from '../../api/services/operationalReadinessService';
import { canRetryExtractionDeadLetter } from './TenantAdminOperationsPage';

const item = (blocksReadiness: boolean, canRetry: boolean): ExtractionDeadLetter => ({
  jobId: 1, batchId: 'batch', sourceDocumentOccurrenceId: 2, fileName: 'rfq.doc',
  sourceType: 'Email', attempts: 3, maxAttempts: 3, failureCategory: 'EVIDENCE_MISSING',
  createdOn: '2026-08-29T00:00:00Z', updatedOn: '2026-08-29T00:00:00Z',
  resolution: blocksReadiness ? 'Open' : 'SourceObjectUnavailable', blocksReadiness, canRetry,
});

describe('tenant extraction recovery actions', () => {
  it('keeps terminal source-loss evidence visible without offering another retry', () => {
    expect(canRetryExtractionDeadLetter(item(false, false))).toBe(false);
    expect(canRetryExtractionDeadLetter(item(true, true))).toBe(true);
  });

  it('does not confuse a readiness blocker with an actually retryable row', () => {
    expect(canRetryExtractionDeadLetter(item(true, false))).toBe(false);
    expect(canRetryExtractionDeadLetter(item(false, true))).toBe(true);
  });
});
