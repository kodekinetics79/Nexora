import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import PlatformGuard from './components/PlatformGuard';
import PlatformLayout from './components/PlatformLayout';

// Code-split every platform page, mirroring the tenant app's lazy-route pattern.
const OverviewPage = lazy(() => import('./pages/OverviewPage'));
const TenantsPage = lazy(() => import('./pages/TenantsPage'));
const TenantDetailPage = lazy(() => import('./pages/TenantDetailPage'));
const PipelinePage = lazy(() => import('./pages/PipelinePage'));
const PlansFlagsPage = lazy(() => import('./pages/PlansFlagsPage'));
const AuditLogPage = lazy(() => import('./pages/AuditLogPage'));

const PlatformLoader = () => (
  <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: '60vh', width: '100%' }}>
    <CircularProgress />
  </Box>
);

/**
 * The self-contained `/platform/*` route tree. Mounted once in App.tsx via
 * `<Route path="/platform/*" element={<PlatformRoutes />} />`.
 *
 * PlatformGuard gates the whole tree on platform scope; PlatformLayout provides
 * the control-plane chrome (sidebar + topbar) and renders each page via Outlet.
 */
export default function PlatformRoutes() {
  return (
    <PlatformGuard>
      <Suspense fallback={<PlatformLoader />}>
        <Routes>
          <Route element={<PlatformLayout />}>
            <Route index element={<Navigate to="overview" replace />} />
            <Route path="overview" element={<OverviewPage />} />
            <Route path="tenants" element={<TenantsPage />} />
            <Route path="tenants/:id" element={<TenantDetailPage />} />
            <Route path="pipeline" element={<PipelinePage />} />
            <Route path="plans" element={<PlansFlagsPage />} />
            <Route path="audit" element={<AuditLogPage />} />
            <Route path="*" element={<Navigate to="overview" replace />} />
          </Route>
        </Routes>
      </Suspense>
    </PlatformGuard>
  );
}
