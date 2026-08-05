import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, FormControl, FormControlLabel, FormLabel, MenuItem, Radio,
  RadioGroup, Stack, TextField, Typography,
} from '@mui/material';
import { toast } from 'react-hot-toast';
import leadService, {
  type ClientCandidateDTO, type LeadItemResponseDTO, type LeadResponseDTO,
} from '../../api/services/leadService';
import customerService from '../../api/services/customerService';
import contactService from '../../api/services/contactService';
import extractionReviewService, {
  type ReviewItemPayload, type SubmitReviewPayload,
} from '../../api/services/extractionReviewService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { candidateExplanation, clientCandidates, confidencePercent, type ClientIdentityLike } from './ClientCell';

/**
 * The one place a person links a lead to a client organisation.
 *
 * Ranked machine candidates first — each with the evidence that produced it —
 * then a search over the tenant's real customers. Two things are deliberately
 * absent:
 *
 *  - No "create new customer". Inventing a customer record from extracted text
 *    is exactly how a wrong client gets onto a lead, and a wrong client is worse
 *    than an unresolved one. New customers are created in Customers, on purpose.
 *  - No way to write a "suggested" state. The machine proposes; a person either
 *    confirms a real customer or leaves the lead unresolved.
 */

/**
 * Builds the review payload that sets (only) the client organisation.
 *
 * Two backend behaviours drive the shape, both verified in
 * `Repositories/LeadRepository.SubmitLeadReviewAsync`:
 *
 *  1. HEADER — only non-null fields are applied. Omitting rfqno/buyersName/etc.
 *     is therefore a no-op, which is what we want: this is a pure "set the
 *     client" write and must not silently re-stamp other header fields.
 *  2. ITEMS — items that exist in the database but are absent from the payload
 *     are DELETED. Sending `items: []` would wipe every line on the lead, so the
 *     stored lines are echoed back in full. `ApplyItemFields` assigns every
 *     field unconditionally, so the echo has to be faithful.
 *
 * The single deliberate omission is a non-positive quantity: the backend
 * rejects `quantity <= 0` on save, and it only applies quantity when supplied
 * (`if (dto.Quantity.HasValue)`), so omitting it preserves the stored value
 * instead of failing the whole action over an unrelated extraction defect.
 */
export const buildClientReviewPayload = (
  lead: Pick<LeadResponseDTO, 'reviewVersion' | 'leadItems'>,
  selection: { customerId: number; contactId?: number | null },
): SubmitReviewPayload => ({
  action: 'save',
  // ReviewVersion is 1-based server-side and the DTO rejects 0.
  expectedVersion: Math.max(1, lead.reviewVersion ?? 1),
  header: {
    customerId: selection.customerId,
    ...(selection.contactId != null ? { contactId: selection.contactId } : {}),
  },
  items: (lead.leadItems ?? []).map<ReviewItemPayload>((it: LeadItemResponseDTO) => ({
    id: it.id,
    lineItemNo: it.lineItemNo || undefined,
    productShortName: it.productShortName || undefined,
    productShortDescription: it.productShortDescription || undefined,
    commodityProduct: it.commodityProduct || undefined,
    itemMaterialCode: it.itemMaterialCode || undefined,
    currency: it.currency || undefined,
    unitOfMeasure: it.unitOfMeasure || undefined,
    unitPrice: it.unitPrice ?? undefined,
    quantity: it.quantity != null && it.quantity > 0 ? it.quantity : undefined,
    manufacturerName: it.manufacturerName || undefined,
    manufacturerPartNumber: it.manufacturerPartNumber || undefined,
    alternateProductName: it.alternateProductName || undefined,
    alternatePartNumber: it.alternatePartNumber || undefined,
    itemText: it.itemText || undefined,
    leadTime: it.leadTime || undefined,
  })),
});

/**
 * Fallback wording when the server does not supply its own sentence. The
 * pointer to Extraction Review is load-bearing: the client link is written
 * through the extraction-review endpoint, so a lead that is no longer awaiting
 * review, or whose lines fail review validation, is refused there.
 */
export const CLIENT_LINK_FAILURE_MESSAGE =
  'The client could not be linked. Nothing was changed — open this lead in Extraction Review to complete it there.';

export interface ClientSelection {
  customerId: number;
  contactId?: number | null;
  /** Display name of the chosen client, so a deferred host can render it. */
  customerName?: string | null;
}

