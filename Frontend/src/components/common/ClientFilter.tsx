import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { Autocomplete, TextField } from '@mui/material';
import customerService from '../../api/services/customerService';

export interface ClientChoice {
  id: number;
  name: string;
}

/**
 * The "Client" filter on the Leads, RFQs and Quotes lists: type part of a name, pick one. The
 * clients offered are the ones on file — including every client the application recognised from
 * a document and a rep added — so the filter and the Client column name the same companies.
 */
const ClientFilter: React.FC<{
  value: ClientChoice | null;
  onChange: (value: ClientChoice | null) => void;
  width?: number;
}> = ({ value, onChange, width = 220 }) => {
  const [input, setInput] = React.useState('');
  const [term, setTerm] = React.useState('');
  React.useEffect(() => {
    const timer = window.setTimeout(() => setTerm(input.trim()), 250);
    return () => window.clearTimeout(timer);
  }, [input]);
  const clients = useQuery({
    queryKey: ['client-filter', term],
    queryFn: () => customerService.getAll({ name: term || undefined, pageSize: 20, isActive: true }),
    staleTime: 60_000,
  });
  const options = (clients.data?.items ?? []).map((customer) => ({ id: customer.id, name: customer.name }));

  return (
    <Autocomplete
      size="small"
      value={value}
      onChange={(_event, next) => onChange(next)}
      inputValue={input}
      onInputChange={(_event, next) => setInput(next)}
      options={value && !options.some((option) => option.id === value.id) ? [value, ...options] : options}
      getOptionLabel={(option) => option.name}
      isOptionEqualToValue={(option, chosen) => option.id === chosen.id}
      filterOptions={(all) => all}
      loading={clients.isFetching}
      noOptionsText={term ? 'No client with that name' : 'No clients yet'}
      sx={{ width: { xs: '100%', sm: width } }}
      renderInput={(params) => <TextField {...params} label="Client" placeholder="All clients" />}
    />
  );
};

export default ClientFilter;
