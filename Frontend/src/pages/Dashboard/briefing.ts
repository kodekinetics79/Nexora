// Pure helpers that compose the plain-language "daily briefing" hero from real
// API numbers (adapted from the briefing variant of this dashboard). Rules:
//   - a clause is emitted ONLY when its query succeeded AND its number is > 0;
//     missing or failed data silently drops the clause (never invented),
//   - every clause carries a deep link to the screen that backs the number,
//   - no jargon, no raw decimals, no enum values, no sentinel dates.

import { formatMoney } from './components/dashboardTheme';

export interface BriefingClause {
  /** Stable key for React lists. */
  key: string;
  /** Plain-language fragment, e.g. "3 bid deadlines in the next 72 hours". */
  text: string;
  /** Route the clause links to. */
  to: string;
}

export interface BriefingInput {
  /** Leads closing within the next 72h (already sentinel/past-filtered); null = source failed. */
  deadlineCount: number | null;
  /** Total leads waiting for extraction review. */
  needsReviewCount: number | null;
  /** Quotes still awaiting a customer response. */
  pendingQuoteCount: number | null;
  /** Real total of all quoted work, used to give the quote clause a value. */
  totalQuotedAmount: number | null;
  /** Copilot actions held for human approval. */
  pendingApprovalCount: number | null;
  /** Documents ingested in the last 24 hours (real created dates only). */
  overnightDocCount: number | null;
}

const plural = (n: number, one: string, many: string): string => (n === 1 ? one : many);

export function greetingForHour(hour: number, name?: string): string {
  const part = hour < 12 ? 'Good morning' : hour < 17 ? 'Good afternoon' : 'Good evening';
  const first = name?.trim().split(/\s+/)[0];
  return first ? `${part}, ${first}.` : `${part}.`;
}

/** Main briefing clauses (the situation sentence), in priority order. */
export function composeClauses(input: BriefingInput): BriefingClause[] {
  const clauses: BriefingClause[] = [];

  if (input.deadlineCount !== null && input.deadlineCount > 0) {
    clauses.push({
      key: 'deadlines',
      text: `${input.deadlineCount} bid ${plural(input.deadlineCount, 'deadline', 'deadlines')} in the next 72 hours`,
      to: '/procurement/leads/all',
    });
  }

  if (input.needsReviewCount !== null && input.needsReviewCount > 0) {
    clauses.push({
      key: 'review',
      text: `${input.needsReviewCount} ${plural(input.needsReviewCount, 'lead', 'leads')} waiting for your review`,
      to: '/procurement/extraction/review',
    });
  }

  if (input.pendingQuoteCount !== null && input.pendingQuoteCount > 0) {
    const value =
      input.totalQuotedAmount !== null && input.totalQuotedAmount > 0
        ? ` (${formatMoney(input.totalQuotedAmount)} quoted overall)`
        : '';
    clauses.push({
      key: 'quotes',
      text: `${input.pendingQuoteCount} ${plural(input.pendingQuoteCount, 'quote', 'quotes')} awaiting customer response${value}`,
      to: '/sales/quotes',
    });
  }

  if (input.pendingApprovalCount !== null && input.pendingApprovalCount > 0) {
    clauses.push({
      key: 'approvals',
      text: `${input.pendingApprovalCount} ${plural(input.pendingApprovalCount, 'action', 'actions')} held for your approval`,
      to: '/copilot/approvals',
    });
  }

  return clauses;
}

/** Closing sentence about overnight ingestion, or null when nothing happened. */
export function composeOvernightClause(input: BriefingInput): BriefingClause | null {
  const n = input.overnightDocCount;
  if (n === null || n <= 0) return null;
  // The window we derive from holds at most 50 leads.
  const shown = n >= 50 ? '50+' : String(n);
  return {
    key: 'overnight',
    text: `Nexora processed ${shown} new ${n === 1 ? 'document' : 'documents'} in the last 24 hours.`,
    to: '/procurement/leads/all',
  };
}