export interface ResolveClientDialogProps {
  open: boolean;
  /** Lead to resolve. Null keeps the dialog closed. */
  leadId: number | null;
  /**
   * Candidates already on the row, so the dialog can rank instantly instead of
   * waiting on a round trip. The dedicated endpoint refines them once loaded.
   */
  lead?: ClientIdentityLike | null;
  onClose: () => void;
  /** Fired after a successful link so the caller can refresh its own queries. */
  onResolved?: (customerId: number) => void;
  /**
   * DEFERRED MODE. When supplied, confirming reports the choice instead of
   * writing it, and the host submits it with its own payload.
   *
   * This exists for the Extraction Review workbench: that page holds unsaved
   * header and line-item edits plus the lead's `reviewVersion`. A write from
   * here would bump that version and make the reviewer's own Save conflict, so
   * the client choice travels in the reviewer's single submission instead.
   */
  onSelect?: (selection: ClientSelection) => void;
}

const CandidateRow: React.FC<{ candidate: ClientCandidateDTO }> = ({ candidate }) => {
  const pct = confidencePercent(candidate.confidence);
  const why = candidateExplanation(candidate);
  return (
    <Box sx={{ py: 0.5, minWidth: 0 }}>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Typography sx={{ fontWeight: 700, fontSize: '0.9rem', overflowWrap: 'anywhere' }}>
          {candidate.customerName?.trim() || `Customer #${candidate.customerId}`}
        </Typography>
        {pct != null && (
          <Chip
            size="small"
            label={`${pct}% confident`}
            sx={{ height: 18, fontSize: '0.65rem', fontWeight: 800, color: 'warning.main', bgcolor: 'transparent', border: '1px solid', borderColor: 'warning.main' }}
          />
        )}
      </Stack>
      {why && (
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', lineHeight: 1.4 }}>
          {why}
        </Typography>
      )}
    </Box>
  );
};

