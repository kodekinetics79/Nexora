import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import PlatformGuard from './components/PlatformGuard';
import PlatformLayout from './components/PlatformLayout';

// Code-split every platform page, mirroring the tenant app's lazy-route pattern.
/** Absolute landing path for the control plane. See the redirect note below. */
const PLATFORM_HOME = '/platform/overview';

const OverviewPage = lazy(() => import('./pages/OverviewPage'));
const TenantDetailPage = lazy(() => import('./pages/TenantDetailPage'));
// The redesigned single-page customer screen. Mounted ALONGSIDE the twelve-tab screen rather
// than replacing it, so the two can be compared on the same data before anything is retired —
// and so every ?tab= deep link already pasted into a support ticket keeps working.
const CustomerPage = lazy(() => import('./pages/CustomerPage'));
const CustomersPage = lazy(() => import('./pages/CustomersPage'));
const NewCustomerPage = lazy(() => import('./pages/NewCustomerPage'));
const PipelinePage = lazy(() => import('./pages/PipelinePage'));
const PlansFlagsPage = lazy(() => import('./pages/PlansFlagsPage'));
const AuditLogPage = lazy(() => import('./pages/AuditLogPage'));
const PlatformUsersPage = lazy(() => import('./pages/PlatformUsersPage'));
const BillingPage = lazy(() => import('./pages/BillingPage'));
const SupportPage = lazy(() => import('./pages/SupportPage'));
const SecurityPage = lazy(() => import('./pages/SecurityPage'));
const EmailSettingsPage = lazy(() => import('./pages/EmailSettingsPage'));
const PlatformAuthenticationPage = lazy(() => import('./pages/PlatformAuthenticationPage'));

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
            <Route path="customers" element={<CustomersPage />} />

            {/*
              THE OLD TENANT LIST IS RETIRED. It answered none of the questions an operator opens
              this screen with, and leaving it mounted "for comparison" is how a redesign becomes
              a second screen nobody uses instead of a replacement.
            */}
            <Route path="tenants" element={<Navigate to="/platform/customers" replace />} />

            {/*
              The twelve-tab screen survives at ONE address and only as the Advanced surface: it
              still owns contract, module and deployment writes, which have not moved yet, and
              every ?tab= link already pasted into a support ticket has to keep resolving. It is
              no longer where anybody lands — the customer page is — and when the remaining write
              paths move, this route goes with them.
            */}
            <Route path="tenants/:id" element={<TenantDetailPage />} />
            {/* Ahead of :id, or "new" is read as a tenant id and the page tries to load it. */}
            <Route path="customers/new" element={<NewCustomerPage />} />
            <Route path="customers/:id" element={<CustomerPage />} />
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
