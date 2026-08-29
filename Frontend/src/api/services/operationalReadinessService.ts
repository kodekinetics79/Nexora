import axiosInstance from '../axiosInstance';

export type ReadinessCheck = {
  name: string;
  status: string;
  durationMilliseconds: number;
};

export type OperationsQueue = {
  key: string;
  label: string;
  pending: number;
  inFlight: number;
  deadLetter: number;
};

export type OperationsReadiness = {
  checkedAt: string;
  deploymentReadiness: string;
  blockingReasons: string[];
  healthChecks: ReadinessCheck[];
  queues: OperationsQueue[];
  aiExternalDependency: {
    total: number;
    local: number;
    external: number;
    authorizedExternal: number;
    unresolved: number;
    externalSharePercent: number;
    ceilingPercent: number;
    windowSize: number;
    ceilingBreached: boolean;
  };
};

type LegacyAiOperationsSummary = {
  total: number;
  local: number;
  external: number;
  unresolved: number;
  externalSharePercent: number;
};

type OperationsReadinessWire = Omit<OperationsReadiness, 'aiExternalDependency'> & {
  aiExternalDependency?: OperationsReadiness['aiExternalDependency'];
  aiLast30Days?: LegacyAiOperationsSummary;
};

export type ExtractionDeadLetter = {
  jobId: number;
  batchId: string;
  sourceDocumentOccurrenceId: number | null;
  fileName: string;
  sourceType: string;
  attempts: number;
  maxAttempts: number;
  failureCategory: string;
  createdOn: string;
  updatedOn: string;
  resolution: string;
  blocksReadiness: boolean;
  /** Server-owned recoverability decision; independent from deployment readiness impact. */
  canRetry: boolean;
  /**
   * What an operator must DO about `failureCategory`, in words. Absent when the
   * category says it all, and absent from older backends. Never the raw stored
   * error — that can quote the customer's document — but a fixed sentence per
   * category. Without it, `AI_NOT_AUTHORIZED` reached the screen as two
   * underscored words with nothing actionable behind them.
   */
  operatorAction?: string | null;
};

export type DeadLetterRecoveryResult = {
  jobId: number;
  batchId: string;
  status: string;
  blocksReadiness: boolean;
  idempotentReplay: boolean;
};

/** Keeps the page available while Vercel and the backend roll out in either order. */
export const normalizeOperationsReadiness = (wire: OperationsReadinessWire): OperationsReadiness => {
  if (wire.aiExternalDependency) {
    return { ...wire, aiExternalDependency: wire.aiExternalDependency };
  }
  const legacy = wire.aiLast30Days;
  const share = legacy?.externalSharePercent ?? 0;
  return {
    ...wire,
    aiExternalDependency: {
      total: legacy?.total ?? 0,
      local: legacy?.local ?? 0,
      external: legacy?.external ?? 0,
      authorizedExternal: 0,
      unresolved: legacy?.unresolved ?? 0,
      externalSharePercent: share,
      ceilingPercent: 10,
      windowSize: legacy?.total ?? 0,
      ceilingBreached: share > 10,
    },
  };
};

const terminalLegacyCategories = new Set([
  'EVIDENCE_INTEGRITY', 'MALWARE', 'OCR_PIXEL_LIMIT_EXCEEDED',
]);

export const normalizeExtractionDeadLetter = (item: ExtractionDeadLetter): ExtractionDeadLetter => ({
  ...item,
  // Older backends do not return CanRetry. Fail closed for terminal dispositions/categories,
  // while preserving recovery for ordinary open provider/time-out/parser failures.
  canRetry: typeof item.canRetry === 'boolean'
    ? item.canRetry
    : item.resolution !== 'SourceObjectUnavailable'
      && !terminalLegacyCategories.has(item.failureCategory),
});

const operationalReadinessService = {
  get: async () => normalizeOperationsReadiness(
    (await axiosInstance.get<OperationsReadinessWire>('/api/operations/readiness')).data,
  ),
  getExtractionDeadLetters: async () => (
    await axiosInstance.get<ExtractionDeadLetter[]>('/api/operations/readiness/extraction-dead-letters')
  ).data.map(normalizeExtractionDeadLetter),
  recoverExtractionDeadLetter: async (jobId: number, reason: string, idempotencyKey: string) => (
    await axiosInstance.post<DeadLetterRecoveryResult>(
      `/api/operations/readiness/extraction-dead-letters/${jobId}/recover`,
      { reason, idempotencyKey },
    )
  ).data,
};

export default operationalReadinessService;
