import { describe, expect, it } from 'vitest';
import {
  cheapestEligibleOffer,
  offerScoreState,
  orderOffersForComparison,
  rankScoredOffers,
  recommendationTradeOff,
} from './supplierComparison';

const offer = (
  supplierQuotedItemId: number,
  landedUnitCost: number | null,
  leadTimeDays: number | null = null,
  eligible = true,
) => ({ supplierQuotedItemId, landedUnitCost, leadTimeDays, eligible });

const sar = (value: number) => `SAR ${value.toFixed(2)}`;

describe('cheapestEligibleOffer', () => {
  it('finds the lowest landed cost among eligible offers', () => {
    const cheapest = cheapestEligibleOffer([offer(1, 1400), offer(2, 1240), offer(3, 1800)]);
    expect(cheapest?.supplierQuotedItemId).toBe(2);
  });

  it('ignores blocked offers — an offer nobody can award is not the cheapest anything', () => {
    const cheapest = cheapestEligibleOffer([offer(1, 900, null, false), offer(2, 1240)]);
    expect(cheapest?.supplierQuotedItemId).toBe(2);
  });

  it('ignores offers with no landed cost rather than treating them as free', () => {
    const cheapest = cheapestEligibleOffer([offer(1, null), offer(2, 1240)]);
    expect(cheapest?.supplierQuotedItemId).toBe(2);
  });

  it('returns null rather than inventing a cheapest offer', () => {
    expect(cheapestEligibleOffer([])).toBeNull();
    expect(cheapestEligibleOffer([offer(1, null), offer(2, null)])).toBeNull();
  });
});

describe('recommendationTradeOff', () => {
  it('says so plainly when the recommended offer is also the cheapest', () => {
    const winner = offer(1, 1240, 20);
    expect(recommendationTradeOff(winner, winner, sar)).toBe('also the lowest landed cost');
  });

  it('states the premium and the days bought with it when the winner is not the cheapest', () => {
    expect(recommendationTradeOff(offer(1, 2480, 8), offer(2, 1240, 20), sar))
      .toBe('SAR 1240.00 more than the cheapest, 12 days faster');
  });

  it('omits the speed claim when either lead time is missing', () => {
    expect(recommendationTradeOff(offer(1, 2480, null), offer(2, 1240, 20), sar))
      .toBe('SAR 1240.00 more than the cheapest');
    expect(recommendationTradeOff(offer(1, 2480, 8), offer(2, 1240, null), sar))
      .toBe('SAR 1240.00 more than the cheapest');
  });

  it('never claims a saving the recommendation did not make', () => {
    // Same cost, slower than the cheapest: there is nothing good to say, so it says nothing good.
    expect(recommendationTradeOff(offer(1, 1240, 30), offer(2, 1240, 20), sar))
      .toBe('not the lowest landed cost');
  });

  it('falls back to a plain statement when no cheapest offer could be identified', () => {
    expect(recommendationTradeOff(offer(1, 1240, 20), null, sar))
      .toBe('also the lowest landed cost');
  });
});

describe('rankScoredOffers', () => {
  it('ranks scored offers highest-first and states the field size', () => {
    const ranks = rankScoredOffers([
      { supplierQuotedItemId: 1, weightedScore: 64 },
      { supplierQuotedItemId: 2, weightedScore: 82 },
      { supplierQuotedItemId: 3, weightedScore: 71 },
    ]);
    expect(ranks.get(2)).toEqual({ rank: 1, of: 3 });
    expect(ranks.get(3)).toEqual({ rank: 2, of: 3 });
    expect(ranks.get(1)).toEqual({ rank: 3, of: 3 });
  });

  it('leaves an unscored offer unranked instead of putting it last', () => {
    // R-F: a missing criterion means no score. A last place would be a zero by another name.
    const ranks = rankScoredOffers([
      { supplierQuotedItemId: 1, weightedScore: 82 },
      { supplierQuotedItemId: 2, weightedScore: null },
      { supplierQuotedItemId: 3, weightedScore: undefined },
    ]);
    expect(ranks.get(1)).toEqual({ rank: 1, of: 1 });
    expect(ranks.has(2)).toBe(false);
    expect(ranks.has(3)).toBe(false);
  });

  it('scores a zero as a real score, not as a missing one', () => {
    const ranks = rankScoredOffers([
      { supplierQuotedItemId: 1, weightedScore: 0 },
      { supplierQuotedItemId: 2, weightedScore: 40 },
    ]);
    expect(ranks.get(1)).toEqual({ rank: 2, of: 2 });
  });

  it('does not reorder the array it was given', () => {
    const offers = [
      { supplierQuotedItemId: 1, weightedScore: 10 },
      { supplierQuotedItemId: 2, weightedScore: 90 },
    ];
    rankScoredOffers(offers);
    expect(offers.map((entry) => entry.supplierQuotedItemId)).toEqual([1, 2]);
  });
});

