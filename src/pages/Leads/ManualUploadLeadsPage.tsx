import React, { useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, Stack,
  CircularProgress, Alert, IconButton, List,
  ListItem, ListItemIcon, ListItemText,
} from '@mui/material';
import {
  AutoAwesome as AIIcon,
  Delete as DeleteIcon,
  Description as DocIcon,
  Info as InfoIcon,
  Inbox as InboxIcon,
} from '@mui/icons-material';
import leadService from '../../api/services/leadService';
import { useSnackbar } from 'notistack';

const ManualUploadLeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const { enqueueSnackbar } = useSnackbar();
  const [files, setFiles] = useState<File[]>([]);
  const [uploading, setUploading] = useState(false);

  const uploadMutation = useMutation({
    mutationFn: (fd: FormData) => leadService.uploadManual(fd),
    onSuccess: () => {
      enqueueSnackbar('Lead documents processed successfully by AI engine!', { variant: 'success' });
      setFiles([]);
    },
    onError: (err: any) => {
      enqueueSnackbar(err.response?.data?.message || 'Upload failed', { variant: 'error' });
    },
    onSettled: () => setUploading(false),
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
    setUploading(true);
    const fd = new FormData();
    files.forEach(f => fd.append('files', f));
    uploadMutation.mutate(fd);
  };

  return (
    <Box sx={{ maxWidth: 1000, mx: 'auto', py: 3, px: 3 }}>
      {/* Header */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Typography variant="h5" sx={{ fontWeight: 800 }}>
          {t('manual_upload') || 'Manual Lead Upload'}
        </Typography>
      </Stack>

      <Paper sx={{ p: 0, borderRadius: 3, border: '1px solid', borderColor: 'divider', overflow: 'hidden' }}>
        {/* Banner Section */}
        <Box sx={{ px: 4, pt: 4 }}>
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
            <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>AI-Powered Lead Extraction</Typography>
            <Typography variant="caption">
              Upload RFQ documents, spreadsheets, or images. Our engine will automatically extract
              technical specifications and buyer details.
            </Typography>
          </Alert>

          {/* Upload Area */}
          <Paper
            component="label"
            sx={{
              p: 8,
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
            <InboxIcon sx={{ fontSize: 56, color: '#1e293b', mb: 2 }} />
            <Typography variant="h6" sx={{ fontWeight: 700, color: '#1e293b' }}>
              Click or drag files to this area to select
            </Typography>
            <Typography variant="body2" color="text.secondary">
              Support for PDF, DOCX, XLSX, and Images. Maximum size: 25MB per file.
            </Typography>
          </Paper>

          {/* File Queue Preview */}
          {files.length > 0 && (
            <List sx={{ mb: 4, border: '1px solid', borderColor: 'divider', borderRadius: 2 }}>
              {files.map((file, i) => (
                <ListItem
                  key={i}
                  divider={i < files.length - 1}
                  secondaryAction={
                    <IconButton edge="end" onClick={() => removeFile(i)} disabled={uploading}>
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
          <Box sx={{ pb: 4 }}>
            <Button
              fullWidth
              variant="contained"
              disabled={files.length === 0 || uploading}
              onClick={handleUpload}
              sx={{
                height: 52,
                fontWeight: 700,
                borderRadius: 2,
                textTransform: 'none',
                bgcolor: files.length > 0 ? 'primary.main' : '#f1f5f9',
                color: files.length > 0 ? 'white' : '#94a3b8',
                boxShadow: files.length > 0 ? '0 4px 12px rgba(25, 118, 210, 0.2)' : 'none',
                '&:hover': { bgcolor: files.length > 0 ? 'primary.dark' : '#f1f5f9' },
                '&.Mui-disabled': { bgcolor: '#f1f5f9', color: '#cbd5e1', cursor: 'not-allowed' }
              }}
              startIcon={uploading ? <CircularProgress size={20} color="inherit" /> : <AIIcon />}
            >
              {uploading ? 'Analyzing Documents...' : 'Analyze & Process Leads'}
            </Button>
          </Box>
        </Box>
      </Paper>
    </Box>
  );
};

export default ManualUploadLeadsPage;
