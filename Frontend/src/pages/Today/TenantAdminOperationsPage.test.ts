import { describe, expect, it } from 'vitest';
import type { ExtractionDeadLetter } from '../../api/services/operationalReadinessService';
import { normalizeExtractionDeadLetter, normalizeOperationsReadiness } from '../../api/services/operationalReadinessService';
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

  it('fails closed for terminal rows returned by an older backend', () => {
    const legacy = { ...item(true, true), canRetry: undefined } as unknown as ExtractionDeadLetter;
    expect(normalizeExtractionDeadLetter({ ...legacy, failureCategory: 'EVIDENCE_INTEGRITY' }).canRetry).toBe(false);
    expect(normalizeExtractionDeadLetter({ ...legacy, failureCategory: 'PASSWORD_PROTECTED' }).canRetry).toBe(false);
    expect(normalizeExtractionDeadLetter({ ...legacy, failureCategory: 'UNSUPPORTED_DOCUMENT' }).canRetry).toBe(false);
    expect(normalizeExtractionDeadLetter({ ...legacy, failureCategory: 'PROCESSING_PROVIDER' }).canRetry).toBe(true);
    expect(normalizeExtractionDeadLetter({ ...legacy, resolution: 'SourceObjectUnavailable' }).canRetry).toBe(false);
  });

  it('keeps readiness renderable while the backend rolls from the legacy contract', () => {
    const normalized = normalizeOperationsReadiness({
      checkedAt: '2026-08-29T00:00:00Z', deploymentReadiness: 'Healthy', blockingReasons: [],
      healthChecks: [], queues: [],
      aiLast30Days: { total: 4, local: 3, external: 1, unresolved: 0, externalSharePercent: 25 },
    });
    expect(normalized.aiExternalDependency).toMatchObject({
      total: 4, local: 3, external: 1, authorizedExternal: 0,
      externalSharePercent: 25, ceilingPercent: 10, windowSize: 4, ceilingBreached: true,
    });
  });
});
