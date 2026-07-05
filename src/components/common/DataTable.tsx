import React from 'react';
import { Box, Paper, Typography, type SxProps, type Theme } from '@mui/material';
import { DataGrid, type DataGridProps } from '@mui/x-data-grid';
import { InboxOutlined } from '@mui/icons-material';

interface DataTableProps extends DataGridProps {
  height?: number | string;
  sxContainer?: SxProps<Theme>;
}

const DataTable: React.FC<DataTableProps> = ({ height = 'auto', sxContainer, sx, ...props }) => (
  <Paper
    sx={{
      height,
      width: '100%',
      overflow: 'hidden',
      display: 'flex',
      flexDirection: 'column',
      ...sxContainer,
    }}
  >
    <DataGrid
      autoHeight
      disableRowSelectionOnClick
      pageSizeOptions={[10, 25, 50]}
      slots={{
        noRowsOverlay: () => (
          <Box
            sx={{
              height: '100%',
              minHeight: 220,
              display: 'flex',
              flexDirection: 'column',
              alignItems: 'center',
              justifyContent: 'center',
              textAlign: 'center',
              color: 'text.secondary',
              gap: 1,
            }}
          >
            <InboxOutlined sx={{ fontSize: 44, opacity: 0.55 }} />
            <Typography variant="subtitle2" color="text.primary">
              No records found
            </Typography>
            <Typography variant="body2" sx={{ maxWidth: 360 }}>
              Try a different search, adjust filters, or create a new record.
            </Typography>
          </Box>
        ),
        ...props.slots,
      }}
      {...props}
      sx={{
        flex: height === 'auto' ? 'initial' : 1,
        minHeight: props.rows?.length ? 0 : 260,
        '& .MuiDataGrid-virtualScroller': {
          minHeight: props.rows?.length ? 0 : 160,
        },
        '& .MuiDataGrid-cell:focus, & .MuiDataGrid-columnHeader:focus': {
          outline: 'none',
        },
        ...sx,
      }}
    />
  </Paper>
);

export default DataTable;
