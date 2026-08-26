import type {
  FitAssessmentDTO,
  LeadDecisionWorkbenchDTO,
  LineParticipationDecision,
  FitCriterionDTO,
} from '../../../api/services/leadDecisionService';

export interface EditableLineDecision {
  decision: LineParticipationDecision;
  reasonCode?: string;
  note?: string;
  productId?: number;
  quantity?: number;
  unitOfMeasure?: string;
  currency?: string;
}

export type DecisionMap = Record<number, EditableLineDecision>;

export interface DecisionCounts {
  total: number;
  bid: number;
  noBid: number;
  clarify: number;
  pending: number;
}

export const initializeDecisionMap = (workbench: LeadDecisionWorkbenchDTO): DecisionMap => {
  const decisions: DecisionMap = {};
  for (const line of workbench.lines) {
    decisions[line.revisionLineId] = {
      decision: line.participation?.decision ?? 'Pending',
      ...(line.participation?.reasonCode ? { reasonCode: line.participation.reasonCode } : {}),
      ...(line.participation?.note ? { note: line.participation.note } : {}),
      ...(line.participation?.productId ? { productId: line.participation.productId } : {}),
      ...((line.participation?.quantity ?? (Number.isInteger(line.normalizedQuantity) ? line.normalizedQuantity : line.quantity)) != null
        ? { quantity: line.participation?.quantity ?? (Number.isInteger(line.normalizedQuantity) ? line.normalizedQuantity! : line.quantity!) } : {}),
      ...((line.participation?.unitOfMeasure ?? line.normalizedUom ?? line.unitOfMeasure)
        ? { unitOfMeasure: line.participation?.unitOfMeasure ?? line.normalizedUom ?? line.unitOfMeasure! } : {}),
      ...((line.participation?.currency ?? line.currency)
        ? { currency: line.participation?.currency ?? line.currency! } : {}),
    };
  }
  return decisions;
};

export const countDecisions = (decisions: DecisionMap): DecisionCounts => {
  const counts: DecisionCounts = { total: 0, bid: 0, noBid: 0, clarify: 0, pending: 0 };
  for (const value of Object.values(decisions)) {
    counts.total += 1;
    if (value.decision === 'Bid') counts.bid += 1;
    else if (value.decision === 'NoBid') counts.noBid += 1;
    else if (value.decision === 'Clarify') counts.clarify += 1;
    else counts.pending += 1;
  }
  return counts;
};

export const decisionRecordIsLocked = (
  workbench: Pick<LeadDecisionWorkbenchDTO, 'participationStatus' | 'promotion' | 'blockers'>,
  decisions: DecisionMap,
): boolean => {
  if (workbench.promotion) return true;
  if (workbench.blockers.some(({ code }) => ['LEGACY_RFQ', 'INCONSISTENT_CONVERTED_STATE', 'RFQ_REVISION_REQUIRED'].includes(code))) return true;
  const counts = countDecisions(decisions);
  return workbench.participationStatus === 'COMMITTED'
    && counts.total > 0
    && counts.noBid === counts.total;
};

export const validGovernedDecision = (decision: EditableLineDecision): boolean => {
  if (decision.decision === 'Pending' || decision.decision === 'Bid') return true;
  return Boolean(decision.reasonCode?.trim());
};

export const fitAssessmentDraftComplete = (
  criteria: FitCriterionDTO[],
  rationale: string,
): boolean => criteria.length > 0
  && criteria.every((criterion) => criterion.decision !== 'UNKNOWN')
  && criteria.every((criterion) => criterion.decision !== 'CONCERN' || (criterion.note?.trim().length ?? 0) >= 5)
  && rationale.trim().length >= 5;

export const blockerAction = (
  blocker: LeadDecisionWorkbenchDTO['blockers'][number],
  leadId: number,
): { label: string; path: string } | null => {
  // The lifecycle endpoint historically returned `/procurement/leads/:id`, but the actual Lead
  // detail route includes `/view`. This blocker must always land on the governed lifecycle UI.
  if (blocker.code === 'LEAD_NOT_ELIGIBLE') {
    return { label: blocker.actionLabel || 'Open Lead lifecycle decision', path: `/procurement/leads/view/${leadId}` };
  }
  if (blocker.actionLabel && blocker.actionPath?.startsWith('/')) {
    return { label: blocker.actionLabel, path: blocker.actionPath };
  }
  return null;
};

export interface PromotionRuleInput {
  workbench: LeadDecisionWorkbenchDTO;
  decisions: DecisionMap;
  fitAssessment?: FitAssessmentDTO | null;
  dirty: boolean;
  participationStatus: LeadDecisionWorkbenchDTO['participationStatus'];
  participationVersion?: number | null;
}

