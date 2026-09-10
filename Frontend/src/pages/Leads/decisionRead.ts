import type { LeadDecisionSummary } from '../../api/services/decisionService';

/**
 * How Nexora's read on a lead is named to a person, shared by the leads list and the decide
 * screen so the same recommendation never has two names. The raw enum ("bid"/"review"/"skip")
 * is never shown.
 */
export interface DecisionMeta {
  label: string;
  color: 'success' | 'warning' | 'default';
}

export const DECISION_META: Record<string, DecisionMeta | undefined> = {
  bid: { label: 'Worth bidding', color: 'success' },
  review: { label: 'Needs a look', color: 'warning' },
  skip: { label: 'Likely skip', color: 'default' },
};

export const decisionLabel = (recommendation: string | null | undefined): string =>
  DECISION_META[(recommendation ?? '').toLowerCase()]?.label ?? 'No read yet';

/** How long is left, in the words a rep uses. */
export const daysLeftSentence = (daysLeft: number | null | undefined): string | null => {
  if (daysLeft == null) return null;
  if (daysLeft < 0) {
    const overdue = Math.abs(daysLeft);
    return `${overdue} ${overdue === 1 ? 'day' : 'days'} past deadline`;
  }
  if (daysLeft === 0) return 'Due today';
  return `${daysLeft} ${daysLeft === 1 ? 'day' : 'days'} left`;
};

/** Plain-language facts behind the read. */
export const decisionFacts = (s: Pick<LeadDecisionSummary, 'estimatedValue' | 'coveragePct' | 'daysLeft'>): string[] => {
  const facts: string[] = [];
  if (s.estimatedValue != null) {
    facts.push(`Est. value: ${s.estimatedValue.toLocaleString('en-US', { maximumFractionDigits: 0 })}`);
  }
  if (s.coveragePct != null) {
    facts.push(`We stock ~${Math.round(s.coveragePct)}%`);
  }
  const days = daysLeftSentence(s.daysLeft);
  if (days) facts.push(days);
  return facts;
};
