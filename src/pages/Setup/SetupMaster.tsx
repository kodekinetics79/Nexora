import React, { useState, useMemo, useEffect } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box,
  Typography,
  Paper,
  Button,
  TextField,
  Chip,
  IconButton,
  Dialog,
  DialogTitle,
  DialogContent,
  DialogActions,
  Grid,
  FormControlLabel,
  Switch,
  CircularProgress,
  Tooltip,
  Divider,
  MenuItem,
  Card,
  CardContent,
  Avatar,
} from '@mui/material';
import {
  Add as AddIcon,
  Edit as EditIcon,
  Settings as SettingsIcon,
  Layers as LayersIcon,
  CheckCircle as ActiveIcon,
  Cancel as InactiveIcon,
} from '@mui/icons-material';
import { useTranslation } from 'react-i18next';
import { useLocation } from 'react-router-dom';
import setupService from '../../api/services/setupService';
import type { SetupMasterDTO, SetupMasterCreateDTO, SetupMasterUpdateDTO } from '../../api/services/setupService';
import { useAuth } from '../../context/AuthContext';
import SearchField from '../../components/common/SearchField';
import lodash from 'lodash';

const SetupMaster: React.FC = () => {
  const { userData } = useAuth();
  const location = useLocation();
  useTranslation();
  const queryClient = useQueryClient();

  const getSetupTypeFromPath = (path: string) => {
    const p = path.toLowerCase();
    if (p.includes('currency')) return 'CURRENCY';
    if (p.includes('warehouse')) return 'WAREHOUSE';
    if (p.includes('uom')) return 'UOM';
    if (p.includes('country')) return 'COUNTRY';
    if (p.includes('state')) return 'STATE';
    if (p.includes('city')) return 'CITY';
    return undefined;
  };

  const setupType = getSetupTypeFromPath(location.pathname);

  // Filters
  const [search, setSearch] = useState('');
  const [filterActive, setFilterActive] = useState<boolean | 'all'>('all');

  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<SetupMasterDTO | null>(null);

  const [formData, setFormData] = useState<Partial<SetupMasterDTO>>({
    setupType: setupType || '',
    setupCode: '',
    setupName: '',
    description: '',
    isActive: true,
    parentSetupId: null,
  });

  // Sync form data when selection changes
  useEffect(() => {
    if (selectedRecord) {
      setFormData({
        setupType: selectedRecord.setupType,
        setupCode: selectedRecord.setupCode || '',
        setupName: selectedRecord.setupName,
        description: selectedRecord.description || '',
        isActive: selectedRecord.isActive,
        parentSetupId: selectedRecord.parentSetupId,
      });
    } else {
      setFormData({
        setupType: setupType || '',
        setupCode: '',
        setupName: '',
        description: '',
        isActive: true,
        parentSetupId: null,
      });
    }
  }, [selectedRecord, setupType]);

  const { data: allSetups, isLoading } = useQuery({
    queryKey: ['setups-global-all'],
    queryFn: () => setupService.getAll({ pageSize: 5000 }),
  });

  const groupedData = useMemo(() => {
    if (!allSetups?.items) return {};

    let filtered = allSetups.items;

    if (setupType) {
      filtered = filtered.filter(item => item.setupType === setupType);
    }

    if (search) {
      const lowerSearch = search.toLowerCase();
      filtered = filtered.filter(item =>
        item.setupName.toLowerCase().includes(lowerSearch) ||
        (item.setupCode && item.setupCode.toLowerCase().includes(lowerSearch)) ||
        item.setupType.toLowerCase().includes(lowerSearch)
      );
    }

    if (filterActive !== 'all') {
      const isActiveFilter = filterActive === true || (filterActive as any) === 'true';
      filtered = filtered.filter(item => item.isActive === isActiveFilter);
    }

    return lodash.groupBy(filtered, 'setupType');
  }, [allSetups, search, filterActive, setupType]);

  const distinctTypes = useMemo(() => Object.keys(groupedData).sort(), [groupedData]);

  const createMutation = useMutation({
    mutationFn: (newRecord: SetupMasterCreateDTO) => setupService.create(newRecord),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['setups-global-all'] });
      setIsModalOpen(false);
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, updateData }: { id: number; updateData: SetupMasterUpdateDTO }) => setupService.update(id, updateData),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['setups-global-all'] });
      setIsModalOpen(false);
    },
  });

  const handleSave = () => {
    if (selectedRecord) {
      updateMutation.mutate({
        id: selectedRecord.setupId,
        updateData: {
          setupType: formData.setupType || '',
          setupCode: formData.setupCode || '',
          setupName: formData.setupName || '',
          description: formData.description || '',
          parentSetupId: formData.parentSetupId,
          isActive: !!formData.isActive,
          modifiedBy: userData.userName || 'System'
        }
      });
    } else {
      createMutation.mutate({
        setupType: formData.setupType || '',
        setupCode: formData.setupCode || '',
        setupName: formData.setupName || '',
        description: formData.description || '',
        parentSetupId: formData.parentSetupId,
        isActive: !!formData.isActive,
        createdBy: userData.userName || 'System'
      });
    }
  };

  const [isNewType, setIsNewType] = useState(false);

  return (
    <Box sx={{ width: '100%', p: 3, backgroundColor: (theme) => theme.palette.mode === 'dark' ? '#0a0a0a' : '#f4f6f8', minHeight: '100vh' }}>
      {/* Header Section */}
      <Box sx={{ mb: 3, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 900, color: 'text.primary', display: 'flex', alignItems: 'center', gap: 1.5 }}>
            <SettingsIcon sx={{ fontSize: 32, color: 'primary.main' }} />
            {setupType || 'Setup Master'}
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600, ml: 6, mt: -0.5 }}>
            Manage master categories and configuration entries.
          </Typography>
        </Box>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          onClick={() => { setSelectedRecord(null); setIsModalOpen(true); }}
          sx={{
            borderRadius: 2.5,
            px: 3,
            py: 1,
            fontWeight: 800,
            textTransform: 'none',
            boxShadow: '0 4px 12px rgba(0,0,0,0.1)'
          }}
        >
          New Item
        </Button>
      </Box>

      {/* Filter Bar */}
      <Paper sx={{ p: 1.5, mb: 4, borderRadius: 3, display: 'flex', gap: 2, alignItems: 'center', border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField
          width="350px"
          value={search}
          onChange={setSearch}
          placeholder="Search configuration..."
        />
        <Divider orientation="vertical" flexItem />
        <TextField
          select
          size="small"
          value={filterActive}
          onChange={(e) => setFilterActive(e.target.value as any)}
          sx={{ minWidth: 140, '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
        >
          <MenuItem value="all">All Status</MenuItem>
          <MenuItem value="true">Active Only</MenuItem>
          <MenuItem value="false">Inactive Only</MenuItem>
        </TextField>
      </Paper>

      {/* Content Grid */}
      {isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', p: 10 }}>
          <CircularProgress size={40} thickness={4} />
        </Box>
      ) : (
        <Box sx={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
          {distinctTypes.map((type) => (
            <Box key={type}>
              <Box sx={{ display: 'flex', alignItems: 'center', gap: 2, mb: 2.5, pl: 0.5 }}>
                <Typography variant="subtitle1" sx={{ fontWeight: 900, letterSpacing: '0.05em', color: 'text.primary', textTransform: 'uppercase' }}>
                  {type}
                </Typography>
                <Chip
                  label={groupedData[type].length}
                  size="small"
                  sx={{ fontWeight: 800, backgroundColor: 'action.selected', fontSize: '0.7rem' }}
                />
                <Divider sx={{ flex: 1, opacity: 0.5 }} />
              </Box>
              <Grid container spacing={2}>
                {groupedData[type].map((item) => (
                  <Grid size={{ xs: 12, sm: 6, md: 4, lg: 3, xl: 2.4 }} key={item.setupId}>
                    <Card
                      sx={{
                        borderRadius: 3,
                        border: '1px solid',
                        borderColor: 'divider',
                        boxShadow: 'none',
                        transition: 'all 0.2s',
                        '&:hover': {
                          borderColor: 'primary.main',
                          transform: 'translateY(-2px)',
                          boxShadow: '0 8px 24px rgba(0,0,0,0.05)'
                        }
                      }}
                    >
                      <CardContent sx={{ p: 2 }}>
                        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 1.5 }}>
                          <Box sx={{ display: 'flex', alignItems: 'center', gap: 1 }}>
                            <Avatar sx={{ width: 28, height: 28, bgcolor: item.isActive ? 'primary.light' : 'action.disabled', fontSize: '0.8rem', fontWeight: 800 }}>
                              {item.setupName.charAt(0).toUpperCase()}
                            </Avatar>
                            <Typography variant="body2" sx={{ fontWeight: 800, color: 'text.primary' }}>
                              {item.setupCode || 'NO-CODE'}
                            </Typography>
                          </Box>
                          <Tooltip title="Edit">
                            <IconButton
                              size="small"
                              onClick={() => { setSelectedRecord(item); setIsModalOpen(true); }}
                              sx={{ mt: -0.5, mr: -0.5, color: 'text.secondary' }}
                            >
                              <EditIcon fontSize="small" />
                            </IconButton>
                          </Tooltip>
                        </Box>

                        <Typography variant="subtitle2" sx={{ fontWeight: 700, mb: 0.5, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {item.setupName}
                        </Typography>

                        <Typography variant="caption" sx={{ color: 'text.secondary', mb: 2, height: 32, overflow: 'hidden', textOverflow: 'ellipsis', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' }}>
                          {item.description || 'No description available for this item.'}
                        </Typography>

                        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                          <Chip
                            icon={item.isActive ? <ActiveIcon sx={{ fontSize: '12px !important' }} /> : <InactiveIcon sx={{ fontSize: '12px !important' }} />}
                            label={item.isActive ? 'Active' : 'Disabled'}
                            size="small"
                            color={item.isActive ? 'success' : 'default'}
                            sx={{ height: 20, fontSize: '0.65rem', fontWeight: 800, borderRadius: 1.5 }}
                          />
                          <Typography variant="caption" sx={{ fontSize: '0.6rem', color: 'text.disabled', fontWeight: 600 }}>
                            ID: #{item.setupId}
                          </Typography>
                        </Box>
                      </CardContent>
                    </Card>
                  </Grid>
                ))}
              </Grid>
            </Box>
          ))}
          {distinctTypes.length === 0 && (
            <Box sx={{ textAlign: 'center', py: 10 }}>
              <LayersIcon sx={{ fontSize: 64, color: 'action.disabled', mb: 2 }} />
              <Typography variant="h6" color="text.secondary" sx={{ fontWeight: 700 }}>
                No items match your search.
              </Typography>
            </Box>
          )}
        </Box>
      )}

      {/* Standard Setup Dialog */}
      <Dialog
        open={isModalOpen}
        onClose={() => setIsModalOpen(false)}
        fullWidth
        maxWidth="sm"
      >
        <DialogTitle sx={{ fontWeight: 'bold' }}>
          {selectedRecord ? 'Update Setup Record' : 'Create New Setup Record'}
        </DialogTitle>
        <DialogContent dividers>
          <Grid container spacing={2} sx={{ mt: 0.5 }}>
            <Grid size={{ xs: 12, md: 6 }}>
              {isNewType || !selectedRecord ? (
                isNewType ? (
                  <TextField
                    fullWidth
                    label="New Setup Type"
                    variant="outlined"
                    value={formData.setupType}
                    onChange={(e) => setFormData({ ...formData, setupType: e.target.value.toUpperCase() })}
                    required
                    placeholder="E.G. CURRENCY"
                  />
                ) : (
                  <TextField
                    select
                    fullWidth
                    label="Setup Type"
                    variant="outlined"
                    value={formData.setupType}
                    onChange={(e) => setFormData({ ...formData, setupType: e.target.value })}
                    required
                  >
                    {distinctTypes.map((type) => (
                      <MenuItem key={type} value={type}>
                        {type}
                      </MenuItem>
                    ))}
                    {distinctTypes.length === 0 && <MenuItem disabled>No types available</MenuItem>}
                  </TextField>
                )
              ) : (
                <TextField
                  fullWidth
                  label="Setup Type"
                  variant="outlined"
                  value={formData.setupType}
                  disabled
                />
              )}
            </Grid>
            {!selectedRecord && (
              <Grid size={{ xs: 12, md: 6 }} sx={{ display: 'flex', alignItems: 'center' }}>
                <FormControlLabel
                  control={<Switch checked={isNewType} onChange={(e) => setIsNewType(e.target.checked)} />}
                  label="Enter New Type?"
                />
              </Grid>
            )}
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                label="Setup Code"
                variant="outlined"
                value={formData.setupCode}
                onChange={(e) => setFormData({ ...formData, setupCode: e.target.value })}
              />
            </Grid>
            <Grid size={{ xs: 12, md: 6 }}>
              <TextField
                fullWidth
                label="Setup Name"
                variant="outlined"
                value={formData.setupName}
                onChange={(e) => setFormData({ ...formData, setupName: e.target.value })}
                required
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <TextField
                fullWidth
                label="Description"
                variant="outlined"
                multiline
                rows={3}
                value={formData.description}
                onChange={(e) => setFormData({ ...formData, description: e.target.value })}
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <FormControlLabel
                control={
                  <Switch
                    checked={formData.isActive}
                    onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })}
                    color="primary"
                  />
                }
                label="Is Active"
              />
            </Grid>
          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit">
            Cancel
          </Button>
          <Button
            onClick={handleSave}
            variant="contained"
            color="primary"
            disabled={createMutation.isPending || updateMutation.isPending}
          >
            {selectedRecord ? 'Update' : 'Create'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default SetupMaster;
