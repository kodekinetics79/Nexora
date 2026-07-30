import axiosInstance from '../axiosInstance';

export type GovernedArtifactType = 'CommercialTaxonomy' | 'DocumentSkill' | 'Model' | 'Rule' |
  'Dataset' | 'Connector' | 'TestSuite' | 'ReleaseCandidate';
export type GovernedLifecycleStatus = 'Draft' | 'Test' | 'Production' | 'Archived';
export type HumanActionStatus = 'Open' | 'InReview' | 'Escalated' | 'Completed' | 'Rejected';
export type HumanActionPriority = 'Low' | 'Medium' | 'High' | 'Critical';

export interface HumanActionItem {
  id: number;
  actionType: string;
  sourceType: string;
  sourceReference: string;
  title: string;
  summary: string;
  recommendation: string;
  evidenceJson: string;
  confidence: number;
  commercialImpact: string;
  resumeActionCode: string;
  priority: HumanActionPriority;
  status: HumanActionStatus;
  assignedToUserId?: number | null;
  dueOn: string;
  isOverdue: boolean;
  version: number;
  updatedOn: string;
}

export interface HumanActionDetail {
  item: HumanActionItem;
  events: Array<{
    id: number;
    fromStatus?: HumanActionStatus | null;
    toStatus: HumanActionStatus;
    action: string;
    comment: string;
    actorUserId: number;
    occurredOn: string;
  }>;
}

export interface AiTrustPolicy {
  isEnabled: boolean;
  externalProcessingAllowed: boolean;
  allowedPurposes: string;
  allowedProvider?: string | null;
  allowedModel?: string | null;
  monthlySoftTokenLimit?: number | null;
  monthlyHardTokenLimit?: number | null;
  maxTokensPerDocument?: number | null;
  externalInputCostPerMillionTokens?: number | null;
  externalOutputCostPerMillionTokens?: number | null;
  externalCostCurrency?: string | null;
  externalPricingVersion?: string | null;
  externalDependencyCeilingPercent: number;
  redactionRequired: boolean;
  allowedDataClassifications: string;
  egressPolicy: string;
  dataResidency: string;
  retentionDays: number;
  inputOutputAuditAllowed: boolean;
  privacyReviewRequired: boolean;
  localComputeCostPerHour?: number | null;
  ocrCostPerPage?: number | null;
  localCostCurrency?: string | null;
  version: number;
  updatedOn: string;
  updatedBy: string;
}

export interface AiTrustCenterView {
  policy: AiTrustPolicy;
  usage: {
    requests: number; localRequests: number; externalRequests: number;
    externalDependencyPercent: number; dependencyCeilingBreached: boolean;
    deniedRequests: number; failedRequests: number; injectionDetections: number;
    inputTokens: number; outputTokens: number; reservedTokens: number; settledTokens: number;
    softTokenLimit?: number | null; hardTokenLimit?: number | null;
    estimatedExternalCost: Record<string, number>;
  };
  requests: Array<{
    id: string; operation: string; provider: string; providerClass: 'Unknown' | 'External' | 'Local';
    model: string; status: string; promptVersion: string; inputTokens: number; outputTokens: number;
    estimatedCost?: number | null; costCurrency?: string | null; costStatus: string;
    injectionDetected: boolean; errorCode?: string | null; createdOn: string; completedOn?: string | null;
  }>;
  audit: Array<{ id: number; action: string; reason: string; actorUserId: number; occurredOn: string }>;
}

export interface ConnectorSdkContract {
  contractVersion: string;
  connectorTypes: string[];
  authenticationModes: string[];
  commercialOperations: string[];
  requiredDefinitionFields: string[];
  deliveryGuarantees: string[];
  webhookEnvelope: string;
  secretRule: string;
}

export interface GovernedArtifactSummary {
  id: number;
  artifactType: GovernedArtifactType;
  artifactKey: string;
  name: string;
  description: string;
  status: GovernedLifecycleStatus;
  currentVersionNumber: number;
  productionVersionNumber?: number | null;
  version: number;
  updatedOn: string;
  updatedByUserId: number;
}

