import React, { useEffect, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Typography,
  Paper,
  Button,
  Grid,
  TextField,
  CircularProgress,
  Divider,
} from '@mui/material';
import {
  Save as SaveIcon,
} from '@mui/icons-material';
import quoteConfigurationService, { type QuoteConfigurationDTO } from '../../../api/services/quoteConfigurationService';
import { useAuth } from '../../../context/AuthContext';

const QuoteFormatPage: React.FC = () => {
  const { userData } = useAuth();
  const queryClient = useQueryClient();

  const [formData, setFormData] = useState<Partial<QuoteConfigurationDTO>>({
    logo: '',
    primaryColor: '#000000',
    termsAndConditions: '',
    companyAddress: '',
    companyPhone: '',
    companyEmail: '',
    footerText: '',
  });

  const { data, isLoading } = useQuery({
    queryKey: ['quote-configuration', userData.businessUnitId],
    queryFn: () => quoteConfigurationService.getByBusinessUnitId(userData.businessUnitId || 1),
  });

  useEffect(() => {
    if (data) {
      setFormData({
        logo: data.logo || '',
        primaryColor: data.primaryColor || '#000000',
        termsAndConditions: data.termsAndConditions || '',
        companyAddress: data.companyAddress || '',
        companyPhone: data.companyPhone || '',
        companyEmail: data.companyEmail || '',
        footerText: data.footerText || '',
      });
    }
  }, [data]);

  const saveMutation = useMutation({
    mutationFn: (payload: any) => quoteConfigurationService.createOrUpdate(payload),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['quote-configuration'] });
      alert('Configuration saved successfully!');
    },
  });

  const handleSave = () => {
    const businessUnitId = userData.businessUnitId || 1;
    const payload = {
      ...formData,
      businessUnitId,
      createdBy: userData.userName || 'System',
    };
    saveMutation.mutate(payload);
  };

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', p: 5 }}><CircularProgress /></Box>;

  return (
    <Box sx={{ width: '100%', px: 1, py: 1 }}>
      <Box sx={{ mb: 2, display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 800, letterSpacing: '-0.02em', mb: 0.5 }}>Quote Format Setup</Typography>
          <Typography variant="body2" color="text.secondary">Configure document appearance and company details for quotations</Typography>
        </Box>
        <Button variant="contained" startIcon={<SaveIcon />} onClick={handleSave} disabled={saveMutation.isPending} sx={{ px: 4 }}>
          {saveMutation.isPending ? <CircularProgress size={24} color="inherit" /> : 'Save Configuration'}
        </Button>
      </Box>

      <Grid container spacing={4}>
        <Grid size={{ xs: 12, md: 8 }}>
          <Paper sx={{ p: 2.5, borderRadius: 2, border: '1px solid', borderColor: 'divider' }}>
            <Typography variant="h6" sx={{ fontWeight: 700, mb: 2 }}>Company Information</Typography>
            <Grid container spacing={3}>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth label="Logo URL" value={formData.logo} onChange={(e) => setFormData({ ...formData, logo: e.target.value })} placeholder="https://example.com/logo.png" />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label="Company Email" value={formData.companyEmail} onChange={(e) => setFormData({ ...formData, companyEmail: e.target.value })} />
              </Grid>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth label="Company Phone" value={formData.companyPhone} onChange={(e) => setFormData({ ...formData, companyPhone: e.target.value })} />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth label="Company Address" multiline rows={2} value={formData.companyAddress} onChange={(e) => setFormData({ ...formData, companyAddress: e.target.value })} />
              </Grid>
            </Grid>

            <Divider sx={{ my: 4 }} />

            <Typography variant="h6" sx={{ fontWeight: 700, mb: 3 }}>Document Styling & Content</Typography>
            <Grid container spacing={3}>
              <Grid size={{ xs: 12, sm: 6 }}>
                <TextField fullWidth type="color" label="Primary Theme Color" value={formData.primaryColor} onChange={(e) => setFormData({ ...formData, primaryColor: e.target.value })} sx={{ '& input': { height: 40 } }} />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth label="Terms & Conditions" multiline rows={6} value={formData.termsAndConditions} onChange={(e) => setFormData({ ...formData, termsAndConditions: e.target.value })} />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <TextField fullWidth label="Footer Text" multiline rows={2} value={formData.footerText} onChange={(e) => setFormData({ ...formData, footerText: e.target.value })} />
              </Grid>
            </Grid>
          </Paper>
        </Grid>

        <Grid size={{ xs: 12, md: 4 }}>
          <Paper sx={{ p: 3, borderRadius: 4, backgroundColor: 'rgba(0,0,0,0.02)', border: '1px dashed', borderColor: 'divider' }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 2, color: 'text.secondary', textTransform: 'uppercase' }}>Live Preview Hint</Typography>
            <Typography variant="body2" sx={{ color: 'text.secondary' }}>
              These settings will be applied to all generated PDF quotations. Ensure your logo URL is publicly accessible and colors are high-contrast for readability.
            </Typography>
            <Box sx={{ mt: 3, p: 2, borderRadius: 2, backgroundColor: '#fff', border: '1px solid', borderColor: 'divider', minHeight: 200, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center' }}>
              {formData.logo ? <img src={formData.logo} alt="Preview" style={{ maxWidth: '100%', maxHeight: 80, marginBottom: 16 }} /> : <Typography variant="caption">No Logo</Typography>}
              <Box sx={{ width: '80%', height: 4, backgroundColor: formData.primaryColor, mb: 2 }} />
              <Typography variant="caption" sx={{ color: 'text.secondary', textAlign: 'center' }}>{formData.companyAddress || 'No Address'}</Typography>
            </Box>
          </Paper>
        </Grid>
      </Grid>
    </Box>
  );
};

export default QuoteFormatPage;
