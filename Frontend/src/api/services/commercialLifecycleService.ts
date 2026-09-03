import axiosInstance from '../axiosInstance';

export type LifecycleAggregate = 'leads' | 'rfqs';
export interface LifecycleTransitionOption { statusId: number; statusCode: string; label: string; requiresReason: boolean; }
export interface LifecycleState {
  aggregateId: number;
  currentStatusCode: string;
  version: number;
  isTerminal: boolean;
  allowedTransitions: LifecycleTransitionOption[];
  /**
   * Whether `POST .../reopen` would be accepted from this state — the server's own
   * `LifecyclePolicy.IsReopenable`, not a guess from {@link isTerminal}.
   *
   * The two are different questions and a screen must not confuse them: a lead that was passed
   * on, lost or cancelled can come back, while one that completed or was merged as a duplicate
   * cannot, and all of those are terminal. Offering Reopen on `isTerminal` would advertise a verb
   * the server refuses after the click.
   *
   * Optional only so a client running against a backend that predates the field degrades to
   * "no reopen offered" instead of offering one that always fails.
   */
  canReopen?: boolean;
}
/** A row of the governed outcome-reason picklist — the same one a quote outcome uses. */
export interface OutcomeReason { id: number; code: string; label: string; }

/**
 * The lead terminals that mean the inquiry was lost or abandoned before a quotation existed.
 * These must name a governed outcome reason; the server refuses anything else.
 */
/**
 * The reason CODE stamped on every reopen. It is a constant because the informative part is the
 * operator's sentence, which travels in `reasonNotes`; the server requires both.
 */
export const REOPEN_REASON_CODE = 'REOPENED';

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

  /**
   * Brings a closed record back into play — "we passed on it, the customer came back".
   *
   * A separate endpoint from `transition` on purpose: the server refuses an ordinary transition
   * out of a terminal state, and this one carries `[RequireManagerRole]` on top of Leads:Edit.
   * The target status is the server's decision, so nothing here names one.
   *
   * `reason` is the operator's sentence and is what the audit trail will show. The transition
   * refuses a blank one, so callers must not offer to send an empty string.
   */
  reopen: async (aggregate: LifecycleAggregate, id: number, state: LifecycleState, reason: string) =>
    (await axiosInstance.post(`/api/commercial-cases/${aggregate}/${id}/reopen`, {
      expectedVersion: state.version,
      reasonCode: REOPEN_REASON_CODE,
      reasonNotes: reason,
      idempotencyKey: crypto.randomUUID(),
    })).data,
};

export default lifecycleService;
