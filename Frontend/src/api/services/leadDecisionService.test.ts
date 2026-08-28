import { beforeEach, describe, expect, it, vi } from 'vitest';
import axiosInstance from '../axiosInstance';
import leadDecisionService, { type SaveFitAssessmentRequest, type SaveParticipationRequest } from './leadDecisionService';

vi.mock('../axiosInstance', () => ({
  default: {
    put: vi.fn(),
    post: vi.fn(),
  },
}));

const put = vi.mocked(axiosInstance.put);
const post = vi.mocked(axiosInstance.post);

beforeEach(() => {
  put.mockReset();
  post.mockReset();
});

describe('leadDecisionService idempotency contract', () => {
  it('uses the fit key supplied by the retry owner without regenerating it', async () => {
    const request: SaveFitAssessmentRequest = {
      expectedLeadRevisionId: 71,
      expectedDecisionVersion: 4,
      overallDecision: 'FIT',
      rationale: 'All governed criteria were reviewed.',
      criteria: [{ code: 'CAPABILITY', decision: 'PASS' }],
    };
    put.mockResolvedValue({ data: { version: 1, overallDecision: 'FIT', rationale: request.rationale, criteria: [] } });

    await leadDecisionService.saveFitAssessment(7, request, 'lead-fit:7:stable');
    await leadDecisionService.saveFitAssessment(7, request, 'lead-fit:7:stable');

    expect(put).toHaveBeenCalledTimes(2);
    expect(put.mock.calls.map((call) => call[2]?.headers?.['Idempotency-Key']))
      .toEqual(['lead-fit:7:stable', 'lead-fit:7:stable']);
  });

  it('uses the participation key supplied by the retry owner', async () => {
    const request: SaveParticipationRequest = {
      expectedLeadRevisionId: 71,
      expectedDecisionVersion: 4,
      expectedParticipationVersion: null,
      commit: false,
      lines: [{ revisionLineId: 711, decision: 'Clarify', reasonCode: 'SPEC_MISSING' }],
    };
    put.mockResolvedValue({ data: { decisionVersion: 5, participationVersion: 1, participationStatus: 'DRAFT' } });

    await leadDecisionService.saveParticipation(7, request, 'lead-participation-draft:7:stable');

    expect(put).toHaveBeenCalledWith(
      '/api/leads/7/participation',
      request,
      { headers: { 'Idempotency-Key': 'lead-participation-draft:7:stable' } },
    );
  });

  it('keeps a stable idempotency key on RFQ amendment-review retries', async () => {
    const request = {
      rfqId: 19,
      expectedLeadRevisionId: 72,
      reconciliationReason: 'Reviewed quantity and delivery changes against the RFQ.',
      confirmedHistoricalRfqUnchanged: true,
    };
    post.mockResolvedValue({ data: {
      rfqId: 19,
      reviewedThroughLeadRevisionId: 72,
      resolvedImpactCount: 1,
      replayed: false,
    } });

    await leadDecisionService.resolveRfqRevisionImpact(7, request, 'rfq-impact:7:stable');
    await leadDecisionService.resolveRfqRevisionImpact(7, request, 'rfq-impact:7:stable');

    expect(post).toHaveBeenCalledTimes(2);
    expect(post.mock.calls.map((call) => call[2]?.headers?.['Idempotency-Key']))
      .toEqual(['rfq-impact:7:stable', 'rfq-impact:7:stable']);
  });
});
