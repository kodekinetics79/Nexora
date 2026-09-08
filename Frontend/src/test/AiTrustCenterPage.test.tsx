import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { AiTrustCenterView } from '../api/services/platformGovernanceService';
import AiTrustCenterPage from '../pages/PlatformGovernance/AiTrustCenterPage';

const getAiTrust = vi.fn();

vi.mock('../api/services/platformGovernanceService', () => ({
  platformGovernanceService: { getAiTrust: () => getAiTrust() },
}));

const VIEW: AiTrustCenterView = {
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
    authorizedExternalRequests: 0,
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
  dependency: {
    total: 3,
    local: 3,
    external: 0,
    authorizedExternal: 0,
    unresolved: 0,
    externalSharePercent: 0,
    ceilingPercent: 5,
    windowSize: 50,
    ceilingBreached: false,
  },
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

  it('does not warn about a ceiling when every external call carried an authorization', async () => {
    // The reported defect: a deployment whose inference endpoint is not loopback classifies
    // every call External, so the screen showed "External dependency is 100.00%, above the
    // 10.00% ceiling" permanently — while enforcement denied nothing, because each of those
    // calls held an allow-list receipt and the ceiling exempts those. The banner is raised on
    // the unauthorized share now, so all-authorized egress is reported, not alarmed about.
    getAiTrust.mockResolvedValue({
      ...VIEW,
      usage: {
        ...VIEW.usage,
        requests: 40,
        localRequests: 0,
        externalRequests: 40,
        authorizedExternalRequests: 40,
        externalDependencyPercent: 0,
        dependencyCeilingBreached: false,
      },
      dependency: {
        ...VIEW.dependency,
        total: 40,
        local: 0,
        external: 40,
        authorizedExternal: 40,
        externalSharePercent: 0,
        ceilingBreached: false,
      },
    });
    renderPage();

    expect(await screen.findByText('0 / 40 (40 authorized)')).toBeVisible();
    expect(screen.queryByText(/above the .* ceiling/i)).not.toBeInTheDocument();
  });

  it('warns when the unauthorized share breaches the ceiling', async () => {
    getAiTrust.mockResolvedValue({
      ...VIEW,
      usage: {
        ...VIEW.usage,
        requests: 40,
        localRequests: 10,
        externalRequests: 30,
        authorizedExternalRequests: 20,
        externalDependencyPercent: 25,
        dependencyCeilingBreached: true,
      },
      dependency: {
        ...VIEW.dependency,
        total: 40,
        local: 10,
        external: 30,
        authorizedExternal: 20,
        externalSharePercent: 25,
        ceilingBreached: true,
      },
    });
    renderPage();

    expect(await screen.findByText(
      /Unauthorized external dependency is 25\.00%, above the 5\.00% ceiling, across the last 40 of 50 governed calls/i,
    )).toBeVisible();
  });
});
