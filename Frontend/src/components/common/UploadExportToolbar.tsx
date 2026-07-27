import React, { useRef, useState } from 'react';
import { Button, Box, CircularProgress, Tooltip } from '@mui/material';
import {
  FileDownload as ExportIcon,
  Upload as UploadIcon,
  FileDownload as TemplateIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';

interface Props {
  onDownloadTemplate: () => Promise<any>;
  onUpload: (file: File) => Promise<any>;
  onExport: () => Promise<any>;
  templateFileName?: string;
  exportFileName?: string;
  canUpload?: boolean;
}

const downloadBlob = (data: any, filename: string) => {
  const url = window.URL.createObjectURL(new Blob([data]));
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  window.URL.revokeObjectURL(url);
};

const UploadExportToolbar: React.FC<Props> = ({
  onDownloadTemplate,
  onUpload,
  onExport,
  templateFileName = 'Template.xlsx',
  exportFileName = 'Export.xlsx',
  canUpload = true,
}) => {
  const { enqueueSnackbar } = useSnackbar();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [loading, setLoading] = useState<'template' | 'upload' | 'export' | null>(null);

  const handleTemplate = async () => {
    try {
      setLoading('template');
      const res = await onDownloadTemplate();
      downloadBlob(res.data, templateFileName);
    } catch {
      enqueueSnackbar('Failed to download template', { variant: 'error' });
    } finally {
      setLoading(null);
    }
  };

  const handleExport = async () => {
    try {
      setLoading('export');
      const res = await onExport();
      downloadBlob(res.data, exportFileName);
    } catch {
      enqueueSnackbar('Failed to export data', { variant: 'error' });
    } finally {
      setLoading(null);
    }
  };

  const handleFileChange = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    try {
      setLoading('upload');
      const res = await onUpload(file);
      enqueueSnackbar(res.data?.message || 'Upload successful!', { variant: 'success' });
    } catch (err: any) {
      enqueueSnackbar(err?.response?.data?.message || 'Upload failed', { variant: 'error' });
    } finally {
      setLoading(null);
      if (fileInputRef.current) fileInputRef.current.value = '';
    }
  };

  return (
    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
      <input ref={fileInputRef} type="file" accept=".xlsx,.xls" hidden onChange={handleFileChange} />

      <Tooltip title="Download Excel Template">
        <Button
          size="small"
          variant="outlined"
          startIcon={loading === 'template' ? <CircularProgress size={14} /> : <TemplateIcon />}
          onClick={handleTemplate}
          disabled={!!loading}
          sx={{ textTransform: 'none', fontWeight: 600 }}
        >
          Template
        </Button>
      </Tooltip>

      {canUpload && <Tooltip title="Upload filled template">
        <Button
          size="small"
          variant="outlined"
          color="secondary"
          startIcon={loading === 'upload' ? <CircularProgress size={14} /> : <UploadIcon />}
          onClick={() => fileInputRef.current?.click()}
          disabled={!!loading}
          sx={{ textTransform: 'none', fontWeight: 600 }}
        >
          Import
        </Button>
      </Tooltip>}

      <Tooltip title="Export current data to Excel">
        <Button
          size="small"
          variant="outlined"
          color="success"
          startIcon={loading === 'export' ? <CircularProgress size={14} /> : <ExportIcon />}
          onClick={handleExport}
          disabled={!!loading}
          sx={{ textTransform: 'none', fontWeight: 600 }}
        >
          Export
        </Button>
      </Tooltip>
    </Box>
  );
};

export default UploadExportToolbar;
