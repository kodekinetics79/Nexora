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

const operationalReadinessService = {
  get: async () => (await axiosInstance.get<OperationsReadiness>('/api/operations/readiness')).data,
};

export default operationalReadinessService;
