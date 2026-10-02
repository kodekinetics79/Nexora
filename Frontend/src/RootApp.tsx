import { Suspense } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import RouteAnnouncer from './components/layout/RouteAnnouncer';
import { useAuth } from './context/AuthContext';
import lazyWithRetry from './utils/lazyWithRetry';

// Authentication screens form their own small route graph. Loading one of them must not parse the
// tenant navigation, permission guards, setup catalog, or the hundreds of workspace route imports.
const WorkspaceApp = lazyWithRetry(() => import('./App'));
const LoginPage = lazyWithRetry(() => import('./pages/Login/LoginPage'));
const ActivateAccountPage = lazyWithRetry(() => import('./pages/Activation/ActivateAccountPage'));
const ForgotPasswordPage = lazyWithRetry(() => import('./pages/PasswordReset/ForgotPasswordPage'));
const ResetPasswordPage = lazyWithRetry(() => import('./pages/PasswordReset/ResetPasswordPage'));

const PageLoader = () => (
  <div
    role="status"
    aria-live="polite"
    aria-label="Loading page"
    style={{
      minHeight: '60vh',
      display: 'grid',
      placeItems: 'center',
      color: '#8f690f',
    }}
  >
    <span className="route-loading-indicator" aria-hidden="true" />
  </div>
);

const RootRedirect = () => {
  const { token } = useAuth();
  return <Navigate to={token ? '/dashboard' : '/login'} replace />;
};

const isPublicAuthenticationPath = (pathname: string) => (
  pathname === '/'
  || pathname === '/login'
  || /^\/activate\/[^/]+$/.test(pathname)
  || pathname === '/forgot-password'
  || /^\/reset-password\/[^/]+$/.test(pathname)
);

/**
 * Keeps the public authentication surface independent from the authenticated workspace bundle.
 * Once the address belongs to a tenant screen, the complete workspace router is fetched once and
 * stays mounted for all subsequent in-app navigation.
 */
function RootApp() {
  const { pathname } = useLocation();

  if (!isPublicAuthenticationPath(pathname)) {
    return (
      <Suspense fallback={<PageLoader />}>
        <WorkspaceApp />
      </Suspense>
    );
  }

  return (
    <>
      <RouteAnnouncer />
      <Suspense fallback={<PageLoader />}>
        <Routes>
          <Route path="/" element={<RootRedirect />} />
          <Route path="/login" element={<LoginPage />} />
          <Route path="/activate/:token" element={<ActivateAccountPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password/:token" element={<ResetPasswordPage />} />
        </Routes>
      </Suspense>
    </>
  );
}

export default RootApp;
