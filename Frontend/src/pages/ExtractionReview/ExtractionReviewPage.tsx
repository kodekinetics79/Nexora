import React, { useState, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box, Typography, Paper, Button, IconButton, Tooltip, Alert,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel,
} from '@mui/x-data-grid';
import {
  ChevronRight as ChevronIcon,
  Refresh as RefreshIcon,
  TaskAlt as CaughtUpIcon,
} from '@mui/icons-material';
import extractionReviewService from '../../api/services/extractionReviewService';
import type { NeedsReviewItem } from '../../api/services/extractionReviewService';
import operationalReadinessService from '../../api/services/operationalReadinessService';
import SearchField from '../../components/common/SearchField';
import InboxFrame, { INBOX_CARD_SX } from '../Inbox/InboxFrame';
import { useAuth } from '../../context/AuthContext';
import { formatDateSafe, formatRelativeReceived } from '../../utils/dates';


// This queue used to render an extraction-confidence percentage per row in
// red/amber/green. That number was never measured — on the structured path it
// is a literal (1.0 parsed / 0.2 not / 0 blank), on the model path it is the
// model's own self-report against a rubric in its own prompt — so it is no
// longer shown anywhere. Every document in this queue needs a human look; what
// the reviewer needs from the list is which one to open next, not a score.

