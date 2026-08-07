import { describe, expect, it } from 'vitest';
import { normalizePlatformRole, permissionsForRole } from './permissions';
import { PLATFORM_OPERATOR_ROLES } from '../types';

describe('platform operator role normalisation', () => {
  it('accepts every role the platform issues, in any casing', () => {
    PLATFORM_OPERATOR_ROLES.forEach((role) => {
      expect(normalizePlatformRole(role)).toBe(role);
      expect(normalizePlatformRole(role.toUpperCase())).toBe(role);
      expect(normalizePlatformRole(`  ${role.toLowerCase()}  `)).toBe(role);
    });
  });

  it('refuses to guess at anything it does not recognise', () => {
    // The legacy display fallback the login screen used to invent. It is not a role,
    // and treating it as Owner would show controls the server refuses.
    expect(normalizePlatformRole('Platform Owner')).toBeNull();
    expect(normalizePlatformRole('')).toBeNull();
    expect(normalizePlatformRole(undefined)).toBeNull();
    expect(normalizePlatformRole(null)).toBeNull();
  });
});

describe('platform permissions mirror the server policies', () => {
  it('gives Owner everything', () => {
    const owner = permissionsForRole('Owner');
    expect(owner).toMatchObject({
      role: 'Owner',
      isOwner: true,
      canAdministerTenants: true,
      canAdministerBilling: true,
      canImpersonate: true,
      roleUnknown: false,
    });
  });

  it('lets a SupportAdmin operate tenants but never touch money', () => {
    const support = permissionsForRole('SupportAdmin');
    expect(support.canAdministerTenants).toBe(true);
    expect(support.canImpersonate).toBe(true);
    // Separation of duties: suspending a customer is an operational act; repricing them
    // is not, and Platform.Billing is Owner|BillingAdmin.
    expect(support.canAdministerBilling).toBe(false);
    // Nothing irreversible. A support engineer takes a customer off the product; only an
    // Owner destroys them.
    expect(support.isOwner).toBe(false);
  });

  it('lets a BillingAdmin decide what a customer pays but not operate them', () => {
    const billing = permissionsForRole('BillingAdmin');
    expect(billing.canAdministerBilling).toBe(true);
    expect(billing.canAdministerTenants).toBe(false);
    expect(billing.canImpersonate).toBe(false);
    expect(billing.isOwner).toBe(false);
  });

  it('gives ReadOnlyOps no mutation authority at all', () => {
    const readOnly = permissionsForRole('ReadOnlyOps');
    expect(readOnly.isOwner).toBe(false);
    expect(readOnly.canAdministerTenants).toBe(false);
    expect(readOnly.canAdministerBilling).toBe(false);
    expect(readOnly.canImpersonate).toBe(false);
    expect(readOnly.roleUnknown).toBe(false);
  });

  it('treats an unrecognised session as the least privilege there is, and says so', () => {
    const unknown = permissionsForRole('Wizard');
    expect(unknown.role).toBeNull();
    expect(unknown.roleUnknown).toBe(true);
    expect(unknown.isOwner).toBe(false);
    expect(unknown.canAdministerTenants).toBe(false);
    expect(unknown.canAdministerBilling).toBe(false);
    expect(unknown.canImpersonate).toBe(false);
  });
});
