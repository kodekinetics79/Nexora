import axiosInstance from '../api/axiosInstance';

export type CommercialExceptionType = 'UnassignedLead' | 'OverdueFollowUp';
export type CommercialExceptionSeverity = 'Low' | 'Medium' | 'High' | 'Critical';
export type CommercialExceptionStatus = 'Open' | 'Acknowledged' | 'Resolved' | 'Dismissed';
export type SourceCoverageStatus = 'Complete' | 'Partial' | 'Unavailable';

export interface CommercialCommandIdentity {
  correlationId: string;
  idempotencyKey: string;
}

export interface CommercialExceptionItem {
  id: number;
  commercialCaseId: number;
  nexoraSerial: string;
  exceptionType: CommercialExceptionType;
  severity: CommercialExceptionSeverity;
  status: CommercialExceptionStatus;
  title: string;
  summary: string;
  reasonCode: string;
  recommendedActionCode: string;
  sourceType: string;
  sourceId: number;
  sourceVersion: number;
  ownerUserId?: number | null;
  ownerName?: string | null;
  firstDetectedAtUtc: string;
  lastDetectedAtUtc: string;
  slaDueAtUtc: string;
  isOverdue: boolean;
  evidenceJson: string;
  ruleVersion: string;
  version: number;
}

export interface CommercialExceptionPage {
  items: CommercialExceptionItem[];
  total: number;
  active: number;
  critical: number;
  overdue: number;
  generatedAtUtc: string;
  ruleVersion: string;
  scope: string;
  pageNumber: number;
  pageSize: number;
  coverageStatus: SourceCoverageStatus;
  sourceCoverage: Array<{
    sourceType: string;
    isAvailable: boolean;
    status: string;
    detail: string;
  }>;
  metricDefinitions: {
    total: string;
    active: string;
    critical: string;
    overdue: string;
  };
}

export interface CommercialExceptionFilters {
  status?: CommercialExceptionStatus;
  type?: CommercialExceptionType;
  minimumSeverity?: CommercialExceptionSeverity;
  overdueOnly?: boolean;
  pageNumber?: number;
  pageSize?: number;
}

export interface RefreshCommercialExceptionsResult {
  detected: number;
  reopened: number;
  refreshed: number;
  resolved: number;
  reconciledAtUtc: string;
  ruleVersion: string;
}

const root = '/api/commercial-exceptions';

const actionCode = (status: Exclude<CommercialExceptionStatus, 'Open'>) => ({
  Acknowledged: 'ACKNOWLEDGE',
  Resolved: 'RESOLVE',
  Dismissed: 'DISMISS',
}[status]);

export const createCommercialCommandIdentity = (): CommercialCommandIdentity => ({
  correlationId: crypto.randomUUID(),
  idempotencyKey: crypto.randomUUID(),
});

const commandConfig = (identity: CommercialCommandIdentity) => ({
  headers: {
    'X-Correlation-ID': identity.correlationId,
    'Idempotency-Key': identity.idempotencyKey,
  },
});

const commercialExceptionService = {
  getPage: async (filters: CommercialExceptionFilters): Promise<CommercialExceptionPage> =>
    (await axiosInstance.get<CommercialExceptionPage>(root, { params: filters })).data,

  refresh: async (
    actorId: string,
    identity: CommercialCommandIdentity,
  ): Promise<RefreshCommercialExceptionsResult> => {
    return (await axiosInstance.post<RefreshCommercialExceptionsResult>(
      `${root}/refresh`,
      {
        correlationId: identity.correlationId,
        idempotencyKey: identity.idempotencyKey,
        actorId,
      },
      commandConfig(identity),
    )).data;
  },

  transition: async (
    id: number,
    expectedVersion: number,
    targetStatus: Exclude<CommercialExceptionStatus, 'Open'>,
    reason: string,
    actorId: string,
    identity: CommercialCommandIdentity,
  ): Promise<CommercialExceptionItem> => {
    return (await axiosInstance.post<CommercialExceptionItem>(
      `${root}/${id}/transition`,
      {
        expectedVersion,
        targetStatus,
        actionCode: actionCode(targetStatus),
        reason,
        correlationId: identity.correlationId,
        idempotencyKey: identity.idempotencyKey,
        actorId,
      },
      commandConfig(identity),
    )).data;
  },
};

export default commercialExceptionService;