export const promotionBlockers = (input: PromotionRuleInput): string[] => {
  const { workbench, decisions, fitAssessment, dirty, participationStatus, participationVersion } = input;
  const counts = countDecisions(decisions);
  const blockers = workbench.blockers.map((item) => item.message);

  if (workbench.promotion) blockers.push(`This revision was already promoted to RFQ ${workbench.promotion.rfqNumber ?? `#${workbench.promotion.rfqId}`}.`);
  if (!workbench.customerId) blockers.push('Resolve the customer before promoting an RFQ.');
  if (workbench.verificationStatus !== 'VERIFIED') blockers.push('Complete source validation before promoting an RFQ.');
  if (!fitAssessment || fitAssessment.version <= 0) blockers.push('Save the fit assessment before deciding participation.');
  else if (fitAssessment.overallDecision === 'NOT_FIT') blockers.push('A lead assessed as not fit cannot be promoted.');
  if (counts.pending > 0) blockers.push(`Decide the remaining ${counts.pending} line${counts.pending === 1 ? '' : 's'}.`);
  if (counts.clarify > 0) blockers.push(`Resolve the ${counts.clarify} line${counts.clarify === 1 ? '' : 's'} awaiting clarification before promotion.`);
  if (Object.values(decisions).some((decision) => !validGovernedDecision(decision))) {
    blockers.push('Every no-bid or clarification decision needs a governed reason.');
  }
  for (const line of workbench.lines) {
    const decision = decisions[line.revisionLineId];
    if (decision?.decision !== 'Bid') continue;
    if (!decision.quantity || decision.quantity <= 0) blockers.push(`Bid line ${line.lineItemNo ?? line.id} needs a positive quantity.`);
    if (!decision.unitOfMeasure?.trim()) blockers.push(`Bid line ${line.lineItemNo ?? line.id} needs a unit of measure.`);
    if (!decision.currency?.trim()) blockers.push(`Bid line ${line.lineItemNo ?? line.id} needs a currency.`);
    if (line.needsAttention && (!decision.note || decision.note.trim().length < 5)) {
      blockers.push(`Bid line ${line.lineItemNo ?? line.id} needs a human acknowledgement of its catalog or normalization warning.`);
    }
  }
  if (counts.bid === 0) blockers.push('Mark at least one line as Bid, or close the lead as a full no-bid.');
  if (dirty) blockers.push('Save and commit the current participation decision before promotion.');
  if (participationStatus !== 'COMMITTED' || !participationVersion) blockers.push('Commit the participation decision before promotion.');

  return [...new Set(blockers)];
};

const displayedBlockerFamily = (message: string): string => {
  const normalized = message.trim().toLowerCase();
  if (/source-field evidence|source validation|source lineage|authoritative source/.test(normalized)) return 'source-readiness';
  if (/fit assessment|human fit/.test(normalized)) return 'fit-assessment';
  if (/participation choices for every|decide the remaining \d+ line/.test(normalized)) return 'line-decisions';
  if (/save and commit the current participation|commit the participation decision/.test(normalized)) return 'participation-commit';
  return normalized.replaceAll(/[^a-z0-9]+/g, ' ').trim();
};

/**
 * Compresses equivalent client/server explanations for display only.
 *
 * `promotionBlockers` remains the authoritative client-side gate and the API keeps returning every
 * server blocker. The first message in a family wins, which intentionally preserves the server's
 * more specific wording because server blockers are appended before locally derived guidance.
 */
export const deduplicateDisplayedPromotionBlockers = (blockers: string[]): string[] => {
  const seen = new Set<string>();
  return blockers.filter((blocker) => {
    const family = displayedBlockerFamily(blocker);
    if (seen.has(family)) return false;
    seen.add(family);
    return true;
  });
};

export const decisionsEqual = (left: DecisionMap, right: DecisionMap): boolean => {
  const leftKeys = Object.keys(left);
  const rightKeys = Object.keys(right);
  if (leftKeys.length !== rightKeys.length) return false;
  for (const key of leftKeys) {
    const a = left[Number(key)];
    const b = right[Number(key)];
    if (!b || a.decision !== b.decision || (a.reasonCode ?? '') !== (b.reasonCode ?? '')
      || (a.note ?? '') !== (b.note ?? '') || (a.productId ?? null) !== (b.productId ?? null)
      || (a.quantity ?? null) !== (b.quantity ?? null)
      || (a.unitOfMeasure ?? '') !== (b.unitOfMeasure ?? '')
      || (a.currency ?? '') !== (b.currency ?? '')) return false;
  }
  return true;
};
