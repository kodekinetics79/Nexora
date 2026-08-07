import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import PlatformGuard from './components/PlatformGuard';
import PlatformLayout from './components/PlatformLayout';

// Code-split every platform page, mirroring the tenant app's lazy-route pattern.
/** Absolute landing path for the control plane. See the redirect note below. */
const PLATFORM_HOME = '/platform/overview';

const OverviewPage = lazy(() => import('./pages/OverviewPage'));
const TenantsPage = lazy(() => import('./pages/TenantsPage'));
const TenantDetailPage = lazy(() => import('./pages/TenantDetailPage'));
const PipelinePage = lazy(() => import('./pages/PipelinePage'));
const PlansFlagsPage = lazy(() => import('./pages/PlansFlagsPage'));
const AuditLogPage = lazy(() => import('./pages/AuditLogPage'));
const PlatformUsersPage = lazy(() => import('./pages/PlatformUsersPage'));
const BillingPage = lazy(() => import('./pages/BillingPage'));
const SupportPage = lazy(() => import('./pages/SupportPage'));
const SecurityPage = lazy(() => import('./pages/SecurityPage'));
const EmailSettingsPage = lazy(() => import('./pages/EmailSettingsPage'));

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
            {/*
              These redirects MUST be absolute. A relative `to="overview"` resolves against
              the current URL, so any unmatched platform path (e.g. someone guessing
              /platform/login) redirects to /platform/login/overview, which is still
              unmatched, which redirects again — appending "overview" until the router
              throws and the error boundary swallows the console entirely.
            */}
            <Route index element={<Navigate to={PLATFORM_HOME} replace />} />
            <Route path="overview" element={<OverviewPage />} />
            <Route path="tenants" element={<TenantsPage />} />
            <Route path="tenants/:id" element={<TenantDetailPage />} />
            <Route path="pipeline" element={<PipelinePage />} />
            <Route path="plans" element={<PlansFlagsPage />} />
            <Route path="users" element={<PlatformUsersPage />} />
            <Route path="billing" element={<BillingPage />} />
            <Route path="support" element={<SupportPage />} />
            <Route path="security" element={<SecurityPage />} />
            <Route path="email" element={<EmailSettingsPage />} />
            <Route path="audit" element={<AuditLogPage />} />
            {/*
              /platform/login is not a real route — PlatformGuard renders the sign-in screen
              in place at whatever platform URL you land on. It is still the address people
              type and bookmark, so it is accepted here and sent to the console home rather
              than falling through to the catch-all.
            */}
            <Route path="login" element={<Navigate to={PLATFORM_HOME} replace />} />
            <Route path="*" element={<Navigate to={PLATFORM_HOME} replace />} />
          </Route>
        </Routes>
      </Suspense>
    </PlatformGuard>
  );
}
