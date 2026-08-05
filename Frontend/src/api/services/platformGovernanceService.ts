import axiosInstance from '../axiosInstance';

export type GovernedArtifactType = 'CommercialTaxonomy' | 'DocumentSkill' | 'Model' | 'Rule' |
  'Dataset' | 'Connector' | 'TestSuite' | 'ReleaseCandidate' | 'ArchivePolicy' | 'QualityMetricSet';
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
  /** Deployment stance resolved once at startup; read-only telemetry, not a control. */
  inferencePosture: 'LocalFirst' | 'ExternalAuthorized';
}

export interface ArchiveDocumentItem {
  occurrenceId: number; sourceDocumentId: number; fileName: string; mimeType: string;
  byteSize: number; contentHash: string; ingestedOn: string; intakeStatus: string;
  securityStatus: string; processingStatus: string; documentType: string; reviewStatus: string;
  classificationConfidence?: number | null; leadId?: number | null; nexoraSerial?: string | null;
  customerRfq?: string | null; customerId?: number | null; contactId?: number | null;
  commercialLinks: string[]; legalHold: boolean; deletionRequested: boolean; governanceVersion: number;
}

export interface ArchiveSearchResult {
  items: ArchiveDocumentItem[]; page: number; pageSize: number; total: number;
  searchScope: string; definitionVersion: string;
}

