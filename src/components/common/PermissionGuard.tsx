import React from 'react';
import { Navigate } from 'react-router-dom';
import { useAuth } from '../../context/AuthContext';
import { Box, Typography, Button } from '@mui/material';
import { Security as SecurityIcon } from '@mui/icons-material';

interface PermissionGuardProps {
  moduleName: string;
  action?: 'view' | 'create' | 'edit' | 'delete';
  children: React.ReactNode;
  fallback?: React.ReactNode;
  redirect?: boolean;
}

const PermissionGuard: React.FC<PermissionGuardProps> = ({ 
  moduleName, 
  action = 'view', 
  children, 
  fallback, 
  redirect = false 
}) => {
  const { hasPermission } = useAuth();

  const isAuthorized = hasPermission(moduleName, action);

  if (!isAuthorized) {
    if (redirect) {
      return <Navigate to="/dashboard" replace />;
    }

    if (fallback) {
      return <>{fallback}</>;
    }

    if (action === 'view') {
      return (
        <Box sx={{ 
          display: 'flex', 
          flexDirection: 'column', 
          alignItems: 'center', 
          justifyContent: 'center', 
          height: '60vh',
          textAlign: 'center',
          p: 3
        }}>
          <SecurityIcon sx={{ fontSize: 64, color: 'text.secondary', mb: 2, opacity: 0.5 }} />
          <Typography variant="h5" sx={{ fontWeight: 700, mb: 1 }}>
            Access Denied
          </Typography>
          <Typography variant="body1" sx={{ color: 'text.secondary', mb: 3 }}>
            You do not have permission to access the <strong>{moduleName}</strong> module.
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
