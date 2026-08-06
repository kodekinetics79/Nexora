import { describe, expect, it } from 'vitest';
import {
  ROLE_RANK_ADMIN,
  ROLE_RANK_MANAGER,
  ROLE_RANK_MEMBER,
  ROLE_RANK_OWNER,
  asRoleRank,
  callerRankRange,
  canGrantRoleRank,
  isRoleSetupType,
  roleRankChoices,
  roleRankLabel,
} from './roleRankTiers';

const OWNER = { isSuperAdmin: true, isManager: true };
const MANAGER_OR_ADMIN = { isSuperAdmin: false, isManager: true };
const PLAIN_MEMBER = { isSuperAdmin: false, isManager: false };

const availabilityByRank = (caller: Parameters<typeof roleRankChoices>[0], currentRank?: number | null) =>
  Object.fromEntries(
    roleRankChoices(caller, { currentRank }).map((choice) => [choice.rank, choice.availability]),
  );

describe('isRoleSetupType', () => {
  it('matches the server rule: whitespace-stripped, case-insensitive "role"', () => {
    expect(isRoleSetupType('role')).toBe(true);
    expect(isRoleSetupType('Role')).toBe(true);
    expect(isRoleSetupType(' Role ')).toBe(true);
    expect(isRoleSetupType('ROLE')).toBe(true);
  });

  it('rejects lookup types and absent values', () => {
    for (const type of ['ROLES', 'CURRENCY', 'UOM', 'user role', '']) {
      expect(isRoleSetupType(type)).toBe(false);
    }
    expect(isRoleSetupType(null)).toBe(false);
    expect(isRoleSetupType(undefined)).toBe(false);
  });
});

describe('asRoleRank', () => {
  it('keeps the four defined tiers', () => {
    expect(asRoleRank(ROLE_RANK_MEMBER)).toBe(ROLE_RANK_MEMBER);
    expect(asRoleRank(ROLE_RANK_MANAGER)).toBe(ROLE_RANK_MANAGER);
    expect(asRoleRank(ROLE_RANK_ADMIN)).toBe(ROLE_RANK_ADMIN);
    expect(asRoleRank(ROLE_RANK_OWNER)).toBe(ROLE_RANK_OWNER);
  });

  it('falls back to Member for anything missing or unrecognised', () => {
    expect(asRoleRank(undefined)).toBe(ROLE_RANK_MEMBER);
    expect(asRoleRank(null)).toBe(ROLE_RANK_MEMBER);
    expect(asRoleRank(15)).toBe(ROLE_RANK_MEMBER);
    expect(asRoleRank(99)).toBe(ROLE_RANK_MEMBER);
  });
});

describe('roleRankLabel', () => {
  it('never shows a raw number', () => {
    expect(roleRankLabel(ROLE_RANK_MEMBER)).toBe('Member — no administrative authority');
    expect(roleRankLabel(ROLE_RANK_OWNER)).toBe('Owner — unrestricted, including role administration');
  });
});

describe('callerRankRange', () => {
  it('pins a super administrator at Owner', () => {
    expect(callerRankRange(OWNER)).toEqual({ min: ROLE_RANK_OWNER, max: ROLE_RANK_OWNER });
  });

  it('spans Manager..Admin when the session only says "manager or administrator"', () => {
    expect(callerRankRange(MANAGER_OR_ADMIN)).toEqual({ min: ROLE_RANK_MANAGER, max: ROLE_RANK_ADMIN });
  });

  it('fails closed at Member for an unknown or flagless session', () => {
    expect(callerRankRange(PLAIN_MEMBER)).toEqual({ min: ROLE_RANK_MEMBER, max: ROLE_RANK_MEMBER });
    expect(callerRankRange(null)).toEqual({ min: ROLE_RANK_MEMBER, max: ROLE_RANK_MEMBER });
    expect(callerRankRange(undefined)).toEqual({ min: ROLE_RANK_MEMBER, max: ROLE_RANK_MEMBER });
  });
});

