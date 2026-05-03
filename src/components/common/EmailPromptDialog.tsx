import React, { useEffect, useState } from 'react';
import {
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Button,
  Box,
  Typography,
  TextField,
  IconButton,
  Stack,
  Autocomplete,
  CircularProgress
} from '@mui/material';
import {
  Close as CloseIcon,
  Email as EmailIcon,
} from '@mui/icons-material';
import debounce from 'lodash/debounce';
import customerService, { type CustomerDTO } from '../../api/services/customerService';

interface EmailPromptDialogProps {
  open: boolean;
  initialEmail?: string;
  initialSubject?: string;
  initialBody?: string;
  onCancel: () => void;
  onConfirm: (email: string, subject?: string, body?: string, customerId?: number) => void;
  title?: string;
  loading?: boolean;
  businessUnitId: number;
  customerId?: number | null;
}

const EmailPromptDialog: React.FC<EmailPromptDialogProps> = ({
  open,
  initialEmail,
  initialSubject,
  initialBody,
  onCancel,
  onConfirm,
  title,
  loading = false,
  businessUnitId,
  customerId: preselectedCustomerId
}) => {
  const [email, setEmail] = useState('');
  const [subject, setSubject] = useState('');
  const [body, setBody] = useState('');
  const [selectedCustomerId, setSelectedCustomerId] = useState<number | null>(null);
  const [customers, setCustomers] = useState<CustomerDTO[]>([]);
  const [fetchingCustomers, setFetchingCustomers] = useState(false);
  const [isEmailReadonly, setIsEmailReadonly] = useState(false);

  useEffect(() => {
    if (open) {
      setEmail(initialEmail || '');
      setSubject(initialSubject || '');
      setBody(initialBody || '');
      setSelectedCustomerId(preselectedCustomerId || null);
      setIsEmailReadonly(!!preselectedCustomerId && !!initialEmail);
    }
  }, [open, initialEmail, initialSubject, initialBody, preselectedCustomerId]);

  const fetchCustomers = debounce(async (search: string) => {
    if (!businessUnitId) return;
    setFetchingCustomers(true);
    try {
      const res = await customerService.getAll({ name: search });
      setCustomers(res.items);
    } catch (error) {
      console.error('Failed to fetch customers', error);
    } finally {
      setFetchingCustomers(false);
    }
  }, 500);

  const handleConfirm = () => {
    if (!email) return;
    onConfirm(email, subject, body, selectedCustomerId || undefined);
  };

  return (
    <Dialog open={open} onClose={onCancel} maxWidth="sm" fullWidth>
      <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontWeight: 900, px: 3, py: 2 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
          <EmailIcon color="primary" />
          {title || 'Confirm Email & Approval'}
        </Box>
        <IconButton onClick={onCancel} size="small" sx={{ borderRadius: 1.5 }}>
          <CloseIcon />
        </IconButton>
      </DialogTitle>

      <DialogContent dividers sx={{ p: 3 }}>
        <Stack spacing={3}>
          <Box>
            <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', mb: 1, display: 'block' }}>
              Associate Customer
            </Typography>
            <Autocomplete
              options={customers}
              getOptionLabel={(o) => `${o.name} (${o.contactEmail || 'No Email'})`}
              loading={fetchingCustomers}
              onInputChange={(_, val) => fetchCustomers(val)}
              value={customers.find(c => c.id === selectedCustomerId) || null}
              onChange={(_, val) => {
                setSelectedCustomerId(val?.id || null);
                if (val?.contactEmail) {
                  setEmail(val.contactEmail);
                  setIsEmailReadonly(true);
                } else {
                  setIsEmailReadonly(false);
                }
              }}
              renderInput={(params) => (
                <TextField
                  {...params}
                  placeholder="Search customer to associate..."
                  size="small"
                />
              )}
            />
            <Typography variant="caption" sx={{ mt: 0.5, display: 'block', color: 'text.secondary', fontWeight: 600 }}>
              {selectedCustomerId ? 'This RFQ will be linked to the selected customer.' : 'Linking a customer is recommended for better reporting.'}
            </Typography>
          </Box>

          <TextField
            fullWidth
            label="Recipient Email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            disabled={isEmailReadonly}
            placeholder="customer@example.com"
            slotProps={{ input: { sx: { fontWeight: 800 } } }}
          />

          <TextField
            fullWidth
            label="Email Subject"
            value={subject}
            onChange={(e) => setSubject(e.target.value)}
            slotProps={{ input: { sx: { fontWeight: 700 } } }}
          />

          <TextField
            fullWidth
            multiline
            rows={6}
            label="Email Message"
            value={body}
            onChange={(e) => setBody(e.target.value)}
          />
        </Stack>
      </DialogContent>

      <DialogActions sx={{ px: 3, py: 2, bgcolor: 'action.hover' }}>
        <Button onClick={onCancel} sx={{ fontWeight: 800, borderRadius: 2 }}>Cancel</Button>
        <Button
          variant="contained"
          onClick={handleConfirm}
          disabled={!email || loading}
          sx={{ fontWeight: 900, borderRadius: 2, px: 3, bgcolor: 'success.main', '&:hover': { bgcolor: 'success.dark' } }}
        >
          {loading ? <CircularProgress size={20} color="inherit" /> : 'Confirm & Approve'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default EmailPromptDialog;
