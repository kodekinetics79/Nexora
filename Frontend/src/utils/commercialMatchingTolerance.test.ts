import { describe, expect, it } from 'vitest';
import {
  DEFAULT_COMMERCIAL_TOLERANCES,
  priceMatches,
  quantityMatches,
  withinTolerance,
} from './commercialMatchingTolerance';

/**
 * FR-COM-04. This is the client-side mirror of the server's `CommercialMatchingTolerance`, and the
 * cases below are the same cases its `Within_is_symmetric_and_takes_the_wider_of_percentage_and_minimum`
 * theory asserts. If the two ever diverge, the capture screen starts telling operators something
 * the discrepancy report will contradict — which is the defect this file exists to prevent.
 */
describe('withinTolerance', () => {
  it.each([
    // Exact equality is inside every tolerance, including a tolerance of nothing at all.
    [100, 100, 0, 0, true],
    [101.5, 100, 2, 0, true],
    // Symmetric: rounding has no direction, and a one-sided rule would contradict its own label.
    [98.5, 100, 2, 0, true],
    [102.01, 100, 2, 0, false],
    [97.99, 100, 2, 0, false],
    // A percentage alone misbehaves at small values; the minimum amount is what covers them.
    [0.11, 0.1, 2, 0, false],
    [0.11, 0.1, 2, 0.01, true],
    // A negative allowance cannot turn a tolerance into a trigger.
    [100, 100, -5, -5, true],
  ])('treats %s against %s at %s%% / %s as within = %s', (actual, reference, percent, minimum, expected) => {
    expect(withinTolerance(actual, reference, percent, minimum)).toBe(expected);
  });

  it('refuses to call a value that is not a number a match', () => {
    expect(withinTolerance(Number.NaN, 100, 100, 100)).toBe(false);
  });
});

describe('priceMatches', () => {
  it('takes the percentage against the quoted price, which is the stable side', () => {
    const policy = { ...DEFAULT_COMMERCIAL_TOLERANCES, priceTolerancePercent: 2 };
    expect(priceMatches(101.5, 100, policy)).toBe(true);
    expect(priceMatches(105, 100, policy)).toBe(false);
  });

  it('reproduces exact equality when the tenant turns the tolerance off', () => {
    const policy = { ...DEFAULT_COMMERCIAL_TOLERANCES, priceTolerancePercent: 0 };
    expect(priceMatches(101.5, 100, policy)).toBe(false);
    expect(priceMatches(100, 100, policy)).toBe(true);
  });
});

describe('quantityMatches', () => {
  it('compares what we are awarding against what the buyer ordered, defaulting to exact', () => {
    expect(quantityMatches(4, 6, DEFAULT_COMMERCIAL_TOLERANCES)).toBe(false);
    expect(quantityMatches(6, 6, DEFAULT_COMMERCIAL_TOLERANCES)).toBe(true);
  });

  it('obeys its own tolerance, which is not the price tolerance', () => {
    const policy = { ...DEFAULT_COMMERCIAL_TOLERANCES, quantityTolerancePercent: 5 };
    // Ordered 10, awarding 9.6: a 0.4 difference against a 0.5 allowance.
    expect(quantityMatches(9.6, 10, policy)).toBe(true);
    expect(quantityMatches(8, 10, policy)).toBe(false);
  });
});
