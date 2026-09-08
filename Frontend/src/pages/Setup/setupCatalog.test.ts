import { describe, expect, it } from 'vitest';
import {
  SETUP_ENTRIES,
  SETUP_GROUPS,
  entryForLocation,
  entryMatches,
  groupOfEntry,
  normaliseSetupType,
  setupEntryLabel,
} from './setupCatalog';
import { SETUP_ROUTES, SETUP_ADOPTED_ROUTES } from './setupRoutes';
import { ENFORCED_MODULE_NAMES } from './permissionModules';

/**
 * The catalogue is the register of what Setup governs, and these tests are what make it binding.
 *
 * Setup grew to fourteen sidebar rows by accretion — each addition reasonable on its own, none
 * checked against the rest, and two of them (roles, price structures) already editable from a
 * second screen. A register nobody verifies drifts back into that within a release, so: every
 * `/setup/*` route in App.tsx is listed here exactly once, every listed path is a real route, and
 * no path or key appears twice.
 */

/**
 * Every path App.tsx mounts inside Setup's shell: the `/setup/*` subtree plus the screens adopted
 * at their own addresses (`/security/*`, `/admin/platform/*`).
 */
const routedSetupPaths = (): string[] => [
  ...SETUP_ROUTES.map((route) => `/setup/${route.path}`),
  ...SETUP_ADOPTED_ROUTES.map((route) => route.path),
];

/** The route backing a catalogue entry, from whichever of the two tables mounts it. */
const routeFor = (entryPath: string) => {
  const path = routeOf(entryPath);
  return (
    SETUP_ROUTES.find((route) => `/setup/${route.path}` === path) ??
    SETUP_ADOPTED_ROUTES.find((route) => route.path === path)
  );
};

/** A catalogue path stripped of any query — `/setup/master?type=role` routes to `/setup/master`. */
const routeOf = (path: string) => path.split('?')[0];

describe('the setup catalogue', () => {
  it('lists every routed setup screen', () => {
    const listed = new Set(SETUP_ENTRIES.map((entry) => routeOf(entry.path)));
    const missing = routedSetupPaths().filter((path) => !listed.has(path));
    expect(
      missing,
      'a screen routed under /setup but absent from the catalogue is unreachable: the hub, the ' +
        'jump field and the sidebar all read the catalogue and nothing else',
    ).toEqual([]);
  });

  it('lists nothing that is not routed', () => {
    const routed = new Set(routedSetupPaths());
    const dangling = SETUP_ENTRIES.filter((entry) => !routed.has(routeOf(entry.path)));
    expect(dangling.map((entry) => entry.path), 'catalogue entries must point at a real route').toEqual([]);
  });

  it('gives each screen exactly one entry', () => {
    // Two entries may share a route only when they scope it differently (Roles is
    // /setup/master?type=role); the same *path* twice is the duplication this guards against.
    const paths = SETUP_ENTRIES.map((entry) => entry.path);
    expect(new Set(paths).size, `duplicate paths in the catalogue: ${paths.join(', ')}`).toBe(paths.length);

    const keys = SETUP_ENTRIES.map((entry) => entry.key);
    expect(new Set(keys).size, `duplicate keys in the catalogue: ${keys.join(', ')}`).toBe(keys.length);
  });

  it('gates each entry on the module its route actually guards', () => {
    // A card gated on a different module than its route is worse than no card: it either offers a
    // screen that answers Access Denied, or hides one the user is entitled to open.
    //
    // The one permitted divergence is a DEEP-LINKED entry — one whose path carries a query that
    // narrows a shared screen. Roles is /setup/master?type=role: the route serves ordinary
    // picklists too, so it is gated on the looser module, while the Roles card is gated on
    // "Roles & Permissions" because that is what the server demands before a role row may be
    // written (SetupMasterController.RoleAdministrationDenialAsync). Offering that card to
    // someone who cannot act on it is the failure this rule prevents; a tighter card is the
    // remedy, not a violation.
    for (const entry of SETUP_ENTRIES) {
      const route = routeFor(entry.path);
      expect(route, `${entry.key} should have a route`).toBeDefined();
      if (entry.path.includes('?')) continue;
      expect(
        entry.moduleName,
        `${entry.key} is listed under "${entry.moduleName}" but its route guards "${route!.moduleName}"`,
      ).toBe(route!.moduleName);
    }
  });

  it('never gates a screen on a module the permission matrix cannot grant', () => {
    // The defect this closes: seven Setup entries — Roles among them — were gated on "UOM", which
    // is not a permission module at all. It appears in no [RequireModulePermission] anywhere in
    // the backend and is absent from ModuleCatalog, so hasPermission('UOM') could only ever be
    // true for a super admin. Every one of those screens answered "Access Denied — you do not have
    // permission to access the UOM module" to a genuine administrator, naming a grant no
    // administrator could ever tick.
    const ungrantable = SETUP_ENTRIES
      .filter((entry) => entry.moduleName && !ENFORCED_MODULE_NAMES.has(entry.moduleName))
      .map((entry) => `${entry.key} -> ${entry.moduleName}`);
    expect(
      ungrantable,
      'these entries are gated on modules that grant nothing and can never be granted',
    ).toEqual([]);
  });

  it('leaves an adopted screen at the address it already had', () => {
    // Absorbing "User & Access" and "Platform Governance" into Setup was a navigation change, not
    // a URL change: bookmarks, the a11y spec's title assertions and the e2e suite all point at
    // these paths. A rename here would spend those links to buy nothing the reader can see.
    expect(SETUP_ADOPTED_ROUTES.map((route) => route.path).sort()).toEqual([
      '/admin/platform/ai-trust',
      '/admin/platform/archive',
      '/admin/platform/integrations',
      '/admin/platform/lifecycle',
      '/admin/platform/quality',
      '/admin/platform/releases',
      '/admin/platform/retention',
      '/admin/platform/taxonomy',
      '/security/roles',
      '/security/users',
    ]);
    for (const route of SETUP_ADOPTED_ROUTES) {
      expect(route.path.startsWith('/setup'), `${route.path} is not an adopted address`).toBe(false);
    }
  });

  it('describes every entry in a sentence a non-engineer can act on', () => {
    for (const entry of SETUP_ENTRIES) {
      expect(entry.label.length, `${entry.key} needs a label`).toBeGreaterThan(0);
      expect(entry.description.length, `${entry.key} needs a description`).toBeGreaterThan(20);
      expect(entry.icon, `${entry.key} needs an icon`).toBeTruthy();
    }
  });

  it('places every entry in exactly one group', () => {
    for (const entry of SETUP_ENTRIES) {
      const groups = SETUP_GROUPS.filter((group) => group.entries.some((item) => item.key === entry.key));
      expect(groups, `${entry.key} should belong to one group`).toHaveLength(1);
      expect(groupOfEntry(entry.key)?.key).toBe(groups[0].key);
    }
  });
});

