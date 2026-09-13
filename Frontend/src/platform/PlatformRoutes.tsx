import { Suspense } from 'react';
import lazyWithRetry from '../utils/lazyWithRetry';
import { Navigate, Route, Routes } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import PlatformGuard from './components/PlatformGuard';
import PlatformLayout from './components/PlatformLayout';

// Code-split every platform page, mirroring the tenant app's lazy-route pattern.
/** Absolute landing path for the control plane. See the redirect note below. */
const PLATFORM_HOME = '/platform/overview';

const OverviewPage = lazyWithRetry(() => import('./pages/OverviewPage'));
const TenantsPage = lazyWithRetry(() => import('./pages/TenantsPage'));
const TenantDetailPage = lazyWithRetry(() => import('./pages/TenantDetailPage'));
const PipelinePage = lazyWithRetry(() => import('./pages/PipelinePage'));
const PlansFlagsPage = lazyWithRetry(() => import('./pages/PlansFlagsPage'));
const AuditLogPage = lazyWithRetry(() => import('./pages/AuditLogPage'));
const PlatformUsersPage = lazyWithRetry(() => import('./pages/PlatformUsersPage'));
const BillingPage = lazyWithRetry(() => import('./pages/BillingPage'));
const SupportPage = lazyWithRetry(() => import('./pages/SupportPage'));
const SecurityPage = lazyWithRetry(() => import('./pages/SecurityPage'));
const EmailSettingsPage = lazyWithRetry(() => import('./pages/EmailSettingsPage'));
const PlatformAuthenticationPage = lazyWithRetry(() => import('./pages/PlatformAuthenticationPage'));

export const PlatformLoader = () => (
  <Box
    role="status"
    aria-live="polite"
    aria-label="Loading platform page"
    sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: '60vh', width: '100%' }}
  >
    <CircularProgress aria-hidden="true" />
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
            {/* Nested under security deliberately: this is Platform Admin → Security →
                Platform Authentication, and the URL should say so. SecurityPage stays the
                per-operator enrollment/session view; this is the plane-wide policy. */}
            <Route path="security/authentication" element={<PlatformAuthenticationPage />} />
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
