import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Box, Button, Chip, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogTitle, Divider, FormControl, FormControlLabel, FormLabel, MenuItem, Radio,
  RadioGroup, Stack, TextField, Typography,
} from '@mui/material';
import { AddBusiness as AddBusinessIcon, PersonAdd as PersonAddIcon } from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import leadService, { type ClientCandidateDTO } from '../../api/services/leadService';
import customerService from '../../api/services/customerService';
import contactService from '../../api/services/contactService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import { candidateExplanation, clientCandidates, confidencePercent, type ClientIdentityLike } from './ClientCell';
import { useAuth } from '../../context/AuthContext';
import { commercialActionPermissions } from '../../utils/commercialActionPermissions';

/**
 * The one place a person links a lead to a client organisation.
 *
 * Ranked machine candidates first — each with the evidence that produced it —
 * then a search over the tenant's real customers.
 *
 *  - A client the search cannot find is added HERE, and confirmed in the same
 *    click (owner rule, 2026-09-13, the same one that adds a missing part from
 *    the resolve-product dialog). The earlier two-step — create, then a separate
 *    Confirm — and the eight-field form behind it were a trip to Customers by
 *    another name, and the rep with a real enquiry from a new buyer either
 *    abandoned the lead or left the screen. The guard against a wrong client is
 *    that nothing is inferred: the name is what the rep typed or the document
 *    printed, shown in an editable box before the one button, and the client
 *    still needs a person to press it. Address and registration details are
 *    added later under Customers, where they belong.
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
  const { hasPermission } = useAuth();
  const commercialAccess = commercialActionPermissions(hasPermission);
  const [selectedCustomerId, setSelectedCustomerId] = React.useState<number | null>(null);
  const [selectedContactId, setSelectedContactId] = React.useState<number | ''>('');
  const [searchTerm, setSearchTerm] = React.useState('');
  const [debouncedTerm, setDebouncedTerm] = React.useState('');
  // Inline client creation. Held here rather than in a nested dialog so the operator never
  // loses the lead they were resolving. Small on purpose: a name and an address to write to.
  const [creating, setCreating] = React.useState(false);
  const [newName, setNewName] = React.useState('');
  const [newEmail, setNewEmail] = React.useState('');
  // Adding a buyer AT the chosen client — the person the enquiry came from. Only ever for
  // the client selected above, so a buyer can never be filed under the wrong company.
  const [addingContact, setAddingContact] = React.useState(false);
  const [newContact, setNewContact] = React.useState({ firstName: '', lastName: '', email: '', phoneNo: '', position: '' });

  // Hosts intentionally retain their selected Lead while the dialog is open. Re-deriving this
  // from live authority closes every host consistently when a refresh fails or access is revoked.
  const isOpen = open && leadId != null && commercialAccess.canLinkLeadClient;
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
    mutationFn: (selection: { customerId: number; contactId?: number | null; addedName?: string }) => {
      // Re-check at the write boundary as well as hiding/closing the UI. This covers the narrow
      // race where authority expires after the last render but before the click is dispatched.
      if (!hasPermission('Leads', 'edit')) {
        throw new Error('Current Lead edit permission is required. The client was not linked.');
      }
      return leadService.linkClient(Number(leadId), { customerId: selection.customerId, contactId: selection.contactId });
    },
    onSuccess: (_data, selection) => {
      toast.success(selection.addedName
        ? `“${selection.addedName}” added as a client and linked to this lead.`
        : 'Client linked to this lead.');
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
   * Adds the client, then confirms it for this lead in the same click: the link write follows
   * the create (or, in deferred mode, the host is handed the new client as the choice). The name
   * the rep pressed the button over is the name that is written — nothing is inferred behind
   * the form.
   */
  const createClient = useMutation({
    mutationFn: async () => {
      if (!hasPermission('Leads', 'edit') || !hasPermission('Customers', 'create')) {
        throw new Error('Current Customer create permission is required. The client was not created.');
      }
      const form = new FormData();
      form.append('Name', newName.trim());
      // Only a non-empty email is sent. An empty string is not "no value" to a server that
      // stores what it is given — it is a blank that later reads as answered.
      if (newEmail.trim()) form.append('ContactEmail', newEmail.trim());
      return customerService.create(form);
    },
    onSuccess: (created) => {
      const id = Number((created as { id?: number | string }).id);
      const name = newName.trim();
      queryClient.invalidateQueries({ queryKey: ['customer-search'] });
      queryClient.invalidateQueries({ queryKey: ['client-customer-search'] });
      setCreating(false);
      setSearchTerm(name);
      if (!Number.isFinite(id) || id <= 0) {
        // Created, but the server did not say which record: the rep picks it from the search.
        toast.success(`“${name}” added as a client — pick it below to confirm.`);
        return;
      }
      setSelectedCustomerId(id);
      if (deferred) {
        onSelect?.({ customerId: id, contactId: null, customerName: name });
        onClose();
        return;
      }
      mutation.mutate({ customerId: id, contactId: null, addedName: name });
    },
    onError: (error) => toast.error(presentableErrorMessage(error, 'The client could not be added.')),
  });

  /**
   * Creates the buyer under the SELECTED client and selects them; linking still happens on
   * Confirm. The customer id comes from the selection, never from the form, so the contact
   * lands on the company the operator is looking at.
   */
  const createContact = useMutation({
    mutationFn: async () => {
      if (selectedCustomerId == null) throw new Error('Choose the client first.');
      if (!hasPermission('Leads', 'edit') || !hasPermission('Customers', 'create')) {
        throw new Error('Current Customer create permission is required. The buyer was not added.');
      }
      return contactService.create({
        customerId: selectedCustomerId,
        firstName: newContact.firstName.trim(),
        lastName: newContact.lastName.trim() || undefined,
        email: newContact.email.trim() || undefined,
        phoneNo: newContact.phoneNo.trim() || undefined,
        position: newContact.position.trim() || undefined,
        isActive: true,
        isPrimary: false,
      });
    },
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: ['client-customer-contacts', selectedCustomerId] });
      const id = Number((created as { id?: number | string }).id);
      if (Number.isFinite(id) && id > 0) setSelectedContactId(id);
      setAddingContact(false);
      toast.success(`Buyer “${newContact.firstName.trim()}” added at ${selectedCustomerName ?? 'this client'}.`);
    },
    onError: (error) => toast.error(presentableErrorMessage(error, 'The buyer could not be added.')),
  });

  const openAddContact = () => {
    // Pre-filled from the enquiry when it named a person; corrected before saving.
    const named = (prefill?.contactName ?? '').trim();
    const space = named.indexOf(' ');
    setNewContact({
      firstName: space > 0 ? named.slice(0, space) : named,
      lastName: space > 0 ? named.slice(space + 1) : '',
      email: (prefill?.email ?? '').trim(),
      phoneNo: '',
      position: '',
    });
    setAddingContact(true);
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

  const canConfirm = selectedCustomerId != null && !mutation.isPending;
  const busy = mutation.isPending || createClient.isPending;
  const noMatch = debouncedTerm.length >= 2 && !searchQuery.isFetching && !searchQuery.isError && searchResults.length === 0;

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

            {noMatch && !creating && (
              <Box sx={{ py: 0.5 }}>
                <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
                  No client matches “{debouncedTerm}”.
                  {!commercialAccess.canCreateClientFromLead
                    ? ' Ask an administrator to add it under Customers, then confirm it here.'
                    : ''}
                </Typography>
                {/* A DEAD END IS NOT AN ANSWER. The rep holding a real enquiry from a new buyer
                    adds the client here — the name they typed is the name offered — and confirms
                    it in the same click. Sending them to Customers and back was the dead end. */}
                {commercialAccess.canCreateClientFromLead ? (
                  <Button
                    size="small"
                    variant="outlined"
                    startIcon={<AddBusinessIcon />}
                    onClick={() => {
                      setNewName(debouncedTerm || prefill?.name?.trim() || '');
                      setNewEmail(prefill?.email?.trim() || '');
                      setCreating(true);
                    }}
                    sx={{ fontWeight: 700 }}
                  >
                    Add “{debouncedTerm}” as a new client
                  </Button>
                ) : null}
              </Box>
            )}
          </RadioGroup>
        </FormControl>

        {commercialAccess.canCreateClientFromLead && creating && (
          <Box component="form" onSubmit={(event) => { event.preventDefault(); if (newName.trim()) createClient.mutate(); }}
            sx={{ mt: 2, p: 2, border: '1px solid', borderColor: 'divider', borderRadius: 2 }}>
            <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 0.5 }}>
              New client
            </Typography>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1.5 }}>
              Check the name — it is written exactly as it reads here. Address and registration details
              can be added later under Customers.
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
                disabled={busy}
                error={newName.trim().length === 0}
                helperText={newName.trim().length === 0 ? 'A client needs a name.' : ' '}
              />
              <TextField
                size="small" fullWidth
                label="Contact email (optional)"
                type="email"
                value={newEmail}
                onChange={(e) => setNewEmail(e.target.value)}
                disabled={busy}
                helperText="The address this enquiry arrived from, if you have it."
              />
              <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
                <Button onClick={() => setCreating(false)} color="inherit" disabled={busy}>
                  Cancel
                </Button>
                <Button
                  type="submit"
                  variant="contained"
                  disabled={newName.trim().length === 0 || busy}
                  startIcon={busy ? <CircularProgress size={16} color="inherit" /> : undefined}
                  sx={{ fontWeight: 700 }}
                >
                  Add and confirm client
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
              helperText={`Only people at ${selectedCustomerName ?? 'this client'} are listed. Leave blank if you are not sure who the buyer is — the client link still counts.`}
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
            {!addingContact && hasPermission('Leads', 'edit') && hasPermission('Customers', 'create') && (
              <Button
                size="small"
                startIcon={<PersonAddIcon />}
                onClick={openAddContact}
                disabled={mutation.isPending}
                sx={{ mt: 1, fontWeight: 700 }}
              >
                Add a buyer at {selectedCustomerName ?? 'this client'}
              </Button>
            )}
            {addingContact && (
              <Box sx={{ mt: 1.5, p: 1.5, border: '1px solid', borderColor: 'divider', borderRadius: 1 }}>
                <Typography variant="subtitle2" sx={{ fontWeight: 800, mb: 1 }}>
                  New buyer at {selectedCustomerName ?? 'this client'}
                </Typography>
                <Stack spacing={1.5}>
                  <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
                    <TextField size="small" fullWidth required label="First name" value={newContact.firstName}
                      onChange={(e) => setNewContact((c) => ({ ...c, firstName: e.target.value }))} />
                    <TextField size="small" fullWidth label="Last name" value={newContact.lastName}
                      onChange={(e) => setNewContact((c) => ({ ...c, lastName: e.target.value }))} />
                  </Stack>
                  <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
                    <TextField size="small" fullWidth label="Email" type="email" value={newContact.email}
                      onChange={(e) => setNewContact((c) => ({ ...c, email: e.target.value }))} />
                    <TextField size="small" fullWidth label="Phone" value={newContact.phoneNo}
                      onChange={(e) => setNewContact((c) => ({ ...c, phoneNo: e.target.value }))} />
                  </Stack>
                  <TextField size="small" fullWidth label="Role or department (optional)" value={newContact.position}
                    onChange={(e) => setNewContact((c) => ({ ...c, position: e.target.value }))} />
                  <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end' }}>
                    <Button size="small" color="inherit" onClick={() => setAddingContact(false)} disabled={createContact.isPending}>
                      Cancel
                    </Button>
                    <Button
                      size="small"
                      variant="contained"
                      disabled={!newContact.firstName.trim() || createContact.isPending}
                      startIcon={createContact.isPending ? <CircularProgress size={14} color="inherit" /> : undefined}
                      onClick={() => createContact.mutate()}
                      sx={{ fontWeight: 800 }}
                    >
                      Save buyer
                    </Button>
                  </Stack>
                </Stack>
              </Box>
            )}
          </Box>
        )}
      </DialogContent>
      <DialogActions sx={{ p: 2, flexWrap: 'wrap', gap: 1, justifyContent: 'space-between' }}>
        <Button onClick={handleLeaveUnresolved} color="inherit" disabled={mutation.isPending} sx={{ fontWeight: 700 }}>
          None of these — leave unresolved
        </Button>
        <Stack direction="row" spacing={1}>
          <Button onClick={onClose} color="inherit" disabled={busy}>Cancel</Button>
          {/* One primary button per state: while the new-client form is open, its own
              "Add and confirm client" is that button. */}
          {!creating ? (
            <Button
              variant="contained"
              disabled={!canConfirm}
              startIcon={mutation.isPending ? <CircularProgress size={16} color="inherit" /> : undefined}
              onClick={handleConfirm}
              sx={{ fontWeight: 800 }}
            >
              Confirm client
            </Button>
          ) : null}
        </Stack>
      </DialogActions>
    </Dialog>
  );
};

export default ResolveClientDialog;
