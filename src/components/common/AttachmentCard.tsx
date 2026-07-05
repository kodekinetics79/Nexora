import React from 'react';
import {
  Box,
  Button,
  IconButton,
  Paper,
  Stack,
  Typography,
  alpha,
  useTheme,
} from '@mui/material';
import {
  AttachFile as AttachIcon,
  Download as DownloadIcon,
  InsertDriveFile as FileIcon,
} from '@mui/icons-material';
import EmptyState from './EmptyState';

export interface AttachmentItem {
  id?: string | number;
  name: string;
  size?: string;
  type?: string;
  onDownload?: () => void;
}

interface AttachmentCardProps {
  title?: React.ReactNode;
  subtitle?: React.ReactNode;
  attachments?: AttachmentItem[];
  onViewAll?: () => void;
}

const AttachmentCard: React.FC<AttachmentCardProps> = ({
  title = 'Attachments',
  subtitle,
  attachments = [],
  onViewAll,
}) => {
  const theme = useTheme();

  return (
    <Paper sx={{ p: 2.5 }}>
      <Stack direction="row" spacing={1.25} sx={{ alignItems: 'center', mb: 2 }}>
        <Box
          sx={{
            width: 36,
            height: 36,
            borderRadius: 2,
            display: 'grid',
            placeItems: 'center',
            color: 'primary.main',
            bgcolor: alpha(theme.palette.primary.main, 0.09),
            border: `1px solid ${alpha(theme.palette.primary.main, 0.14)}`,
          }}
        >
          <AttachIcon sx={{ fontSize: 19 }} />
        </Box>
        <Box sx={{ minWidth: 0 }}>
          <Typography variant="subtitle1" sx={{ fontWeight: 900 }}>
            {title}
          </Typography>
          {subtitle ? (
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 650 }}>
              {subtitle}
            </Typography>
          ) : null}
        </Box>
      </Stack>

      {attachments.length > 0 ? (
        <Stack spacing={1.25}>
          {attachments.map((attachment, index) => (
            <Stack
              key={attachment.id || `${attachment.name}-${index}`}
              direction="row"
              spacing={1.25}
              sx={{
                alignItems: 'center',
                p: 1,
                borderRadius: 2,
                border: '1px solid',
                borderColor: 'divider',
                bgcolor: 'background.default',
              }}
            >
              <Box
                sx={{
                  width: 34,
                  height: 34,
                  borderRadius: 1.5,
                  display: 'grid',
                  placeItems: 'center',
                  bgcolor: alpha(theme.palette.primary.main, 0.08),
                  color: 'primary.main',
                  flexShrink: 0,
                }}
              >
                <FileIcon sx={{ fontSize: 18 }} />
              </Box>
              <Box sx={{ minWidth: 0, flex: 1 }}>
                <Typography variant="body2" sx={{ fontWeight: 850 }} noWrap>
                  {attachment.name}
                </Typography>
                {attachment.size || attachment.type ? (
                  <Typography variant="caption" color="text.secondary">
                    {[attachment.type, attachment.size].filter(Boolean).join(' / ')}
                  </Typography>
                ) : null}
              </Box>
              <IconButton size="small" onClick={attachment.onDownload} disabled={!attachment.onDownload}>
                <DownloadIcon sx={{ fontSize: 18 }} />
              </IconButton>
            </Stack>
          ))}
          {onViewAll ? (
            <Button fullWidth variant="outlined" onClick={onViewAll}>
              View All Attachments
            </Button>
          ) : null}
        </Stack>
      ) : (
        <EmptyState title="No attachments" message="No files are attached to this record yet." />
      )}
    </Paper>
  );
};

export default AttachmentCard;
