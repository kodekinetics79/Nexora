import React, { useState, useEffect } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Box, Typography, Paper, Button, Chip, Stack, Tooltip, Alert, Badge,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel,
} from '@mui/x-data-grid';
import {
  Refresh as RefreshIcon,
  Layers as ItemsIcon,
  RateReview as ReviewIcon,
  FactCheck as QueueIcon,
  TaskAlt as CaughtUpIcon,
  ErrorOutlined as NeedsCheckDotIcon,
} from '@mui/icons-material';
import dayjs from 'dayjs';
import relativeTime from 'dayjs/plugin/relativeTime';
import extractionReviewService from '../../api/services/extractionReviewService';
import type { NeedsReviewItem } from '../../api/services/extractionReviewService';
import SearchField from '../../components/common/SearchField';
import ViewTabs from '../../components/layout/ViewTabs';
import { formatDateSafe } from '../../utils/dates';

dayjs.extend(relativeTime);

// This queue used to render an extraction-confidence percentage per row in
// red/amber/green. That number was never measured — on the structured path it
// is a literal (1.0 parsed / 0.2 not / 0 blank), on the model path it is the
// model's own self-report against a rubric in its own prompt — so it is no
// longer shown anywhere. Every document in this queue needs a human look; what
// the reviewer needs from the list is which one to open next, not a score.

