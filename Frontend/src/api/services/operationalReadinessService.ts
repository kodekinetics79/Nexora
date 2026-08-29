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

const operationalReadinessService = {
  get: async () => (await axiosInstance.get<OperationsReadiness>('/api/operations/readiness')).data,
  getExtractionDeadLetters: async () => (
    await axiosInstance.get<ExtractionDeadLetter[]>('/api/operations/readiness/extraction-dead-letters')
  ).data,
  recoverExtractionDeadLetter: async (jobId: number, reason: string, idempotencyKey: string) => (
    await axiosInstance.post<DeadLetterRecoveryResult>(
      `/api/operations/readiness/extraction-dead-letters/${jobId}/recover`,
      { reason, idempotencyKey },
    )
  ).data,
};

export default operationalReadinessService;
