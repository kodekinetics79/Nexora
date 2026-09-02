import { describe, expect, it } from 'vitest';
import {
  ALL_PRESETS,
  DESK_PRESETS,
  ROLE_LADDER,
  administersEverything,
  describeDrift,
  driftFromPreset,
  presetForRoleName,
  presetGrantFor,
} from './rolePresets';
import { ROLE_RANK_MANAGER, ROLE_RANK_MEMBER, ROLE_RANK_OWNER } from './roleRankTiers';

/**
 * Behaviour of the preset catalogue.
 *
 * That these presets MATCH `TenantBaselineCatalog.cs` is asserted on the backend side, in
 * `FrontendCatalogueMirrorTests`: the authority verifies its own mirror, and a Vite test cannot
 * read a file outside the frontend project. What is asserted here is the product shape — three
 * rungs, two desks, and a reset that behaves.
 */
describe('the ladder the customer is offered', () => {
  it('has exactly three rungs', () => {
    // Three is the product decision, and the reason this file exists. A fourth rung is a decision
    // the customer has to make on their first day for no benefit.
    expect(ROLE_LADDER).toHaveLength(3);
    expect(ROLE_LADDER.map((preset) => preset.name))
      .toEqual(['System Administrator', 'Sales Manager', 'Sales Representative']);
  });

  it('never offers the tenant owner', () => {
    // Owner roles are provisioned when the organization is created. Offering the tier here is how
    // a live tenant ends up with every one of its users at the top of the tenant plane.
    expect(ALL_PRESETS.every((preset) => preset.rank < ROLE_RANK_OWNER)).toBe(true);
  });

  it('keeps the other two desks off the ladder and at the same authority as a rep', () => {
    // Procurement and Finance are a SECOND DIMENSION — which desk, not which rung. Folding them
    // into the ladder would produce a five-rung ordering that nobody can rank.
    expect(DESK_PRESETS.map((preset) => preset.name))
      .toEqual(['Procurement Officer', 'Finance Officer']);
    expect(DESK_PRESETS.every((preset) => preset.rank === ROLE_RANK_MEMBER)).toBe(true);
    for (const desk of DESK_PRESETS) {
      expect(ROLE_LADDER).not.toContainEqual(desk);
    }
  });

  it('keeps raising an exchange rate separate from approving one', () => {
    // One user who could create an FX rate and approve it could move money on their own
    // signature, and an approved rate re-bases quote totals, the pricing floor and the AI spend
    // cap. No preset may hold both.
    for (const preset of ALL_PRESETS) {
      const holdsApproval = preset.grants.some((grant) => grant.module === 'Exchange Rate Approval');
      expect(holdsApproval, `${preset.name} must not hold rate approval`).toBe(false);
    }
    const finance = ALL_PRESETS.find((preset) => preset.code === 'FINANCE_OFFICER')!;
    expect(finance.grants.find((grant) => grant.module === 'Exchange Rates')?.canCreate).toBe(true);
  });
});

describe('a role that administers everything', () => {
  const admin = ROLE_LADDER.find((preset) => preset.code === 'SYSTEM_ADMIN')!;

  it('is identified by its tier, not by its name', () => {
    expect(administersEverything(admin)).toBe(true);
    expect(administersEverything({ rank: ROLE_RANK_MANAGER })).toBe(false);
    expect(administersEverything({ rank: ROLE_RANK_MEMBER })).toBe(false);
  });

  it('holds no grants at all, so there is no matrix to contradict', () => {
    // The server satisfies every module check by rank before reading a permission row. A single
    // grant here would render as a checkbox that revokes nothing when cleared.
    expect(admin.grants).toEqual([]);
  });

  it('reports no drift, because it has no matrix to drift from', () => {
    const everythingGranted = new Map([
      ['leads', { canView: true, canCreate: true, canEdit: true, canDelete: true }],
    ]);
    expect(driftFromPreset(admin, everythingGranted)).toEqual([]);
  });
});

describe('drift from a preset', () => {
  const rep = ROLE_LADDER.find((preset) => preset.code === 'SALES_REP')!;

  /** The live permission rows for a role that matches the preset exactly. */
  const matching = () =>
    new Map(rep.grants.map((grant) => {
      const { module, ...flags } = grant;
      return [module.trim().toLowerCase(), flags];
    }));

  it('reports nothing when the role matches the preset', () => {
    expect(driftFromPreset(rep, matching())).toEqual([]);
    expect(describeDrift(rep, [])).toBe('Matches the standard Sales Representative setup exactly.');
  });

  it('reports a grant that was REMOVED from the preset', () => {
    const rows = matching();
    rows.set('leads', { canView: true, canCreate: false, canEdit: false, canDelete: false });

    const drift = driftFromPreset(rep, rows);
    expect(drift.map((item) => item.module)).toEqual(['Leads']);
  });

  it('reports a grant ADDED on a module the preset leaves empty', () => {
    // The direction that quietly WIDENS a role. Reporting only removals would hide exactly the
    // change an administrator most needs to see.
    // "Customer Payments" rather than "Accounts Receivable": the representative preset now READS
    // receivables (the rail gates Receivables on it), so a module it genuinely leaves empty is the
    // honest example.
    const rows = matching();
    rows.set('customer payments', { canView: true, canCreate: true, canEdit: true, canDelete: true });

    const drift = driftFromPreset(rep, rows);
    expect(drift.map((item) => item.module)).toEqual(['Customer Payments']);
  });

  it('ignores modules that grant nothing', () => {
    // "UOM" and the other eight ungoverned rows still arrive from GET /api/Module. Counting them
    // would report drift that a reset could never clear.
    const rows = matching();
    rows.set('uom', { canView: true, canCreate: true, canEdit: true, canDelete: true });

    expect(driftFromPreset(rep, rows)).toEqual([]);
  });

  it('describes drift in modules, never in checkbox counts', () => {
    const rows = matching();
    rows.set('leads', { canView: false, canCreate: false, canEdit: false, canDelete: false });
    rows.set('orders', { canView: false, canCreate: false, canEdit: false, canDelete: false });

    const message = describeDrift(rep, driftFromPreset(rep, rows));
    expect(message).toContain('differs from the standard Sales Representative setup in 2 places');
    expect(message).toContain('Leads');
    expect(message).not.toMatch(/checkbox/i);
  });

  it('names the first few and counts the rest rather than listing everything', () => {
    const drift = ['A', 'B', 'C', 'D', 'E'].map((module) => ({
      module,
      expected: { canView: true, canCreate: false, canEdit: false, canDelete: false },
      actual: { canView: false, canCreate: false, canEdit: false, canDelete: false },
    }));
    expect(describeDrift(rep, drift)).toBe(
      'Customised: differs from the standard Sales Representative setup in 5 places — A, B, C, and 2 more.',
    );
  });
});

describe('matching a live role to its preset', () => {
  it('matches on the name a Setup_Master role row carries', () => {
    expect(presetForRoleName('Sales Representative')?.code).toBe('SALES_REP');
    expect(presetForRoleName('  sales manager  ')?.code).toBe('SALES_MANAGER');
    expect(presetForRoleName('Something Bespoke')).toBeUndefined();
    expect(presetForRoleName('')).toBeUndefined();
    expect(presetForRoleName(null)).toBeUndefined();
  });

  it('grants nothing for a module the preset does not name', () => {
    const rep = ROLE_LADDER.find((preset) => preset.code === 'SALES_REP')!;
    expect(presetGrantFor(rep, 'Period Close')).toEqual({
      canView: false, canCreate: false, canEdit: false, canDelete: false,
    });
  });
});
