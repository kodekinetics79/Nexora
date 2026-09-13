import React from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Box, Button, Chip, CircularProgress, Link, Paper, Stack, Typography,
} from '@mui/material';
import {
  Business as ClientIcon,
  CheckCircle as ConfirmIcon,
  HelpOutlined as UnknownIcon,
} from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import leadService, { type ClientCandidateDTO, type LeadResponseDTO } from '../../api/services/leadService';
import { presentableErrorMessage } from '../../utils/apiErrors';
import {
  clientCandidates, clientDisplayName, clientIdentityState, clientStatusLabel,
  confidencePercent, isHumanConfirmedStatus, matchExplanation, realSenderAddress,
} from './ClientCell';
import ResolveClientDialog, {
  CLIENT_LINK_FAILURE_MESSAGE, type ClientSelection,
} from './ResolveClientDialog';
import { useAuth } from '../../context/AuthContext';
import { commercialActionPermissions } from '../../utils/commercialActionPermissions';

/**
 * THE client identity on a lead. Always rendered — there is no state in which
 * this panel is absent, because "who is this from?" is the first question a rep
 * asks and a missing panel reads as "the system has nothing to say".
 *
 * Three states, none of them a dead end:
 *
 *   resolved   — the client, linked, in the engine's own words quoting the document,
 *                plus the honest statement that it is now locked and how to correct it.
 *   suggested  — Nexora's best guess, confirmable in ONE click.
 *   unresolved — no link, but every scrap of evidence Nexora does hold, so the
 *                decision takes five seconds instead of a hunt through the
 *                source document.
 *
 * The evidence block in the unresolved state is the whole point of this panel:
 * previously a rep saw the word "Unresolved" and had nowhere to go.
 */

interface EvidenceRow {
  label: string;
  value: string;
}

