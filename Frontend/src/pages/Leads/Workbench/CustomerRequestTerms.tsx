import React from 'react';
import { Alert, Box, Stack, Typography } from '@mui/material';

interface CustomerRequestTermsProps {
  requiredDeliveryDate?: string | null;
  deliveryLocation?: string | null;
  agreementReference?: string | null;
  historicalReadOnly?: boolean;
}

const Term = ({ label, value }: { label: string; value?: string | null }) => (
  <Box>
    <Typography variant="caption" color="text.secondary">{label}</Typography>
    <Typography variant="body2" sx={{ fontWeight: 750, overflowWrap: 'anywhere' }}>
      {value || 'Not captured'}
    </Typography>
  </Box>
);

// Required delivery is a customer calendar date, not an instant. Formatting an ISO value
// through the browser timezone can move midnight UTC to the previous day in North America.
const formatCalendarDate = (value: string) => {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(value);
  if (!match) return value;
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return date.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
};

const CustomerRequestTerms: React.FC<CustomerRequestTermsProps> = ({
  requiredDeliveryDate,
  deliveryLocation,
  agreementReference,
  historicalReadOnly = false,
}) => {
  const missing = !requiredDeliveryDate || !deliveryLocation || !agreementReference;
  return (
    <Alert severity={missing && !historicalReadOnly ? 'warning' : 'info'} sx={{ mb: 1.5 }}>
      <Typography sx={{ fontWeight: 850, mb: 0.75 }}>Customer request terms</Typography>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={{ xs: 1, md: 4 }}>
        <Term label="Required delivery date" value={requiredDeliveryDate ? formatCalendarDate(requiredDeliveryDate) : null} />
        <Term label="Delivery location" value={deliveryLocation} />
        <Term label="Agreement reference" value={agreementReference} />
      </Stack>
      {missing ? (
        <Typography variant="caption" sx={{ display: 'block', mt: 1 }}>
          {historicalReadOnly
            ? 'This historical revision predates frozen customer terms. No values were inferred from the mutable Lead.'
            : 'One or more customer terms were not captured. Check the source evidence before committing participation.'}
        </Typography>
      ) : null}
    </Alert>
  );
};

export default CustomerRequestTerms;
