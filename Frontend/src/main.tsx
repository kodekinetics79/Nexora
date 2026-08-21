import React from 'react';
import ReactDOM from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
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
          </BrowserRouter>
        </AuthProvider>
      </ThemeContextProvider>
      <ReactQueryDevtools initialIsOpen={false} />
    </QueryClientProvider>
  </React.StrictMode>
);
