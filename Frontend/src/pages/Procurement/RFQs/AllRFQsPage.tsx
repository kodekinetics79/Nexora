import React, { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useSearchParams } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import {
  Box, Typography, Paper, Button, IconButton,
  Tooltip, Stack, Alert,
} from '@mui/material';
import {
  DataGrid, type GridColDef, type GridPaginationModel
} from '@mui/x-data-grid';
import {
  ChevronRight as OpenIcon,
  Refresh as RefreshIcon,
  Layers as ItemsIcon,
  CloudUpload as UploadIcon,
} from '@mui/icons-material';
import rfqService, { type RfqResponseDTO } from '../../../api/services/rfqService';
import ExportExcelButton, { loadAllPages, type ExportColumn } from '../../../components/common/ExportExcelButton';
import SearchField from '../../../components/common/SearchField';
import gridEmptyOverlay from '../../../components/common/gridOverlays';
import ViewTabs from '../../../components/layout/ViewTabs';
import { useAuth } from '../../../context/AuthContext';
import { formatDateSafe, formatDateTimeSafe } from '../../../utils/dates';
import { DEADLINE_COLOR, deadlineWords } from '../../../utils/deadline';

/**
 * Same defect as QuotesPage: two rail entries point here at a FILTERED address while the page
 * heads itself "All RFQs / Manage and track all Request for Quotations" either way.
 *
 * RfqRepository.GetAllAsync narrows on 'ready-for-quote' and nothing else, so it is the only value
 * that earns a filter chip. "Sourcing Cases" sends ?state=requires-sourcing, which the server drops
 * on the floor — that rail entry lands the user on EVERY RFQ under a heading promising a sourcing
 * subset, so it gets a stated warning rather than a silently complete list.
 */
const RFQ_FILTERS: Record<string, { label: string }> = {
  'ready-for-quote': { label: 'Ready for Quote' },
};

const RFQ_EXPORT_COLUMNS: ExportColumn<RfqResponseDTO>[] = [
  { header: 'Nexora Serial', value: (r) => r.nexoraSerial || r.commercialCaseReference },
  { header: 'RFQ #', value: (r) => r.rfqno },
  { header: 'Customer RFQ reference', value: (r) => r.customerRfqReference },
  { header: 'Customer', value: (r) => r.customerName },
  { header: 'Customer email', value: (r) => r.customerEmail || r.leadEmail },
  { header: 'Buyer', value: (r) => r.buyersName },
  { header: 'Contact', value: (r) => r.contactName },
  { header: 'Account owner', value: (r) => r.accountOwnerName },
  { header: 'Opportunity owner', value: (r) => r.opportunityOwnerName },
  { header: 'Lines', value: (r) => r.noOfLineItems ?? 0 },
  { header: 'Received', value: (r) => formatDateSafe(r.recDate, '') },
  { header: 'Closing date', value: (r) => formatDateSafe(r.bidClosingDate, '') },
  { header: 'Closing date (Hijri)', value: (r) => r.bidClosingDateHijri },
  { header: 'Required delivery', value: (r) => formatDateSafe(r.requiredDeliveryDate, '') },
  { header: 'Delivery location', value: (r) => r.deliveryLocation },
  { header: 'Agreement reference', value: (r) => r.agreementReference },
  { header: 'Opportunity #', value: (r) => r.opportunityNo },
  { header: 'RFQ type', value: (r) => r.rfqtype },
  { header: 'Inquiry type', value: (r) => r.inquiryType },
  { header: 'Agreement duration', value: (r) => r.durationAgreement },
  { header: 'Bidding decision', value: (r) => r.biddingDecision },
  { header: 'Acknowledged', value: (r) => formatDateSafe(r.acknowledgmentDate, '') },
  { header: 'Submitted', value: (r) => formatDateSafe(r.subDate, '') },
  { header: 'Status', value: (r) => r.rfqstatusValue },
  { header: 'Readiness', value: (r) => r.readiness },
  { header: 'Lead revision', value: (r) => r.sourceLeadRevisionNumber },
  { header: 'Made from lead by', value: (r) => r.promotedBy },
  { header: 'Made from lead on', value: (r) => formatDateTimeSafe(r.promotedAtUtc, '') },
  { header: 'Remarks', value: (r) => r.headerRemarks },
  { header: 'Business unit', value: (r) => r.businessUnitName },
  { header: 'Created by', value: (r) => r.createdBy },
  { header: 'Created', value: (r) => formatDateTimeSafe(r.createdDate, '') },
  { header: 'Modified by', value: (r) => r.modifiedBy },
  { header: 'Modified', value: (r) => formatDateTimeSafe(r.modifiedDate, '') },
];

