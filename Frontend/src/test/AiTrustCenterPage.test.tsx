import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import AiTrustCenterPage from '../pages/PlatformGovernance/AiTrustCenterPage';

const getAiTrust = vi.fn();

vi.mock('../api/services/platformGovernanceService', () => ({
  platformGovernanceService: { getAiTrust: () => getAiTrust() },
}));

const VIEW = {
  policy: {
    isEnabled: true,
    externalProcessingAllowed: false,
    allowedPurposes: 'RFQ extraction',
    allowedProvider: null,
    allowedModel: null,
    monthlySoftTokenLimit: 10_000,
    monthlyHardTokenLimit: 20_000,
    maxTokensPerDocument: 2_000,
    externalDependencyCeilingPercent: 5,
    redactionRequired: true,
    allowedDataClassifications: 'Commercial',
    egressPolicy: 'DenyByDefault',
    dataResidency: 'US',
    retentionDays: 30,
    inputOutputAuditAllowed: false,
    privacyReviewRequired: true,
    version: 7,
    updatedOn: '2026-08-08T12:00:00Z',
    updatedBy: 'owner@nexora.local',
  },
  usage: {
    requests: 3,
    localRequests: 3,
    externalRequests: 0,
    externalDependencyPercent: 0,
    dependencyCeilingBreached: false,
    deniedRequests: 0,
    failedRequests: 0,
    injectionDetections: 0,
    inputTokens: 300,
    outputTokens: 100,
    reservedTokens: 0,
    settledTokens: 400,
    softTokenLimit: 10_000,
    hardTokenLimit: 20_000,
    estimatedExternalCost: {},
  },
  requests: [],
  audit: [{
    id: 41,
    action: 'POLICY_UPDATED',
    reason: 'Approved governance change',
    actorUserId: 9,
    occurredOn: '2026-08-08T12:00:00Z',
  }],
  inferencePosture: 'LocalFirst' as const,
};

const renderPage = () => render(
  <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <AiTrustCenterPage />
  </QueryClientProvider>,
);

beforeEach(() => {
  vi.clearAllMocks();
  getAiTrust.mockResolvedValue(VIEW);
});

describe('AiTrustCenterPage authority boundary', () => {
  it('keeps governance evidence readable and directs changes to Platform Admin', async () => {
    renderPage();

    expect(await screen.findByText(/AI policy and provider authorization are managed by a Platform Admin Owner/i)).toBeVisible();
    expect(screen.getByText('Local-first (no third-party egress)')).toBeVisible();
    expect(screen.queryByRole('button', { name: /edit policy/i })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole('tab', { name: 'Audit history' }));
    expect(screen.getByText('Approved governance change')).toBeVisible();
    expect(screen.queryByRole('button', { name: /restore prior state/i })).not.toBeInTheDocument();
    expect(getAiTrust).toHaveBeenCalledTimes(1);
  });
});
