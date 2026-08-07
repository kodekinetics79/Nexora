import { describe, expect, it } from 'vitest';
import {
  confirmationMatches,
  confirmationProblem,
  destructiveReasonProblem,
  MIN_DESTRUCTION_REASON_LENGTH,
} from './destructiveConfirm';

describe('typed confirmation for the irreversible operations', () => {
  it('accepts the tenant name exactly', () => {
    expect(confirmationMatches('Acme Trading LLC', 'Acme Trading LLC')).toBe(true);
  });

  it('forgives surrounding whitespace, because the server trims too', () => {
    expect(confirmationMatches('  Acme Trading LLC  ', 'Acme Trading LLC')).toBe(true);
    expect(confirmationMatches('Acme Trading LLC', ' Acme Trading LLC ')).toBe(true);
  });

  it('is case-sensitive — "acme" is a different customer from "ACME"', () => {
    expect(confirmationMatches('acme trading llc', 'Acme Trading LLC')).toBe(false);
    expect(confirmationMatches('ACME TRADING LLC', 'Acme Trading LLC')).toBe(false);
  });

  it('refuses a near miss', () => {
    expect(confirmationMatches('Acme Trading', 'Acme Trading LLC')).toBe(false);
    expect(confirmationMatches('Acme  Trading LLC', 'Acme Trading LLC')).toBe(false);
  });

  it('never matches when there is nothing to match against', () => {
    // A blank required string would otherwise make an empty box a valid confirmation,
    // which is the one way this control could be bypassed entirely.
    expect(confirmationMatches('', '')).toBe(false);
    expect(confirmationMatches('   ', '   ')).toBe(false);
  });

  it('stays quiet until the operator has typed something', () => {
    expect(confirmationProblem('', 'Acme Trading LLC')).toBeNull();
    expect(confirmationProblem('   ', 'Acme Trading LLC')).toBeNull();
  });

  it('names the exact string once the operator gets it wrong', () => {
    expect(confirmationProblem('acme', 'Acme Trading LLC')).toContain('Acme Trading LLC');
  });
});

describe('reason floor for destroying a customer', () => {
  it('demands a reason at all', () => {
    expect(destructiveReasonProblem('')).toBeTruthy();
    expect(destructiveReasonProblem('    ')).toBeTruthy();
  });

  it('rejects a reason too short to be read by a person later', () => {
    expect(destructiveReasonProblem('done')).toBeTruthy();
    expect(destructiveReasonProblem('x'.repeat(MIN_DESTRUCTION_REASON_LENGTH - 1))).toBeTruthy();
  });

  it('accepts one that meets the platform floor', () => {
    expect(destructiveReasonProblem('x'.repeat(MIN_DESTRUCTION_REASON_LENGTH))).toBeNull();
    expect(destructiveReasonProblem('Contract terminated, ticket OPS-4412')).toBeNull();
  });

  it('measures the trimmed reason, so padding cannot buy length', () => {
    expect(destructiveReasonProblem(`  ${'x'.repeat(MIN_DESTRUCTION_REASON_LENGTH - 2)}  `)).toBeTruthy();
  });
});
