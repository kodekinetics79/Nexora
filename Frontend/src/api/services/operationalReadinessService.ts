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
  aiLast30Days: {
    total: number;
    local: number;
    external: number;
    unresolved: number;
    externalSharePercent: number;
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