describe('orderOffersForComparison', () => {
  // The screen renders these; the comparison decides where they go.
  const row = (id: number, rfqItemId = 10) => ({ id, rfqItemId });
  const placement = (
    weightedScore: number | null,
    eligible = true,
    landedUnitCost: number | null = null,
  ) => ({ weightedScore, eligible, landedUnitCost });

  it('puts the best score at the top instead of leaving the API order alone', () => {
    // Without this the weights change the numbers in the cells and move nothing on screen, so a
    // sales engineer sees the same order before and after and decides the control does not work.
    const placements = new Map([
      [1, placement(64)],
      [2, placement(82)],
      [3, placement(71)],
    ]);
    const ordered = orderOffersForComparison(
      [row(1), row(2), row(3)],
      (offer) => placements.get(offer.id),
    );
    expect(ordered.map((offer) => offer.id)).toEqual([2, 3, 1]);
  });

  it('reorders when the weights change the score, from the same input order', () => {
    const offers = [row(1), row(2)];
    const cheapWins = orderOffersForComparison(offers, (offer) =>
      offer.id === 1 ? placement(90) : placement(40));
    const speedWins = orderOffersForComparison(offers, (offer) =>
      offer.id === 1 ? placement(40) : placement(90));
    expect(cheapWins.map((offer) => offer.id)).toEqual([1, 2]);
    expect(speedWins.map((offer) => offer.id)).toEqual([2, 1]);
  });

  it('keeps an unscored but awardable offer above one that cannot be awarded', () => {
    const placements = new Map([
      [1, placement(null, false, 100)],
      [2, placement(null, true, 900)],
      [3, placement(55)],
    ]);
    const ordered = orderOffersForComparison(
      [row(1), row(2), row(3)],
      (offer) => placements.get(offer.id),
    );
    // Scored first, then the offer with a value missing, then the blocked one — even though the
    // blocked offer is by far the cheapest.
    expect(ordered.map((offer) => offer.id)).toEqual([3, 2, 1]);
  });

  it('breaks a tie by landed cost and then by id, so two identical requests render alike', () => {
    const placements = new Map([
      [7, placement(70, true, 1400)],
      [3, placement(70, true, 1240)],
      [9, placement(70, true, 1240)],
    ]);
    const ordered = orderOffersForComparison(
      [row(7), row(9), row(3)],
      (offer) => placements.get(offer.id),
    );
    expect(ordered.map((offer) => offer.id)).toEqual([3, 9, 7]);
  });

  it('ranks offers only against the RFQ line they were quoted for', () => {
    // A score is worked out within one line's candidates. Interleaving two lines would rank
    // offers that were never in competition with each other.
    const placements = new Map([
      [1, placement(30)],
      [2, placement(95)],
      [3, placement(60)],
      [4, placement(20)],
    ]);
    const ordered = orderOffersForComparison(
      [row(1, 10), row(2, 20), row(3, 10), row(4, 20)],
      (offer) => placements.get(offer.id),
    );
    expect(ordered.map((offer) => offer.id)).toEqual([3, 1, 2, 4]);
  });

  it('does not push an offer to the bottom while its comparison is still loading', () => {
    const ordered = orderOffersForComparison([row(4), row(2)], () => undefined);
    expect(ordered.map((offer) => offer.id)).toEqual([2, 4]);
  });

  it('treats a zero score as a real score and not as a missing one', () => {
    const placements = new Map([
      [1, placement(0)],
      [2, placement(null, true)],
    ]);
    const ordered = orderOffersForComparison(
      [row(2), row(1)],
      (offer) => placements.get(offer.id),
    );
    expect(ordered.map((offer) => offer.id)).toEqual([1, 2]);
  });

  it('leaves the array it was given alone', () => {
    const offers = [row(1), row(2)];
    orderOffersForComparison(offers, (offer) => placement(offer.id === 1 ? 10 : 90));
    expect(offers.map((offer) => offer.id)).toEqual([1, 2]);
  });
});

describe('offerScoreState', () => {
  it('never tells a blocked offer it can still be awarded', () => {
    // The row that refuses an offer used to carry the sentence that says it is awardable, because
    // both states arrive with no score.
    const state = offerScoreState({
      eligible: false,
      weightedScore: null,
      blockers: ['supplier registration expired'],
      scoreUnavailableReason: 'Not scored — this offer cannot be awarded as it stands',
    });
    expect(state.status).toBe('BLOCKED');
    expect(state.detail).not.toMatch(/can still be awarded/i);
    expect(state.detail).toContain('supplier registration expired');
    expect(state.headline).toBe('Not scored — this offer cannot be awarded as it stands');
  });

  it('says why an awardable offer has no score, and that it is still awardable', () => {
    const state = offerScoreState({
      eligible: true,
      weightedScore: null,
      scoreUnavailableReason: 'Cannot score — lead time missing',
      blockers: [],
    });
    expect(state.status).toBe('NOT_SCORED');
    expect(state.headline).toBe('Cannot score — lead time missing');
    expect(state.detail).toMatch(/can still be awarded/i);
  });

  it('states the blockers even when the server sent no reason of its own', () => {
    // A mixed-currency set blocks every offer before anything is scored, so no reason comes back.
    const state = offerScoreState({
      eligible: false,
      weightedScore: null,
      blockers: ['currency not comparable without approved FX evidence'],
    });
    expect(state.detail).toContain('currency not comparable without approved FX evidence');
    expect(state.detail).not.toMatch(/can still be awarded/i);
  });

  it('reads eligibility before the score, so a number cannot talk past a blocker', () => {
    const state = offerScoreState({ eligible: false, weightedScore: 91, blockers: ['on hold'] });
    expect(state.status).toBe('BLOCKED');
  });

  it('shows the score with its ceiling, never a bare number', () => {
    const state = offerScoreState({ eligible: true, weightedScore: 82.5, blockers: [] });
    expect(state.status).toBe('SCORED');
    expect(state.headline).toBe('Weighted 82.5/100');
  });

  it('claims nothing at all while the comparison is still loading', () => {
    const state = offerScoreState(undefined);
    expect(state.status).toBe('PENDING');
    expect(state.detail).not.toMatch(/awarded/i);
  });
});
