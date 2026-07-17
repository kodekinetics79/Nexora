import axiosInstance from '../axiosInstance';

// Decision Brief engine contracts.
//
// POST /api/intelligence/leads/decision-summaries  { leadIds: number[] } (cap 100)
//   -> { summaries: { [leadId: string]: LeadDecisionSummary } }
// GET  /api/intelligence/leads/{id}/decision-brief
//   -> full brief including plain-language reasons[].
//
// The backend may not be deployed everywhere yet — callers must degrade
// silently on 404/error and never block their primary data flow on this.

export type DecisionUrgency = 'overdue' | 'critical' | 'soon' | 'comfortable' | 'unknown';
export type DecisionRecommendation = 'bid' | 'review' | 'skip';

export interface LeadDecisionSummary {
  estimatedValue: number | null;
  coveragePct: number | null;
  daysLeft: number | null;
  urgency: DecisionUrgency;
  recommendation: DecisionRecommendation;
}

export interface DecisionSummariesResponse {
  summaries: Record<string, LeadDecisionSummary>;
}

export interface LeadDecisionBrief extends LeadDecisionSummary {
  reasons: string[];
}

/** The batch endpoint caps a request at 100 lead ids. */
export const DECISION_SUMMARY_BATCH_CAP = 100;

const decisionService = {
  /** One batched call for a page of leads. Ids beyond the cap are dropped. */
  getDecisionSummaries: async (leadIds: number[]): Promise<DecisionSummariesResponse> => {
    const r = await axiosInstance.post('/api/intelligence/leads/decision-summaries', {
      leadIds: leadIds.slice(0, DECISION_SUMMARY_BATCH_CAP),
    });
    return r.data;
  },

  getDecisionBrief: async (id: number): Promise<LeadDecisionBrief> => {
    const r = await axiosInstance.get(`/api/intelligence/leads/${id}/decision-brief`);
    return r.data;
  },
};

export default decisionService;
