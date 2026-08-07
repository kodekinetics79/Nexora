import { useMemo } from 'react';
import { usePlatformAuth } from './usePlatformAuth';
import { permissionsForRole, type PlatformPermissions } from './permissions';

/**
 * The signed-in operator's authority, derived from the platform session's `platformRole`
 * claim. Every privileged control in the console reads this rather than assuming Owner.
 */
export const usePlatformPermissions = (): PlatformPermissions => {
  const { platformUser } = usePlatformAuth();
  return useMemo(() => permissionsForRole(platformUser?.role), [platformUser?.role]);
};
