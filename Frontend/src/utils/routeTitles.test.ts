import { describe, expect, it } from 'vitest';
import { resolveRouteTitle } from './routeTitles';

describe('platform route titles', () => {
  it.each([
    ['/platform/users', 'Platform Users'],
    ['/platform/billing', 'Platform Billing'],
    ['/platform/support', 'Platform Support'],
    ['/platform/email', 'Platform Email'],
    ['/platform/security', 'Platform Security'],
    ['/platform/security/authentication', 'Platform Authentication'],
  ])('maps %s to a unique title', (path, title) => {
    expect(resolveRouteTitle(path)).toBe(title);
  });
});
