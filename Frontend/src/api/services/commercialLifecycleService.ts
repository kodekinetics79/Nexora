import axiosInstance from '../axiosInstance';

export type LifecycleAggregate = 'leads' | 'rfqs';
export interface LifecycleTransitionOption { statusId: number; statusCode: string; label: string; requiresReason: boolean; }
export interface LifecycleState { aggregateId: number; currentStatusCode: string; version: number; isTerminal: boolean; allowedTransitions: LifecycleTransitionOption[]; }
/** A row of the governed outcome-reason picklist — the same one a quote outcome uses. */
export interface OutcomeReason { id: number; code: string; label: string; }

/**
 * The lead terminals that mean the inquiry was lost or abandoned before a quotation existed.
 * These must name a governed outcome reason; the server refuses anything else.
 */
export const LEAD_LOSS_STATUS_CODES = ['DISQUALIFIED', 'LOST', 'CANCELLED'];

export const isLeadLoss = (aggregate: LifecycleAggregate, statusCode: string) =>
  aggregate === 'leads' && LEAD_LOSS_STATUS_CODES.includes(statusCode);

const lifecycleService = {
  getState: async (aggregate: LifecycleAggregate, id: number): Promise<LifecycleState> =>
    (await axiosInstance.get(`/api/commercial-cases/${aggregate}/${id}/lifecycle`)).data,
  /** Never hardcode the reasons — this is the tenant's governed list, including its own additions. */
  getLeadOutcomeReasons: async (): Promise<OutcomeReason[]> =>
    (await axiosInstance.get('/api/commercial-cases/leads/outcome-reasons')).data,
  transition: async (
    aggregate: LifecycleAggregate,
    id: number,
    state: LifecycleState,
    option: LifecycleTransitionOption,
    reasonCode?: string,
    reasonNotes?: string,
  ) =>
    (await axiosInstance.post(`/api/commercial-cases/${aggregate}/${id}/transition`, {
      targetStatusCode: option.statusCode,
      expectedVersion: state.version,
      reasonCode: reasonCode || null,
      reasonNotes: reasonNotes || null,
      idempotencyKey: crypto.randomUUID(),
    })).data,
};

export default lifecycleService;
