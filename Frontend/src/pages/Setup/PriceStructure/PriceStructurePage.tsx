import React, { useState, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, TextField, Chip, IconButton, Dialog,
  DialogTitle, DialogContent, DialogActions, Grid, Switch, FormControlLabel,
  CircularProgress, MenuItem, Card, CardContent, InputAdornment,
  Divider
} from '@mui/material';
import {
  Add as AddIcon,
  Edit as EditIcon,
  LocalAtm as PriceIcon,
  CheckCircle as ActiveIcon,
  Cancel as InactiveIcon
} from '@mui/icons-material';
import setupService from '../../../api/services/setupService';
import type { SetupMasterDTO, SetupMasterCreateDTO, SetupMasterUpdateDTO } from '../../../api/services/setupService';
import { useAuth } from '../../../context/AuthContext';
import SearchField from '../../../components/common/SearchField';
import { toast } from 'react-hot-toast';

const PriceStructurePage: React.FC = () => {
  const { userData } = useAuth();
  const queryClient = useQueryClient();

  const [search, setSearch] = useState('');
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [selectedRecord, setSelectedRecord] = useState<SetupMasterDTO | null>(null);

  const [formData, setFormData] = useState({
    name: '',
    type: 'MARKUP', // MARKUP or DISCOUNT
    value: '0',
    isActive: true,
  });

  const { data: structures, isLoading } = useQuery({
    queryKey: ['price-structures'],
    queryFn: () => setupService.getAll({ setupType: 'PRICE_STRUCTURE', pageSize: 1000 }),
  });

  const filteredData = useMemo(() => {
    if (!structures?.items) return [];
    return structures.items.filter(i =>
      i.setupName.toLowerCase().includes(search.toLowerCase())
    );
  }, [structures, search]);

  const createMutation = useMutation({
    mutationFn: (newRecord: SetupMasterCreateDTO) => setupService.create(newRecord),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['price-structures'] });
      toast.success('Price structure created');
      setIsModalOpen(false);
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: number; data: SetupMasterUpdateDTO }) => setupService.update(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['price-structures'] });
      toast.success('Price structure updated');
      setIsModalOpen(false);
    },
  });

  const handleEdit = (record: SetupMasterDTO) => {
    setSelectedRecord(record);
    setFormData({
      name: record.setupName,
      type: record.setupCode === 'D' ? 'DISCOUNT' : 'MARKUP',
      value: record.description || '0',
      isActive: record.isActive,
    });
    setIsModalOpen(true);
  };

  const handleSave = () => {
    const payload = {
      setupType: 'PRICE_STRUCTURE',
      setupName: formData.name,
      setupCode: formData.type === 'DISCOUNT' ? 'D' : 'M',
      description: formData.value,
      isActive: formData.isActive,
    };

    if (selectedRecord) {
      updateMutation.mutate({
        id: selectedRecord.setupId,
        data: { ...payload, modifiedBy: userData?.userName || 'system' }
      });
    } else {
      createMutation.mutate({
        ...payload,
        createdBy: userData?.userName || 'system'
      });
    }
  };

  return (
    <Box sx={{ p: 3, bgcolor: '#f8f9fa', minHeight: '100vh' }}>
      <Box sx={{ mb: 4, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 950, color: '#1a237e', display: 'flex', alignItems: 'center', gap: 2 }}>
            <PriceIcon sx={{ fontSize: 32 }} />
            Price Structure Module
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600, mt: 0.5 }}>
            Define global markup and discount rules for procurement and sales.
          </Typography>
        </Box>
        <Button
          variant="contained"
          startIcon={<AddIcon />}
          onClick={() => {
            setSelectedRecord(null);
            setFormData({ name: '', type: 'MARKUP', value: '0', isActive: true });
            setIsModalOpen(true);
          }}
          sx={{ borderRadius: 2, px: 3, fontWeight: 800, textTransform: 'none' }}
        >
          Add New Structure
        </Button>
      </Box>

      <Paper sx={{ p: 2, mb: 4, borderRadius: 3, display: 'flex', gap: 2, alignItems: 'center', boxShadow: 'none', border: '1px solid #eee' }}>
        <SearchField
          width="400px"
          value={search}
          onChange={setSearch}
          placeholder="Search price structures..."
        />
      </Paper>

      {isLoading ? (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 10 }}><CircularProgress /></Box>
      ) : (
        <Grid container spacing={3}>
          {filteredData.map((item) => (
            <Grid size={{ xs: 12, sm: 6, md: 4, lg: 3 }} key={item.setupId}>
              <Card sx={{
                borderRadius: 4,
                border: '1px solid #eee',
                boxShadow: 'none',
                transition: 'all 0.2s',
                '&:hover': { transform: 'translateY(-4px)', boxShadow: '0 12px 24px rgba(0,0,0,0.05)', borderColor: '#1a237e' }
              }}>
                <CardContent sx={{ p: 3 }}>
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', mb: 2 }}>
                    <Chip
                      label={item.setupCode === 'D' ? 'Discount' : 'Markup'}
                      size="small"
                      color={item.setupCode === 'D' ? 'error' : 'success'}
                      sx={{ fontWeight: 900, borderRadius: 1.5, fontSize: '0.65rem' }}
                    />
                    <IconButton size="small" onClick={() => handleEdit(item)}><EditIcon fontSize="small" /></IconButton>
                  </Box>
                  <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>{item.setupName}</Typography>
                  <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 0.5 }}>
                    <Typography variant="h4" sx={{ fontWeight: 950, color: item.setupCode === 'D' ? '#d32f2f' : '#2e7d32' }}>
                      {item.description}%
                    </Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>{item.setupCode === 'D' ? 'OFF' : 'ADD-ON'}</Typography>
                  </Box>
                  <Divider sx={{ my: 2 }} />
                  <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <Chip
                      icon={item.isActive ? <ActiveIcon /> : <InactiveIcon />}
                      label={item.isActive ? 'Active' : 'Inactive'}
                      size="small"
                      variant="outlined"
                      sx={{ height: 24, fontSize: '0.7rem', fontWeight: 700 }}
                    />
                    <Typography variant="caption" color="text.disabled">ID: #{item.setupId}</Typography>
                  </Box>
                </CardContent>
              </Card>
            </Grid>
          ))}
          {filteredData.length === 0 && (
            <Grid size={{ xs: 12 }}>
              <Box sx={{ textAlign: 'center', py: 8, bgcolor: 'white', borderRadius: 4, border: '1px dashed #ccc' }}>
                <Typography color="text.secondary" sx={{ fontWeight: 700 }}>No price structures found.</Typography>
              </Box>
            </Grid>
          )}
        </Grid>
      )}

      <Dialog open={isModalOpen} onClose={() => setIsModalOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle sx={{ fontWeight: 900 }}>{selectedRecord ? 'Edit Price Structure' : 'New Price Structure'}</DialogTitle>
        <DialogContent dividers>
          <Grid container spacing={3} sx={{ mt: 0.5 }}>
            <Grid size={{ xs: 12 }}>
              <TextField
                fullWidth label="Structure Name"
                placeholder="e.g. Standard Retail Markup"
                value={formData.name}
                onChange={(e) => setFormData({ ...formData, name: e.target.value })}
              />
            </Grid>
            <Grid size={{ xs: 6 }}>
              <TextField
                select fullWidth label="Type"
                value={formData.type}
                onChange={(e) => setFormData({ ...formData, type: e.target.value })}
              >
                <MenuItem value="MARKUP">Markup (+)</MenuItem>
                <MenuItem value="DISCOUNT">Discount (-)</MenuItem>
              </TextField>
            </Grid>
            <Grid size={{ xs: 6 }}>
              <TextField
                fullWidth label="Value (%)"
                type="number"
                value={formData.value}
                onChange={(e) => setFormData({ ...formData, value: e.target.value })}
                slotProps={{
                  input: {
                    endAdornment: <InputAdornment position="end">%</InputAdornment>,
                  }
                }}
              />
            </Grid>
            <Grid size={{ xs: 12 }}>
              <FormControlLabel
                control={<Switch checked={formData.isActive} onChange={(e) => setFormData({ ...formData, isActive: e.target.checked })} />}
                label="Is Active"
              />
            </Grid>
          </Grid>
        </DialogContent>
        <DialogActions sx={{ p: 2 }}>
          <Button onClick={() => setIsModalOpen(false)} color="inherit" sx={{ fontWeight: 700 }}>Cancel</Button>
          <Button onClick={handleSave} variant="contained" sx={{ fontWeight: 800, px: 3 }}>
            {selectedRecord ? 'Update Structure' : 'Create Structure'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default PriceStructurePage;
