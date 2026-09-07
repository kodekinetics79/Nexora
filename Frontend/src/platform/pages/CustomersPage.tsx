import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Button, InputAdornment, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, ToggleButton, ToggleButtonGroup, Typography,
} from '@mui/material';
import {
  AddOutlined as AddIcon,
  SearchOutlined as SearchIcon,
} from '@mui/icons-material';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import PageHeader from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import Stack from '../components/Flex';
import type { CustomerListRow } from '../types';

/** One head-cell style, so the columns share a rhythm instead of each inventing one. */
const HEAD = { fontSize: 11, fontWeight: 700, letterSpacing: '0.08em', textTransform: 'uppercase' as const, color: 'text.secondary' };

/**
 * THE CUSTOMERS SCREEN — the book of business, filtered to what needs a person today.
 *
 * WHAT WAS WRONG. The tenant list showed name, country, plan, billing mode, trial end, status
 * and created date. Seven columns, and not one of them answers the only question an operator
 * opens this screen with: *which of these needs me today?* They found out by opening each
 * customer in turn and reading a twelve-tab page — which is how a customer sits unusable for
 * days without anybody noticing, because nothing on the list said so.
 *
 * WHAT THIS IS. The same rows, with the server's own next-action sentence on each one, and
 * filters that select exceptions rather than attributes. "Needs attention" is the default view
 * because a list that opens on everything is a list you have to read.
 *
 * The next action is computed by the same policy evaluation the customer page uses, so a row and
 * the page behind it cannot disagree — the failure mode where a list says "fine" and the page
 * says "blocked on 7 items".
 */
export default function CustomersPage() {
  const navigate = useNavigate();
  const permissions = usePlatformPermissions();
  const [view, setView] = useState<'attention' | 'all'>('attention');
  const [search, setSearch] = useState('');

  const customers = useQuery({
    queryKey: platformKeys.customers(),
    queryFn: () => platformApi.listCustomers(),
  });

  const rows = customers.data ?? [];
  const attentionCount = rows.filter((r) => r.needsAttention).length;

  const visible = useMemo(() => {
    const term = search.trim().toLowerCase();
    return rows
      .filter((r) => (view === 'all' ? true : r.needsAttention))
      .filter((r) => term === '' || r.name.toLowerCase().includes(term)
        || (r.legalName ?? '').toLowerCase().includes(term)
        || (r.planCode ?? '').toLowerCase().includes(term)
        || (r.countryCode ?? '').toLowerCase().includes(term))
      // Rows that need somebody come first; within that, the most blocked first. A list sorted
      // by creation date buries the customer who has been stuck since Tuesday.
      .sort((a, b) => Number(b.needsAttention) - Number(a.needsAttention)
        || b.blockerCount - a.blockerCount
        || a.name.localeCompare(b.name));
  }, [rows, view, search]);

  if (customers.isLoading) return <LoadingState label="Loading customers…" minHeight="60vh" />;
  if (customers.isError) {
    return (
      <ErrorState
        message={platformErrorMessage(customers.error, 'The customer list could not be loaded.')}
        onRetry={() => customers.refetch()}
      />
    );
  }

  return (
    <Box>
      <PageHeader
        title="Customers"
        subtitle={
          attentionCount > 0
            ? `${attentionCount} of ${rows.length} need someone today`
            : `${rows.length} customers, none waiting on us`
        }
        actions={
          permissions.canAdministerTenants && (
            <Button variant="contained" startIcon={<AddIcon />} onClick={() => navigate('/platform/customers/new')}>
              New customer
            </Button>
          )
        }
      />

      <Box sx={{ display: 'flex', gap: 2, alignItems: 'center', flexWrap: 'wrap', mb: 2.5 }}>
        {/*
          Two views, not five dropdowns. The old screen had filters for status, plan, billing mode,
          country and search — attributes, all of which require you to already know what you are
          looking for. The question is "who needs me", so that is the filter.
        */}
        <ToggleButtonGroup
          size="small"
          exclusive
          value={view}
          onChange={(_e, next) => next && setView(next)}
          aria-label="Which customers to show"
        >
          <ToggleButton value="attention" sx={{ fontWeight: 700, px: 2 }}>
            Needs attention{attentionCount > 0 ? ` · ${attentionCount}` : ''}
          </ToggleButton>
          <ToggleButton value="all" sx={{ fontWeight: 700, px: 2 }}>
            All · {rows.length}
          </ToggleButton>
        </ToggleButtonGroup>

        <TextField
          size="small"
          placeholder="Find a customer"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          sx={{ minWidth: 260 }}
          slotProps={{
            input: {
              startAdornment: <InputAdornment position="start"><SearchIcon fontSize="small" /></InputAdornment>,
            },
          }}
        />
      </Box>

      {visible.length === 0 ? (
        <EmptyState
          title={
            search.trim() !== '' ? `No customer matches “${search.trim()}”`
              : view === 'attention' ? 'Nothing is waiting on us'
                : 'No customers yet'
          }
          message={
            search.trim() !== ''
              // Saying "No customers yet" over a full book is how somebody creates a duplicate.
              ? `${rows.length} customers exist. Clear the search to see them.`
              : view === 'attention'
                ? 'Every customer is either live and clear, or already closed. Switch to All to see the whole book.'
                : 'Create the first customer to get started.'
          }
          action={search.trim() !== ''
            ? <Button onClick={() => setSearch('')}>Clear search</Button>
            : undefined}
        />
      ) : (
        <Box sx={{ overflowX: 'auto' }}>
          <Table size="small" sx={{ '& td, & th': { borderColor: 'divider' } }}>
            <TableHead>
              <TableRow>
                <TableCell sx={{ ...HEAD, width: '26%' }}>Customer</TableCell>
                <TableCell sx={{ ...HEAD, width: 120 }}>Status</TableCell>
                <TableCell sx={{ ...HEAD, width: 140 }}>Plan</TableCell>
                <TableCell sx={{ ...HEAD, width: 130 }} align="right">Renews</TableCell>
                <TableCell sx={{ ...HEAD }}>What happens next</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {visible.map((row) => <CustomerRow key={row.tenantId} row={row} onOpen={navigate} />)}
            </TableBody>
          </Table>
        </Box>
      )}
    </Box>
  );
}

