import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import { SnackbarProvider } from 'notistack';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { platformApi } from '../../api/client';
import type { Tenant, TenantAiPolicy } from '../../types';
import AiGovernanceTab from './AiGovernanceTab';

const policy: TenantAiPolicy = {
  businessUnitId: '4', isEnabled: true, externalProcessingAllowed: false,
  allowedPurposes: ['RfqExtraction'], allowedProvider: null, allowedModel: null,
  monthlySoftTokenLimit: 10_000, monthlyHardTokenLimit: 20_000, maxTokensPerDocument: 2_000,
  externalInputCostPerMillionTokens: null, externalOutputCostPerMillionTokens: null,
  externalCostCurrency: null, externalPricingVersion: null, externalDependencyCeilingPercent: 5,
  redactionRequired: true, allowedDataClassifications: 'Public,Internal',
  egressPolicy: 'RedactedFieldsOnly', dataResidency: 'US', retentionDays: 30,
  inputOutputAuditAllowed: false, privacyReviewRequired: true, localComputeCostPerHour: null,
  ocrCostPerPage: null, localCostCurrency: null, version: 2,
  updatedOn: '2026-08-08T12:00:00Z', updatedBy: 'owner@nexora.local',
};

beforeEach(() => {
  vi.restoreAllMocks();
  vi.spyOn(platformApi, 'getTenantAiPolicy').mockResolvedValue(policy);
  vi.spyOn(platformApi, 'getTenantAiProviders').mockResolvedValue({
    resolvedProvider: {
      provider: 'Ollama', endpoint: 'https://ollama.example', model: 'llama3',
      providerClass: 'External', classificationReason: 'non_loopback_endpoint', isResolved: true,
    },
    resolvedProviderIsAuthorizedForUnstructured: false,
    resolvedProviderDecisionReason: 'external_endpoint_not_authorized',
    authorizations: [],
  });
});

describe('AiGovernanceTab', () => {
  it('renders policy evidence and the resolved provider with Owner controls', async () => {
    render(
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SnackbarProvider><AiGovernanceTab tenant={{ id: '9', name: 'Acme' } as Tenant} /></SnackbarProvider>
      </QueryClientProvider>,
    );

    expect(await screen.findByText('Effective AI policy')).toBeVisible();
    expect(screen.getByText(/https:\/\/ollama\.example/)).toBeVisible();
    expect(screen.getByRole('button', { name: 'Edit policy' })).toBeVisible();
    expect(screen.getByRole('button', { name: 'Authorize provider' })).toBeVisible();
    expect(platformApi.getTenantAiPolicy).toHaveBeenCalledWith('9');
    expect(platformApi.getTenantAiProviders).toHaveBeenCalledWith('9');
  });
});