describe('roleRankChoices on create', () => {
  it('always offers all four tiers so a blocked one can explain itself', () => {
    expect(roleRankChoices(OWNER).map((choice) => choice.rank))
      .toEqual([ROLE_RANK_MEMBER, ROLE_RANK_MANAGER, ROLE_RANK_ADMIN, ROLE_RANK_OWNER]);
  });

  it('lets an owner grant everything below Owner, but never a second Owner', () => {
    expect(availabilityByRank(OWNER)).toEqual({
      [ROLE_RANK_MEMBER]: 'grantable',
      [ROLE_RANK_MANAGER]: 'grantable',
      [ROLE_RANK_ADMIN]: 'grantable',
      [ROLE_RANK_OWNER]: 'blocked',
    });
  });

  it('marks Manager uncertain for a manager-or-administrator, because the session cannot tell them apart', () => {
    expect(availabilityByRank(MANAGER_OR_ADMIN)).toEqual({
      [ROLE_RANK_MEMBER]: 'grantable',
      [ROLE_RANK_MANAGER]: 'uncertain',
      [ROLE_RANK_ADMIN]: 'blocked',
      [ROLE_RANK_OWNER]: 'blocked',
    });
  });

  it('blocks every tier, Member included, for a caller with no administrative authority', () => {
    const choices = roleRankChoices(PLAIN_MEMBER);
    expect(choices.every((choice) => choice.disabled)).toBe(true);
    expect(choices[0].note).toContain('do not hold administrative authority');
  });

  it('explains a blocked tier by naming it', () => {
    const owner = roleRankChoices(OWNER).find((choice) => choice.rank === ROLE_RANK_OWNER);
    expect(owner?.note).toContain('at or above your own authority');
  });
});

describe('roleRankChoices when editing an existing role', () => {
  it('always allows leaving the stored tier alone, even when it outranks the caller', () => {
    const choices = roleRankChoices(MANAGER_OR_ADMIN, { currentRank: ROLE_RANK_OWNER });
    const owner = choices.find((choice) => choice.rank === ROLE_RANK_OWNER);
    expect(owner?.disabled).toBe(false);
    expect(owner?.note).toContain('unchanged');
  });

  it('refuses to DEMOTE a role that already sits at or above the caller', () => {
    const availability = availabilityByRank(MANAGER_OR_ADMIN, ROLE_RANK_OWNER);
    expect(availability[ROLE_RANK_MEMBER]).toBe('blocked');
    expect(availability[ROLE_RANK_MANAGER]).toBe('blocked');
    const member = roleRankChoices(MANAGER_OR_ADMIN, { currentRank: ROLE_RANK_OWNER })
      .find((choice) => choice.rank === ROLE_RANK_MEMBER);
    expect(member?.note).toContain('only someone above it can move it');
  });

  it('lets an owner move a role that is below them', () => {
    expect(availabilityByRank(OWNER, ROLE_RANK_MANAGER)).toEqual({
      [ROLE_RANK_MEMBER]: 'grantable',
      [ROLE_RANK_MANAGER]: 'grantable',
      [ROLE_RANK_ADMIN]: 'grantable',
      [ROLE_RANK_OWNER]: 'blocked',
    });
  });

  it('treats moving a Manager role as uncertain for a manager-or-administrator', () => {
    // The stored Manager(10) tier equals the caller's own if they are a manager, and is below it if
    // they are an administrator — the session cannot say which, so the move is flagged, not hidden.
    const member = roleRankChoices(MANAGER_OR_ADMIN, { currentRank: ROLE_RANK_MANAGER })
      .find((choice) => choice.rank === ROLE_RANK_MEMBER);
    expect(member?.availability).toBe('uncertain');
    expect(member?.disabled).toBe(false);
    expect(member?.note).toContain('may already carry authority at or above your own');
  });

  it('blocks any move of an Admin role, which is at or above every non-owner caller', () => {
    expect(availabilityByRank(MANAGER_OR_ADMIN, ROLE_RANK_ADMIN)[ROLE_RANK_MEMBER]).toBe('blocked');
  });
});

describe('canGrantRoleRank', () => {
  it('answers the one question the save button needs', () => {
    expect(canGrantRoleRank(OWNER, ROLE_RANK_ADMIN)).toBe(true);
    expect(canGrantRoleRank(OWNER, ROLE_RANK_OWNER)).toBe(false);
    expect(canGrantRoleRank(MANAGER_OR_ADMIN, ROLE_RANK_MEMBER)).toBe(true);
    expect(canGrantRoleRank(MANAGER_OR_ADMIN, ROLE_RANK_ADMIN)).toBe(false);
    expect(canGrantRoleRank(PLAIN_MEMBER, ROLE_RANK_MEMBER)).toBe(false);
  });

  it('permits an unchanged tier on an outranking role', () => {
    expect(canGrantRoleRank(MANAGER_OR_ADMIN, ROLE_RANK_OWNER, { currentRank: ROLE_RANK_OWNER })).toBe(true);
  });
});
