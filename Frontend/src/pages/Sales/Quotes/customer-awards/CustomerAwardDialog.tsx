import React from 'react';
import {
  Dialog,
  DialogContent,
  DialogTitle,
  IconButton,
  Tooltip,
  useMediaQuery,
  useTheme,
} from '@mui/material';
import { Close as CloseIcon } from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import CustomerAwardWorkspace, {
  type CustomerAwardCompletion,
  type CustomerAwardQuote,
} from './CustomerAwardWorkspace';
import type { QuoteAwardProjection } from '../../../../api/services/customerAwardService';

export interface CustomerAwardDialogProps {
  open: boolean;
  quote: CustomerAwardQuote | null;
  initialProjection?: QuoteAwardProjection;
  onClose: () => void;
  onCompleted?: (result: CustomerAwardCompletion) => void;
}

const CustomerAwardDialog: React.FC<CustomerAwardDialogProps> = ({
  open,
  quote,
  initialProjection,
  onClose,
  onCompleted,
}) => {
  const theme = useTheme();
  const fullScreen = useMediaQuery(theme.breakpoints.down('md'));
  const [busy, setBusy] = React.useState(false);

  React.useEffect(() => {
    if (!open) setBusy(false);
  }, [open]);

  const close = () => {
    if (!busy) onClose();
  };

  const completed = (result: CustomerAwardCompletion) => {
    toast.success(
      result.order
        ? `Award ${result.award.awardNumber} confirmed and order ${result.order.orderNo} created`
        : `Award ${result.award.awardNumber} confirmed`,
    );
    onCompleted?.(result);
    onClose();
  };

  return (
    <Dialog
      open={open}
      onClose={close}
      fullWidth
      fullScreen={fullScreen}
      maxWidth="lg"
      aria-labelledby="customer-award-dialog-title"
    >
      <DialogTitle id="customer-award-dialog-title" sx={{ pr: 7, fontWeight: 800 }}>
        Capture and match Client PO
        <Tooltip title={busy ? 'Client PO submission in progress' : 'Close'}>
          <span>
            <IconButton
              aria-label="Close customer award dialog"
              onClick={close}
              disabled={busy}
              sx={{ position: 'absolute', top: 10, right: 10 }}
            >
              <CloseIcon />
            </IconButton>
          </span>
        </Tooltip>
      </DialogTitle>
      <DialogContent dividers sx={{ p: { xs: 2, md: 3 } }}>
        {quote && (
          <CustomerAwardWorkspace
            key={quote.id}
            quote={quote}
            initialProjection={initialProjection}
            onCancel={close}
            onCompleted={completed}
            onBusyChange={setBusy}
          />
        )}
      </DialogContent>
    </Dialog>
  );
};

export default CustomerAwardDialog;