function CustomerRow({ row, onOpen }: { row: CustomerListRow; onOpen: (to: string) => void }) {
  const open = () => onOpen(`/platform/customers/${row.tenantId}`);
  return (
    <TableRow
      hover
      onClick={open}
      sx={{
        cursor: 'pointer',
        '&:last-child td': { border: 0 },
        'td:first-of-type': { boxShadow: 'inset 2px 0 0 transparent' },
        '&:hover td:first-of-type': { boxShadow: (t) => `inset 2px 0 0 ${t.palette.primary.main}` },
      }}
    >
      <TableCell>
        <Typography variant="body2" sx={{ fontWeight: 700 }}>{row.name}</Typography>
        {row.legalName && row.legalName !== row.name && (
          <Typography variant="caption" color="text.secondary">{row.legalName}</Typography>
        )}
      </TableCell>

      <TableCell>
        <Stack direction="row" spacing={0.9} sx={{ alignItems: 'center' }}>
          <Box sx={{
            width: 7, height: 7, borderRadius: '50%', flexShrink: 0,
            bgcolor: row.status === 'Active' ? 'success.main'
              : row.status === 'PastDue' ? 'error.main'
                : row.status === 'Provisioning' ? 'warning.main' : 'text.disabled',
          }} />
          <Typography variant="body2">{row.status}</Typography>
        </Stack>
      </TableCell>

      <TableCell>
        <Typography variant="body2">{row.planCode ?? '—'}</Typography>
        <Typography variant="caption" color="text.secondary">{row.billingMode}</Typography>
      </TableCell>

      <TableCell align="right">
        <Typography
          variant="body2"
          sx={{ whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}
        >
          {asDate(row.contractEndOn)}
        </Typography>
      </TableCell>

      {/*
        The column the old list did not have, and the reason this screen exists. It is the same
        sentence the customer page shows, computed once on the server.
      */}
      <TableCell>
        {row.nextAction ? (
          <>
            <Typography
              variant="body2"
              sx={{ fontWeight: 700, color: row.blockerCount > 0 ? 'warning.main' : 'text.primary' }}
            >
              {row.nextAction.label}
            </Typography>
            <Typography variant="caption" color="text.secondary">{row.nextAction.detail}</Typography>
          </>
        ) : (
          <Typography variant="body2" color="text.secondary">Live, nothing outstanding</Typography>
        )}
      </TableCell>


    </TableRow>
  );
}

/** A renewal date is a calendar date a contract names; the time component is meaningless noise. */
function asDate(value: string | null): string {
  if (!value) return '—';
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime())
    ? value
    : parsed.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}
