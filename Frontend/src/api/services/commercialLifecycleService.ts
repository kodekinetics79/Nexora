import axiosInstance from '../axiosInstance';

export type LifecycleAggregate = 'leads' | 'rfqs';
export interface LifecycleTransitionOption { statusId: number; statusCode: string; label: string; requiresReason: boolean; }
export interface LifecycleState { aggregateId: number; currentStatusCode: string; version: number; isTerminal: boolean; allowedTransitions: LifecycleTransitionOption[]; }

const lifecycleService = {
  getState: async (aggregate: LifecycleAggregate, id: number): Promise<LifecycleState> =>
    (await axiosInstance.get(`/api/commercial-cases/${aggregate}/${id}/lifecycle`)).data,
  transition: async (aggregate: LifecycleAggregate, id: number, state: LifecycleState, option: LifecycleTransitionOption, reasonCode?: string) =>
    (await axiosInstance.post(`/api/commercial-cases/${aggregate}/${id}/transition`, {
      targetStatusCode: option.statusCode,
      expectedVersion: state.version,
      reasonCode: reasonCode || null,
      reasonNotes: null,
      idempotencyKey: crypto.randomUUID(),
    })).data,
};

export default lifecycleService;
