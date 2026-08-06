/**
 * Role authority tiers (`Setup_Master.RoleRank`), mirrored from the server so the roles screen can
 * offer the tiers a user may actually grant instead of a control that 403s on save.
 *
 * The authority stays on the server (`SetupMasterController.RoleRankDenialAsync`, backed by
 * `Authorization/RoleRanks.cs`). This module reproduces two rules, in the same order:
 *
 *   1. A caller may never set a role to a tier AT OR ABOVE their own tier — on create that
 *      includes Member, because every tier on a new role is a tier the caller chose.
 *   2. A caller may never change the tier of a role that already sits at or above their own.
 *      Leaving that role's tier untouched is always allowed: the server returns early when the
 *      requested tier equals the stored one.
 *
 * DTO gap: the session only reports BOOLEANS (`isSuperAdmin`, `isManager` from
 * `GET /api/User/me/permissions`), never the caller's numeric rank. `isManager` is true for both
 * Manager(10) and Admin(20) — `IRoleGate.IsManagerOrAdminAsync` is `rank >= Manager` — so for a
 * manager-or-administrator the caller's own tier is a RANGE, not a number. Tiers that fall inside
 * that range are marked `uncertain`: offered, because hiding them would stop a real administrator
 * from ever creating a manager role, but labelled so the user knows the service may decline.
 */

export const ROLE_RANK_MEMBER = 0;
export const ROLE_RANK_MANAGER = 10;
export const ROLE_RANK_ADMIN = 20;
export const ROLE_RANK_OWNER = 30;

export type RoleRank =
  | typeof ROLE_RANK_MEMBER
  | typeof ROLE_RANK_MANAGER
  | typeof ROLE_RANK_ADMIN
  | typeof ROLE_RANK_OWNER;

/** The safe default, and the value the server stores when a client sends nothing. */
export const DEFAULT_ROLE_RANK: RoleRank = ROLE_RANK_MEMBER;

/**
 * `SetupTypes.IsRole`: whitespace-stripped, case-insensitive equality with "role". Production
 * stores 'Role', and ' Role ' variants exist in live data, so match the server's tolerance rather
 * than a literal comparison.
 */
export const isRoleSetupType = (setupType: string | null | undefined): boolean =>
  (setupType ?? '').replace(/\s/g, '').toLowerCase() === 'role';

/** Anything the server did not send, or a tier this build does not know, reads as Member. */
export const asRoleRank = (value: number | null | undefined): RoleRank => {
  switch (value) {
    case ROLE_RANK_OWNER: return ROLE_RANK_OWNER;
    case ROLE_RANK_ADMIN: return ROLE_RANK_ADMIN;
    case ROLE_RANK_MANAGER: return ROLE_RANK_MANAGER;
    default: return ROLE_RANK_MEMBER;
  }
};

export interface RoleRankTier {
  rank: RoleRank;
  /** The tier's name on its own, for a chip or a column. */
  label: string;
  /** One clause saying what the tier grants, shown beside the label. */
  summary: string;
  /** Two sentences of plain commercial language for the person choosing. */
  description: string;
}

/** Lowest first, matching `RoleRanks.All`. */
export const ROLE_RANK_TIERS: readonly RoleRankTier[] = [
  {
    rank: ROLE_RANK_MEMBER,
    label: 'Member',
    summary: 'no administrative authority',
    description:
      'Can see and do only what this role is explicitly granted, module by module. This is the right '
      + 'choice for almost everyone.',
  },
  {
    rank: ROLE_RANK_MANAGER,
    label: 'Manager',
    summary: 'team and workload administration',
    description:
      'Sees the whole organization’s work and decides who handles it — assigning leads, working '
      + 'queues and reviewing the team’s pipeline.',
  },
  {
    rank: ROLE_RANK_ADMIN,
    label: 'Administrator',
    summary: 'full module administration',
    description:
      'Administers every module in the organization. A missing permission line cannot lock this role '
      + 'out of the modules it is responsible for.',
  },
  {
    rank: ROLE_RANK_OWNER,
    label: 'Owner',
    summary: 'unrestricted, including role administration',
    description:
      'The top of the organization: unrestricted access, including deciding who holds which role. '
      + 'Owner roles are provisioned when the organization is created, not from this screen.',
  },
];

export const roleRankTier = (rank: number | null | undefined): RoleRankTier => {
  const normalized = asRoleRank(rank);
  // ROLE_RANK_TIERS covers every RoleRank, so the fallback is unreachable; it exists to keep the
  // return type non-optional under strict TS rather than to describe a real state.
  return ROLE_RANK_TIERS.find((tier) => tier.rank === normalized) ?? ROLE_RANK_TIERS[0];
};

/** "Manager — team and workload administration". */
export const roleRankLabel = (rank: number | null | undefined): string => {
  const tier = roleRankTier(rank);
  return `${tier.label} — ${tier.summary}`;
};

/**
 * `grantable` the server will accept, `blocked` it will certainly refuse, `uncertain` depends on
 * whether the caller holds Manager or Administrator — a distinction the session does not expose.
 */
export type RoleRankAvailability = 'grantable' | 'uncertain' | 'blocked';

export interface RoleRankChoice extends RoleRankTier {
  availability: RoleRankAvailability;
  /** `blocked` tiers are rendered but never selectable. */
  disabled: boolean;
  /** Why this tier is refused, or the caveat on an uncertain one. Null when plainly grantable. */
  note: string | null;
}

