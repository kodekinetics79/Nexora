import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Chip, Grid,
  CircularProgress, Divider, Avatar, Stack,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  Edit as EditIcon,
  Person as CustomerIcon,
  Email as EmailIcon,
  Receipt as BillingIcon,
  LocalShipping as ShippingIcon,
} from '@mui/icons-material';
import customerService from '../../api/services/customerService';

const InfoRow: React.FC<{ label: string; value: React.ReactNode }> = ({ label, value }) => (
  <Box sx={{ display: 'flex', gap: 2, py: 0.9, borderBottom: '1px solid', borderColor: 'divider', alignItems: 'center', '&:last-child': { border: 'none' } }}>
    <Typography component="span" sx={{ minWidth: 160, color: 'text.secondary', fontSize: '0.8rem', fontWeight: 600, flexShrink: 0 }}>
      {label}
    </Typography>
    <Box sx={{ fontSize: '0.875rem', fontWeight: 600 }}>
      {value ?? <Typography component="span" sx={{ color: '#9ca3af', fontSize: '0.875rem' }}>—</Typography>}
    </Box>
  </Box>
);

const Section: React.FC<{ title: string; icon: React.ReactNode; children: React.ReactNode }> = ({ title, icon, children }) => (
  <Box>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', textTransform: 'uppercase', letterSpacing: '0.08em', display: 'block', mb: 1.5 }}>
      {title}
    </Typography>
    <Paper sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', overflow: 'hidden' }}>
      <Box sx={{ px: 2, py: 1.5, display: 'flex', alignItems: 'center', gap: 1, bgcolor: 'action.hover', borderBottom: '1px solid', borderColor: 'divider' }}>
        <Box sx={{ color: 'primary.main', display: 'flex' }}>{icon}</Box>
        <Typography sx={{ fontWeight: 700, fontSize: '0.8rem' }}>{title}</Typography>
      </Box>
      <Box sx={{ px: 2.5, py: 1.5 }}>{children}</Box>
    </Paper>
  </Box>
);

const CustomerDetailPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const { data: customer, isLoading } = useQuery({
    queryKey: ['customer-detail', Number(id)],
    queryFn: () => customerService.getById(Number(id)),
    enabled: !!id,
  });

  if (isLoading) return <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: '60vh' }}><CircularProgress /></Box>;
  if (!customer) return <Box sx={{ p: 4 }}><Typography>Customer not found.</Typography></Box>;

  return (
    <Box sx={{ p: 3, width: '100%' }}>
      {/* Top Bar */}
      <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', mb: 3 }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2 }}>
          <Button startIcon={<BackIcon />} onClick={() => navigate('/customers')} size="small" variant="outlined" sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            Back
          </Button>
          <Divider orientation="vertical" flexItem />
          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
            <CustomerIcon sx={{ fontSize: 20, color: 'text.secondary' }} />
            <Typography sx={{ fontWeight: 700, fontSize: '0.9rem', color: 'text.secondary' }}>Customers /</Typography>
            <Typography sx={{ fontWeight: 800, fontSize: '0.9rem' }}>{customer.name}</Typography>
          </Box>
        </Box>
        <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
          <Chip label={customer.isActive ? 'Active' : 'Inactive'} color={customer.isActive ? 'success' : 'default'} size="small" variant="outlined" />
          <Button variant="contained" startIcon={<EditIcon />} onClick={() => navigate('/customers', { state: { editId: customer.id } })} disableElevation sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 1.5 }}>
            Edit Customer
          </Button>
        </Box>
      </Box>

      {/* Header Card */}
      <Paper sx={{ p: 3, mb: 3, borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <Box sx={{ display: 'flex', alignItems: 'center', gap: 2.5 }}>
          <Avatar
            src={customer.imageUrl}
            sx={{ width: 64, height: 64, bgcolor: 'primary.main', fontSize: '1.5rem', fontWeight: 900 }}
          >
            {customer.name?.[0]?.toUpperCase()}
          </Avatar>
          <Box sx={{ flex: 1 }}>
            <Typography variant="h5" sx={{ fontWeight: 900, mb: 0.3 }}>{customer.name}</Typography>
            <Box sx={{ display: 'flex', gap: 1.5, flexWrap: 'wrap', alignItems: 'center' }}>
              {customer.docId && (
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem', bgcolor: 'action.hover', px: 1, py: 0.2, borderRadius: 1, fontWeight: 700 }}>
                  {customer.docId}
                </Typography>
              )}
              {customer.contactEmail && (
                <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                  <EmailIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
                  <Typography sx={{ fontSize: '0.85rem', color: 'text.secondary' }}>
                    {customer.contactEmail}
                  </Typography>
                </Stack>
              )}
            </Box>
          </Box>
        </Box>
      </Paper>

      {/* Body */}
      <Grid container spacing={3}>
        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Billing Address" icon={<BillingIcon sx={{ fontSize: 16 }} />}>
            <InfoRow label="Address Line 1" value={customer.billingAddressLine1} />
            <InfoRow label="Address Line 2" value={customer.billingAddressLine2} />
            <InfoRow label="City" value={customer.billingCity} />
            <InfoRow label="State" value={customer.billingState} />
            <InfoRow label="Country" value={customer.billingCountry} />
            <InfoRow label="Postal Code" value={customer.billingPostalCode} />
          </Section>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <Section title="Shipping Address" icon={<ShippingIcon sx={{ fontSize: 16 }} />}>
            <InfoRow label="Address Line 1" value={customer.shippingAddressLine1} />
            <InfoRow label="Address Line 2" value={customer.shippingAddressLine2} />
            <InfoRow label="City" value={customer.shippingCity} />
            <InfoRow label="State" value={customer.shippingState} />
            <InfoRow label="Country" value={customer.shippingCountry} />
            <InfoRow label="Postal Code" value={customer.shippingPostalCode} />
          </Section>
        </Grid>
      </Grid>
    </Box>
  );
};

export default CustomerDetailPage;
