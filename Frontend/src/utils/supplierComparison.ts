/**
 * The two facts the supplier comparison must never let drift apart: which offer is genuinely the
 * cheapest, and what the recommended offer cost relative to it.
 *
 * A weighted score is a claim about a policy the customer configured. "Lowest landed cost" is a
 * claim a buyer can verify against the quotes on their desk. Replacing the second with the first
 * would trade a verifiable statement for an unverifiable one, so the screen shows both and the
 * recommendation always states its own price.
 */
export interface ComparableOffer {
  supplierQuotedItemId: number;
  eligible: boolean;
  landedUnitCost?: number | null;
  leadTimeDays?: number | null;
}

/** Null when no eligible offer has a landed cost — never a fabricated "cheapest". */
export const cheapestEligibleOffer = <T extends ComparableOffer>(offers: T[]): T | null =>
  offers
    .filter((offer) => offer.eligible && offer.landedUnitCost != null)
    .reduce<T | null>(
      (best, offer) =>
        best === null || (offer.landedUnitCost ?? 0) < (best.landedUnitCost ?? 0) ? offer : best,
      null,
    );

/**
 * What the recommendation cost, in the buyer's own units: "SAR 1,240.00 more than the cheapest,
 * 12 days faster". Only clauses backed by values that actually exist are produced — a missing lead
 * time yields no speed claim rather than a claim of zero days.
 */
export const recommendationTradeOff = (
  recommended: ComparableOffer,
  cheapest: ComparableOffer | null,
  formatCurrency: (value: number) => string,
): string => {
  if (!cheapest || cheapest.supplierQuotedItemId === recommended.supplierQuotedItemId)
    return 'also the lowest landed cost';

  const clauses: string[] = [];
  const premium =
    recommended.landedUnitCost != null && cheapest.landedUnitCost != null
      ? recommended.landedUnitCost - cheapest.landedUnitCost
      : null;
  if (premium != null && premium > 0)
    clauses.push(`${formatCurrency(premium)} more than the cheapest`);

  const daysSaved =
    recommended.leadTimeDays != null && cheapest.leadTimeDays != null
      ? cheapest.leadTimeDays - recommended.leadTimeDays
      : null;
  if (daysSaved != null && daysSaved > 0) clauses.push(`${daysSaved} days faster`);

  return clauses.length > 0 ? clauses.join(', ') : 'not the lowest landed cost';
};

/**
 * The four states a row's score can be in, and the exact words the screen says about each.
 *
 * The wording lives here rather than in the table because two of these states share one symptom —
 * no score — and the screen used to treat them as one. A blocked offer and an offer with a value
 * missing both arrive with no number, and a single branch on "no number" told a blocked supplier's
 * row that the offer "can still be awarded" while the chips beside it listed the reasons it could
 * not be. One place to write the sentence is one place to keep it true.
 */
export interface ScoreStateInput {
  eligible: boolean;
  weightedScore?: number | null;
  scoreUnavailableReason?: string | null;
  blockers?: string[] | null;
}

export type OfferScoreState =
  /** The comparison has not come back yet. Nothing is claimed either way. */
  | { status: 'PENDING'; headline: string; detail: string }
  /** A real score, and the row shows what it is made of. */
  | { status: 'SCORED'; headline: string; detail: string; score: number }
  /**
   * Awardable, but one of the weighted values was never captured for this supplier. R-F: no score,
   * no zero, and no loss of standing — the buyer can still award it.
   */
  | { status: 'NOT_SCORED'; headline: string; detail: string }
  /** Cannot be awarded as it stands. It is never told it can be. */
  | { status: 'BLOCKED'; headline: string; detail: string };

/**
 * Eligibility is read BEFORE the score. An offer nobody can award is blocked whatever number is
 * attached to it, so a stray score can never talk a buyer past a blocker.
 */
export const offerScoreState = (
  line: ScoreStateInput | null | undefined,
): OfferScoreState => {
  if (!line)
    return {
      status: 'PENDING',
      headline: 'Checking',
      detail: 'Waiting for the supplier comparison.',
    };

  const blockers = (line.blockers ?? []).filter((reason) => reason.trim().length > 0);
  if (!line.eligible)
    return {
      status: 'BLOCKED',
      // The server's own sentence when it has one; it is the same sentence the comparison used.
      headline: line.scoreUnavailableReason ?? 'Not scored — this offer cannot be awarded as it stands',
      detail:
        blockers.length > 0
          ? `This offer cannot be awarded as it stands: ${blockers.join('; ')}.`
          : 'This offer cannot be awarded as it stands.',
    };

  if (line.weightedScore != null)
    return {
      status: 'SCORED',
      headline: `Weighted ${roundedPoints(line.weightedScore)}/100`,
      detail: '',
      score: line.weightedScore,
    };

  return {
    status: 'NOT_SCORED',
    headline: line.scoreUnavailableReason ?? 'Cannot score — a value the weighting needs is missing',
    detail: 'Nothing is scored when one of the weighted values is missing. This offer can still be awarded.',
  };
};

