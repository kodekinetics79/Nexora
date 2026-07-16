import type { ReactNode } from 'react';
import { usePlatformAuth } from '../auth/usePlatformAuth';
import PlatformLoginScreen from './PlatformLoginScreen';

/**
 * Gates the entire `/platform` tree on a dedicated platform session.
 *
 * The platform console has its OWN auth context (a `scope=platform` JWT from
 * `/api/platform/auth/login`), independent of the tenant RBAC session. When no
 * platform session is present we render the platform login screen IN PLACE —
 * no hard redirect — so signing in (or a 401-driven session clear) simply
 * re-renders this component without a full-page reload.
 */
export default function PlatformGuard({ children }: { children: ReactNode }) {
  const { isPlatformAuthed } = usePlatformAuth();

  if (!isPlatformAuthed) {
    return <PlatformLoginScreen />;
  }

  return <>{children}</>;
}
