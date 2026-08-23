import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, FormControl, FormControlLabel, FormLabel, MenuItem, Radio,
  RadioGroup, Stack, TextField, Typography,
} from '@mui/material';
import { AddBusiness as AddBusinessIcon } from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import leadService, { type ClientCandidateDTO } from '../../api/services/leadService';
import customerService from '../../api/services/customerService';
import contactService from '../../api/services/contactService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { candidateExplanation, clientCandidates, confidencePercent, type ClientIdentityLike } from './ClientCell';

/**
 * The one place a person links a lead to a client organisation.
 *
 * Ranked machine candidates first — each with the evidence that produced it —
 * then a search over the tenant's real customers. Two things are deliberately
 * absent:
 *
 *  - Creating a client here IS allowed, but only as an explicit, editable act.
 *    This reverses an earlier decision to forbid it outright, whose reasoning —
 *    inventing a customer from extracted text is how a wrong client gets onto a
 *    lead, and a wrong client is worse than an unresolved one — still stands and
 *    is what shapes the flow. The mitigation is that nothing is inferred and
 *    written in one step: the form opens pre-filled from the enquiry, every field
 *    stays editable, creating a client only SELECTS it, and linking it to the lead
 *    remains the separate Confirm action it always was. The alternative in
 *    practice was worse — an operator holding a genuine inquiry from a new buyer
 *    had no way forward without abandoning the lead.
 *  - No way to write a "suggested" state. The machine proposes; a person either
 *    confirms a real customer or leaves the lead unresolved.
 */

/**
 * Fallback wording when the server does not supply its own sentence.
 *
 * It used to end "open this lead in Extraction Review to complete it there", which was
 * advice that could not be followed: the reason the link failed was that the lead was no
 * longer IN extraction review, and there is no way back into it. Sending an operator to a
 * screen that will refuse them is worse than saying nothing. The link now has its own
 * endpoint with none of those preconditions, so the only honest fallback is that the write
 * did not happen and nothing changed.
 */
export const CLIENT_LINK_FAILURE_MESSAGE =
  'The client could not be linked, and nothing was changed. Try again in a moment.';

export interface ClientSelection {
  customerId: number;
  contactId?: number | null;
  /** Display name of the chosen client, so a deferred host can render it. */
  customerName?: string | null;
}

