import { describe, expect, it } from 'vitest';
import {
  WEIGHT_CRITERIA,
  WEIGHT_PRESETS,
  matchingPreset,
  sameWeights,
  weightFieldError,
  weightTotal,
  weightTotalError,
  type SupplierWeightsForm,
} from './supplierScoringWeights';

const form = (
  priceWeight: string,
  leadTimeWeight: string,
  warrantyWeight: string,
  paymentTermsWeight: string,
): SupplierWeightsForm => ({ priceWeight, leadTimeWeight, warrantyWeight, paymentTermsWeight });

const preset = (id: string) => WEIGHT_PRESETS.find((candidate) => candidate.id === id)!;

describe('supplier scoring weights', () => {
  it('offers exactly the four criteria FR-QTM-03 names, and no coverage weight', () => {
    expect(WEIGHT_CRITERIA.map((criterion) => criterion.key)).toEqual([
      'priceWeight',
      'leadTimeWeight',
      'warrantyWeight',
      'paymentTermsWeight',
    ]);
  });

  it('reproduces today\'s behaviour exactly under "Cheapest wins"', () => {
    // If this drifts, every tenant that chose "do not change how offers are ranked" silently gets
    // a different recommendation.
    expect(preset('CHEAPEST').weights).toEqual(form('100', '0', '0', '0'));
  });

  it('defaults "Balanced" to price 80, lead time 20, warranty 0, payment terms 0', () => {
    expect(preset('BALANCED').weights).toEqual(form('80', '20', '0', '0'));
  });

  it('lets lead time outweigh price under "Speed matters"', () => {
    expect(preset('SPEED').weights).toEqual(form('40', '60', '0', '0'));
    expect(Number(preset('SPEED').weights.leadTimeWeight))
      .toBeGreaterThan(Number(preset('SPEED').weights.priceWeight));
  });

  it('gives warranty zero weight in every preset, because no existing line carries the months yet', () => {
    // The months are captured per supplier quote line and are blank on every line recorded before
    // this release, so a preset that weighted warranty would refuse to rank anything on day one.
    WEIGHT_PRESETS.forEach((candidate) => expect(candidate.weights.warrantyWeight).toBe('0'));
  });

  it('tells the truth about warranty under the weight box: a number of months, longer scores higher', () => {
    // The screen that switches warranty scoring on used to say warranty was free text and to leave
    // this weight at zero — advice that contradicted the field the score is now computed from.
    const warranty = WEIGHT_CRITERIA.find((criterion) => criterion.key === 'warrantyWeight')!;
    expect(warranty.helper).toMatch(/months/i);
    expect(warranty.helper).toMatch(/longer/i);
    expect(warranty.helper).not.toMatch(/free text/i);
    // It must not tell the reader to leave the weight at zero; that is the customer's decision now.
    expect(warranty.helper).not.toMatch(/zero/i);
  });

  it('keeps every criterion helper naming the value it is scored from', () => {
    // A weight box with no stated input is an unexplained score waiting to happen.
    WEIGHT_CRITERIA.forEach((criterion) => expect(criterion.helper.trim().length).toBeGreaterThan(0));
    expect(WEIGHT_CRITERIA.find((c) => c.key === 'paymentTermsWeight')!.helper).toMatch(/credit days/i);
  });

  it('makes every preset total 100', () => {
    WEIGHT_PRESETS.forEach((candidate) => expect(weightTotal(candidate.weights)).toBe(100));
    WEIGHT_PRESETS.forEach((candidate) => expect(weightTotalError(candidate.weights)).toBeNull());
  });

  it('refuses a set that does not total 100, and says what it totals', () => {
    expect(weightTotalError(form('70', '20', '0', '20')))
      .toBe('The four weights total 110. They must total 100.');
    expect(weightTotalError(form('10', '10', '10', '10')))
      .toBe('The four weights total 40. They must total 100.');
  });

  it('treats a blank weight as an error and never as a zero', () => {
    expect(weightFieldError('')).toBe('Enter a whole number between 0 and 100.');
    expect(weightFieldError('  ')).toBe('Enter a whole number between 0 and 100.');
    expect(weightFieldError('abc')).toBe('Enter a whole number between 0 and 100.');
  });

  it('rejects fractional and out-of-range weights', () => {
    expect(weightFieldError('12.5')).toBe('Weights are whole numbers.');
    expect(weightFieldError('-1')).toBe('Must be between 0 and 100.');
    expect(weightFieldError('101')).toBe('Must be between 0 and 100.');
    expect(weightFieldError('0')).toBeNull();
    expect(weightFieldError('100')).toBeNull();
  });

  it('does not report a total error while an individual field is still invalid', () => {
    // Two errors on one field's worth of input reads as two separate problems to fix.
    expect(weightTotalError(form('', '20', '0', '10'))).toBeNull();
  });

  it('recognises a preset regardless of how the number was typed', () => {
    expect(matchingPreset(form('80', '20', '0', '0'))?.id).toBe('BALANCED');
    expect(matchingPreset(form(' 80 ', '20', '0', '0'))?.id).toBe('BALANCED');
    expect(matchingPreset(form('70', '20', '5', '5'))).toBeUndefined();
  });

  it('compares weight sets by value, so a re-typed identical set is not a change', () => {
    expect(sameWeights(form('70', '20', '0', '10'), form('70', '20', '0', '10'))).toBe(true);
    expect(sameWeights(form('70', '20', '0', '10'), form('70', '21', '0', '9'))).toBe(false);
  });
});
