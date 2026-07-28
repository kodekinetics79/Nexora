import React, { useRef, useState } from 'react';
import { useMutation } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router-dom';
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

const MAX_FILE_BYTES = 25 * 1024 * 1024;
const MAX_BATCH_BYTES = 200 * 1024 * 1024;
const MAX_FILES = 50;
const SUPPORTED_EXTENSIONS = [
  '.pdf', '.doc', '.docx', '.xls', '.xlsx', '.csv', '.txt',
  '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.tif', '.tiff', '.webp',
];
const ACCEPTED_FILE_TYPES = SUPPORTED_EXTENSIONS.join(',');

const extensionOf = (name: string): string => {
  const separator = name.lastIndexOf('.');
  return separator >= 0 ? name.slice(separator).toLowerCase() : '';
};

const ManualUploadLeadsPage: React.FC = () => {
  const { t } = useTranslation();
  const { enqueueSnackbar } = useSnackbar();
  const navigate = useNavigate();
  const inputRef = useRef<HTMLInputElement>(null);
  const [files, setFiles] = useState<File[]>([]);
  const [uploading, setUploading] = useState(false);
  const [selectionError, setSelectionError] = useState<string | null>(null);

  const uploadMutation = useMutation({
    mutationFn: (fd: FormData) => leadService.uploadGoverned(fd),
    onSuccess: (result) => {
      const stopped = result.jobs.filter((job) => ['Skipped', 'Rejected', 'Quarantined', 'Error'].includes(job.outcome));
      enqueueSnackbar(
        stopped.length === 0
          ? 'Documents queued for ingestion and reconciliation.'
          : `${stopped.length} document${stopped.length === 1 ? '' : 's'} need attention. Opened the batch outcomes.`,
        { variant: stopped.length === 0 ? 'success' : 'warning' },
      );
      if (result.batchId) {
        setFiles([]);
        navigate(`/procurement/leads/ingestion/${encodeURIComponent(result.batchId)}`);
      } else {
        setSelectionError('The server did not return a batch reference. Your selected files have been retained.');
      }
    },
    onError: (err: any) => {
      enqueueSnackbar(err.response?.data?.message || 'Upload failed', { variant: 'error' });
    },
    onSettled: () => setUploading(false),
  });

  const addFiles = (incoming: File[]) => {
    const combined = [...files, ...incoming];
    const unsupported = incoming.filter((file) => !SUPPORTED_EXTENSIONS.includes(extensionOf(file.name)));
    const oversized = incoming.filter((file) => file.size > MAX_FILE_BYTES);
    const batchTooLarge = combined.reduce((total, file) => total + file.size, 0) > MAX_BATCH_BYTES;
    if (unsupported.length > 0 || oversized.length > 0 || combined.length > MAX_FILES || batchTooLarge) {
      const reasons = [
        unsupported.length > 0 ? `${unsupported.length} unsupported format${unsupported.length === 1 ? '' : 's'}` : null,
        oversized.length > 0 ? `${oversized.length} file${oversized.length === 1 ? '' : 's'} over 25 MB` : null,
        combined.length > MAX_FILES ? `a maximum of ${MAX_FILES} files per batch` : null,
        batchTooLarge ? 'the batch is over 200 MB' : null,
      ].filter(Boolean);
      setSelectionError(`Selection stopped: ${reasons.join(', ')}.`);
      return;
    }

    setSelectionError(null);
    setFiles(combined);
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (e.target.files) addFiles(Array.from(e.target.files));
    e.target.value = '';
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
              bgcolor: '#eff6ff',
              color: '#1e40af',
              border: '1px solid #bfdbfe',
              '& .MuiAlert-icon': { color: '#3b82f6' }
            }}
          >
            <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>Lead ingestion and reconciliation</Typography>
            <Typography variant="caption">
              Upload RFQ documents, spreadsheets, or images. Nexora preserves each source, extracts
              its commercial facts, and checks whether it is new, duplicated, revised, or needs review.
            </Typography>
          </Alert>

          {/* Upload Area */}
          <Paper
            role="button"
            tabIndex={0}
            aria-label="Select RFQ documents"
            onClick={() => inputRef.current?.click()}
            onKeyDown={(event) => {
              if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                inputRef.current?.click();
              }
            }}
            onDragOver={(event) => event.preventDefault()}
            onDrop={(event) => {
              event.preventDefault();
              if (!uploading) addFiles(Array.from(event.dataTransfer.files));
            }}
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
            <input ref={inputRef} type="file" multiple hidden accept={ACCEPTED_FILE_TYPES} onChange={handleFileChange} />
            <InboxIcon sx={{ fontSize: 56, color: '#1e293b', mb: 2 }} />
            <Typography variant="h6" sx={{ fontWeight: 700, color: '#1e293b' }}>
              Click or drag files to this area to select
            </Typography>
            <Typography variant="body2" color="text.secondary">
              PDF, DOC, DOCX, XLS, XLSX, CSV, TXT, and common images. Maximum 25 MB per file.
            </Typography>
          </Paper>

          {selectionError && <Alert severity="warning" sx={{ mb: 3 }}>{selectionError}</Alert>}

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
              {uploading ? 'Queueing documents...' : 'Queue for reconciliation'}
            </Button>
          </Box>
        </Box>
      </Paper>
    </Box>
  );
};

export default ManualUploadLeadsPage;
