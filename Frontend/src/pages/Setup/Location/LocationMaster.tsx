import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box,
  Typography,
  Paper,
  Button,
  IconButton,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Grid,
  TextField,
  CircularProgress,
  List,
  ListItem,
  ListItemText,
  ListItemSecondaryAction,
  Stack,
  Chip,
} from '@mui/material';
import {
  Add as AddIcon,
  Edit as EditIcon,
  ChevronRight as ChevronRightIcon,
  Public as CountryIcon,
  Map as StateIcon,
  LocationCity as CityIcon,
} from '@mui/icons-material';
import countryService, { type CountryDTO } from '../../../api/services/countryService';
import stateService, { type StateDTO } from '../../../api/services/stateService';
import cityService from '../../../api/services/cityService';
import { useAuth } from '../../../context/AuthContext';
import SearchField from '../../../components/common/SearchField';
import ApiErrorNotice from '../../../components/common/ApiErrorNotice';
// The grid is sized against the viewport, and the Setup breadcrumb bar is part of that viewport.
import { SETUP_CHROME_HEIGHT } from '../SetupShell';
import { handleApiError } from '../../../utils/errorHandler';
import { useSnackbar } from 'notistack';

const LocationMaster: React.FC = () => {
  const { t } = useTranslation();
  const { userData } = useAuth();
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();

  // Selection State
  const [selectedCountry, setSelectedCountry] = useState<CountryDTO | null>(null);
  const [selectedState, setSelectedState] = useState<StateDTO | null>(null);

  // Search State
  const [countrySearch, setCountrySearch] = useState('');
  const [stateSearch, setStateSearch] = useState('');
  const [citySearch, setCitySearch] = useState('');

  // Modal State
  const [modalType, setModalType] = useState<'country' | 'state' | 'city' | null>(null);
  const [editingRecord, setEditingRecord] = useState<any>(null);
  const [formData, setFormData] = useState<any>({});

  // Queries.
  //
  // `isError` on all three is load-bearing, not defensive tidying. Each list below is derived with
  // `(data || []).filter(...)`, so a failed read produces an empty array that is indistinguishable
  // from a genuinely empty table — and the countries column had no length guard at all, so it
  // painted a BLANK WHITE PANEL: no rows, no message, no error, nothing to act on. An operator
  // reads that as "this tenant has no countries" and starts re-entering reference data that is
  // already there.
  const {
    data: countries, isLoading: loadingCountries,
    isError: countriesFailed, error: countriesError, refetch: refetchCountries,
  } = useQuery({
    queryKey: ['countries', userData.businessUnitId],
    queryFn: () => countryService.getAll(userData.businessUnitId || 1),
  });

  const {
    data: states, isLoading: loadingStates,
    isError: statesFailed, error: statesError, refetch: refetchStates,
  } = useQuery({
    queryKey: ['states', userData.businessUnitId, selectedCountry?.countryId],
    queryFn: () => stateService.getAll(userData.businessUnitId || 1),
    enabled: !!selectedCountry,
  });

  const {
    data: cities, isLoading: loadingCities,
    isError: citiesFailed, error: citiesError, refetch: refetchCities,
  } = useQuery({
    queryKey: ['cities', userData.businessUnitId, selectedState?.stateId],
    queryFn: () => cityService.getAll(userData.businessUnitId || 1),
    enabled: !!selectedState,
  });

  // Filtered Data
  const filteredCountries = (countries || []).filter(c =>
    c.countryName.toLowerCase().includes(countrySearch.toLowerCase()) ||
    c.countryCode.toLowerCase().includes(countrySearch.toLowerCase())
  );

  const filteredStates = (states || []).filter(s =>
    s.countryId === selectedCountry?.countryId &&
    (s.stateName.toLowerCase().includes(stateSearch.toLowerCase()) ||
      s.stateCode.toLowerCase().includes(stateSearch.toLowerCase()))
  );

  const filteredCities = (cities || []).filter(c =>
    c.stateId === selectedState?.stateId &&
    c.cityName.toLowerCase().includes(citySearch.toLowerCase())
  );

  // Mutations
  const saveMutation = useMutation({
    mutationFn: async ({ type, data, id }: { type: string, data: any, id?: number }) => {
      const buid = userData.businessUnitId || 1;
      const payload = { ...data, buid };
      if (type === 'country') return id ? countryService.update(id, payload) : countryService.create(payload);
      if (type === 'state') return id ? stateService.update(id, payload) : stateService.create(payload);
      if (type === 'city') return id ? cityService.update(id, payload) : cityService.create(payload);
    },
    onSuccess: (_, variables) => {
      queryClient.invalidateQueries({ queryKey: [variables.type + 's'] });
      enqueueSnackbar(`${variables.type.charAt(0).toUpperCase() + variables.type.slice(1)} saved successfully`, { variant: 'success' });
      setModalType(null);
      setEditingRecord(null);
      setFormData({});
    },
    onError: (error: any) => handleApiError(error),
  });

  const handleOpenModal = (type: 'country' | 'state' | 'city', record?: any) => {
    setModalType(type);
    setEditingRecord(record || null);
    if (record) {
      setFormData(record);
    } else {
      setFormData({
        isActive: true,
        countryId: type === 'state' ? selectedCountry?.countryId : (type === 'city' ? selectedCountry?.countryId : undefined),
        stateId: type === 'city' ? selectedState?.stateId : undefined,
      });
    }
  };

  return (
    <Box sx={{ height: `calc(100vh - ${120 + SETUP_CHROME_HEIGHT}px)`, px: 1, py: 1 }}>
      <Box sx={{ mb: 2 }}>
        <Typography variant="h5" sx={{ fontWeight: 800 }}>{t('locations')}</Typography>
        <Typography variant="body2" color="text.secondary">Manage countries, states, and cities in a single unified view</Typography>
      </Box>

      <Grid container spacing={2} sx={{ height: 'calc(100% - 60px)' }}>
        {/* Country Column */}
        <Grid size={{ xs: 12, md: 4 }} sx={{ height: '100%' }}>
          <Paper sx={{ height: '100%', display: 'flex', flexDirection: 'column', borderRadius: 2, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
            <Box sx={{ p: 1.5, borderBottom: '1px solid', borderColor: 'divider', backgroundColor: 'rgba(0,0,0,0.01)' }}>
              <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1.5 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 700, display: 'flex', alignItems: 'center', gap: 1 }}>
                  <CountryIcon fontSize="small" color="primary" /> {t('country') || 'Countries'}
                </Typography>
                <Button size="small" startIcon={<AddIcon />} onClick={() => handleOpenModal('country')}>Add</Button>
              </Stack>
              <SearchField width="100%" value={countrySearch} onChange={setCountrySearch} placeholder="Search..." />
            </Box>
            <Box sx={{ flexGrow: 1, overflowY: 'auto' }}>
              {loadingCountries ? (
                <Box sx={{ p: 2, textAlign: 'center' }}><CircularProgress size={20} /></Box>
              ) : countriesFailed ? (
                <ApiErrorNotice
                  error={countriesError}
                  fallbackMessage="Countries could not be loaded, so this column is not a picture of what exists. Nothing has been removed."
                  onRetry={() => void refetchCountries()}
                  sx={{ m: 1.5, borderRadius: 2 }}
                />
              ) : (
                <List disablePadding>
                  {filteredCountries.map(c => (
                    <ListItem
                      key={c.countryId}
                      onClick={() => { setSelectedCountry(c); setSelectedState(null); }}
                      sx={{ py: 1, cursor: 'pointer', '&.Mui-selected': { backgroundColor: 'action.selected' } }}
                      className={selectedCountry?.countryId === c.countryId ? 'Mui-selected' : ''}
                    >
                      <ListItemText
                        primary={c.countryName}
                        secondary={c.countryCode}
                        slotProps={{
                          primary: { sx: { fontWeight: 600 }, variant: 'body2' }
                        }}
                      />
                      <ListItemSecondaryAction>
                        <IconButton size="small" onClick={() => handleOpenModal('country', c)}><EditIcon fontSize="inherit" /></IconButton>
                        <ChevronRightIcon fontSize="small" sx={{ ml: 1, opacity: 0.3 }} />
                      </ListItemSecondaryAction>
                    </ListItem>
                  ))}
                  {/* The guard the other two columns always had. Without it an empty result — read
                      or failed — was an unexplained expanse of white. */}
                  {filteredCountries.length === 0 && <Box sx={{ p: 2, textAlign: 'center', color: 'text.secondary' }}>No countries found</Box>}
                </List>
              )}
            </Box>
          </Paper>
        </Grid>

        {/* State Column */}
        <Grid size={{ xs: 12, md: 4 }} sx={{ height: '100%' }}>
          <Paper sx={{ height: '100%', display: 'flex', flexDirection: 'column', borderRadius: 2, border: '1px solid', borderColor: 'divider', overflow: 'hidden', opacity: selectedCountry ? 1 : 0.6 }}>
            <Box sx={{ p: 1.5, borderBottom: '1px solid', borderColor: 'divider', backgroundColor: 'rgba(0,0,0,0.01)' }}>
              <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1.5 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 700, display: 'flex', alignItems: 'center', gap: 1 }}>
                  <StateIcon fontSize="small" color="secondary" /> States {selectedCountry && <Chip label={selectedCountry.countryName} size="small" sx={{ height: 18, fontSize: 10 }} />}
                </Typography>
                <Button size="small" startIcon={<AddIcon />} disabled={!selectedCountry} onClick={() => handleOpenModal('state')}>Add</Button>
              </Stack>
              <SearchField width="100%" value={stateSearch} onChange={setStateSearch} placeholder="Search states..." />
            </Box>
            <Box sx={{ flexGrow: 1, overflowY: 'auto' }}>
              {!selectedCountry ? (
                <Box sx={{ p: 4, textAlign: 'center', color: 'text.secondary' }}>Select a country to view states</Box>
              ) : loadingStates ? (
                <Box sx={{ p: 2, textAlign: 'center' }}><CircularProgress size={20} /></Box>
              ) : statesFailed ? (
                <ApiErrorNotice
                  error={statesError}
                  fallbackMessage="States could not be loaded for this country. This column is not saying there are none."
                  onRetry={() => void refetchStates()}
                  sx={{ m: 1.5, borderRadius: 2 }}
                />
              ) : (
                <List disablePadding>
                  {filteredStates.map(s => (
                    <ListItem
                      key={s.stateId}
                      onClick={() => setSelectedState(s)}
                      sx={{ py: 1, cursor: 'pointer', '&.Mui-selected': { backgroundColor: 'action.selected' } }}
                      className={selectedState?.stateId === s.stateId ? 'Mui-selected' : ''}
                    >
                      <ListItemText
                        primary={s.stateName}
                        secondary={s.stateCode}
                        slotProps={{
                          primary: { sx: { fontWeight: 600 }, variant: 'body2' }
                        }}
                      />
                      <ListItemSecondaryAction>
                        <IconButton size="small" onClick={() => handleOpenModal('state', s)}><EditIcon fontSize="inherit" /></IconButton>
                        <ChevronRightIcon fontSize="small" sx={{ ml: 1, opacity: 0.3 }} />
                      </ListItemSecondaryAction>
                    </ListItem>
                  ))}
                  {filteredStates.length === 0 && <Box sx={{ p: 2, textAlign: 'center', variant: 'caption', color: 'text.secondary' }}>No states found</Box>}
                </List>
              )}
            </Box>
          </Paper>
        </Grid>

        {/* City Column */}
        <Grid size={{ xs: 12, md: 4 }} sx={{ height: '100%' }}>
          <Paper sx={{ height: '100%', display: 'flex', flexDirection: 'column', borderRadius: 2, border: '1px solid', borderColor: 'divider', overflow: 'hidden', opacity: selectedState ? 1 : 0.6 }}>
            <Box sx={{ p: 1.5, borderBottom: '1px solid', borderColor: 'divider', backgroundColor: 'rgba(0,0,0,0.01)' }}>
              <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 1.5 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 700, display: 'flex', alignItems: 'center', gap: 1 }}>
                  <CityIcon fontSize="small" color="success" /> {t('city') || 'Cities'} {selectedState && <Chip label={selectedState.stateName} size="small" sx={{ height: 18, fontSize: 10 }} />}
                </Typography>
                <Button size="small" startIcon={<AddIcon />} disabled={!selectedState} onClick={() => handleOpenModal('city')}>Add</Button>
              </Stack>
              <SearchField width="100%" value={citySearch} onChange={setCitySearch} placeholder="Search cities..." />
            </Box>
            <Box sx={{ flexGrow: 1, overflowY: 'auto' }}>
              {!selectedState ? (
                <Box sx={{ p: 4, textAlign: 'center', color: 'text.secondary' }}>Select a state to view cities</Box>
              ) : loadingCities ? (
                <Box sx={{ p: 2, textAlign: 'center' }}><CircularProgress size={20} /></Box>
              ) : citiesFailed ? (
                <ApiErrorNotice
                  error={citiesError}
                  fallbackMessage="Cities could not be loaded for this state. This column is not saying there are none."
                  onRetry={() => void refetchCities()}
                  sx={{ m: 1.5, borderRadius: 2 }}
                />
              ) : (
                <List disablePadding>
                  {filteredCities.map(c => (
                    <ListItem key={c.cityId} sx={{ py: 1 }}>
                      <ListItemText
                        primary={c.cityName}
                        slotProps={{
                          primary: { sx: { fontWeight: 600 }, variant: 'body2' }
                        }}
                      />
                      <ListItemSecondaryAction>
                        <IconButton size="small" onClick={() => handleOpenModal('city', c)}><EditIcon fontSize="inherit" /></IconButton>
                      </ListItemSecondaryAction>
                    </ListItem>
                  ))}
                  {filteredCities.length === 0 && <Box sx={{ p: 2, textAlign: 'center', variant: 'caption', color: 'text.secondary' }}>No cities found</Box>}
                </List>
              )}
            </Box>
          </Paper>
        </Grid>
      </Grid>

      {/* Unified Dialog */}
      <Dialog open={!!modalType} onClose={() => setModalType(null)} fullWidth maxWidth="xs">
        <DialogTitle sx={{ fontWeight: 800 }}>
          {editingRecord ? 'Edit' : 'Add'} {modalType?.charAt(0).toUpperCase()}{modalType?.slice(1)}
        </DialogTitle>
        <DialogContent dividers sx={{ p: 3 }}>
          <Stack spacing={3}>
            {modalType === 'city' && (
              <Typography variant="caption" color="text.secondary">Adding to: {selectedCountry?.countryName} &gt; {selectedState?.stateName}</Typography>
            )}
            {modalType === 'state' && (
              <Typography variant="caption" color="text.secondary">Adding to: {selectedCountry?.countryName}</Typography>
            )}

            <TextField
              fullWidth
              label={modalType === 'city' ? 'City Name' : (modalType === 'state' ? 'State Name' : 'Country Name')}
              value={modalType === 'city' ? formData.cityName : (modalType === 'state' ? formData.stateName : formData.countryName)}
              onChange={(e) => setFormData({ ...formData, [modalType === 'city' ? 'cityName' : (modalType === 'state' ? 'stateName' : 'countryName')]: e.target.value })}
            />

            {modalType !== 'city' && (
              <TextField
                fullWidth
                label={modalType === 'state' ? 'State Code' : 'Country Code'}
                value={modalType === 'state' ? formData.stateCode : formData.countryCode}
                onChange={(e) => setFormData({ ...formData, [modalType === 'state' ? 'stateCode' : 'countryCode']: e.target.value })}
              />
            )}

            <TextField
              fullWidth
              label="Description"
              multiline
              rows={2}
              value={formData.description || ''}
              onChange={(e) => setFormData({ ...formData, description: e.target.value })}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setModalType(null)}>Cancel</Button>
          <Button
            variant="contained"
            onClick={() => saveMutation.mutate({ type: modalType!, data: formData, id: editingRecord?.[modalType === 'city' ? 'cityId' : (modalType === 'state' ? 'stateId' : 'countryId')] })}
            disabled={saveMutation.isPending}
          >
            {saveMutation.isPending ? <CircularProgress size={20} /> : 'Save'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default LocationMaster;
