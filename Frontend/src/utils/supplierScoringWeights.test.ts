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

  it('defaults "Balanced" to price 70, lead time 20, payment terms 10, warranty 0', () => {
    expect(preset('BALANCED').weights).toEqual(form('80', '20', '0', '0'));
  });

  it('lets lead time outweigh price under "Speed matters"', () => {
    expect(preset('SPEED').weights).toEqual(form('40', '60', '0', '0'));
    expect(Number(preset('SPEED').weights.leadTimeWeight))
      .toBeGreaterThan(Number(preset('SPEED').weights.priceWeight));
  });

  it('gives warranty zero weight in every preset, because warranty is free text today', () => {
    WEIGHT_PRESETS.forEach((candidate) => expect(candidate.weights.warrantyWeight).toBe('0'));
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