const EvidenceList: React.FC<{ rows: EvidenceRow[] }> = ({ rows }) => (
  <Box
    component="dl"
    sx={{
      m: 0, mt: 1.5, display: 'grid',
      gridTemplateColumns: { xs: '1fr', sm: 'minmax(140px, max-content) 1fr' },
      columnGap: 2, rowGap: 0.75,
    }}
  >
    {rows.map((row) => (
      <React.Fragment key={row.label}>
        <Box component="dt" sx={{ fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', letterSpacing: '0.03em', pt: 0.15 }}>
          {row.label}
        </Box>
        <Box component="dd" sx={{ m: 0, fontSize: '0.85rem', fontWeight: 600, color: 'text.primary', overflowWrap: 'anywhere' }}>
          {row.value}
        </Box>
      </React.Fragment>
    ))}
  </Box>
);

/**
 * WHY THIS CLIENT — the one line that lets a rep check the machine against the document.
 *
 * The resolver writes a sentence quoting the page it read (`"Saudi Electricity Company"
 * appears in the delivery address: "Saudi Electricity Company-DAMMAM".`), and the quoted
 * words ARE the evidence. Set in disabled grey under the name they read as a system note
 * nobody is meant to act on, which is how a checkable fact became decoration. Labelled
 * plainly and set in body text, a rep can hold it against the document in three seconds.
 */
const WhyLine: React.FC<{ children: React.ReactNode }> = ({ children }) => (
  <Box sx={{ mt: 1 }}>
    <Typography
      component="span"
      sx={{
        fontSize: '0.65rem', fontWeight: 800, color: 'text.disabled',
        textTransform: 'uppercase', letterSpacing: '0.03em', mr: 1,
      }}
    >
      Why
    </Typography>
    <Typography component="span" sx={{ fontSize: '0.85rem', color: 'text.primary', overflowWrap: 'anywhere' }}>
      {children}
    </Typography>
  </Box>
);

export interface ClientIdentityPanelProps {
  lead: LeadResponseDTO;
  /** False renders the state and evidence read-only, with no write affordances. */
  canEdit?: boolean;
  /** Called after the panel writes a link, so the host can refresh. */
  onChanged?: () => void;
  /**
   * DEFERRED MODE (the Extraction Review workbench). When supplied the panel
   * never writes: it hands the chosen client to the host form, which submits it
   * inside the reviewer's own save. Writing from here would bump the lead's
   * review version and make the reviewer's Save conflict.
   */
  onSelect?: (selection: ClientSelection) => void;
  /** The host's staged choice in deferred mode, so the panel can reflect it. */
  pendingSelection?: ClientSelection | null;
}

const ClientIdentityPanel: React.FC<ClientIdentityPanelProps> = ({
  lead, canEdit = true, onChanged, onSelect, pendingSelection,
}) => {
  const queryClient = useQueryClient();
  const { hasPermission } = useAuth();
  const commercialAccess = commercialActionPermissions(hasPermission);
  const canResolveClient = canEdit && commercialAccess.canLinkLeadClient;
  const [dialogOpen, setDialogOpen] = React.useState(false);
  const deferred = typeof onSelect === 'function';

  const state = clientIdentityState(lead);

  // Only ask for candidates when there is a decision to support. A resolved lead
  // needs no suggestions, and the endpoint degrades to [] where it is absent.
  const candidatesQuery = useQuery({
    queryKey: ['lead-client-candidates', lead.id],
    queryFn: () => leadService.getClientCandidates(lead.id),
    enabled: state !== 'resolved' && Number.isFinite(lead.id),
    retry: false,
    staleTime: 60_000,
  });

  const candidates = React.useMemo<ClientCandidateDTO[]>(() => {
    const fromEndpoint = candidatesQuery.data ?? [];
    if (fromEndpoint.length > 0) return [...fromEndpoint].sort((a, b) => (a.rank ?? 0) - (b.rank ?? 0));
    return clientCandidates(lead);
  }, [candidatesQuery.data, lead]);

  const confirmMutation = useMutation({
    mutationFn: (selection: { customerId: number; contactId?: number | null }) => {
      // Re-check at the write boundary. The permission snapshot can expire between rendering
      // the suggestion and clicking Confirm, and the client must fail closed before the request.
      if (!hasPermission('Leads', 'edit')) {
        throw new Error('Current Lead edit permission is required. The client was not linked.');
      }
      return leadService.linkClient(lead.id, selection);
    },
    onSuccess: () => {
      toast.success('Client confirmed for this lead.');
      queryClient.invalidateQueries({ queryKey: ['lead-detail', lead.id] });
      queryClient.invalidateQueries({ queryKey: ['lead-client-candidates', lead.id] });
      queryClient.invalidateQueries({ queryKey: ['leads'] });
      onChanged?.();
    },
    onError: (error: unknown) => {
      toast.error(presentableErrorMessage(error, CLIENT_LINK_FAILURE_MESSAGE));
    },
  });

  const confirmCandidate = (candidate: ClientCandidateDTO) => {
    if (deferred) {
      if (!hasPermission('Leads', 'edit')) {
        toast.error('Current Lead edit permission is required. The client choice was not staged.');
        return;
      }
      onSelect?.({ customerId: candidate.customerId, contactId: null, customerName: candidate.customerName });
      return;
    }
    confirmMutation.mutate({ customerId: candidate.customerId, contactId: null });
  };

  const openDialog = () => setDialogOpen(true);

  const dialog = (
    <ResolveClientDialog
      open={dialogOpen}
      leadId={lead.id}
      lead={lead}
      onClose={() => setDialogOpen(false)}
      onResolved={() => onChanged?.()}
      // The document already told us the company, the buyer and the sentence that proves it.
      // Opening this dialog empty made the rep retype a name the page was displaying two inches
      // above the button. The lead-detail screen has passed a prefill all along.
      prefill={{
        name: lead.customerCompanyNameExtracted,
        email: lead.clientemail,
        contactName: lead.buyersName,
        evidence: lead.customerCompanyEvidence,
      }}
      {...(deferred ? { onSelect } : {})}
    />
  );

  const shell = (children: React.ReactNode, tone: 'resolved' | 'suggested' | 'unresolved') => (
    <Paper
      variant="outlined"
      sx={{
        p: 2, mt: 1.5, borderRadius: 2, width: 'fit-content', maxWidth: '100%',
        borderColor: tone === 'suggested' ? 'warning.main' : 'divider',
        borderLeft: '4px solid',
        borderLeftColor: tone === 'resolved' ? 'success.main' : tone === 'suggested' ? 'warning.main' : 'text.disabled',
      }}
    >
      {children}
      {dialog}
    </Paper>
  );

  // ── Deferred mode: the host has staged a client but not yet saved it. ──────
  if (deferred && pendingSelection) {
    return shell(
      <Box>
        <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', fontSize: '0.65rem' }}>
          Client
        </Typography>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', mt: 0.25 }}>
          <ClientIcon sx={{ fontSize: 18, color: 'warning.main' }} />
          <Typography sx={{ fontWeight: 900, fontSize: '1.05rem', overflowWrap: 'anywhere' }}>
            {pendingSelection.customerName?.trim() || `Customer #${pendingSelection.customerId}`}
          </Typography>
          <Chip size="small" label="Not saved yet" color="warning" variant="outlined" sx={{ fontWeight: 800, height: 20, fontSize: '0.65rem' }} />
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75 }}>
          This client is applied when you save or approve this review.
        </Typography>
        {canResolveClient && (
          <Button size="small" onClick={openDialog} sx={{ mt: 1, fontWeight: 800, textTransform: 'none' }}>
            Choose another
          </Button>
        )}
      </Box>,
      'suggested',
    );
  }

  // ── Resolved ──────────────────────────────────────────────────────────────
  if (state === 'resolved') {
    const explanation = matchExplanation(lead);
    return shell(
      <Box>
        <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', fontSize: '0.65rem' }}>
          Client
        </Typography>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap', mt: 0.25 }}>
          <ClientIcon sx={{ fontSize: 18, color: 'success.main' }} />
          <Link
            component={RouterLink}
            to={`/customers/${lead.customerId}`}
            underline="hover"
            sx={{ fontWeight: 900, fontSize: '1.05rem', color: 'text.primary', overflowWrap: 'anywhere' }}
          >
            {clientDisplayName(lead)}
          </Link>
          {/* Green only when a PERSON confirmed it. A machine-linked client is
              correct far more often than not, but a rep about to quote should be
              able to tell the two apart at a glance. */}
          <Chip
            size="small"
            label={clientStatusLabel(lead.customerMatchStatus)}
            color={isHumanConfirmedStatus(lead.customerMatchStatus) ? 'success' : 'default'}
            variant="outlined"
            sx={{ fontWeight: 700, height: 20, fontSize: '0.65rem' }}
          />
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
          {lead.contactId
            ? `Buyer contact on file${lead.buyersName ? `: ${lead.buyersName}` : ''}.`
            : 'No buyer contact linked at this client yet.'}
        </Typography>
        {/* The engine's own sentence, quoting the document — not the category phrase. */}
        {explanation && <WhyLine>{explanation}</WhyLine>}
        {/* NO "Change client" HERE — the control could not do what it offered.
            The server's re-pointing guard (LeadRepository.LinkClientCoreAsync) refuses to
            move the customer on a lead that has already become an RFQ, because
            Rfq.InheritCommercialIdentity would then leave the lead and the RFQ naming
            different buyers with no way to re-inherit. That refusal comes back as 409, and
            the shared error layer titles every 409 "This changed while you were working"
            and tells the rep to refresh and reapply — advice that cannot succeed here:
            nothing changed underneath them, and the answer is the same every time. So the
            rep refreshed and retried, and retried.
            DecidePage has always hidden its own picker once a customer is set, which is how
            the two screens came to contradict each other on the same lead. An honest
            sentence beats a button that cannot work; the way out is named instead. */}
        {canResolveClient && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1.25, maxWidth: 560 }}>
            This client is locked now that the inquiry is confirmed, so the quote and the order
            cannot end up naming different buyers. If it is wrong, reject this inquiry and raise
            it again — or, if it has already become an RFQ, correct the client there.
          </Typography>
        )}
      </Box>,
      'resolved',
    );
  }

  // ── Suggested (includes AMBIGUOUS, which simply has ≥2 candidates) ────────
  const top = candidates[0];
  if (state === 'suggested' && top) {
    const confidence = top.confidence ?? lead.customerMatchConfidence;
    const pct = confidencePercent(confidence);
    // The engine's own sentence about THIS candidate first, then the one it wrote on the
    // lead, and only then the reason-code phrase. The candidate rows in ResolveClientDialog
    // have always preferred the authored sentence (see candidateExplanation); this panel was
    // holding the same sentence in `top.explanation` and dropping it for the category phrase,
    // so "Nexora thinks this is Marafiq" never said what on the page made it think so.
    const why = (top.explanation ?? '').trim()
      || (lead.customerMatchExplanation ?? '').trim()
      || matchExplanation({ customerMatchReasonCode: top.reasonCode ?? lead.customerMatchReasonCode });
    const others = candidates.length - 1;
    // A numbering pattern, an earlier sender or a contact's name is a hint, not a read of the
    // document. Printed as "Nexora thinks this is Saudi Aramco" with a gold Confirm button, a
    // 55% hint read as the product not knowing who wrote the page it had just read. Below the
    // strong-suggestion line the sentence says "might be", the explanation says it is weak, and
    // choosing the client is the primary control.
    const weak = confidence != null && Number(confidence) < 0.70;
    return shell(
      <Box>
        <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', fontSize: '0.65rem' }}>
          Client — not confirmed
        </Typography>
        <Typography sx={{ fontSize: '1rem', mt: 0.25 }}>
          {weak ? 'This might be' : 'Nexora thinks this is'}{' '}
          <Box component="strong" sx={{ fontWeight: 900 }}>
            {top.customerName?.trim() || `Customer #${top.customerId}`}
          </Box>
          {pct != null ? ` (${pct}%)` : ''}
        </Typography>
        {/* The warning is its own sentence so the engine's words survive intact beneath it.
            Folding them together used to lowercase the first character, which turned a
            sentence opening on a quoted company name into `"saudi Electricity Company"…`. */}
        {weak && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25 }}>
            Only a weak hint — check the document before confirming.
          </Typography>
        )}
        {why && <WhyLine>{why}</WhyLine>}
        {others > 0 && (
          <Typography variant="body2" color="warning.main" sx={{ mt: 0.25, fontWeight: 700 }}>
            {others === 1 ? '1 other client also matches the evidence.' : `${others} other clients also match the evidence.`}
          </Typography>
        )}
        {canResolveClient && (
          <Stack direction="row" spacing={1} sx={{ mt: 1.25, flexWrap: 'wrap', gap: 1 }}>
            {weak && (
              <Button variant="contained" size="small" startIcon={<ClientIcon />} onClick={openDialog} disabled={confirmMutation.isPending} sx={{ fontWeight: 800, textTransform: 'none' }}>
                Choose the client
              </Button>
            )}
            <Button
              variant={weak ? 'outlined' : 'contained'}
              size="small"
              startIcon={confirmMutation.isPending ? <CircularProgress size={15} color="inherit" /> : <ConfirmIcon />}
              disabled={confirmMutation.isPending}
              onClick={() => confirmCandidate(top)}
              sx={{ fontWeight: 800, textTransform: 'none' }}
            >
              {`Confirm ${top.customerName?.trim() || 'this client'}`}
            </Button>
            {!weak && (
              <Button size="small" onClick={openDialog} disabled={confirmMutation.isPending} sx={{ fontWeight: 800, textTransform: 'none' }}>
                Choose another
              </Button>
            )}
          </Stack>
        )}
      </Box>,
      'suggested',
    );
  }

  // An ambiguous classifier result can arrive before the candidate endpoint has materialized a
  // ranked row (or while that supporting read is temporarily unavailable). That is still not the
  // same as "no match": the operator must be told that several clients may fit and be routed to
  // an explicit review, not shown contradictory unresolved copy.
  if (state === 'suggested') {
    return shell(
      <Box>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
          <UnknownIcon sx={{ fontSize: 18, color: 'warning.main' }} />
          <Typography sx={{ fontWeight: 900, fontSize: '1.05rem' }}>Several clients may match</Typography>
        </Stack>
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 560 }}>
          The available identity signals are not strong enough to choose safely. Review the client list and confirm the correct organisation.
        </Typography>
        {canResolveClient && (
          <Button
            variant="contained"
            size="small"
            startIcon={<ClientIcon />}
            onClick={openDialog}
            sx={{ mt: 1.75, fontWeight: 800, textTransform: 'none' }}
          >
            Review possible clients
          </Button>
        )}
      </Box>,
      'suggested',
    );
  }

  // ── Unresolved: show what we DO know, then a way forward. ─────────────────
  const sender = realSenderAddress(lead.clientemail);
  // The company the document named, when the reader found one. Production, 2026-09-12: a Marafiq
  // RFQ read "MARAFIQ" and the sentence proving it, then offered the rep an empty search box.
  const documentName = (lead.customerCompanyNameExtracted ?? '').trim();
  const namedOnDocument = documentName.length > 0 && documentName.length <= 60 ? documentName : null;
  const evidence: EvidenceRow[] = [];
  if (sender) evidence.push({ label: 'Sent from', value: sender });
  if (lead.customerCompanyNameExtracted?.trim()) {
    evidence.push({ label: 'Company on document', value: lead.customerCompanyNameExtracted.trim() });
  }
  if (lead.customerCompanyEvidence?.trim()) {
    evidence.push({ label: 'Where it says so', value: `“${lead.customerCompanyEvidence.trim()}”` });
  }
  if (lead.customerPortalNameExtracted?.trim()) {
    evidence.push({ label: 'Portal', value: lead.customerPortalNameExtracted.trim() });
  }
  if (lead.supplierAccountRefOnDocument?.trim()) {
    evidence.push({ label: 'Our vendor code', value: lead.supplierAccountRefOnDocument.trim() });
  }
  if (lead.buyersName?.trim() && lead.buyersName.trim().toLowerCase() !== 'unknown buyer') {
    evidence.push({ label: 'Buyer contact', value: lead.buyersName.trim() });
  }
  if (lead.rfqno?.trim() && lead.rfqno.trim().toUpperCase() !== 'NO RFQ #') {
    evidence.push({ label: 'Their RFQ number', value: lead.rfqno.trim() });
  }

  return shell(
    <Box>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <UnknownIcon sx={{ fontSize: 18, color: 'text.disabled' }} />
        <Typography sx={{ fontWeight: 900, fontSize: '1.05rem' }}>No client linked yet</Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 560 }}>
        {namedOnDocument
          ? `No customer on your list matches this document. It names ${namedOnDocument}, which looks like a buyer you have not set up yet.`
          : evidence.length > 0
            ? 'No customer on your list matches this document. Here is everything it does know — enough to pick the right one.'
            : 'Nothing on this document says who is buying, so nothing was guessed.'}
      </Typography>
      {evidence.length > 0 && <EvidenceList rows={evidence} />}
      {canResolveClient && (
        <Stack direction="row" spacing={1} sx={{ mt: 1.75, flexWrap: 'wrap', gap: 1 }}>
          {/* The commonest reason a document does not match is that the buyer is new. Offering
              the company BY NAME turns a search the rep has to retype into one click. */}
          <Button
            variant="contained"
            size="small"
            startIcon={<ClientIcon />}
            onClick={openDialog}
            sx={{ fontWeight: 800, textTransform: 'none' }}
          >
            {namedOnDocument ? `Set up ${namedOnDocument}` : 'Choose the customer'}
          </Button>
          {namedOnDocument && (
            <Button size="small" onClick={openDialog} sx={{ fontWeight: 800, textTransform: 'none' }}>
              Search your customers
            </Button>
          )}
        </Stack>
      )}
    </Box>,
    'unresolved',
  );
};

export default ClientIdentityPanel;
