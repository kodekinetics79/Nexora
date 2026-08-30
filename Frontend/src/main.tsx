import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter, useLocation } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import CssBaseline from '@mui/material/CssBaseline';
import { AuthProvider } from './context/AuthContext';
import { ThemeContextProvider } from './context/ThemeContext';
import App from './App';
import { queryClient } from './api/queryClient';
import ErrorBoundary from './components/common/ErrorBoundary';
import { SnackbarProvider } from 'notistack';
import { Toaster } from 'react-hot-toast';
import './index.css';
import './i18n';

// Developer tooling must never ship to a customer build. Besides leaving the black floating
// toggle visible over application controls, the panel exposes query keys, cached payloads and
// request state to anyone who can open the tenant portal. Vite folds this branch away in a
// production build, so the package is neither loaded nor emitted into production assets.
const DevelopmentQueryDevtools = import.meta.env.DEV
  ? React.lazy(async () => {
      const module = await import('@tanstack/react-query-devtools');
      return { default: module.ReactQueryDevtools };
    })
  : null;

const DevelopmentQueryDevtoolsRouteGate: React.FC = () => {
  const { pathname } = useLocation();
  const isPublicAuthenticationSurface = pathname === '/login'
    || pathname === '/forgot-password'
    || pathname.startsWith('/reset-password');

  if (!DevelopmentQueryDevtools || isPublicAuthenticationSurface) return null;
  return (
    <React.Suspense fallback={null}>
      <DevelopmentQueryDevtools initialIsOpen={false} />
    </React.Suspense>
  );
};

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <ThemeContextProvider>
        <AuthProvider>
          <CssBaseline />
          <BrowserRouter>
            <SnackbarProvider maxSnack={3} anchorOrigin={{ vertical: 'top', horizontal: 'right' }}>
              <ErrorBoundary>
                <App />
                {/* Inside the boundary, not beside it: a render throw originating in a toast used
                    to be uncaught and unmounted the entire application root. */}
                <Toaster position="top-right" />
              </ErrorBoundary>
            </SnackbarProvider>
            <DevelopmentQueryDevtoolsRouteGate />
          </BrowserRouter>
        </AuthProvider>
      </ThemeContextProvider>
    </QueryClientProvider>
  </React.StrictMode>
);