export interface QualityMetric {
  key: string; label: string; value?: number | null; unit: string; numerator: number;
  denominator: number; definition: string; evidenceStatus: string; drilldownKey: string;
}
export interface QualityAnalyticsView {
  from: string; to: string; metrics: QualityMetric[];
  exceptionCauses: Array<{ category: string; code: string; count: number }>;
  records: Array<{ occurrenceId: number; fileName: string; ingestedOn: string; intakeStatus: string;
    processingStatus: string; processingPath: string; humanReview: boolean;
    localProcessing: boolean; externalProcessing: boolean; processingReused: boolean;
    actualCost: number; costStatus: string }>;
  recommendations: Array<{ priority: string; title: string; recommendation: string;
    evidence: string; drilldownKey: string }>;
  definitionVersion: string; accuracyLimitation: string;
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

/* ────────────────────────────────────────────────────────────────────────────
 * Evidence retention & stored-byte purge
 *
 * The purge deletes STORED FILE BYTES ONLY. The `source_documents` row, its SHA-256 content hash,
 * filename, byte size and every derived extraction record (pages, regions, field evidence, the
 * lead and its items) are retained — the database physically refuses to delete them. Every type
 * and every string in this section has to stay true to that, because the difference between
 * "we deleted the file" and "we erased the data" is the difference between a defensible audit
 * answer and a false compliance claim.
 *
 * This surface ships ahead of / alongside its backend. Rather than assume the response shape, the
 * readers below normalise defensively: a field the deployment did not send becomes `null` and is
 * rendered as "not reported", never as `0`. Quoting "0 bytes reclaimable" because a key was absent
 * would be a lie about an irreversible operation.
 * ──────────────────────────────────────────────────────────────────────────── */

/** Compliance-approved default: 90 days is the dispute / re-extraction buffer after extraction. */
export const EVIDENCE_RETENTION_DEFAULT_DAYS = 90;
/** Floor. Anything shorter stops being a retention policy and starts being a delete button. */
export const EVIDENCE_RETENTION_MIN_DAYS = 30;
/** Ceiling, mirroring the AI governance `RetentionDays` bound (1–3650). */
export const EVIDENCE_RETENTION_MAX_DAYS = 3650;

export interface EvidenceRetentionPolicy {
  /** Days the original file is kept after extraction COMPLETES. `null` when not reported. */
  retentionDays: number | null;
  /** Scheduled purging is opt-in — irreversible deletion is never on by default. */
  isEnabled: boolean | null;
  /** Server-enforced bounds. Preferred over the local constants when reported. */
  minimumRetentionDays: number | null;
  maximumRetentionDays: number | null;
  version: number | null;
  updatedOn: string | null;
}

export interface EvidenceRetentionStorage {
  /** Bytes currently held by stored document files for this business unit. */
  usedBytes: number | null;
  /** Bytes the current policy would free right now. */
  reclaimableBytes: number | null;
  /** Documents whose bytes are still stored. */
  documentCount: number | null;
  /** Documents whose bytes have already been purged (the record and lineage remain). */
  purgedCount: number | null;
  /** How many documents the reclaimable byte figure covers. */
  reclaimableDocumentCount: number | null;
}

export interface EvidenceRetentionSummary {
  policy: EvidenceRetentionPolicy;
  storage: EvidenceRetentionStorage;
  /**
   * Contract fields this deployment did not return. The page shows these as unknown rather than
   * inventing a zero.
   */
  missingFields: string[];
}

export interface EvidenceRetentionExclusion {
  documentId: number | null;
  fileName: string | null;
  /** Raw reason code from the eligibility evaluation, e.g. `LEGAL_HOLD`. */
  reason: string | null;
}

export interface EvidenceRetentionRunResult {
  /** True when nothing was deleted — the estimate only. */
  dryRun: boolean;
  scanned: number | null;
  eligible: number | null;
  purged: number | null;
  bytesReclaimed: number | null;
  /** Older non-content-addressed copies of the same files, removed alongside. */
  legacyCopiesDeleted: number | null;
  /**
   * Legacy copies that could not be matched back to a purged document. Reported rather than
   * silently skipped: those bytes are still on disk and the tenant is owed that fact.
   */
  legacyCopiesUnresolved: number | null;
  /**
   * The server's authoritative disclosure sentence. Rendered verbatim so the compliance wording
   * has exactly one source of truth.
   */
  disclosure: string | null;
  /** True when this Idempotency-Key had already run; nothing additional was deleted. */
  idempotentReplay: boolean;
  skipped: EvidenceRetentionExclusion[];
  /**
   * False when the deployment returned no `skipped` array at all. An empty list then means
   * "not reported", not "nothing was excluded" — the page must not claim the latter.
   */
  skippedReported: boolean;
  missingFields: string[];
}

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

const asCount = (value: unknown): number | null =>
  typeof value === 'number' && Number.isFinite(value) ? value : null;

const asFlag = (value: unknown): boolean | null => (typeof value === 'boolean' ? value : null);

const asText = (value: unknown): string | null => {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
};

/** Records `path` as missing when the reader produced `null`, so the UI can say so out loud. */
const track = (missing: string[], path: string, value: unknown): void => {
  if (value === null) missing.push(path);
};

export const readEvidenceRetentionSummary = (payload: unknown): EvidenceRetentionSummary => {
  const root = isRecord(payload) ? payload : {};
  const policyRaw = isRecord(root.policy) ? root.policy : {};
  const storageRaw = isRecord(root.storage) ? root.storage : {};
  const missingFields: string[] = [];

  const policy: EvidenceRetentionPolicy = {
    retentionDays: asCount(policyRaw.retentionDays),
    isEnabled: asFlag(policyRaw.isEnabled),
    minimumRetentionDays: asCount(policyRaw.minimumRetentionDays),
    maximumRetentionDays: asCount(policyRaw.maximumRetentionDays),
    version: asCount(policyRaw.version),
    updatedOn: asText(policyRaw.updatedOn),
  };
  const storage: EvidenceRetentionStorage = {
    usedBytes: asCount(storageRaw.usedBytes),
    reclaimableBytes: asCount(storageRaw.reclaimableBytes),
    documentCount: asCount(storageRaw.documentCount),
    purgedCount: asCount(storageRaw.purgedCount),
    reclaimableDocumentCount: asCount(storageRaw.reclaimableDocumentCount),
  };

  track(missingFields, 'policy.retentionDays', policy.retentionDays);
  track(missingFields, 'policy.isEnabled', policy.isEnabled);
  track(missingFields, 'storage.usedBytes', storage.usedBytes);
  track(missingFields, 'storage.reclaimableBytes', storage.reclaimableBytes);
  track(missingFields, 'storage.documentCount', storage.documentCount);
  track(missingFields, 'storage.purgedCount', storage.purgedCount);

  return { policy, storage, missingFields };
};

const readExclusion = (entry: unknown): EvidenceRetentionExclusion => {
  if (!isRecord(entry)) return { documentId: null, fileName: null, reason: asText(entry) };
  return {
    documentId: asCount(entry.documentId) ?? asCount(entry.sourceDocumentId),
    fileName: asText(entry.fileName) ?? asText(entry.originalFileName),
    reason: asText(entry.reason) ?? asText(entry.reasonCode),
  };
};

export const readEvidenceRetentionRun = (
  payload: unknown,
  requestedDryRun: boolean,
): EvidenceRetentionRunResult => {
  const root = isRecord(payload) ? payload : {};
  const missingFields: string[] = [];

  const scanned = asCount(root.scanned);
  const eligible = asCount(root.eligible);
  const purged = asCount(root.purged);
  const bytesReclaimed = asCount(root.bytesReclaimed);
  track(missingFields, 'scanned', scanned);
  track(missingFields, 'eligible', eligible);
  track(missingFields, 'purged', purged);
  track(missingFields, 'bytesReclaimed', bytesReclaimed);

  const skippedReported = Array.isArray(root.skipped);
  if (!skippedReported) missingFields.push('skipped');

  return {
    // Trust the server's own echo of the mode when it sends one; a server that says it executed
    // must never be presented as a preview.
    dryRun: asFlag(root.dryRun) ?? requestedDryRun,
    scanned,
    eligible,
    purged,
    bytesReclaimed,
    legacyCopiesDeleted: asCount(root.legacyCopiesDeleted),
    legacyCopiesUnresolved: asCount(root.legacyCopiesUnresolved),
    disclosure: asText(root.disclosure),
    idempotentReplay: asFlag(root.idempotentReplay) ?? false,
    skipped: skippedReported ? (root.skipped as unknown[]).map(readExclusion) : [],
    skippedReported,
    missingFields,
  };
};

/**
 * True when the deployment does not expose the retention endpoints at all (backend not shipped to
 * this environment yet). The page degrades to an explanatory panel instead of an error.
 */
export const isEvidenceRetentionUnavailable = (error: unknown): boolean => {
  if (!isRecord(error) || !isRecord(error.response)) return false;
  const status = error.response.status;
  return status === 404 || status === 405 || status === 501;
};

const randomKey = (): string => {
  const webCrypto = globalThis.crypto;
  if (webCrypto && typeof webCrypto.randomUUID === 'function') return webCrypto.randomUUID();
  return `k-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
};

/**
 * Minted once per confirmed purge — NOT once per HTTP attempt. If the response is lost to a
 * network failure and the user retries, the same key must reach the server so the purge cannot
 * execute twice.
 */
export const newIdempotencyKey = (): string => randomKey();

const key = () => randomKey();

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
  searchArchive: async (params: { search?: string; documentType?: string; status?: string; sort?: string }) => {
    const { data } = await axiosInstance.get<ArchiveSearchResult>('/api/platform-governance/archive',
      { params: { ...params, page: 1, pageSize: 100 } });
    return data;
  },
  governArchiveDocument: async (item: ArchiveDocumentItem, action: string, reason: string) => {
    const { data } = await axiosInstance.post(
      `/api/platform-governance/archive/${item.occurrenceId}/govern`,
      { expectedVersion: item.governanceVersion, action, reason },
      { headers: { 'Idempotency-Key': key() } });
    return data;
  },
  getQualityAnalytics: async (windowDays: number, drilldown?: string) => {
    const { data } = await axiosInstance.get<QualityAnalyticsView>('/api/platform-governance/quality',
      { params: { windowDays, drilldown } });
    return data;
  },
  getEvidenceRetention: async (): Promise<EvidenceRetentionSummary> => {
    const { data } = await axiosInstance.get<unknown>('/api/platform-governance/evidence-retention');
    return readEvidenceRetentionSummary(data);
  },
  updateEvidenceRetentionPolicy: async (command: {
    retentionDays: number;
    isEnabled: boolean;
    reason: string;
  }): Promise<EvidenceRetentionSummary> => {
    const { data } = await axiosInstance.put<unknown>(
      '/api/platform-governance/evidence-retention/policy',
      command,
      { headers: { 'Idempotency-Key': key() } });
    return readEvidenceRetentionSummary(data);
  },
  runEvidenceRetentionPurge: async (command: {
    dryRun: boolean;
    reason: string;
    /** Caller-owned so a retry of the SAME confirmed purge cannot delete twice. */
    idempotencyKey: string;
  }): Promise<EvidenceRetentionRunResult> => {
    const { data } = await axiosInstance.post<unknown>(
      '/api/platform-governance/evidence-retention/purge-run',
      { dryRun: command.dryRun, reason: command.reason },
      { headers: { 'Idempotency-Key': command.idempotencyKey } });
    return readEvidenceRetentionRun(data, command.dryRun);
  },
};
