import React from 'react';
import { Button, Dialog, DialogActions, DialogContent, DialogTitle, Typography } from '@mui/material';

interface ConfirmDialogProps {
  open: boolean;
  title: React.ReactNode;
  description?: React.ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  loading?: boolean;
  color?: 'primary' | 'error' | 'warning' | 'success';
  onClose: () => void;
  onConfirm: () => void;
  children?: React.ReactNode;
}

const ConfirmDialog: React.FC<ConfirmDialogProps> = ({
  open,
  title,
  description,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  loading = false,
  color = 'primary',
  onClose,
  onConfirm,
  children,
}) => (
  <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth>
    <DialogTitle sx={{ fontWeight: 850 }}>{title}</DialogTitle>
    <DialogContent dividers>
      {description ? (
        <Typography variant="body2" color="text.secondary" sx={{ mb: children ? 2 : 0 }}>
          {description}
        </Typography>
      ) : null}
      {children}
    </DialogContent>
    <DialogActions sx={{ p: 2 }}>
      <Button onClick={onClose} color="inherit">
        {cancelLabel}
      </Button>
      <Button variant="contained" color={color} onClick={onConfirm} disabled={loading}>
        {confirmLabel}
      </Button>
    </DialogActions>
  </Dialog>
);

export default ConfirmDialog;