describe('finding the current entry', () => {
  it('matches a plain setup path', () => {
    expect(entryForLocation('/setup/currency')?.key).toBe('currency');
  });

  it('tells a scoped screen apart from the screen it scopes', () => {
    // Both live at /setup/master. The breadcrumb has to say "Roles" for one and
    // "Lists & Picklists" for the other, or the two read as the same place.
    expect(entryForLocation('/setup/master', '?type=role')?.key).toBe('roles');
    expect(entryForLocation('/setup/master')?.key).toBe('master');
  });

  it('ignores the casing the URL happens to use', () => {
    expect(entryForLocation('/setup/master', '?type=Role')?.key).toBe('roles');
  });

  it('finds an adopted screen by its own address', () => {
    // The breadcrumb on /security/users has to read "Setup Master › People & Access › Users".
    expect(entryForLocation('/security/users')?.key).toBe('users');
    expect(entryForLocation('/admin/platform/retention')?.key).toBe('platform-retention');
  });

  it('returns nothing for a path Setup does not govern', () => {
    expect(entryForLocation('/procurement/leads/all')).toBeUndefined();
  });
});

describe('searching the catalogue', () => {
  const entry = SETUP_ENTRIES.find((item) => item.key === 'commercial-policy')!;

  it('matches on words the operator would use, not only our label', () => {
    expect(entryMatches(entry, 'vat')).toBe(true);
    expect(entryMatches(entry, 'zatca')).toBe(true);
    expect(entryMatches(entry, 'tax rates')).toBe(true);
  });

  it('requires every term to match', () => {
    expect(entryMatches(entry, 'tax mailbox')).toBe(false);
  });

  it('matches everything on an empty query', () => {
    expect(entryMatches(entry, '   ')).toBe(true);
  });
});

describe('labels', () => {
  const entry = (key: string) => SETUP_ENTRIES.find((item) => item.key === key)!;

  it('reads the localised name where the product already has one', () => {
    // `t('currency')` also heads a column in the supplier grid and labels a field in the product
    // dialog. The hub shares it deliberately: one name per screen, and every locale keeps the
    // translation it already had.
    const t = (key: string, fallback: string) => (key === 'currency' ? 'العملة' : fallback);
    expect(setupEntryLabel(entry('currency'), t)).toBe('العملة');
  });

  it('falls back to the English label when a locale has no string for the key', () => {
    const t = (_key: string, fallback: string) => fallback;
    expect(setupEntryLabel(entry('currency'), t)).toBe('Currencies');
    expect(setupEntryLabel(entry('sla'), t)).toBe('Deadlines & Alerts');
  });

  it('finds a terse label by the name a newcomer would type', () => {
    // The labels follow the product's own vocabulary ("UOM", "Currency"), which is right on a grid
    // heading and terse on a hub card. Search carries the spelled-out names so the terse label
    // never costs anyone the screen.
    expect(entryMatches(entry('uom'), 'units of measure')).toBe(true);
    expect(entryMatches(entry('uom'), 'unit of measure')).toBe(true);
    expect(entryMatches(entry('currency'), 'currencies')).toBe(true);
    expect(entryMatches(entry('warehouse'), 'warehouses')).toBe(true);
    expect(entryMatches(entry('business-unit'), 'business units')).toBe(true);
  });

  it('still does not match a screen the words do not describe', () => {
    expect(entryMatches(entry('uom'), 'currencies')).toBe(false);
    expect(entryMatches(entry('mailboxes'), 'units of measure')).toBe(false);
  });
});

describe('normalising a stored setup type', () => {
  // Production holds 'Role', ' Role ' and 'role' — see Backend Authorization/SetupTypes.cs.
  it('folds case and stray whitespace the way the backend does', () => {
    expect(normaliseSetupType(' Role ')).toBe('role');
    expect(normaliseSetupType('PRICE_STRUCTURE')).toBe('price_structure');
    expect(normaliseSetupType(null)).toBe('');
  });
});