/** What the session knows about the signed-in user's own authority. */
export interface RoleRankAuthority {
  isSuperAdmin?: boolean | null;
  isManager?: boolean | null;
}

export interface CallerRankRange {
  /** The lowest tier the caller could hold. */
  min: RoleRank;
  /** The highest tier the caller could hold. */
  max: RoleRank;
}

/**
 * Turns the two session booleans into the narrowest range they justify. `isSuperAdmin` pins the
 * caller at Owner; `isManager` alone means Manager OR Administrator; neither means Member.
 */
export const callerRankRange = (caller: RoleRankAuthority | null | undefined): CallerRankRange => {
  if (caller?.isSuperAdmin === true) return { min: ROLE_RANK_OWNER, max: ROLE_RANK_OWNER };
  if (caller?.isManager === true) return { min: ROLE_RANK_MANAGER, max: ROLE_RANK_ADMIN };
  return { min: ROLE_RANK_MEMBER, max: ROLE_RANK_MEMBER };
};

/** Is `rank` strictly below the caller's own tier — certainly, certainly not, or possibly? */
const strictlyBelowCaller = (rank: RoleRank, range: CallerRankRange): RoleRankAvailability => {
  if (rank < range.min) return 'grantable';
  if (rank >= range.max) return 'blocked';
  return 'uncertain';
};

const WORST: Record<RoleRankAvailability, number> = { grantable: 0, uncertain: 1, blocked: 2 };

const worse = (a: RoleRankAvailability, b: RoleRankAvailability): RoleRankAvailability =>
  WORST[a] >= WORST[b] ? a : b;

export interface RoleRankChoiceOptions {
  /**
   * The tier the role is stored at, or null/undefined when creating. A role at or above the
   * caller's own tier cannot be moved at all, but may be left where it is.
   */
  currentRank?: number | null;
}

/**
 * Every tier, annotated with whether this caller may put a role there.
 *
 * Blocked tiers are returned rather than dropped so the form can say WHY a tier is unavailable
 * instead of silently offering a shorter list.
 */
export const roleRankChoices = (
  caller: RoleRankAuthority | null | undefined,
  options?: RoleRankChoiceOptions,
): RoleRankChoice[] => {
  const range = callerRankRange(caller);
  const isEditing = options?.currentRank != null;
  const currentRank = isEditing ? asRoleRank(options?.currentRank) : null;

  // Rule 2 is about the role, not the requested tier, so it is evaluated once.
  const mayMoveThisRole: RoleRankAvailability =
    currentRank === null ? 'grantable' : strictlyBelowCaller(currentRank, range);

  return ROLE_RANK_TIERS.map((tier) => {
    // Choosing the tier the role already has is not a change; the server's guard returns before
    // it compares anything.
    if (currentRank !== null && tier.rank === currentRank) {
      return {
        ...tier,
        availability: 'grantable' as const,
        disabled: false,
        note: 'The tier this role has today. Leaving it unchanged is always allowed.',
      };
    }

    const availability = worse(strictlyBelowCaller(tier.rank, range), mayMoveThisRole);

    return {
      ...tier,
      availability,
      disabled: availability === 'blocked',
      note: noteFor(tier, availability, range, mayMoveThisRole),
    };
  });
};

const noteFor = (
  tier: RoleRankTier,
  availability: RoleRankAvailability,
  range: CallerRankRange,
  mayMoveThisRole: RoleRankAvailability,
): string | null => {
  if (availability === 'grantable') return null;

  // The role itself is the obstacle, so say that rather than blaming the tier being pointed at.
  if (mayMoveThisRole !== 'grantable' && WORST[mayMoveThisRole] >= WORST[availability]) {
    return mayMoveThisRole === 'blocked'
      ? 'This role already carries authority at or above your own, so only someone above it can move it.'
      : 'This role may already carry authority at or above your own — if it does, the change will be declined.';
  }

  if (availability === 'blocked') {
    return range.max === ROLE_RANK_MEMBER
      ? 'You do not hold administrative authority, so you cannot grant any tier.'
      : `${tier.label} is at or above your own authority. Only someone above this tier can grant it.`;
  }

  return `Available to administrators. If you hold ${roleRankTier(ROLE_RANK_MANAGER).label}, the service will decline this tier.`;
};

/** True when this caller can put a NEW role at this tier without the server refusing. */
export const canGrantRoleRank = (
  caller: RoleRankAuthority | null | undefined,
  rank: number | null | undefined,
  options?: RoleRankChoiceOptions,
): boolean => {
  const target = asRoleRank(rank);
  const choice = roleRankChoices(caller, options).find((item) => item.rank === target);
  return choice?.disabled === false;
};

/**
 * Fallback copy for a 403 from the rank guard. `toPresentableError` deliberately never renders
 * server text on a 403 (it can leak internals), so the exact server sentence stays in the
 * "technical detail" disclosure and this names the two reasons in product language.
 */
export const ROLE_RANK_DENIED_MESSAGE =
  'The service refused this authority tier. You can only grant tiers below your own, and a role that '
  + 'already matches or outranks you can only be changed by someone above it. Nothing was saved — '
  + 'open the technical detail below for the exact reason.';
