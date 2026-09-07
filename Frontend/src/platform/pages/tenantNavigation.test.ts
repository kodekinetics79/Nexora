import { describe, expect, it } from 'vitest';
import { TENANT_DETAIL_TABS, tenantOffboardingPath } from './tenantNavigation';

describe('tenant offboarding navigation', () => {
  it('keeps the tenant-list action as a direct, bookmark-compatible link', () => {
    expect(tenantOffboardingPath('tenant 9')).toBe('/platform/tenants/tenant%209?tab=lifecycle');
  });

  /**
   * The property is unchanged and is the one that failed twice in production: activation must be
   * the first thing on this screen, not an entry in a scroller. Overview and Profile & access
   * have moved to the customer page, so activation is now first rather than second — stronger
   * than what this test originally pinned, not weaker.
   */
  it('leads with activation and names offboarding plainly, right beside it', () => {
    expect(TENANT_DETAIL_TABS.slice(0, 2)).toEqual([
      { key: 'activation', label: 'Activation' },
      { key: 'lifecycle', label: 'Offboarding & deletion' },
    ]);
  });

  it('no longer offers a second way to do what the customer screen now does', () => {
    const keys = TENANT_DETAIL_TABS.map((t) => t.key);
    expect(keys).not.toContain('overview');
    expect(keys).not.toContain('profile-access');
  });
});
