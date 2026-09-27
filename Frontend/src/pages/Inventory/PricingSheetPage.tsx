import React from 'react';
import { useMutation, useQuery, useQueryClient, keepPreviousData } from '@tanstack/react-query';
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  FormControlLabel,
  InputAdornment,
  MenuItem,
  Paper,
  Stack,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TablePagination,
  TableRow,
  TextField,
  Typography,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import { useSnackbar } from 'notistack';
import pricingSheetService, { type PriceChange, type PricingRow } from '../../api/services/pricingSheetService';
import SearchField from '../../components/common/SearchField';
import { useAuth } from '../../context/AuthContext';
import { formatMoney } from '../../utils/currency';

/** What the keeper has typed on a row, kept as text so a half-typed number is not lost. */
interface Draft {
  landedCost: string;
  margin: string;
  salePrice: string;
  currencyId: number | '';
}

const round2 = (value: number) => Math.round(value * 100) / 100;
const num = (text: string) => (text.trim() === '' ? null : Number(text));
const valid = (value: number | null) => value === null || (Number.isFinite(value) && value > 0);
const marginOf = (cost: number | null, sale: number | null) =>
  cost && sale && cost > 0 ? String(round2((sale / cost - 1) * 100)) : '';
const day = (value?: string | null) => (value ? new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }) : '');
const describeError = (error: unknown, fallback: string) =>
  (error as { response?: { data?: { detail?: string } } })?.response?.data?.detail || fallback;

/**
 * The Pricing sheet: one row per part, with the landed cost (what one unit costs us, delivered),
 * the sale price and the currency they are in. A manager or other authorised person keeps it; a
 * sales rep reads it, and sets the margin on each quote in the RFQ pricing window.
 */
