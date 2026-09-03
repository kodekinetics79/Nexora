import { ROLE_RANK_ADMIN, ROLE_RANK_MANAGER, ROLE_RANK_MEMBER, type RoleRank } from './roleRankTiers';
import { ENFORCED_MODULES } from './permissionModules';

/**
 * The starter roles a customer picks from, mirrored from
 * `Backend/ERP_RFQ_Automation/Platform/Services/TenantBaselineCatalog.cs`.
 *
 * <b>The problem these solve.</b> Configuring a role meant meeting 212 checkboxes across 44
 * modules with no indication of which combination is a working sales rep. That is a screen for
 * someone who already knows the answer. Salespeople are the users here, and the common case —
 * "give this person the standard sales rep setup" — should be one choice, not 23 ticks spread
 * over 14 modules that nothing on screen identifies.
 *
 * <b>Two dimensions, not one ladder.</b> `ROLE_LADDER` is how much AUTHORITY a role carries and it
 * has exactly three rungs. `DESK_PRESETS` is which JOB a person does, and both entries sit at the
 * bottom rung — a procurement officer is not "more senior" than a sales rep, they do different
 * work. Collapsing the two into one list is what produces ladders with five rungs nobody can
 * order, so they are kept apart and labelled apart.
 *
 * <b>Owner is not offered.</b> The tenant owner is provisioned when the organization is created.
 * Offering it here is how a live tenant ends up with all six of its users holding the top of the
 * tenant plane and nothing narrower available to give anyone.
 *
 * <b>Drift.</b> `rolePresets.test.ts` parses TenantBaselineCatalog.cs and fails if this file and
 * it disagree, so the .cs remains the single authority for what a preset grants.
 */

export interface PresetGrant {
  module: string;
  canView: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
}

export interface RolePreset {
  /** Internal identity, matching the backend catalogue. NEVER rendered — it is a code. */
  code: string;
  /** What the customer sees. */
  name: string;
  rank: RoleRank;
  /** One or two sentences of plain commercial language for the person choosing. */
  summary: string;
  grants: readonly PresetGrant[];
}

/**
 * True when this role's authority comes from its TIER rather than from its grant list.
 *
 * At Administrator and above the server satisfies every module check by rank before it reads a
 * single permission row, so a matrix for such a role is decorative: ticking a box grants nothing
 * that was not already held, and clearing one revokes nothing. The screen must therefore render a
 * sentence instead of checkboxes — not a curated set of ticks, which would state the opposite of
 * what is enforced.
 */
export const administersEverything = (preset: { rank: number }): boolean =>
  preset.rank >= ROLE_RANK_ADMIN;

/**
 * The three rungs of authority, most authority first — the whole of the default path.
 */