/**
 * Points and scores are rounded to two places by the server, and the parts were added up FROM those
 * rounded numbers, so printing them at the same precision keeps a row adding up to its own total.
 */
export const roundedPoints = (value: number): string => String(Math.round(value * 100) / 100);

/** What the display order is decided on, taken from the authoritative comparison. */
export interface ComparisonPlacement extends ScoreStateInput {
  landedUnitCost?: number | null;
}

/**
 * Puts the rows on screen in the order the comparison actually ranked them.
 *
 * Without this the table renders in whatever order the API happened to return, so changing the
 * weights changes the scores in the cells and moves nothing — the buyer sees the same rows in the
 * same places and concludes the setting does not work. The gate is only visible if the winner is
 * at the top.
 *
 * Order within one RFQ line: offers with a score, best first; then offers that are awardable but
 * have no score; then offers that cannot be awarded at all. Ties fall back to the cheapest landed
 * cost and then to the offer's own id, so the same two requests never render differently.
 *
 * Offers are grouped by RFQ line first, in the order the lines arrived. A score is worked out
 * within one line's candidates and means nothing across lines, so interleaving them would rank
 * offers that were never in competition.
 *
 * Nothing here changes which offers can be awarded — this is the order of the rows and no more.
 */
export const orderOffersForComparison = <T extends { id: number; rfqItemId: number }>(
  offers: T[],
  placementFor: (offer: T) => ComparisonPlacement | null | undefined,
): T[] => {
  const band = (placement: ComparisonPlacement | null | undefined): number => {
    const state = offerScoreState(placement);
    if (state.status === 'SCORED') return 0;
    // An offer still being checked sits with the awardable-but-unscored ones: it has not been
    // refused, and dropping it to the bottom would look like a verdict nobody has reached.
    if (state.status === 'BLOCKED') return 2;
    return 1;
  };
  const decorated = offers.map((offer) => {
    const placement = placementFor(offer);
    return {
      offer,
      band: band(placement),
      score: placement?.weightedScore ?? null,
      landedUnitCost: placement?.landedUnitCost ?? null,
    };
  });
  const groups = new Map<number, typeof decorated>();
  decorated.forEach((row) => {
    const group = groups.get(row.offer.rfqItemId) ?? [];
    group.push(row);
    groups.set(row.offer.rfqItemId, group);
  });
  return [...groups.values()].flatMap((group) =>
    [...group]
      .sort((left, right) => {
        if (left.band !== right.band) return left.band - right.band;
        // Only band 0 carries scores, and a zero is a real score there — never a stand-in for one
        // that is missing. Compared for equality first: subtracting two absent scores would give
        // NaN, which a sort quietly reads as "these two are already in order" and leaves the rest
        // of this comparison unreached.
        const leftScore = left.score ?? Number.NEGATIVE_INFINITY;
        const rightScore = right.score ?? Number.NEGATIVE_INFINITY;
        if (leftScore !== rightScore) return rightScore - leftScore;
        const leftCost = left.landedUnitCost ?? Number.POSITIVE_INFINITY;
        const rightCost = right.landedUnitCost ?? Number.POSITIVE_INFINITY;
        if (leftCost !== rightCost) return leftCost - rightCost;
        return left.offer.id - right.offer.id;
      })
      .map((row) => row.offer),
  );
};

/**
 * Rank within the RFQ line, so a score is never shown without the field it beat. Only scored offers
 * are ranked: an offer with a missing weighted value has no score, and inventing a last place
 * for it would be the same as scoring it zero — which R-F forbids.
 */
export const rankScoredOffers = (
  offers: { supplierQuotedItemId: number; weightedScore?: number | null }[],
): Map<number, { rank: number; of: number }> => {
  const scored = offers
    .filter((offer) => offer.weightedScore != null)
    .sort((left, right) => (right.weightedScore ?? 0) - (left.weightedScore ?? 0));
  const ranks = new Map<number, { rank: number; of: number }>();
  scored.forEach((offer, index) =>
    ranks.set(offer.supplierQuotedItemId, { rank: index + 1, of: scored.length }),
  );
  return ranks;
};
