import type { ReactNode } from 'react';
import MainLayout from './MainLayout';
import { RequireAuth } from '../common/PermissionGuard';
import RouteErrorBoundary from '../common/RouteErrorBoundary';

/**
 * The authenticated tenant shell every tenant route renders inside.
 *
 * One element type at the root of every route element, so React Router reuses the same shell when
 * the address changes. `/inbox` and `/advanced` used to start with `<RequireAuth>` while every other
 * screen started with `<MainLayout>`, so moving between the landing Inbox and any other screen threw
 * the whole frame away and drew it again — sidebar state reset, top bar remounted — which looks
 * exactly like the page refreshing.
 *
 * The auth gate comes first, so a signed-out visitor never receives the shell. The route boundary
 * sits inside it, so one screen's failure never removes the navigation.
 */
export default function TenantShell({ children }: { children: ReactNode }) {
  return (
    <RequireAuth>
      <MainLayout>
        <RouteErrorBoundary>{children}</RouteErrorBoundary>
      </MainLayout>
    </RequireAuth>
  );
}
