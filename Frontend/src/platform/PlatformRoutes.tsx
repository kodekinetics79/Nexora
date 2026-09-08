import { lazy, Suspense } from 'react';
import { Navigate, Route, Routes, useLocation, useParams } from 'react-router-dom';
import { Box, CircularProgress } from '@mui/material';
import PlatformGuard from './components/PlatformGuard';
import PlatformLayout from './components/PlatformLayout';

// Code-split every platform page, mirroring the tenant app's lazy-route pattern.
/** Absolute landing path for the control plane. See the redirect note below. */
const PLATFORM_HOME = '/platform/overview';

const OverviewPage = lazy(() => import('./pages/OverviewPage'));
/** The single customer screen. There is no second one. */
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

/**
 * `/platform/tenants/:id` → `/platform/customers/:id`, keeping the query string.
 *
 * The old address was a whole second console for the same customer. Deleting it outright would
 * have broken every link already written into a support ticket, and `<Navigate>` alone cannot do
 * this: it needs the `:id` out of the path and the `?tab=` off the end, both of which are only
 * available inside a routed component. The customer page reads that `tab` key and opens the
 * matching section, so an old link lands exactly where it used to.
 */
function RedirectTenantToCustomer() {
  const { id = '' } = useParams();
  const { search } = useLocation();
  return <Navigate to={`/platform/customers/${encodeURIComponent(id)}${search}`} replace />;
}

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
              THE TAB HOST IS GONE. Its ten panels are sections on the customer page now, so this
              address has nothing of its own left to render — it forwards, carrying the query
              string so a `?tab=lifecycle` link in a year-old support ticket still opens
              offboarding rather than dumping somebody at the top of a page.
            */}
            <Route path="tenants/:id" element={<RedirectTenantToCustomer />} />
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