export interface GovernedArtifactDetail {
  artifact: GovernedArtifactSummary;
  versions: Array<{
    id: number;
    versionNumber: number;
    status: GovernedLifecycleStatus;
    definitionJson: string;
    changeSummary: string;
    createdOn: string;
    createdByUserId: number;
    testedOn?: string | null;
    publishedOn?: string | null;
  }>;
  events: Array<{
    id: number;
    artifactVersionNumber: number;
    action: string;
    reason: string;
    occurredOn: string;
    actorUserId: number;
  }>;
}

const key = () => crypto.randomUUID();

export const platformGovernanceService = {
  listArtifacts: async (types?: GovernedArtifactType[], search?: string) => {
    const responses = await Promise.all((types?.length ? types : [undefined]).map(async (type) => {
      const { data } = await axiosInstance.get<GovernedArtifactSummary[]>('/api/platform-governance/artifacts', {
        params: { type, search: search || undefined },
      });
      return data;
    }));
    return responses.flat().sort((a, b) => a.name.localeCompare(b.name));
  },
  getArtifact: async (id: number) => {
    const { data } = await axiosInstance.get<GovernedArtifactDetail>(`/api/platform-governance/artifacts/${id}`);
    return data;
  },
  createArtifact: async (command: {
    artifactType: GovernedArtifactType;
    artifactKey: string;
    name: string;
    description: string;
    definitionJson: string;
    changeSummary: string;
  }) => {
    const { data } = await axiosInstance.post('/api/platform-governance/artifacts', command,
      { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  createVersion: async (id: number, command: { expectedVersion: number; definitionJson: string; changeSummary: string }) => {
    const { data } = await axiosInstance.post(`/api/platform-governance/artifacts/${id}/versions`, command,
      { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  transitionArtifact: async (id: number, command: {
    expectedVersion: number;
    action: 'TEST' | 'PUBLISH' | 'ROLLBACK' | 'ARCHIVE' | 'RESTORE';
    reason: string;
    targetVersionNumber?: number;
  }) => {
    const { data } = await axiosInstance.post(`/api/platform-governance/artifacts/${id}/transition`, command,
      { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  listActions: async (status?: HumanActionStatus) => {
    const { data } = await axiosInstance.get<HumanActionItem[]>('/api/platform-governance/actions',
      { params: { status } });
    return data;
  },
  getAction: async (id: number) => {
    const { data } = await axiosInstance.get<HumanActionDetail>(`/api/platform-governance/actions/${id}`);
    return data;
  },
  transitionAction: async (item: HumanActionItem, targetStatus: HumanActionStatus, comment: string) => {
    const { data } = await axiosInstance.post(`/api/platform-governance/actions/${item.id}/transition`, {
      expectedVersion: item.version,
      targetStatus,
      action: targetStatus === 'Completed' ? 'APPROVE' : targetStatus.toUpperCase(),
      comment,
    }, { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  bulkTransitionActions: async (items: HumanActionItem[], targetStatus: HumanActionStatus, comment: string) => {
    const { data } = await axiosInstance.post('/api/platform-governance/actions/bulk-transition', {
      targets: items.map((item) => ({ id: item.id, expectedVersion: item.version })),
      targetStatus,
      action: targetStatus === 'Completed' ? 'APPROVE' : targetStatus.toUpperCase(),
      comment,
    }, { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  getAiTrust: async () => {
    const { data } = await axiosInstance.get<AiTrustCenterView>('/api/platform-governance/ai-trust');
    return data;
  },
  updateAiTrustPolicy: async (policy: AiTrustPolicy, reason: string) => {
    const { data } = await axiosInstance.put('/api/platform-governance/ai-trust/policy',
      { ...policy, expectedVersion: policy.version, reason },
      { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  rollbackAiTrustPolicy: async (policy: AiTrustPolicy, auditEventId: number, reason: string) => {
    const { data } = await axiosInstance.post('/api/platform-governance/ai-trust/policy/rollback',
      { expectedVersion: policy.version, auditEventId, reason },
      { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  getConnectorSdk: async () => {
    const { data } = await axiosInstance.get<ConnectorSdkContract>('/api/platform-governance/connector-sdk');
    return data;
  },
};
