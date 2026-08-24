import { describe, expect, it } from 'vitest';
import { ENFORCED_MODULES, ENFORCED_MODULE_NAMES, isEnforcedModule } from './permissionModules';

/**
 * Behaviour of the enforced-module list.
 *
 * The list itself is a mirror of `Backend/ERP_RFQ_Automation/Authorization/ModuleCatalog.cs`, and
 * that mirror is verified on the BACKEND side, in `FrontendCatalogueMirrorTests` — the authority
 * checks its own copy, and Vite refuses to read files outside the frontend project anyway. What is
 * asserted here is what the list is USED for.
 */
describe('the enforced module list', () => {
  it('is not empty', () => {
    expect(ENFORCED_MODULES.length).toBeGreaterThan(0);
  });

  it('never lists a module that grants nothing', () => {
    // The nine live Module rows with no [RequireModulePermission] anywhere in the backend.
    // ModuleCatalogReconciler is insert-only, so these rows are permanent and will keep arriving
    // from GET /api/Module for the life of the product. They must never become a checkbox.
    const ungoverned = [
      'Bulk Uploaders', 'Contacts', 'Currency', 'File Management', 'Locations',
      'Teams', 'UOM', 'User Groups', 'Warehouse',
    ];
    for (const name of ungoverned) {
      expect(ENFORCED_MODULE_NAMES.has(name), `${name} grants nothing and must not be listed`).toBe(false);
      expect(isEnforcedModule(name), `${name} grants nothing and must not be enforced`).toBe(false);
    }
  });

  it('matches the way the server and AuthContext compare module names', () => {
    // Live Module rows are inconsistently cased and padded; both the server and
    // AuthContext.hasPermission trim and lower-case before comparing, so this must too or a real
    // grant would be filtered off the matrix by a stray space.
    expect(isEnforcedModule('  quotations ')).toBe(true);
    expect(isEnforcedModule('ROLES & PERMISSIONS')).toBe(true);
    expect(isEnforcedModule('')).toBe(false);
    expect(isEnforcedModule(null)).toBe(false);
    expect(isEnforcedModule(undefined)).toBe(false);
  });
});
