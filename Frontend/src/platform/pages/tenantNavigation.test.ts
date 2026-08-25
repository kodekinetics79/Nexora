import { describe, expect, it } from 'vitest';
import { TENANT_DETAIL_TABS, tenantOffboardingPath } from './tenantNavigation';

describe('tenant offboarding navigation', () => {
  it('keeps the tenant-list action as a direct, bookmark-compatible link', () => {
    expect(tenantOffboardingPath('tenant 9')).toBe('/platform/tenants/tenant%209?tab=lifecycle');
  });

  it('places clearly named offboarding controls beside overview and activation', () => {
    expect(TENANT_DETAIL_TABS.slice(0, 3)).toEqual([
      { key: 'overview', label: 'Overview' },
      { key: 'activation', label: 'Activation' },
      { key: 'lifecycle', label: 'Offboarding & deletion' },
    ]);
  });
});