const ExtractionReviewPage: React.FC = () => {
  const navigate = useNavigate();
  const { userData, hasPermission } = useAuth();
  const canViewTenantOperations = hasPermission('Users', 'view');
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 50, page: 0 });
  const [search, setSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');

  // Debounce the search box so keystrokes don't fire a request each.
  useEffect(() => {
    const handle = setTimeout(() => {
      setDebouncedSearch(search);
      setPaginationModel((prev) => ({ ...prev, page: 0 }));
    }, 400);
    return () => clearTimeout(handle);
  }, [search]);

  const { data, isLoading, isError, refetch, isFetching } = useQuery({
    queryKey: ['needs-review', paginationModel, debouncedSearch],
    queryFn: () => extractionReviewService.getNeedsReview({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: debouncedSearch || undefined,
    }),
  });

  // This is intentionally a separate read from the review queue. A dead-letter job stopped
  // before a reviewable Lead existed, whereas this page lists Leads that persisted successfully
  // but need a person's validation. Showing the operations count here prevents an empty review
  // queue from falsely suggesting that extraction itself has no outstanding exceptions. The
  // query is permission-gated so ordinary reviewers neither receive a forbidden request nor learn
  // tenant-operational counts they are not authorized to see.
  const operationsReadiness = useQuery({
    queryKey: ['tenant-operational-readiness', userData.businessUnitId],
    queryFn: operationalReadinessService.get,
    enabled: canViewTenantOperations,
  });

  const totalCount = data?.totalCount ?? 0;
  const extractionDeadLetterCount = operationsReadiness.data?.queues
    .find(queue => queue.key === 'extraction')?.deadLetter ?? 0;

  // Through the shared utility: an offset-less server timestamp is UTC, and a received date is
  // never "in 4 hours" — clock skew reads as "just now".
  const formatRelative = (dateStr: string | null) => formatRelativeReceived(dateStr);

  const openReview = (row: NeedsReviewItem) =>
    navigate(`/procurement/extraction/review/${row.id}`, { state: { reviewReason: row.reviewReason } });

  const columns: GridColDef<NeedsReviewItem>[] = [
    {
      field: 'rfqno',
      headerName: 'RFQ #',
      width: 170,
      renderCell: (p) => (
        <Typography noWrap title={p.row.rfqno ?? ''} sx={{ fontWeight: 700, fontSize: '0.9rem', fontVariantNumeric: 'tabular-nums', color: p.row.rfqno ? 'text.primary' : 'text.disabled' }}>
          {p.row.rfqno || 'No RFQ/Bid # yet'}
        </Typography>
      ),
    },
    {
      field: 'buyersName',
      headerName: 'Buyer',
      flex: 1,
      minWidth: 180,
      renderCell: (p) => (
        <Typography noWrap sx={{ fontSize: '0.875rem' }}>
          {p.row.buyersName || 'Buyer not read yet'}
        </Typography>
      ),
    },
    {
      field: 'leadSource',
      headerName: 'Came by',
      width: 110,
      renderCell: (p) => (
        <Typography sx={{ fontSize: '0.85rem', color: 'text.secondary' }}>
          {p.row.leadSource === 'ManualUpload' ? 'Upload' : p.row.leadSource === 'BulkUpload' ? 'Bulk upload' : p.row.leadSource || '—'}
        </Typography>
      ),
    },
    {
      field: 'receivedOn',
      headerName: 'Received',
      width: 150,
      renderCell: (p) => {
        const raw = p.row.receivedOn ?? p.row.recDate;
        return (
          <Tooltip title={formatDateSafe(raw)}>
            <Typography sx={{ fontSize: '0.8rem', fontWeight: 600, color: 'text.secondary' }}>
              {formatRelative(raw)}
            </Typography>
          </Tooltip>
        );
      },
    },
    {
      field: 'itemCount',
      headerName: 'Lines to check',
      width: 130,
      align: 'left',
      headerAlign: 'left',
      renderCell: (p) => {
        const total = p.row.itemCount ?? 0;
        // Rendered only when the backend computes it; never inferred here.
        const needing = p.row.linesNeedingCheck;
        return (
          <Typography className="tabular-nums" sx={{ fontSize: '0.85rem', fontWeight: 700 }}>
            {needing == null
              ? `${total} line${total === 1 ? '' : 's'}`
              : `${needing} of ${total}`}
          </Typography>
        );
      },
    },
    {
      field: 'reviewReason',
      headerName: 'Why',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => {
        const reason = p.row.reviewReason;
        if (!reason) return <Typography sx={{ color: 'text.disabled', fontSize: '0.8rem' }}>—</Typography>;
        return (
          <Tooltip title={reason}>
            <Typography sx={{ fontSize: '0.85rem', color: 'text.secondary', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {reason}
            </Typography>
          </Tooltip>
        );
      },
    },
    {
      field: 'actions',
      headerName: '',
      width: 130,
      sortable: false,
      filterable: false,
      align: 'right',
      headerAlign: 'right',
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'flex-end', height: '100%', width: '100%' }}>
          <Button
            size="small"
            endIcon={<ChevronIcon />}
            onClick={(event) => { event.stopPropagation(); openReview(p.row); }}
            aria-label={`Review extraction for ${p.row.rfqno || 'document'} ${p.row.id}`}
            sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}
          >
            Check it
          </Button>
        </Box>
      ),
    },
  ];

  return (
    <InboxFrame
      summary={data ? `${totalCount} document${totalCount === 1 ? '' : 's'} to check` : undefined}
      tools={<SearchField width={320} value={search} onChange={setSearch} placeholder="Search RFQ # or buyer" />}
      actions={(
        <Tooltip title="Refresh">
          <span>
            <IconButton aria-label="Refresh" onClick={() => refetch()} disabled={isFetching} sx={{ width: 36, height: 36 }}>
              <RefreshIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
      )}
    >
      {extractionDeadLetterCount > 0 && (
        <Alert
          severity="warning"
          sx={{ mb: 1.5, borderRadius: 2 }}
          action={(
            <Button color="inherit" size="small" onClick={() => navigate('/admin/operations')}>
              Manage exceptions
            </Button>
          )}
        >
          {extractionDeadLetterCount.toLocaleString()} extraction exception{extractionDeadLetterCount === 1 ? '' : 's'} stopped before a reviewable Lead was created. These are managed in Tenant admin operations and are not documents in this review queue.
        </Alert>
      )}

      <Paper variant="outlined" sx={{ ...INBOX_CARD_SX, height: 'calc(100vh - 260px)', minHeight: 360, width: '100%' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 2, p: 3, textAlign: 'center' }}>
            <Alert severity="error" sx={{ borderRadius: 2, maxWidth: 480 }}>
              We couldn't load the review queue. The service may be temporarily unavailable.
            </Alert>
            <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()} sx={{ fontWeight: 700, borderRadius: 2 }}>
              Retry
            </Button>
          </Box>
        ) : (
          <DataGrid
            rows={data?.items ?? []}
            columns={columns}
            rowCount={totalCount}
            loading={isLoading}
            pageSizeOptions={[10, 25, 50]}
            paginationModel={paginationModel}
            paginationMode="server"
            onPaginationModelChange={setPaginationModel}
            disableRowSelectionOnClick
            getRowId={(r) => r.id}
            rowHeight={46}
            columnHeaderHeight={40}
            onRowClick={(p) => openReview(p.row)}
            onCellKeyDown={(p, event) => { if (event.key === 'Enter') openReview(p.row); }}
            sx={{
              border: 0,
              '& .MuiDataGrid-row': { cursor: 'pointer' },
              '& .MuiDataGrid-cell': { display: 'flex', alignItems: 'center' },
              '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700, fontSize: 12, letterSpacing: '0.06em', textTransform: 'uppercase', color: 'text.secondary' },
              '& .MuiDataGrid-cell .MuiButton-root': { minHeight: 30, py: 0.25 },
            }}
            slots={{
              noRowsOverlay: () => (
                <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1.5, p: 3, textAlign: 'center' }}>
                  <CaughtUpIcon sx={{ fontSize: 56, color: 'success.main', opacity: 0.85 }} />
                  <Typography sx={{ fontWeight: 800 }}>No documents awaiting review — you're all caught up</Typography>
                  <Typography variant="body2" color="text.secondary">
                    Documents that need a person&apos;s check will appear here.
                  </Typography>
                </Box>
              ),
            }}
          />
        )}
      </Paper>
    </InboxFrame>
  );
};

export default ExtractionReviewPage;
