import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import {
  Box, Button, Chip, InputAdornment, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material';
import {
  AddOutlined as AddIcon,
  ArrowForwardOutlined as OpenIcon,
  SearchOutlined as SearchIcon,
} from '@mui/icons-material';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { usePlatformPermissions } from '../auth/usePlatformPermissions';
import PageHeader from '../components/PageHeader';
import { EmptyState, ErrorState, LoadingState } from '../components/States';
import type { CustomerListRow } from '../types';

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
        || (r.legalName ?? '').toLowerCase().includes(term))
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
          title={view === 'attention' ? 'Nothing is waiting on us' : 'No customers yet'}
          message={
            view === 'attention'
              ? 'Every customer is either live and clear, or already closed. Switch to All to see the whole book.'
              : 'Create the first customer to get started.'
          }
        />
      ) : (
        <Box sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 1, overflowX: 'auto' }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell sx={{ fontWeight: 700 }}>Customer</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Status</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Plan</TableCell>
                <TableCell sx={{ fontWeight: 700 }}>Renews</TableCell>
                <TableCell sx={{ fontWeight: 700, minWidth: 320 }}>What happens next</TableCell>
                <TableCell />
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
      sx={{ cursor: 'pointer', '&:last-child td': { border: 0 } }}
    >
      <TableCell>
        <Typography variant="body2" sx={{ fontWeight: 700 }}>{row.name}</Typography>
        {row.legalName && row.legalName !== row.name && (
          <Typography variant="caption" color="text.secondary">{row.legalName}</Typography>
        )}
      </TableCell>

      <TableCell>
        <Chip
          size="small"
          label={row.status}
          color={row.status === 'Active' ? 'success' : row.status === 'PastDue' ? 'error' : 'default'}
          variant={row.status === 'Active' ? 'filled' : 'outlined'}
          sx={{ fontWeight: 700 }}
        />
      </TableCell>

      <TableCell>
        <Typography variant="body2">{row.planCode ?? '—'}</Typography>
        <Typography variant="caption" color="text.secondary">{row.billingMode}</Typography>
      </TableCell>

      <TableCell>
        <Typography variant="body2" sx={{ whiteSpace: 'nowrap' }}>{asDate(row.contractEndOn)}</Typography>
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

      <TableCell align="right">
        <Tooltip describeChild title={`Open ${row.name}`}>
          <span><OpenIcon fontSize="small" sx={{ color: 'text.disabled', verticalAlign: 'middle' }} /></span>
        </Tooltip>
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