export const ROLE_LADDER: readonly RolePreset[] = [
  {
    code: "SYSTEM_ADMIN",
    name: "System Administrator",
    rank: ROLE_RANK_ADMIN,
    summary:
      "Administers everything in this organization. Individual permission lines do not apply to this role \u2014 its authority comes from its level, not from a list.",
    grants: [],
  },
  {
    code: "SALES_MANAGER",
    name: "Sales Manager",
    rank: ROLE_RANK_MANAGER,
    summary:
      "Runs the sales desk: owns the pipeline end to end, sets quote branding and terms, and sees what customers owe without being able to move money.",
    grants: [
      { module: "Dashboard", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Leads", canView: true, canCreate: true, canEdit: true, canDelete: true },
      { module: "RFQ Management", canView: true, canCreate: true, canEdit: true, canDelete: true },
      { module: "Quotations", canView: true, canCreate: true, canEdit: true, canDelete: true },
      { module: "Orders", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Shipments", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Customers", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Customer Awards", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Suppliers", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Supplier History", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Products", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Product Categories", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Quote Configuration", canView: true, canCreate: false, canEdit: true, canDelete: false },
      { module: "Users", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Accounts Receivable", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Customer Statements", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Collection Controls", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Currencies", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Exchange Rates", canView: true, canCreate: false, canEdit: false, canDelete: false },
    ],
  },
  {
    code: "SALES_REP",
    name: "Sales Representative",
    rank: ROLE_RANK_MEMBER,
    summary:
      "Works enquiries through to quotes and orders for their own customers. No finance, no supplier negotiation, no administration.",
    grants: [
      { module: "Dashboard", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Leads", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "RFQ Management", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Quotations", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Orders", canView: true, canCreate: true, canEdit: false, canDelete: false },
      // The rail shows Fulfilment only to a role that can view "Shipments" and Receivables only
      // to one that can view "Accounts Receivable" (navCatalog.tsx). Both are reads; nothing here
      // lets the desk create a shipment or move money. Mirrors TenantBaselineCatalog.StarterRoles.
      { module: "Shipments", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Accounts Receivable", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Customers", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Customer Awards", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Suppliers", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Supplier History", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Products", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Product Categories", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Currencies", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Exchange Rates", canView: true, canCreate: false, canEdit: false, canDelete: false },
    ],
  },
];

/**
 * The other two desks of a trading business. A SECOND DIMENSION, not a fourth rung: both sit at
 * the same authority as a sales representative and differ only in which work they can reach.
 *
 * Finance's separation of raising from approving is load-bearing and must survive any edit here.
 * A user who could both create an exchange rate and approve it could move money on their own
 * signature, and an approved rate re-bases quote totals, the below-floor pricing guard and the AI
 * spend cap. That is why this desk holds "Exchange Rates" without "Exchange Rate Approval".
 */
export const DESK_PRESETS: readonly RolePreset[] = [
  {
    code: "PROCUREMENT_OFFICER",
    name: "Procurement Officer",
    rank: ROLE_RANK_MEMBER,
    summary:
      "Sources the supply side: suppliers, supplier quotes and negotiation, purchase orders and goods receipts, and the product catalogue they populate.",
    grants: [
      { module: "Dashboard", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "RFQ Management", canView: true, canCreate: false, canEdit: true, canDelete: false },
      { module: "Suppliers", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Supplier History", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Supplier Negotiation", canView: true, canCreate: false, canEdit: true, canDelete: false },
      { module: "Products", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Product Categories", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Orders", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Currencies", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Exchange Rates", canView: true, canCreate: false, canEdit: false, canDelete: false },
    ],
  },
  {
    code: "FINANCE_OFFICER",
    name: "Finance Officer",
    rank: ROLE_RANK_MEMBER,
    summary:
      "Runs receivables and collections and prepares bank reconciliation. Deliberately holds no approval or ledger-control authority.",
    grants: [
      { module: "Dashboard", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Customers", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Orders", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Accounts Receivable", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Customer Payments", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Customer Statements", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Dunning Cases", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Dunning Notices", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Dunning Policies", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Customer Refunds", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Receivable Adjustments", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Receivable Write-offs", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Collection Controls", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Bank Accounts", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Bank Statement Import", canView: true, canCreate: true, canEdit: false, canDelete: false },
      { module: "Bank Reconciliation", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Bank Adjustments", canView: true, canCreate: true, canEdit: false, canDelete: false },
      { module: "General Ledger", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Accounting Periods", canView: true, canCreate: false, canEdit: false, canDelete: false },
      { module: "Currencies", canView: true, canCreate: true, canEdit: true, canDelete: false },
      { module: "Exchange Rates", canView: true, canCreate: true, canEdit: false, canDelete: false },
    ],
  },
];

export const ALL_PRESETS: readonly RolePreset[] = [...ROLE_LADDER, ...DESK_PRESETS];

export const presetByCode = (code: string | null | undefined): RolePreset | undefined =>
  ALL_PRESETS.find((preset) => preset.code === code);

/** Matched on NAME, because that is what a Setup_Master role row carries. */
export const presetForRoleName = (roleName: string | null | undefined): RolePreset | undefined => {
  const needle = (roleName ?? '').trim().toLowerCase();
  if (!needle) return undefined;
  return ALL_PRESETS.find((preset) => preset.name.trim().toLowerCase() === needle);
};

/** The four grant flags, in matrix-column order. */
export const GRANT_FLAGS = ['canView', 'canCreate', 'canEdit', 'canDelete'] as const;
export type GrantFlag = (typeof GRANT_FLAGS)[number];

const NOTHING: Omit<PresetGrant, 'module'> = {
  canView: false, canCreate: false, canEdit: false, canDelete: false,
};

/** What a preset grants on one module — all-false when the preset does not name it. */
export const presetGrantFor = (
  preset: RolePreset,
  moduleName: string,
): Omit<PresetGrant, 'module'> => {
  const match = preset.grants.find(
    (grant) => grant.module.trim().toLowerCase() === moduleName.trim().toLowerCase(),
  );
  if (!match) return NOTHING;
  const { module: _module, ...flags } = match;
  return flags;
};

export interface ModuleDrift {
  module: string;
  expected: Omit<PresetGrant, 'module'>;
  actual: Omit<PresetGrant, 'module'>;
}

/**
 * Where a role's live permissions differ from the preset it was built from.
 *
 * Compared across every ENFORCED module rather than only the ones the preset names, so a grant
 * ADDED to a module the preset leaves empty counts as drift too — that is the direction that
 * quietly widens a role, and reporting only removals would hide it.
 *
 * Modules that grant nothing are not compared at all: they cannot be the reason a role behaves
 * differently, so counting them would report drift a reset could never clear.
 */
export const driftFromPreset = (
  preset: RolePreset,
  actualByModule: ReadonlyMap<string, Omit<PresetGrant, 'module'>>,
): ModuleDrift[] => {
  // A role whose authority comes from its tier has no meaningful matrix, so it cannot drift from
  // one. Reporting differences here would invite a "reset" that changes nothing observable.
  if (administersEverything(preset)) return [];

  const drift: ModuleDrift[] = [];
  for (const { name } of ENFORCED_MODULES) {
    const expected = presetGrantFor(preset, name);
    const actual = actualByModule.get(name.trim().toLowerCase()) ?? NOTHING;
    if (GRANT_FLAGS.some((flag) => expected[flag] !== actual[flag])) {
      drift.push({ module: name, expected, actual });
    }
  }
  return drift;
};

/**
 * Plain-English drift, for the line above the Advanced drawer.
 *
 * Never names a count of checkboxes — a reader does not think in checkboxes, they think in
 * "which parts of the product is this different about".
 */
export const describeDrift = (preset: RolePreset, drift: readonly ModuleDrift[]): string => {
  if (drift.length === 0) return `Matches the standard ${preset.name} setup exactly.`;
  if (drift.length === 1) {
    return `Customised: differs from the standard ${preset.name} setup in 1 place — ${drift[0].module}.`;
  }
  const named = drift.slice(0, 3).map((item) => item.module).join(', ');
  const rest = drift.length > 3 ? `, and ${drift.length - 3} more` : '';
  return `Customised: differs from the standard ${preset.name} setup in ${drift.length} places — ${named}${rest}.`;
};