const ResolveClientDialog: React.FC<ResolveClientDialogProps> = ({
  open, leadId, lead, onClose, onResolved, onSelect,
}) => {
  const queryClient = useQueryClient();
  const [selectedCustomerId, setSelectedCustomerId] = React.useState<number | null>(null);
  const [selectedContactId, setSelectedContactId] = React.useState<number | ''>('');
  const [searchTerm, setSearchTerm] = React.useState('');
  const [debouncedTerm, setDebouncedTerm] = React.useState('');

  const isOpen = open && leadId != null;
  const deferred = typeof onSelect === 'function';

  // Reset every time the dialog is opened for a lead — a stale selection from a
  // previous row must never be carried into a different lead.
  React.useEffect(() => {
    if (!isOpen) return;
    setSelectedCustomerId(null);
    setSelectedContactId('');
    setSearchTerm('');
    setDebouncedTerm('');
  }, [isOpen, leadId]);

  React.useEffect(() => {
    const handle = setTimeout(() => setDebouncedTerm(searchTerm.trim()), 300);
    return () => clearTimeout(handle);
  }, [searchTerm]);

  // The full lead is needed to build a lossless review payload (its line items
  // must be echoed back or the backend deletes them). Deferred mode never
  // submits, so it does not need — and must not wait on — this fetch.
  const leadQuery = useQuery({
    queryKey: ['lead-detail', Number(leadId)],
    queryFn: () => leadService.getById(Number(leadId)),
    enabled: isOpen && !deferred,
  });

  const candidatesQuery = useQuery({
    queryKey: ['lead-client-candidates', Number(leadId)],
    queryFn: () => leadService.getClientCandidates(Number(leadId)),
    enabled: isOpen,
    retry: false,
    staleTime: 60_000,
  });

  const searchQuery = useQuery({
    queryKey: ['client-customer-search', debouncedTerm],
    queryFn: () => customerService.getAll({ name: debouncedTerm, pageSize: 10, isActive: true }),
    enabled: isOpen && debouncedTerm.length >= 2,
    retry: false,
    staleTime: 30_000,
  });

  const contactsQuery = useQuery({
    queryKey: ['client-customer-contacts', selectedCustomerId],
    queryFn: () => contactService.getByCustomer(Number(selectedCustomerId)),
    enabled: isOpen && selectedCustomerId != null,
    retry: false,
  });

  // Prefer the dedicated endpoint; fall back to whatever the row already carried
  // so the dialog is useful before the endpoint exists.
  const candidates = React.useMemo<ClientCandidateDTO[]>(() => {
    const fromEndpoint = candidatesQuery.data ?? [];
    if (fromEndpoint.length > 0) return [...fromEndpoint].sort((a, b) => (a.rank ?? 0) - (b.rank ?? 0));
    const fromDetail = leadQuery.data ? clientCandidates(leadQuery.data) : [];
    if (fromDetail.length > 0) return fromDetail;
    return lead ? clientCandidates(lead) : [];
  }, [candidatesQuery.data, leadQuery.data, lead]);

  const searchResults = React.useMemo(() => {
    const items = searchQuery.data?.items ?? [];
    const candidateIds = new Set(candidates.map((c) => c.customerId));
    return items.filter((c) => !candidateIds.has(c.id));
  }, [searchQuery.data, candidates]);

  const contacts = contactsQuery.data ?? [];

  /** Name of whatever is currently selected, for the deferred handoff. */
  const selectedCustomerName = React.useMemo<string | null>(() => {
    if (selectedCustomerId == null) return null;
    const candidate = candidates.find((c) => c.customerId === selectedCustomerId);
    if (candidate?.customerName) return candidate.customerName;
    const found = (searchQuery.data?.items ?? []).find((c) => c.id === selectedCustomerId);
    return found?.name ?? null;
  }, [selectedCustomerId, candidates, searchQuery.data]);

  const mutation = useMutation({
    mutationFn: async (selection: { customerId: number; contactId?: number | null }) => {
      const full = leadQuery.data;
      if (!full) throw new Error('lead-not-loaded');
      return extractionReviewService.submitReview(full.id, buildClientReviewPayload(full, selection));
    },
    onSuccess: (_data, selection) => {
      toast.success('Client linked to this lead.');
      queryClient.invalidateQueries({ queryKey: ['leads'] });
      queryClient.invalidateQueries({ queryKey: ['leads-outstanding'] });
      queryClient.invalidateQueries({ queryKey: ['leads-assigned'] });
      queryClient.invalidateQueries({ queryKey: ['lead-detail', Number(leadId)] });
      queryClient.invalidateQueries({ queryKey: ['lead-client-candidates', Number(leadId)] });
      queryClient.invalidateQueries({ queryKey: ['needs-review-detail', Number(leadId)] });
      onResolved?.(selection.customerId);
      onClose();
    },
    onError: (error: unknown) => {
      toast.error(presentableErrorMessage(error, CLIENT_LINK_FAILURE_MESSAGE));
    },
  });

  const handleLeaveUnresolved = () => {
    toast('Left unresolved. This lead still shows as having no client.', { icon: 'ℹ️' });
    onClose();
  };

  const handleConfirm = () => {
    if (selectedCustomerId == null) return;
    const contactId = selectedContactId === '' ? null : selectedContactId;
    if (deferred) {
      onSelect?.({ customerId: selectedCustomerId, contactId, customerName: selectedCustomerName });
      onClose();
      return;
    }
    mutation.mutate({ customerId: selectedCustomerId, contactId });
  };

  const leadLoading = !deferred && leadQuery.isPending && isOpen;
  const canConfirm = selectedCustomerId != null && !mutation.isPending && (deferred || !!leadQuery.data);

  return (
    <Dialog open={isOpen} onClose={() => !mutation.isPending && onClose()} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Which client is this lead from?</DialogTitle>
      <DialogContent dividers>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Pick the organisation that sent this enquiry. Nexora only suggests — nothing is linked
          until you confirm it here.
        </Typography>

        {leadQuery.isError && !deferred && (
          <Alert severity="error" sx={{ mb: 2 }} action={<Button color="inherit" size="small" onClick={() => leadQuery.refetch()}>Retry</Button>}>
            We couldn&apos;t load this lead, so the client cannot be linked right now.
          </Alert>
        )}

        {leadLoading && (
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', py: 2 }} role="status" aria-live="polite">
            <CircularProgress size={20} />
            <Typography variant="body2" color="text.secondary">Loading this lead…</Typography>
          </Stack>
        )}

        <FormControl component="fieldset" sx={{ width: '100%' }}>
          <FormLabel component="legend" sx={{ fontWeight: 800, fontSize: '0.7rem', textTransform: 'uppercase', letterSpacing: '0.04em' }}>
            {candidates.length > 0 ? 'What Nexora found' : 'Client'}
          </FormLabel>
          <RadioGroup
            value={selectedCustomerId == null ? '' : String(selectedCustomerId)}
            onChange={(_e, value) => {
              setSelectedCustomerId(Number(value));
              setSelectedContactId('');
            }}
          >
            {candidates.map((candidate) => (
              <FormControlLabel
                key={`candidate-${candidate.customerId}`}
                value={String(candidate.customerId)}
                control={<Radio size="small" />}
                label={<CandidateRow candidate={candidate} />}
                sx={{ alignItems: 'flex-start', mr: 0, mb: 0.5, '& .MuiRadio-root': { pt: 0.75 } }}
              />
            ))}

            {candidates.length === 0 && !leadLoading && (
              <Typography variant="body2" color="text.secondary" sx={{ py: 1 }}>
                Nexora has no suggestion for this lead. Search for the client below.
              </Typography>
            )}

            <Divider sx={{ my: 2 }} />

            <TextField
              size="small"
              fullWidth
              label="Search all clients by name"
              placeholder="Start typing a company name…"
              value={searchTerm}
              onChange={(e) => setSearchTerm(e.target.value)}
              disabled={mutation.isPending}
              sx={{ mb: 1.5 }}
            />

            {searchQuery.isFetching && debouncedTerm.length >= 2 && (
              <Stack direction="row" spacing={1} sx={{ alignItems: 'center', py: 0.5 }} role="status" aria-live="polite">
                <CircularProgress size={16} />
                <Typography variant="caption" color="text.secondary">Searching…</Typography>
              </Stack>
            )}

            {searchQuery.isError && (
              <Alert severity="warning" sx={{ my: 1 }}>Client search is unavailable right now.</Alert>
            )}

            {searchResults.map((customer) => (
              <FormControlLabel
                key={`customer-${customer.id}`}
                value={String(customer.id)}
                control={<Radio size="small" />}
                label={
                  <Box sx={{ py: 0.5, minWidth: 0 }}>
                    <Typography sx={{ fontWeight: 700, fontSize: '0.9rem', overflowWrap: 'anywhere' }}>{customer.name}</Typography>
                    {customer.contactEmail && (
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>{customer.contactEmail}</Typography>
                    )}
                  </Box>
                }
                sx={{ alignItems: 'flex-start', mr: 0, mb: 0.5, '& .MuiRadio-root': { pt: 0.75 } }}
              />
            ))}

            {debouncedTerm.length >= 2 && !searchQuery.isFetching && !searchQuery.isError && searchResults.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ py: 0.5 }}>
                No client matches “{debouncedTerm}”. Clients are created in the Customers area, not here.
              </Typography>
            )}
          </RadioGroup>
        </FormControl>

        {selectedCustomerId != null && (
          <Box sx={{ mt: 2 }}>
            <TextField
              select
              size="small"
              fullWidth
              label="Buyer contact at this client (optional)"
              value={selectedContactId === '' ? '' : String(selectedContactId)}
              onChange={(e) => setSelectedContactId(e.target.value === '' ? '' : Number(e.target.value))}
              disabled={mutation.isPending || contactsQuery.isPending}
              helperText="Leave blank if you are not sure who the buyer is — the client link still counts."
            >
              <MenuItem value="">Not sure yet</MenuItem>
              {contacts
                .filter((contact) => contact.isActive !== false)
                .map((contact) => (
                  <MenuItem key={contact.id} value={String(contact.id)}>
                    {[contact.firstName, contact.lastName].filter(Boolean).join(' ') || `Contact #${contact.id}`}
                    {contact.email ? ` — ${contact.email}` : ''}
                  </MenuItem>
                ))}
            </TextField>
          </Box>
        )}
      </DialogContent>
      <DialogActions sx={{ p: 2, flexWrap: 'wrap', gap: 1, justifyContent: 'space-between' }}>
        <Button onClick={handleLeaveUnresolved} color="inherit" disabled={mutation.isPending} sx={{ fontWeight: 700 }}>
          None of these — leave unresolved
        </Button>
        <Stack direction="row" spacing={1}>
          <Button onClick={onClose} color="inherit" disabled={mutation.isPending}>Cancel</Button>
          <Button
            variant="contained"
            disabled={!canConfirm}
            startIcon={mutation.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
            onClick={handleConfirm}
            sx={{ fontWeight: 800 }}
          >
            Confirm client
          </Button>
        </Stack>
      </DialogActions>
    </Dialog>
  );
};

export default ResolveClientDialog;