const ExtractionReviewPage: React.FC = () => {
  const navigate = useNavigate();
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

  const totalCount = data?.totalCount ?? 0;

  const formatRelative = (dateStr: string | null) => {
    if (!dateStr) return '—';
    const d = dayjs(dateStr);
    return d.isValid() ? d.fromNow() : '—';
  };

  const columns: GridColDef<NeedsReviewItem>[] = [
    {
      // Column index 0, matching the review workbench. Everything in this queue
      // is unreviewed by definition; the dot makes that state readable at a
      // glance instead of a colour-coded score that implied a verdict.
      field: 'checkStatus',
      headerName: 'Status',
      width: 56,
      sortable: false,
      filterable: false,
      align: 'center',
      headerAlign: 'center',
      renderCell: () => (
        <Tooltip title="Not yet reviewed by a person">
          <Box role="img" aria-label="Needs check" sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
            <NeedsCheckDotIcon sx={{ fontSize: 18, color: 'error.main' }} />
          </Box>
        </Tooltip>
      ),
    },
    {
      field: 'rfqno',
      headerName: 'RFQ #',
      width: 170,
      renderCell: (p) => (
        <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', color: 'primary.main', fontFamily: 'monospace', letterSpacing: '-0.02em' }}>
          {p.row.rfqno || 'NO RFQ #'}
        </Typography>
      ),
    },
    {
      field: 'buyersName',
      headerName: 'Buyer',
      flex: 1,
      minWidth: 180,
      renderCell: (p) => (
        <Typography sx={{ fontWeight: 700, fontSize: '0.85rem' }}>
          {p.row.buyersName || 'Unknown Buyer'}
        </Typography>
      ),
    },
    {
      field: 'leadSource',
      headerName: 'Source',
      width: 120,
      renderCell: (p) => (
        <Chip
          label={p.row.leadSource || '—'}
          size="small"
          sx={{
            fontWeight: 900, fontSize: '0.65rem', height: 20, borderRadius: 1,
            color: 'white !important',
            bgcolor: p.row.leadSource === 'Email' ? 'primary.main' : 'secondary.main',
          }}
        />
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
      headerName: 'Lines',
      width: 190,
      align: 'center',
      headerAlign: 'center',
      renderCell: (p) => {
        const total = p.row.itemCount ?? 0;
        // Rendered only when the backend computes it; never inferred here.
        const needing = p.row.linesNeedingCheck;
        return (
          <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', justifyContent: 'center', height: '100%' }}>
            <ItemsIcon sx={{ fontSize: 14, color: 'text.secondary' }} />
            <Typography sx={{ fontSize: '0.85rem', fontWeight: 800 }}>
              {needing == null
                ? `${total} line${total === 1 ? '' : 's'}`
                : `${needing} of ${total} need a check`}
            </Typography>
          </Stack>
        );
      },
    },
    {
      field: 'reviewReason',
      headerName: 'Review Reason',
      flex: 1,
      minWidth: 200,
      renderCell: (p) => {
        const reason = p.row.reviewReason;
        if (!reason) return <Typography sx={{ color: 'text.disabled', fontSize: '0.8rem' }}>—</Typography>;
        return (
          <Tooltip title={reason}>
            <Typography sx={{ fontSize: '0.8rem', color: 'warning.dark', fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {reason}
            </Typography>
          </Tooltip>
        );
      },
    },
    {
      field: 'actions',
      headerName: 'Action',
      width: 130,
      sortable: false,
      filterable: false,
      align: 'center',
      headerAlign: 'center',
      renderCell: (p) => (
        <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100%' }}>
          <Button
            variant="contained"
            size="small"
            startIcon={<ReviewIcon sx={{ fontSize: 16 }} />}
            onClick={() => navigate(`/procurement/extraction/review/${p.row.id}`, { state: { reviewReason: p.row.reviewReason } })}
            aria-label={`Review extraction for ${p.row.rfqno || 'document'} ${p.row.id}`}
            sx={{ fontWeight: 800, borderRadius: 2, textTransform: 'none' }}
          >
            Review
          </Button>
        </Box>
      ),
    },
  ];

  return (
    <Box sx={{ p: 3, bgcolor: 'background.default', minHeight: '100vh' }}>
      {/* Header */}
      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center', mb: 3 }}>
        <Box>
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
            <Badge badgeContent={totalCount} color="warning" max={999} showZero={false}>
              <QueueIcon sx={{ color: 'primary.main', fontSize: 30 }} />
            </Badge>
            <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
              Extraction Review
            </Typography>
          </Stack>
          <Typography variant="body2" color="text.secondary">
            {totalCount > 0
              ? `${totalCount} document${totalCount === 1 ? '' : 's'} awaiting review — every extraction is reviewed by a person`
              : 'Verify and correct extracted documents before they move downstream'}
          </Typography>
        </Box>
        <Tooltip title="Refresh queue">
          <span>
            <Button
              variant="outlined"
              startIcon={<RefreshIcon />}
              onClick={() => refetch()}
              disabled={isFetching}
              sx={{ fontWeight: 800, borderRadius: 2, border: '2px solid' }}
            >
              Refresh
            </Button>
          </span>
        </Tooltip>
      </Stack>

      {/* The four intake doors — the work queue, this review queue, inbound mail and upload — as
          one level of tabs. They were four separate rail rows under "Lead Management". */}
      <ViewTabs primaryKey="inbox" ariaLabel="Inbox views" />

      {/* Filters */}
      <Paper sx={{ p: 1.5, mb: 1.5, display: 'flex', gap: 2, alignItems: 'center', borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        <SearchField width="360px" value={search} onChange={setSearch} placeholder="Search by RFQ No, Buyer Name..." />
      </Paper>

      {/* Grid */}
      <Paper sx={{ height: 'calc(100vh - 240px)', width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider' }}>
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
            slots={{
              noRowsOverlay: () => (
                <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 1.5, p: 3, textAlign: 'center' }}>
                  <CaughtUpIcon sx={{ fontSize: 56, color: 'success.main', opacity: 0.85 }} />
                  <Typography sx={{ fontWeight: 800 }}>No documents awaiting review — you're all caught up</Typography>
                  <Typography variant="body2" color="text.secondary">
                    Every new extraction is routed here for a person to verify before it moves downstream.
                  </Typography>
                </Box>
              ),
            }}
          />
        )}
      </Paper>
    </Box>
  );
};

export default ExtractionReviewPage;
