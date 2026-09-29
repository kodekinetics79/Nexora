import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Autocomplete, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle,
  MenuItem, Stack, TextField, Typography, Alert, CircularProgress,
} from '@mui/material';
import { UploadFile as UploadFileIcon, InsertDriveFile as FileIcon } from '@mui/icons-material';
import dayjs from 'dayjs';
import quoteService from '../../../api/services/quoteService';
import rfqService, { type RfqResponseDTO } from '../../../api/services/rfqService';
import currencyService from '../../../api/services/currencyService';
import commercialPolicyService from '../../../api/services/commercialPolicyService';
import { presentableErrorMessage } from '../../../utils/apiErrors';
import ValidityDateField from './ValidityDateField';

/** What the inspection gate admits for a quote a rep made elsewhere. */
const ACCEPTED = '.pdf,.doc,.docx,.xls,.xlsx,.xlsm,.png,.jpg,.jpeg,.msg,.eml';

interface UploadQuoteDialogProps {
  open: boolean;
  businessUnitId?: number;
  onClose: () => void;
  onSaved: (quote: { quoteId: number; quoteNo: string; replacedDraftNo?: string | null }) => void;
}

/**
 * An RFQ carries one quote. One Nexora never sent is a draft the upload replaces; one that reached
 * the customer is not, and the RFQ cannot be chosen.
 */
const unsentDraftOf = (rfq: RfqResponseDTO) => (rfq.latestQuoteNo && !rfq.latestQuoteSentOn ? rfq.latestQuoteNo : null);
const takenBy = (rfq: RfqResponseDTO) => (rfq.latestQuoteNo && rfq.latestQuoteSentOn ? rfq.latestQuoteNo : null);

const money = (value: number) => value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/**
 * A quote the rep made outside Nexora — by hand, in Excel, straight on the customer's portal. They
 * drop the file, say which RFQ it answers, and it joins the list as a sent quote with its file kept.
 * Everything is pre-filled that can be (today, the base currency, the number from the file name).
 */
