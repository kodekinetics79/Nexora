import React from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import { Box, Button, Typography } from '@mui/material';
import {
  ErrorOutlined as ErrorIcon,
  Security as SecurityIcon,
} from '@mui/icons-material';

interface PermissionGuardProps {
  moduleName: string;
  action?: 'view' | 'create' | 'edit' | 'delete';
  children: React.ReactNode;
  fallback?: React.ReactNode;
  // NOTE: a `redirect` prop used to be declared here but was never destructured, so all ~100 call
  // sites that passed it were no-ops. It is gone rather than implemented: silently bouncing a user
  // off a page they lack access to is exactly the "blank screen with no explanation" behaviour this
  // guard exists to prevent. The explicit Access Denied panel below is the intended treatment.
}

/**
 * The single definition of "what happens when nobody is signed in".
 *
 * Extracted so a layout route can carry the auth gate on its own — `/setup` shipped with the gate
 * only on its CHILD routes, so the index route rendered the whole authenticated shell to anonymous
 * visitors and told them their ROLE lacked access. A guard that has to be repeated per child is a
 * guard the next child will be added without; attach it to the shell and every screen below it
 * inherits it. `PermissionGuard` uses this too, so both paths redirect identically and any future
 * change (e.g. preserving the intended destination across login) lands in one place.
 */
export const RequireAuth: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { token } = useAuth();
  if (!token) return <Navigate to="/login" replace />;
  return <>{children}</>;
};

/**
 * Mirrors the backend's `RequireManagerRole` policy for manager/admin-only read surfaces.
 * `userData.isManager` is resolved by the server from role rank and is true for Manager, Admin,
 * and Owner. Keeping this separate from module permissions prevents a member with a broad read
 * grant from opening a page whose API will correctly answer 403.
 */
export const RequireManager: React.FC<{ children: React.ReactNode }> = ({ children }) => {
  const { token, userData } = useAuth();

  if (!token) return <RequireAuth>{children}</RequireAuth>;
  if (userData.isManager === true) return <>{children}</>;

  return (
    <Box role="alert" aria-live="polite" sx={{
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'center',
      justifyContent: 'center',
      height: '60vh',
      textAlign: 'center',
      p: 3,
    }}>
      <SecurityIcon sx={{ fontSize: 64, color: 'text.secondary', mb: 2, opacity: 0.5 }} />
      <Typography variant="h5" component="h1" sx={{ fontWeight: 700, mb: 1 }}>
        Manager access required
      </Typography>
      <Typography variant="body1" sx={{ color: 'text.secondary', mb: 3, maxWidth: 460 }}>
        This team-wide view is available to Manager, Admin, and Owner roles. Your own sales work
        remains available from Sales rep today.
      </Typography>
      <Button variant="contained" onClick={() => window.history.back()}>
        Go Back
      </Button>
    </Box>
  );
};

/** Wording that matches what an administrator actually ticks in Roles & Permissions. */
const ACTION_LABEL: Record<NonNullable<PermissionGuardProps['action']>, string> = {
  view: 'Can View',
  create: 'Can Create',
  edit: 'Can Edit',
  delete: 'Can Delete',
};

const PermissionGuard: React.FC<PermissionGuardProps> = ({
  moduleName,
  action = 'view',
  children,
  fallback,
}) => {
  const { token, hasPermission, permissionsError, permissionsLoading, refreshPermissions } = useAuth();

  // Auth gate: unauthenticated users are always sent to the login screen
  // (prevents the /dashboard -> /dashboard redirect loop / blank screen). Delegated to RequireAuth
  // so there is exactly one definition of the redirect; with no token it renders <Navigate> and the
  // children below never mount.
  if (!token) {
    return <RequireAuth>{children}</RequireAuth>;
  }

  const isAuthorized = hasPermission(moduleName, action);

  if (!isAuthorized) {
    // The permission set failed to load, so we are denying from an EMPTY set, not from a real
    // decision. Saying "Access Denied" here would send the user to an administrator who would
    // find nothing wrong with their grants. Name the actual fault and offer a retry.
    if (permissionsError && action === 'view') {
      return (
        <Box
          role="alert"
          sx={{
            display: 'flex', flexDirection: 'column', alignItems: 'center',
            justifyContent: 'center', height: '60vh', textAlign: 'center', p: 3,
          }}
        >
          <ErrorIcon sx={{ fontSize: 64, color: 'error.main', mb: 2, opacity: 0.85 }} />
          <Typography variant="h5" component="h1" sx={{ fontWeight: 700, mb: 1 }}>
            We could not check your access
          </Typography>
          <Typography variant="body1" sx={{ color: 'text.secondary', mb: 3, maxWidth: 460 }}>
            {permissionsError}
          </Typography>
          <Button
            variant="contained"
            disabled={permissionsLoading}
            onClick={() => { void refreshPermissions({ force: true }); }}
          >
            {permissionsLoading ? 'Retrying…' : 'Try again'}
          </Button>
        </Box>
      );
    }

    if (fallback) {
      return <>{fallback}</>;
    }

    if (action === 'view') {
      return (
        <Box role="alert" aria-live="polite" sx={{
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          height: '60vh',
          textAlign: 'center',
          p: 3
        }}>
          <SecurityIcon sx={{ fontSize: 64, color: 'text.secondary', mb: 2, opacity: 0.5 }} />
          <Typography variant="h5" component="h1" sx={{ fontWeight: 700, mb: 1 }}>
            Access Denied
          </Typography>
          <Typography variant="body1" sx={{ color: 'text.secondary', mb: 1 }}>
            You do not have permission to access the <strong>{moduleName}</strong> module.
          </Typography>
          {/* Name the exact grant so the user can ask for it by name instead of "more access". */}
          <Typography variant="body2" sx={{ color: 'text.secondary', mb: 3, maxWidth: 460 }}>
            Ask an administrator to grant your role <strong>{ACTION_LABEL[action]}</strong> on{' '}
            <strong>{moduleName}</strong> under Roles &amp; Permissions.
          </Typography>
          <Button variant="contained" onClick={() => window.history.back()}>
            Go Back
          </Button>
        </Box>
      );
    }

    return null; // For create/edit/delete buttons, we just hide them
  }

  return <>{children}</>;
};

export default PermissionGuard;