export interface ResolveClientDialogProps {
  open: boolean;
  /**
   * Evidence from the enquiry used to pre-fill the "create client" form.
   *
   * Only `name` and `email` are ever populated from extraction, because only those two are
   * actually read off the message. Addresses, tax and registration numbers are NOT guessed —
   * a plausible-looking invented address on a customer record is worse than an empty one, and
   * this dialog's whole reason for existing is that a wrong client beats no client only in the
   * eyes of someone who never has to unpick it. The remaining fields are offered blank so the
   * operator can complete the record here instead of making a second trip to Customers.
   */
  prefill?: {
    name?: string | null;
    email?: string | null;
    /** Buyer's personal name, shown as context. Not a customer field. */
    contactName?: string | null;
    /** Verbatim snippet that named the organisation, so the operator can judge the name. */
    evidence?: string | null;
  } | null;
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
  open, leadId, lead, onClose, onResolved, onSelect, prefill,
}) => {
  const queryClient = useQueryClient();
  const [selectedCustomerId, setSelectedCustomerId] = React.useState<number | null>(null);
  const [selectedContactId, setSelectedContactId] = React.useState<number | ''>('');
  const [searchTerm, setSearchTerm] = React.useState('');
  const [debouncedTerm, setDebouncedTerm] = React.useState('');
  // Inline client creation. Held here rather than in a nested dialog so the operator never
  // loses the lead they were resolving.
  const [creating, setCreating] = React.useState(false);
  const [newName, setNewName] = React.useState('');
  const [newEmail, setNewEmail] = React.useState('');
  const [newDetails, setNewDetails] = React.useState({
    billingAddressLine1: '', billingCity: '', billingState: '', billingCountry: '',
    billingPostalCode: '', commercialRegistrationNumber: '', taxRegistrationNumber: '', sector: '',
  });
  const setDetail = (key: keyof typeof newDetails) => (event: React.ChangeEvent<HTMLInputElement>) =>
    setNewDetails((current) => ({ ...current, [key]: event.target.value }));

  const isOpen = open && leadId != null;
  const deferred = typeof onSelect === 'function';

  // Reset every time the dialog is opened for a lead — a stale selection from a
  // previous row must never be carried into a different lead.
  React.useEffect(() => {
    if (!isOpen) return;
    setSelectedCustomerId(null);
    setSelectedContactId('');
    // Opens on the organisation the message named, so the search has already been run by the
    // time the operator looks at it. Typing the company name back in by hand was busywork the
    // enquiry had already done — and it is what made "no match" feel like a dead end rather
    // than the start of creating the client.
    const opening = prefill?.name?.trim() ?? '';
    setSearchTerm(opening);
    setDebouncedTerm(opening);
    setCreating(false);
    setNewDetails({
      billingAddressLine1: '', billingCity: '', billingState: '', billingCountry: '',
      billingPostalCode: '', commercialRegistrationNumber: '', taxRegistrationNumber: '', sector: '',
    });
  }, [isOpen, leadId, prefill?.name]);

  React.useEffect(() => {
    const handle = setTimeout(() => setDebouncedTerm(searchTerm.trim()), 300);
    return () => clearTimeout(handle);
  }, [searchTerm]);

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
    return lead ? clientCandidates(lead) : [];
  }, [candidatesQuery.data, lead]);

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
    mutationFn: (selection: { customerId: number; contactId?: number | null }) =>
      leadService.linkClient(Number(leadId), selection),
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

  /**
   * Creates the client, then SELECTS it — it does not link the lead. Linking stays with the
   * existing Confirm button so the operator still makes that call explicitly, and a mis-typed
   * name is corrected before anything is attached to the inquiry.
   */
  const createClient = useMutation({
    mutationFn: async () => {
      const form = new FormData();
      form.append('Name', newName.trim());
      if (newEmail.trim()) form.append('ContactEmail', newEmail.trim());
      // Only non-empty optional fields are sent. An empty string is not "no value" to a
      // server that stores what it is given — it is a blank that later reads as answered.
      const optional: Array<[string, string]> = [
        ['BillingAddressLine1', newDetails.billingAddressLine1],
        ['BillingCity', newDetails.billingCity],
        ['BillingState', newDetails.billingState],
        ['BillingCountry', newDetails.billingCountry],
        ['BillingPostalCode', newDetails.billingPostalCode],
        ['CommercialRegistrationNumber', newDetails.commercialRegistrationNumber],
        ['TaxRegistrationNumber', newDetails.taxRegistrationNumber],
        ['Sector', newDetails.sector],
      ];
      for (const [field, value] of optional) if (value.trim()) form.append(field, value.trim());
      return customerService.create(form);
    },
    onSuccess: (created) => {
      const id = Number((created as { id?: number | string }).id);
      queryClient.invalidateQueries({ queryKey: ['customer-search'] });
      setCreating(false);
      setSearchTerm(newName.trim());
      if (Number.isFinite(id) && id > 0) setSelectedCustomerId(id);
      toast.success(`Client “${newName.trim()}” created — confirm to link it to this lead.`);
    },
    onError: (error) => toast.error(presentableErrorMessage(error, 'The client could not be created.')),
  });

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

  const canConfirm = selectedCustomerId != null && !mutation.isPending;

  return (
    <Dialog open={isOpen} onClose={() => !mutation.isPending && onClose()} fullWidth maxWidth="sm">
      <DialogTitle sx={{ fontWeight: 800 }}>Which client is this lead from?</DialogTitle>
      <DialogContent dividers>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Pick the organisation that sent this enquiry. Nexora only suggests — nothing is linked
          until you confirm it here.
        </Typography>

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

            {candidates.length === 0 && !candidatesQuery.isPending && (
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

            {debouncedTerm.length >= 2 && !searchQuery.isFetching && !searchQuery.isError && searchResults.length === 0 && !creating && (
              <Box sx={{ py: 0.5 }}>
                <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
                  No client matches “{debouncedTerm}”.
                </Typography>
                {/* A DEAD END IS NOT AN ANSWER. This used to say "clients are created in the
                    Customers area, not here", which leaves the operator holding a real inquiry
                    from a real buyer and no way forward without abandoning the lead. The
                    enquiry itself is the best evidence of who the client is, so the form below
                    opens pre-filled from it — and stays editable, because extracted evidence is
                    a starting point rather than a fact. */}
                <Button
                  size="small"
                  variant="outlined"
                  startIcon={<AddBusinessIcon />}
                  onClick={() => {
                    setNewName(prefill?.name?.trim() || debouncedTerm);
                    setNewEmail(prefill?.email?.trim() || '');
                    setCreating(true);
                  }}
                  sx={{ fontWeight: 700 }}
                >
                  Create “{debouncedTerm}” as a new client
                </Button>
              </Box>
            )}
          </RadioGroup>
        </FormControl>

        {creating && (
          <Box sx={{ mt: 2, p: 2, border: '1px solid', borderColor: 'divider', borderRadius: 2 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 0.5 }}>
              New client
            </Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
              Name and email are pre-filled from this enquiry — correct anything that is wrong
              before you save. The rest is blank on purpose: nothing below was stated in the
              message, and a guessed address is worse than an empty one.
            </Typography>
            {prefill?.evidence && (
              <Alert severity="info" sx={{ mb: 1.5, py: 0.25 }}>
                <Typography variant="caption" sx={{ display: 'block' }}>
                  The message named the organisation here: “{prefill.evidence}”
                  {prefill.contactName ? ` — buyer contact ${prefill.contactName}.` : '.'}
                </Typography>
              </Alert>
            )}
            <Stack spacing={1.5}>
              <TextField
                size="small" fullWidth required autoFocus
                label="Client name"
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                disabled={createClient.isPending}
                error={newName.trim().length === 0}
                helperText={newName.trim().length === 0 ? 'A client needs a name.' : ' '}
              />
              <TextField
                size="small" fullWidth
                label="Contact email"
                value={newEmail}
                onChange={(e) => setNewEmail(e.target.value)}
                disabled={createClient.isPending}
                helperText="The address this enquiry arrived from."
              />
              <Divider textAlign="left" sx={{ pt: 0.5 }}>
                <Typography variant="caption" color="text.secondary">
                  Optional — complete now or later in Customers
                </Typography>
              </Divider>
              <TextField size="small" fullWidth label="Address" value={newDetails.billingAddressLine1}
                onChange={setDetail('billingAddressLine1')} disabled={createClient.isPending} />
              <Stack direction="row" spacing={1.5}>
                <TextField size="small" fullWidth label="City" value={newDetails.billingCity}
                  onChange={setDetail('billingCity')} disabled={createClient.isPending} />
                <TextField size="small" fullWidth label="State / region" value={newDetails.billingState}
                  onChange={setDetail('billingState')} disabled={createClient.isPending} />
              </Stack>
              <Stack direction="row" spacing={1.5}>
                <TextField size="small" fullWidth label="Country" value={newDetails.billingCountry}
                  onChange={setDetail('billingCountry')} disabled={createClient.isPending} />
                <TextField size="small" fullWidth label="Postal code" value={newDetails.billingPostalCode}
                  onChange={setDetail('billingPostalCode')} disabled={createClient.isPending} />
              </Stack>
              <Stack direction="row" spacing={1.5}>
                <TextField size="small" fullWidth label="Commercial registration no."
                  value={newDetails.commercialRegistrationNumber}
                  onChange={setDetail('commercialRegistrationNumber')} disabled={createClient.isPending} />
                <TextField size="small" fullWidth label="Tax registration no."
                  value={newDetails.taxRegistrationNumber}
                  onChange={setDetail('taxRegistrationNumber')} disabled={createClient.isPending} />
              </Stack>
              <TextField size="small" fullWidth label="Sector" value={newDetails.sector}
                onChange={setDetail('sector')} disabled={createClient.isPending} />
              <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
                <Button onClick={() => setCreating(false)} color="inherit" disabled={createClient.isPending}>
                  Cancel
                </Button>
                <Button
                  variant="contained"
                  disabled={newName.trim().length === 0 || createClient.isPending}
                  startIcon={createClient.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
                  onClick={() => createClient.mutate()}
                  sx={{ fontWeight: 700 }}
                >
                  Create client
                </Button>
              </Stack>
            </Stack>
          </Box>
        )}

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