const UploadQuoteDialog: React.FC<UploadQuoteDialogProps> = ({ open, businessUnitId, onClose, onSaved }) => {
  const queryClient = useQueryClient();
  const fileInput = React.useRef<HTMLInputElement>(null);
  const [file, setFile] = React.useState<File | null>(null);
  const [dragging, setDragging] = React.useState(false);
  const [rfq, setRfq] = React.useState<RfqResponseDTO | null>(null);
  const [rfqSearch, setRfqSearch] = React.useState('');
  const [debouncedSearch, setDebouncedSearch] = React.useState('');
  const [quoteNumber, setQuoteNumber] = React.useState('');
  const [sentOn, setSentOn] = React.useState(dayjs().format('YYYY-MM-DD'));
  const [validUntil, setValidUntil] = React.useState('');
  const [amount, setAmount] = React.useState('');
  const [currencyId, setCurrencyId] = React.useState<number | ''>('');

  React.useEffect(() => {
    if (!open) return;
    setFile(null);
    setRfq(null);
    setRfqSearch('');
    setQuoteNumber('');
    setSentOn(dayjs().format('YYYY-MM-DD'));
    setValidUntil('');
    setAmount('');
  }, [open]);

  React.useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedSearch(rfqSearch.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [rfqSearch]);

  // RFQs not yet quoted come first from the server, so the first page is the likely answer.
  const rfqsQuery = useQuery({
    queryKey: ['upload-quote-rfqs', businessUnitId, debouncedSearch],
    queryFn: () => rfqService.getAll({ pageNumber: 1, pageSize: 20, businessUnitId, search: debouncedSearch || undefined })
      .then((page) => page.items),
    enabled: open,
  });
  const currenciesQuery = useQuery({
    queryKey: ['currencies-list', businessUnitId],
    queryFn: () => currencyService.getAll({ pageSize: 100, businessUnitId }).then((page) => page.items),
    enabled: open,
  });
  const currencies = React.useMemo(() => currenciesQuery.data ?? [], [currenciesQuery.data]);
  React.useEffect(() => {
    if (open && currencyId === '' && currencies.length > 0)
      setCurrencyId((currencies.find((c) => c.isBaseCurrency) ?? currencies[0]).id);
  }, [open, currencies, currencyId]);
  const policyQuery = useQuery({
    queryKey: ['commercial-policy'],
    queryFn: () => commercialPolicyService.getPolicy(),
    enabled: open,
  });
  const vatPercent = policyQuery.data?.outputTaxRatePercent ?? null;
  const currencyCode = currencies.find((c) => c.id === currencyId)?.code ?? '';

  const choose = (picked: File | undefined | null) => {
    if (!picked) return;
    setFile(picked);
    // The number printed on the quote is usually in its file name. A guess the rep can overwrite.
    if (!quoteNumber.trim()) setQuoteNumber(picked.name.replace(/\.[^.]+$/, '').slice(0, 100));
  };

  const amountValue = Number(amount);
  const ready = Boolean(file && rfq && quoteNumber.trim() && sentOn && currencyId !== '' && amountValue > 0);

  const save = useMutation({
    mutationFn: () => quoteService.upload({
      file: file!,
      rfqId: rfq!.id,
      quoteNumber,
      sentOn,
      validUntil: validUntil || null,
      currencyId: Number(currencyId),
      amount: amountValue,
    }),
    onSuccess: (saved) => {
      queryClient.invalidateQueries({ queryKey: ['quotes'] });
      queryClient.invalidateQueries({ queryKey: ['upload-quote-rfqs'] });
      onSaved(saved);
    },
  });


  // A refusal from the last attempt never greets a fresh window.
  const resetSave = save.reset;
  React.useEffect(() => { if (open) resetSave(); }, [open, resetSave]);
  return (
    <Dialog open={open} onClose={save.isPending ? undefined : onClose} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Upload a quote</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Box
            role="button"
            tabIndex={0}
            aria-label="Choose the quote file"
            onClick={() => fileInput.current?.click()}
            onKeyDown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fileInput.current?.click(); } }}
            onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
            onDragLeave={() => setDragging(false)}
            onDrop={(e) => { e.preventDefault(); setDragging(false); choose(e.dataTransfer.files?.[0]); }}
            sx={{
              border: '2px dashed', borderColor: dragging ? 'primary.main' : 'divider', borderRadius: 2,
              bgcolor: dragging ? 'action.hover' : 'transparent', p: 2, cursor: 'pointer',
              display: 'flex', alignItems: 'center', gap: 1.5,
              '&:hover, &:focus-visible': { borderColor: 'primary.main', outline: 'none' },
            }}
          >
            {file ? <FileIcon color="primary" /> : <UploadFileIcon color="action" />}
            <Box sx={{ minWidth: 0 }}>
              <Typography sx={{ fontWeight: 700 }} noWrap>
                {file ? file.name : 'Drop the quote file here, or click to choose'}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                {file ? `${(file.size / 1024).toFixed(0)} KB · click to change` : 'PDF, Word, Excel or a photo'}
              </Typography>
            </Box>
            <input
              ref={fileInput}
              type="file"
              hidden
              accept={ACCEPTED}
              data-testid="upload-quote-file"
              onChange={(e) => { choose(e.target.files?.[0]); e.target.value = ''; }}
            />
          </Box>

          <Autocomplete
            options={rfqsQuery.data ?? []}
            value={rfq}
            loading={rfqsQuery.isFetching}
            onChange={(_, value) => setRfq(value)}
            inputValue={rfqSearch}
            onInputChange={(_, value, reason) => { if (reason !== 'reset') setRfqSearch(value); }}
            filterOptions={(options) => options}
            getOptionLabel={(option) => option.rfqno}
            isOptionEqualToValue={(option, value) => option.id === value.id}
            getOptionDisabled={(option) => Boolean(takenBy(option))}
            noOptionsText={debouncedSearch ? 'No RFQ matches' : 'No RFQs'}
            renderOption={(props, option) => {
              const { key, ...optionProps } = props as React.HTMLAttributes<HTMLLIElement> & { key: string };
              return (
                <Box component="li" key={key} {...optionProps} sx={{ display: 'block !important', py: 1 }}>
                  <Typography sx={{ fontWeight: 700, fontSize: '0.875rem' }}>{option.rfqno}</Typography>
                  <Typography variant="caption" color="text.secondary" noWrap component="div">
                    {[option.customerName || 'No customer',
                      takenBy(option) ? `Has ${takenBy(option)}` : null,
                      unsentDraftOf(option) ? `Replaces draft ${unsentDraftOf(option)}` : null].filter(Boolean).join(' · ')}
                  </Typography>
                </Box>
              );
            }}
            renderInput={(params) => (
              <TextField
                {...params}
                label="RFQ"
                required
                placeholder="RFQ number or customer"
                helperText={rfq
                  ? [rfq.customerName || 'This RFQ has no customer yet',
                    unsentDraftOf(rfq) ? `Replaces draft ${unsentDraftOf(rfq)}` : null].filter(Boolean).join(' · ')
                  : ' '}
                slotProps={{
                  // The whole of params.slotProps first: it carries the refs Autocomplete needs.
                  ...params.slotProps,
                  input: {
                    ...params.slotProps.input,
                    endAdornment: (
                      <>
                        {rfqsQuery.isFetching ? <CircularProgress size={16} /> : null}
                        {params.slotProps.input.endAdornment}
                      </>
                    ),
                  },
                }}
              />
            )}
          />

          <TextField
            label="Quote no. on the file"
            required
            value={quoteNumber}
            onChange={(e) => setQuoteNumber(e.target.value)}
            slotProps={{ htmlInput: { maxLength: 100 } }}
          />

          <Stack direction="row" spacing={2}>
            <TextField
              label="Date sent"
              type="date"
              required
              fullWidth
              value={sentOn}
              onChange={(e) => setSentOn(e.target.value)}
              slotProps={{ inputLabel: { shrink: true }, htmlInput: { max: dayjs().format('YYYY-MM-DD') } }}
            />
            <ValidityDateField
              fullWidth
              value={validUntil}
              onChange={setValidUntil}
              from={sentOn}
              min={sentOn || undefined}
              sx={{ width: '100%' }}
            />
          </Stack>

          <Stack direction="row" spacing={2}>
            <TextField
              label="Amount before VAT"
              required
              fullWidth
              type="number"
              value={amount}
              onChange={(e) => setAmount(e.target.value)}
              slotProps={{ htmlInput: { min: 0, step: '0.01', inputMode: 'decimal' } }}
              helperText={vatPercent !== null && amountValue > 0
                ? `With VAT ${vatPercent}%: ${currencyCode} ${money(amountValue * (1 + vatPercent / 100))}`
                : ' '}
            />
            <TextField
              select
              label="Currency"
              required
              value={currencyId}
              onChange={(e) => setCurrencyId(Number(e.target.value))}
              sx={{ minWidth: 120 }}
              helperText=" "
            >
              {currencies.map((c) => <MenuItem key={c.id} value={c.id}>{c.code}</MenuItem>)}
            </TextField>
          </Stack>

          {save.isError && (
            <Alert severity="error">{presentableErrorMessage(save.error, 'The quote could not be saved.')}</Alert>
          )}
        </Stack>
      </DialogContent>
      <DialogActions sx={{ px: 3, pb: 2 }}>
        <Button onClick={onClose} disabled={save.isPending}>Cancel</Button>
        <Button
          variant="contained"
          disabled={!ready || save.isPending}
          onClick={() => save.mutate()}
          sx={{ fontWeight: 800 }}
        >
          {save.isPending ? 'Saving…' : 'Save quote'}
        </Button>
      </DialogActions>
    </Dialog>
  );
};

export default UploadQuoteDialog;