const AllRFQsPage: React.FC = () => {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const readiness = searchParams.get('state') || undefined;
  const unappliedFilter = readiness && !RFQ_FILTERS[readiness] ? readiness : undefined;
  const { userData, hasPermission } = useAuth();
  const [paginationModel, setPaginationModel] = useState<GridPaginationModel>({ pageSize: 25, page: 0 });
  const [search, setSearch] = useState('');

  const { data, isLoading, isError, refetch } = useQuery({
    queryKey: ['rfqs', paginationModel, search, readiness],
    queryFn: () => rfqService.getAll({
      pageNumber: paginationModel.page + 1,
      pageSize: paginationModel.pageSize,
      search: search || undefined,
      businessUnitId: userData?.businessUnitId || undefined,
      readiness,
    }),
  });

  /**
   * The three highest-traffic grids in the product — this one, Leads and Quotes — shipped MUI's
   * bare "No rows". That string cannot say which of the three nothings the reader is looking at,
   * and it offers no way out of any of them. Memoised because DataGrid takes a component TYPE
   * here: rebuilding the factory each render would remount the overlay for nothing.
   */
  const noRowsOverlay = React.useMemo(() => gridEmptyOverlay({
    title: 'No RFQs yet',
    message: 'An RFQ is created when you qualify an enquiry. Qualify one and it lands here, ready to price.',
    icon: <ItemsIcon sx={{ fontSize: 48 }} />,
    action: (
      <Button variant="contained" onClick={() => navigate('/procurement/leads/all')} sx={{ fontWeight: 700 }}>
        See all inquiries
      </Button>
    ),
    filtered: Boolean(search) || Boolean(readiness),
    filteredTitle: 'No RFQ matches this view',
    filteredMessage: readiness
      ? 'Nothing in the list matches this filter. Clear it to see every RFQ.'
      : 'No RFQ matches this search. Clear it to see every RFQ.',
    filteredAction: (
      <Button
        variant="outlined"
        onClick={() => { setSearch(''); navigate('/procurement/rfqs/all'); }}
        sx={{ fontWeight: 700 }}
      >
        Show all RFQs
      </Button>
    ),
  }), [search, readiness, navigate]);

  const openRfq = (id: number) => navigate(`/procurement/rfqs/view/${id}`);
  const cellText = { whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' } as const;

  const columns: GridColDef<RfqResponseDTO>[] = [
    {
      field: 'bidClosingDate',
      headerName: 'Deadline',
      width: 130,
      sortable: false,
      renderCell: (p) => {
        const sent = Boolean(p.row.latestQuoteSentOn);
        const { text, tone } = deadlineWords(p.row.bidClosingDate);
        return (
          <Tooltip title={p.row.bidClosingDate ? formatDateSafe(p.row.bidClosingDate) : ''} placement="top-start">
            <Typography sx={{ ...cellText, fontSize: '0.85rem', fontWeight: sent ? 500 : 700, color: sent ? 'text.secondary' : DEADLINE_COLOR[tone] }}>
              {text}
            </Typography>
          </Tooltip>
        );
      },
    },
    {
      field: 'customerName',
      headerName: 'Customer',
      flex: 1,
      minWidth: 200,
      sortable: false,
      renderCell: (p) => {
        const buyer = p.row.buyersName && p.row.buyersName !== p.row.customerName ? p.row.buyersName : null;
        return (
          <Typography sx={{ ...cellText, fontSize: '0.875rem' }} title={[p.row.customerName, buyer].filter(Boolean).join(' · ')}>
            <Box component="span" sx={{ fontWeight: 700, color: p.row.customerName ? 'text.primary' : 'warning.dark' }}>
              {p.row.customerName || 'No customer'}
            </Box>
            {buyer && <Box component="span" sx={{ color: 'text.secondary' }}> · {buyer}</Box>}
          </Typography>
        );
      },
    },
    {
      field: 'rfqno',
      headerName: t('rfq_number'),
      width: 205,
      sortable: false,
      renderCell: (p) => (
        <Typography title={p.row.rfqno} sx={{ ...cellText, fontFamily: 'monospace', fontSize: '0.82rem', fontWeight: 600 }}>
          {p.row.rfqno || `RFQ-${p.row.id}`}
        </Typography>
      ),
    },
    {
      field: 'nexoraSerial',
      headerName: 'Nexora serial',
      width: 200,
      sortable: false,
      // No fallback through the lead or RFQ: a blank here is real, and "Not linked" says so.
      renderCell: (p) => (
        <Typography title={p.row.nexoraSerial ?? ''} sx={{ ...cellText, fontFamily: 'monospace', fontSize: '0.75rem', color: p.row.nexoraSerial ? 'text.secondary' : 'warning.dark' }}>
          {p.row.nexoraSerial || 'Not linked'}
        </Typography>
      ),
    },
    {
      field: 'itemCount',
      headerName: 'Lines',
      width: 64,
      align: 'right',
      headerAlign: 'right',
      sortable: false,
      renderCell: (p) => (
        <Typography className="tabular-nums" sx={{ fontSize: '0.85rem', fontWeight: 600 }}>
          {p.row.itemCount ?? p.row.noOfLineItems ?? 0}
        </Typography>
      ),
    },
    {
      field: 'ownerName',
      headerName: 'Owner',
      width: 150,
      sortable: false,
      renderCell: (p) => (
        <Typography sx={{ ...cellText, fontSize: '0.85rem', color: p.row.ownerName ? 'text.primary' : 'text.disabled' }}>
          {p.row.ownerName || 'Unassigned'}
        </Typography>
      ),
    },
    {
      field: 'quote',
      headerName: 'Quote',
      width: 270,
      sortable: false,
      renderCell: (p) => {
        const status = (p.row.rfqstatusCode ?? p.row.rfqstatusValue ?? '').toUpperCase();
        const closed = status && !['DRAFT', 'OPEN', 'NEW', 'ACTIVE', 'INPROGRESS', 'IN_PROGRESS'].includes(status);
        const sentOn = p.row.latestQuoteSentOn;
        const text = sentOn
          ? `Sent ${formatDateSafe(sentOn)}`
          : p.row.latestQuoteNo ? 'Not sent yet' : closed ? (p.row.rfqstatusValue ?? '') : 'Not quoted';
        return (
          <Typography sx={{ ...cellText, fontSize: '0.85rem' }} title={p.row.latestQuoteNo ?? ''}>
            <Box component="span" sx={{ fontWeight: 700, color: sentOn ? 'success.dark' : p.row.latestQuoteNo ? 'warning.dark' : 'text.secondary' }}>
              {text}
            </Box>
            {p.row.latestQuoteNo && <Box component="span" sx={{ color: 'text.secondary', fontFamily: 'monospace', fontSize: '0.75rem' }}> · {p.row.latestQuoteNo}</Box>}
          </Typography>
        );
      },
    },
    {
      field: 'open',
      headerName: '',
      width: 44,
      sortable: false,
      disableColumnMenu: true,
      renderCell: (p) => (
        <IconButton size="small" aria-label={`Open RFQ ${p.row.rfqno || p.row.id}`} onClick={() => openRfq(p.row.id)} sx={{ minHeight: 0 }}>
          <OpenIcon fontSize="small" />
        </IconButton>
      ),
    },
  ];

  return (
    <Box sx={{ p: 2 }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 1, flexWrap: 'wrap', rowGap: 1 }}>
        <Typography variant="h5" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.01em' }}>
          RFQs
          {data && <Box component="span" className="tabular-nums" sx={{ color: 'text.secondary', fontWeight: 600 }}> · {data.totalItems}</Box>}
        </Typography>
        <SearchField width={340} value={search} onChange={setSearch} placeholder="Search RFQ, serial, customer or buyer" />
        <Box sx={{ flex: 1 }} />
        {hasPermission('RFQ Management', 'create') && (
          <Tooltip title="Upload a customer inquiry for Lead reconciliation">
            <Button
              variant="outlined"
              startIcon={<UploadIcon />}
              onClick={() => navigate('/procurement/leads/manual-upload')}
              sx={{ fontWeight: 700, minHeight: 36 }}
            >
              Upload inquiry
            </Button>
          </Tooltip>
        )}
        <ExportExcelButton
          name="RFQs"
          columns={RFQ_EXPORT_COLUMNS}
          loadRows={() => loadAllPages((pageNumber, pageSize) => rfqService.getAll({
            pageNumber,
            pageSize,
            search: search || undefined,
            businessUnitId: userData?.businessUnitId || undefined,
            readiness,
          }))}
        />
        <Tooltip title="Refresh">
          <IconButton aria-label="Refresh" onClick={() => refetch()} sx={{ width: 36, height: 36 }}>
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Stack>
      {unappliedFilter && (
        <Alert severity="warning" sx={{ mb: 1 }}>
          This link asked for "{unappliedFilter}", which is not a filter this list applies. Every RFQ is shown.
        </Alert>
      )}

      {/* All / Drafts / Ready for quote: one list to a rep, so tabs rather than rail rows. */}
      <ViewTabs primaryKey="rfqs" ariaLabel="RFQ views" />

      <Paper sx={{ height: 'calc(100vh - 196px)', minHeight: 360, width: '100%', borderRadius: 2, overflow: 'hidden', border: '1px solid', borderColor: 'divider', boxShadow: 'none' }}>
        {isError ? (
          <Box sx={{ height: '100%', display: 'grid', placeItems: 'center', p: 3 }}>
            <Stack spacing={2} sx={{ alignItems: 'center', maxWidth: 480 }}>
              <Alert severity="error">We couldn't load RFQs. No empty result has been assumed.</Alert>
              <Button variant="contained" startIcon={<RefreshIcon />} onClick={() => refetch()}>Retry</Button>
            </Stack>
          </Box>
        ) : <DataGrid
          rows={data?.items ?? []}
          columns={columns}
          rowCount={data?.totalItems ?? 0}
          loading={isLoading}
          slots={{ noRowsOverlay }}
          pageSizeOptions={[25, 50, 100]}
          paginationModel={paginationModel}
          paginationMode="server"
          onPaginationModelChange={setPaginationModel}
          disableRowSelectionOnClick
          getRowId={(r) => r.id}
          rowHeight={48}
          columnHeaderHeight={40}
          onRowClick={(p) => openRfq(p.row.id)}
          onCellKeyDown={(p, event) => { if (event.key === 'Enter') openRfq(p.row.id); }}
          sx={{
            border: 0,
            '& .MuiDataGrid-row': { cursor: 'pointer' },
            '& .MuiDataGrid-cell': { display: 'flex', alignItems: 'center' },
            '& .MuiDataGrid-columnHeaderTitle': { fontWeight: 700 },
          }}
        />}
      </Paper>
    </Box>
  );
};

export default AllRFQsPage;
