import React, { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Stack,
  CircularProgress, Alert, Tabs, Tab,
  IconButton, List, ListItem, ListItemIcon, ListItemText,
} from '@mui/material';
import {
  PlayCircle as ProcessIcon,
  Delete as DeleteIcon,
  Description as DocIcon,
  Info as InfoIcon,
  Inbox as InboxIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import { useSnackbar } from 'notistack';

const FolderUploadLeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const { enqueueSnackbar } = useSnackbar();
  const [tabValue, setTabValue] = useState(0); // 0 = SEC (Customer 1), 1 = Aramco (Customer 2)
  const [files, setFiles] = useState<File[]>([]);
  const [processing, setProcessing] = useState(false);

  const customers = [
    { name: 'Customer 1', type: 'SEC', info: 'Select extracted .doc files for Customer 1 leads.' },
    { name: 'Customer 2', type: 'Aramco', info: 'Select modern .docx RFQ documents for Customer 2 leads.' },
  ];

  const activeCustomer = customers[tabValue];

  const processMutation = useMutation({
    mutationFn: () => leadService.processAllFolderLeads(),
    onSuccess: (data) => {
      enqueueSnackbar(data.message || 'System folder processing started!', { variant: 'success' });
    },
    onError: (err: any) => {
      enqueueSnackbar(err.response?.data?.message || 'Processing failed', { variant: 'error' });
    },
    onSettled: () => setProcessing(false),
  });

  const uploadMutation = useMutation({
    mutationFn: (data: { fd: FormData, type: string }) => leadService.uploadToFolder(data.fd, data.type),
    onSuccess: (_data, variables) => {
      enqueueSnackbar(`Successfully uploaded files to ${variables.type} folder!`, { variant: 'success' });
      setFiles([]);
    },
    onError: (err: any) => {
      enqueueSnackbar(err.response?.data?.message || 'Upload failed', { variant: 'error' });
    },
    onSettled: () => setProcessing(false),
  });

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) {
      setFiles(prev => [...prev, ...Array.from(e.target.files!)]);
    }
  };

  const removeFile = (index: number) => {
    setFiles(prev => prev.filter((_, i) => i !== index));
  };

  const handleUpload = () => {
    if (files.length === 0) return;
    setProcessing(true);
    const fd = new FormData();
    files.forEach(f => fd.append('files', f));
    uploadMutation.mutate({ fd, type: activeCustomer.type });
  };

  const handleProcessAll = () => {
    setProcessing(true);
    processMutation.mutate();
  };

  return (
    <Box sx={{ maxWidth: 1200, mx: 'auto', py: 3, px: 3 }}>
      {/* Header */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Typography variant="h5" sx={{ fontWeight: 800 }}>
          {t('upload_folder_leads') || 'Upload Folder Lead'}
        </Typography>
        <Button
          variant="contained"
          color="success"
          onClick={handleProcessAll}
          disabled={processing}
          startIcon={processing ? <CircularProgress size={20} color="inherit" /> : <ProcessIcon />}
          sx={{
            px: 3, fontWeight: 700, borderRadius: 2, height: 42, textTransform: 'none',
            bgcolor: '#10b981', '&:hover': { bgcolor: '#059669' }
          }}
        >
          Process Folder Leads
        </Button>
      </Stack>

      <Paper sx={{ p: 0, borderRadius: 3, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
        {/* Tabs */}
        <Box sx={{ borderBottom: 1, borderColor: 'divider', px: 2, pt: 1, bgcolor: '#f9fafb' }}>
          <Tabs
            value={tabValue}
            onChange={(_, val) => { setTabValue(val); setFiles([]); }}
            sx={{
              '& .MuiTab-root': {
                textTransform: 'none',
                fontWeight: 600,
                fontSize: '0.875rem',
                minWidth: 100,
                borderRadius: '8px 8px 0 0',
                mx: 0.5,
                '&.Mui-selected': { color: 'primary.main', bgcolor: 'white' }
              },
              '& .MuiTabs-indicator': { display: 'none' }
            }}
          >
            <Tab label="Customer 1" />
            <Tab label="Customer 2" />
          </Tabs>
        </Box>

        <Box sx={{ p: 4 }}>
          {/* Info Alert */}
          <Alert
            icon={<InfoIcon fontSize="inherit" />}
            severity="info"
            sx={{
              mb: 4,
              borderRadius: 2,
              bgcolor: 'rgba(225, 29, 46, 0.06)',
              color: 'text.primary',
              border: '1px solid rgba(225, 29, 46, 0.16)',
              '& .MuiAlert-icon': { color: 'primary.main' }
            }}
          >
            <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Upload Leads for {activeCustomer.name}</Typography>
            <Typography variant="caption">{activeCustomer.info}</Typography>
          </Alert>

          {/* Upload Area */}
          <Paper
            component="label"
            sx={{
              p: 6,
              display: 'flex',
              flexDirection: 'column',
              alignItems: 'center',
              justifyContent: 'center',
              border: '1px dashed',
              borderColor: '#94a3b8',
              borderRadius: 2,
              bgcolor: 'white',
              cursor: 'pointer',
              transition: 'all 0.2s',
              '&:hover': { borderColor: 'primary.main', bgcolor: '#f8fafc' },
              mb: 4
            }}
          >
            <input type="file" multiple hidden onChange={handleFileChange} />
            <InboxIcon sx={{ fontSize: 48, color: '#1e293b', mb: 2 }} />
            <Typography variant="body1" sx={{ fontWeight: 600, color: '#1e293b' }}>
              Click or drag files to this area to select
            </Typography>
            <Typography variant="caption" color="text.secondary">
              Support for a single or bulk upload. Maximum size: 25MB.
            </Typography>
          </Paper>

          {/* File Queue */}
          {files.length > 0 && (
            <List sx={{ mb: 4, border: '1px solid', borderColor: 'divider', borderRadius: 2 }}>
              {files.map((file, i) => (
                <ListItem
                  key={i}
                  divider={i < files.length - 1}
                  secondaryAction={
                    <IconButton edge="end" onClick={() => removeFile(i)} disabled={processing}>
                      <DeleteIcon fontSize="small" color="error" />
                    </IconButton>
                  }
                >
                  <ListItemIcon><DocIcon color="primary" /></ListItemIcon>
                  <ListItemText
                    primary={file.name}
                    secondary={`${(file.size / 1024).toFixed(1)} KB`}
                    slotProps={{ primary: { sx: { fontWeight: 600, fontSize: '0.875rem' } } }}
                  />
                </ListItem>
              ))}
            </List>
          )}

          {/* Footer Action */}
          <Button
            fullWidth
            variant="contained"
            disabled={files.length === 0 || processing}
            onClick={handleUpload}
            sx={{
              height: 48,
              fontWeight: 700,
              borderRadius: 2,
              textTransform: 'none',
              bgcolor: '#f1f5f9',
              color: files.length > 0 ? '#1e293b' : '#94a3b8',
              boxShadow: 'none',
              '&:hover': { bgcolor: files.length > 0 ? '#e2e8f0' : '#f1f5f9' },
              '&.Mui-disabled': { bgcolor: '#f1f5f9', color: '#cbd5e1', cursor: 'not-allowed' }
            }}
          >
            {processing ? <CircularProgress size={24} sx={{ mr: 1 }} /> : null}
            Save to {activeCustomer.name} Folder
          </Button>
        </Box>
      </Paper>
    </Box>
  );
};

export default FolderUploadLeadsPage;