export default function PricingSheetPage() {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const { hasPermission } = useAuth();
  const canEdit = hasPermission('Products', 'edit');

  const [search, setSearch] = React.useState('');
  const [missingOnly, setMissingOnly] = React.useState(true);
  const [page, setPage] = React.useState(0);
  const [pageSize, setPageSize] = React.useState(50);
  // Edits survive paging: a draft is keyed by part and remembers the row it started from.
  const [drafts, setDrafts] = React.useState<Record<number, { draft: Draft; original: PricingRow }>>({});

  const query = useQuery({
    queryKey: ['pricing-sheet', search, missingOnly, page, pageSize],
    queryFn: () => pricingSheetService.get({ search: search || undefined, missingOnly, page: page + 1, pageSize }),
    placeholderData: keepPreviousData,
  });
  const data = query.data;
  const currencies = data?.currencies ?? [];
  const baseCurrency = currencies.find((c) => c.isBase) ?? currencies[0];

  const draftFor = (row: PricingRow): Draft => drafts[row.productId]?.draft ?? {
    landedCost: row.landedCost != null ? String(row.landedCost) : '',
    salePrice: row.salePrice != null ? String(row.salePrice) : '',
    margin: marginOf(row.landedCost ?? null, row.salePrice ?? null),
    currencyId: row.currencyId ?? baseCurrency?.id ?? '',
  };

  const update = (row: PricingRow, field: keyof Draft, value: string) => {
    const current = draftFor(row);
    const next: Draft = { ...current, [field]: field === 'currencyId' ? (value === '' ? '' : Number(value)) : value };
    const cost = num(next.landedCost);
    const margin = num(next.margin);
    // Margin is on cost (owner ruling): sale price = landed cost × (1 + margin). Typing one figure
    // moves the one that depends on it, never the one the keeper just typed.
    if (field === 'margin' && cost && cost > 0 && margin != null && Number.isFinite(margin)) {
      next.salePrice = String(round2(cost * (1 + margin / 100)));
    }
    if (field === 'landedCost') {
      if (cost && cost > 0 && margin != null && Number.isFinite(margin)) next.salePrice = String(round2(cost * (1 + margin / 100)));
      else next.margin = marginOf(cost, num(next.salePrice));
    }
    if (field === 'salePrice') next.margin = marginOf(cost, num(next.salePrice));
    setDrafts((all) => ({ ...all, [row.productId]: { draft: next, original: all[row.productId]?.original ?? row } }));
  };

  const changes = Object.values(drafts).filter(({ draft, original }) =>
    num(draft.landedCost) !== (original.landedCost ?? null)
    || num(draft.salePrice) !== (original.salePrice ?? null)
    || (draft.currencyId === '' ? null : draft.currencyId) !== (original.currencyId ?? null));
  const problems = changes.filter(({ draft }) => !valid(num(draft.landedCost)) || !valid(num(draft.salePrice))
    || ((num(draft.landedCost) != null || num(draft.salePrice) != null) && draft.currencyId === ''));

  const save = useMutation({
    mutationFn: () => pricingSheetService.save(changes.map(({ draft, original }): PriceChange => ({
      productId: original.productId,
      landedCost: num(draft.landedCost),
      salePrice: num(draft.salePrice),
      currencyId: draft.currencyId === '' ? null : draft.currencyId,
    }))),
    onSuccess: (result) => {
      setDrafts({});
      queryClient.invalidateQueries({ queryKey: ['pricing-sheet'] });
      queryClient.invalidateQueries({ queryKey: ['stock-price'] });
      enqueueSnackbar(`Saved ${result.saved} ${result.saved === 1 ? 'price' : 'prices'}.`, { variant: 'success' });
    },
  });

  const rows = data?.rows ?? [];

  return (
    <Box sx={{ pb: changes.length > 0 ? 10 : 2 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { md: 'flex-end' }, mb: 2 }}>
        <Box>
          <Typography variant="h4" sx={{ fontWeight: 800 }}>Pricing sheet</Typography>
          <Typography variant="body2" color="text.secondary">
            Landed cost and sale price for every part, in the currency you choose. The RFQ pricing window starts every quote from these.
          </Typography>
        </Box>
        {data && (
          <Chip
            color={data.missingCount > 0 ? 'warning' : 'success'}
            variant="outlined"
            label={data.missingCount > 0 ? `${data.missingCount} ${data.missingCount === 1 ? 'part needs' : 'parts need'} a price` : 'Every part is priced'}
          />
        )}
      </Stack>

      {!canEdit && (
        <Alert severity="info" sx={{ mb: 2 }}>You can read the sheet. Prices are changed by a manager or other authorised person.</Alert>
      )}

      <Paper variant="outlined" sx={{ p: 1.5, mb: 1.5, borderRadius: 3, display: 'flex', flexWrap: 'wrap', gap: 2, alignItems: 'center' }}>
        <SearchField value={search} onChange={(value) => { setSearch(value); setPage(0); }} placeholder="Search part number or name" />
        <FormControlLabel
          control={<Switch checked={missingOnly} onChange={(event) => { setMissingOnly(event.target.checked); setPage(0); }} />}
          label="Only parts missing a price"
        />
        {query.isFetching && <CircularProgress size={18} aria-label="Loading prices" />}
      </Paper>

      {query.isError && (
        <Alert severity="error" sx={{ mb: 1.5 }} action={<Button color="inherit" onClick={() => query.refetch()}>Try again</Button>}>
          {describeError(query.error, 'The pricing sheet could not be loaded.')}
        </Alert>
      )}

      <Paper variant="outlined" sx={{ borderRadius: 3, overflow: 'hidden' }}>
        <TableContainer>
          <Table size="small" aria-label="Pricing sheet">
            <TableHead>
              <TableRow sx={{ '& th': { fontWeight: 700, whiteSpace: 'nowrap' } }}>
                <TableCell>Part</TableCell>
                <TableCell>Unit</TableCell>
                <TableCell>Currency</TableCell>
                <TableCell align="right">Landed cost</TableCell>
                <TableCell align="right">Margin on cost</TableCell>
                <TableCell align="right">Sale price</TableCell>
                <TableCell align="right">Last purchase</TableCell>
                <TableCell align="right">In stock</TableCell>
                <TableCell>Changed</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {query.isSuccess && rows.length === 0 && (
                <TableRow>
                  <TableCell colSpan={9}>
                    <Typography variant="body2" color="text.secondary" sx={{ py: 3, textAlign: 'center' }}>
                      {missingOnly && !search ? 'Every part has a landed cost, a sale price and a currency.' : 'No parts match.'}
                    </Typography>
                  </TableCell>
                </TableRow>
              )}
              {rows.map((row) => {
                const draft = draftFor(row);
                const changed = changes.some((c) => c.original.productId === row.productId);
                const code = currencies.find((c) => c.id === draft.currencyId)?.code ?? row.currencyCode ?? null;
                const cost = num(draft.landedCost);
                const sale = num(draft.salePrice);
                const belowCost = cost != null && sale != null && sale < cost;
                return (
                  <TableRow key={row.productId} hover sx={(theme) => ({ bgcolor: changed ? alpha(theme.palette.primary.main, 0.05) : undefined })}>
                    <TableCell sx={{ maxWidth: 280 }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>{row.partNo}</Typography>
                      {row.name && <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }} noWrap>{row.name}</Typography>}
                    </TableCell>
                    <TableCell>{row.unit ?? '—'}</TableCell>
                    <TableCell>
                      {canEdit ? (
                        <TextField select size="small" value={draft.currencyId} onChange={(event) => update(row, 'currencyId', event.target.value)}
                          slotProps={{ htmlInput: { 'aria-label': `Currency for ${row.partNo}` } }} sx={{ width: 96 }}>
                          {currencies.map((c) => <MenuItem key={c.id} value={c.id}>{c.code}</MenuItem>)}
                        </TextField>
                      ) : (row.currencyCode ?? 'Not set')}
                    </TableCell>
                    <TableCell align="right">
                      {canEdit ? (
                        <TextField size="small" type="number" value={draft.landedCost} onChange={(event) => update(row, 'landedCost', event.target.value)}
                          error={!valid(cost)} placeholder="Not set"
                          slotProps={{ htmlInput: { min: 0, step: 'any', 'aria-label': `Landed cost for ${row.partNo}`, style: { textAlign: 'right' } } }}
                          sx={{ width: 120 }} />
                      ) : <span className="tabular-nums">{row.landedCost != null ? formatMoney(row.landedCost, row.currencyCode) : 'Not set'}</span>}
                    </TableCell>
                    <TableCell align="right">
                      {canEdit ? (
                        <TextField size="small" type="number" value={draft.margin} onChange={(event) => update(row, 'margin', event.target.value)}
                          disabled={!cost} placeholder="—"
                          slotProps={{
                            htmlInput: { step: 'any', 'aria-label': `Margin on cost for ${row.partNo}`, style: { textAlign: 'right' } },
                            input: { endAdornment: <InputAdornment position="end">%</InputAdornment> },
                          }}
                          sx={{ width: 110 }} />
                      ) : <span className="tabular-nums">{draft.margin ? `${draft.margin}%` : '—'}</span>}
                    </TableCell>
                    <TableCell align="right">
                      {canEdit ? (
                        <TextField size="small" type="number" value={draft.salePrice} onChange={(event) => update(row, 'salePrice', event.target.value)}
                          error={!valid(sale)} placeholder="Not set"
                          helperText={belowCost ? 'Below cost' : undefined}
                          slotProps={{
                            htmlInput: { min: 0, step: 'any', 'aria-label': `Sale price for ${row.partNo}`, style: { textAlign: 'right' } },
                            formHelperText: { sx: { color: 'error.main', textAlign: 'right', mx: 0 } },
                          }}
                          sx={{ width: 120 }} />
                      ) : <span className="tabular-nums">{row.salePrice != null ? formatMoney(row.salePrice, row.currencyCode) : 'Not set'}</span>}
                    </TableCell>
                    <TableCell align="right" className="tabular-nums">
                      <Typography variant="body2" color="text.secondary">{row.lastPurchasePrice != null ? formatMoney(row.lastPurchasePrice, code) : '—'}</Typography>
                    </TableCell>
                    <TableCell align="right" className="tabular-nums">{row.onHand.toLocaleString(undefined, { maximumFractionDigits: 2 })}</TableCell>
                    <TableCell>
                      <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
                        {row.changedOn ? `${row.changedBy ?? ''} · ${day(row.changedOn)}` : 'Never'}
                      </Typography>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </TableContainer>
        <TablePagination
          component="div"
          count={data?.total ?? 0}
          page={page}
          onPageChange={(_, next) => setPage(next)}
          rowsPerPage={pageSize}
          onRowsPerPageChange={(event) => { setPageSize(Number(event.target.value)); setPage(0); }}
          rowsPerPageOptions={[25, 50, 100, 200]}
        />
      </Paper>

      {canEdit && changes.length > 0 && (
        <Paper elevation={6} sx={(theme) => ({
          position: 'fixed', left: '50%', transform: 'translateX(-50%)', bottom: 16, zIndex: theme.zIndex.appBar,
          px: 2, py: 1.25, borderRadius: 3, display: 'flex', gap: 2, alignItems: 'center', maxWidth: 'calc(100vw - 32px)',
        })}>
          <Typography variant="body2">
            {problems.length > 0
              ? `${problems.length} ${problems.length === 1 ? 'row needs' : 'rows need'} a price above 0 and a currency`
              : `${changes.length} ${changes.length === 1 ? 'part' : 'parts'} changed`}
          </Typography>
          {save.isError && <Typography variant="body2" color="error.main">{describeError(save.error, 'The prices could not be saved.')}</Typography>}
          <Button onClick={() => { setDrafts({}); save.reset(); }} disabled={save.isPending}>Discard</Button>
          <Button variant="contained" onClick={() => save.mutate()} disabled={problems.length > 0 || save.isPending}
            startIcon={save.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}>
            Save {changes.length} {changes.length === 1 ? 'change' : 'changes'}
          </Button>
        </Paper>
      )}
    </Box>
  );
}
