import React from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Autocomplete, Box, Button, Chip, CircularProgress, Dialog, DialogActions,
  DialogContent, DialogTitle, Divider, MenuItem, Paper, Stack, Table, TableBody,
  TableCell, TableContainer, TableHead, TableRow, TextField, Typography,
} from '@mui/material';
import {
  AltRoute as RoutingIcon,
  Add as AddIcon,
  Fingerprint as IdentityIcon,
} from '@mui/icons-material';
import { toast } from 'react-hot-toast';
import commercialRoutingService, {
  CUSTOMER_IDENTIFIER_TYPE,
  OWNERSHIP_SCOPE,
  type CustomerIdentifierDTO,
  type CustomerIdentifierType,
  type CustomerOwnershipDTO,
  type OwnershipScope,
} from '../../../api/services/commercialRoutingService';
import customerService, { type CustomerDTO } from '../../../api/services/customerService';
import userService from '../../../api/services/userService';
import { categoryService } from '../../../api/services/categoryService';
import businessUnitService from '../../../api/services/businessUnitService';
import ApiErrorNotice from '../../../components/common/ApiErrorNotice';
import { useAuth } from '../../../context/AuthContext';
import { presentableErrorMessage } from '../../../utils/apiErrors';

/**
 * Master-data screen for FR-RFQ-07: which sales engineer an incoming RFQ is routed to.
 *
 * Every field here maps 1:1 to a column the deterministic routing engine actually reads
 * (`DeterministicRoutingEngine.SelectOwnership` / `LoadMatchingIdentifiersAsync`), so the
 * wording explains the real behaviour rather than the table names.
 *
 * The API exposes create + per-customer read only. There is no list-all, update or delete
 * endpoint for ownership rules, so the page is customer-scoped and does not pretend to edit.
 */

// ─── Scope vocabulary ─────────────────────────────────────────────────────────
// Order matches RoutingPolicy.OwnershipPrecedence: the first rule that matches wins.
interface ScopeSpec {
  value: OwnershipScope;
  label: string;
  /** Plain-language description of what the rule does when it matches. */
  what: string;
  /** Undefined means the rule needs no key — it applies to the whole customer. */
  keyLabel?: string;
  keyHelp?: string;
  keySource?: 'productCategory' | 'branch';
  /** Set when the engine never derives this key from an RFQ on its own. */
  dormant?: boolean;
}

const SCOPES: ScopeSpec[] = [
  {
    value: OWNERSHIP_SCOPE.CustomerException,
    label: 'Customer exception',
    what: 'Sends every RFQ from this customer to one owner, whatever it is for. Checked first, so it beats every other rule.',
  },
  {
    value: OWNERSHIP_SCOPE.ProductCategory,
    label: 'Product category',
    what: 'Sends an RFQ to this owner when the product on the RFQ matches the category below.',
    keyLabel: 'Product category',
    keyHelp: 'Compared with the product or commodity named on the first line of the incoming RFQ. Capitalisation is ignored.',
    keySource: 'productCategory',
  },
  {
    value: OWNERSHIP_SCOPE.Branch,
    label: 'Branch',
    what: 'Sends an RFQ to this owner when it is received into the branch below.',
    keyLabel: 'Branch code',
    keyHelp: 'Compared with the business unit code the RFQ was received into, exactly as it appears in Setup, Business Units.',
    keySource: 'branch',
  },
  {
    value: OWNERSHIP_SCOPE.Territory,
    label: 'Territory',
    what: 'Sends an RFQ to this owner when the request carries the territory below.',
    keyLabel: 'Territory',
    keyHelp: 'Nexora does not yet read a territory off an incoming RFQ, so this rule only applies when a territory is supplied with the routing request.',
    dormant: true,
  },
  {
    value: OWNERSHIP_SCOPE.KeyAccountTeam,
    label: 'Key account team',
    what: 'Sends an RFQ to this owner when the request carries the team below.',
    keyLabel: 'Team',
    keyHelp: 'Nexora does not yet read a key account team off an incoming RFQ, so this rule only applies when a team is supplied with the routing request.',
    dormant: true,
  },
  {
    value: OWNERSHIP_SCOPE.GeneralCustomer,
    label: 'General customer',
    what: 'The fallback owner for this customer, used when none of the more specific rules above match.',
  },
];

const scopeSpec = (value: OwnershipScope): ScopeSpec | undefined =>
  SCOPES.find((scope) => scope.value === value);

// ─── Identifier vocabulary ────────────────────────────────────────────────────
// Only these types are read when Nexora tries to recognise the customer behind an RFQ
// (see LoadMatchingIdentifiersAsync). Offering the others would be misleading.
interface IdentifierSpec {
  value: CustomerIdentifierType;
  label: string;
  help: string;
  placeholder: string;
}

const IDENTIFIER_TYPES: IdentifierSpec[] = [
  {
    value: CUSTOMER_IDENTIFIER_TYPE.ErpAccount,
    label: 'Account or vendor number',
    help: 'The account number this customer quotes on their paperwork. Strongest evidence there is.',
    placeholder: 'e.g. ACC-100234',
  },
  {
    value: CUSTOMER_IDENTIFIER_TYPE.Email,
    label: 'Email address',
    help: 'A buyer mailbox that sends RFQs. One customer only — Nexora will refuse a duplicate held by another customer.',
    placeholder: 'e.g. tenders@customer.com',
  },
  {
    value: CUSTOMER_IDENTIFIER_TYPE.Domain,
    label: 'Email domain',
    help: 'Recognises anyone mailing from this domain. Do not use shared domains such as gmail.com.',
    placeholder: 'e.g. customer.com',
  },
  {
    value: CUSTOMER_IDENTIFIER_TYPE.CustomerName,
    label: 'Company name',
    help: 'The company name as it is written on incoming RFQs. Punctuation and capitalisation are ignored.',
    placeholder: 'e.g. Gulf Industrial Supplies',
  },
  {
    value: CUSTOMER_IDENTIFIER_TYPE.Alias,
    label: 'Other name they trade under',
    help: 'A second name, abbreviation or former name used for the same company.',
    placeholder: 'e.g. GIS Trading',
  },
];

/** Labels for every type, including learned ones the table may display. */
const IDENTIFIER_LABELS: Record<number, string> = {
  [CUSTOMER_IDENTIFIER_TYPE.ErpAccount]: 'Account or vendor number',
  [CUSTOMER_IDENTIFIER_TYPE.TaxRegistration]: 'Tax registration',
  [CUSTOMER_IDENTIFIER_TYPE.Email]: 'Email address',
  [CUSTOMER_IDENTIFIER_TYPE.Domain]: 'Email domain',
  [CUSTOMER_IDENTIFIER_TYPE.Phone]: 'Phone number',
  [CUSTOMER_IDENTIFIER_TYPE.Alias]: 'Other name they trade under',
  [CUSTOMER_IDENTIFIER_TYPE.CustomerName]: 'Company name',
  [CUSTOMER_IDENTIFIER_TYPE.HistoricalInference]: 'Learned from past RFQs',
  [CUSTOMER_IDENTIFIER_TYPE.Portal]: 'Buying portal',
  [CUSTOMER_IDENTIFIER_TYPE.PortalAccount]: 'Portal vendor code',
  [CUSTOMER_IDENTIFIER_TYPE.RfqNumberPattern]: 'RFQ number pattern',
};

const todayIso = () => new Date().toISOString().slice(0, 10);
const toInstant = (day: string) => new Date(`${day}T00:00:00`).toISOString();
const formatDay = (value?: string | null) =>
  value ? new Date(value).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) : '—';

const errorText = (error: any, fallback: string) =>
  error?.response?.data?.error || error?.response?.data?.title || fallback;

interface OwnershipForm {
  scope: OwnershipScope;
  scopeKey: string;
  primaryUserId: number | '';
  backupUserId: number | '';
  priority: string;
  effectiveFrom: string;
  effectiveTo: string;
  reason: string;
}

const emptyOwnershipForm = (): OwnershipForm => ({
  scope: OWNERSHIP_SCOPE.CustomerException,
  scopeKey: '',
  primaryUserId: '',
  backupUserId: '',
  priority: '0',
  effectiveFrom: todayIso(),
  effectiveTo: '',
  reason: '',
});

interface IdentifierForm {
  identifierType: CustomerIdentifierType;
  value: string;
}

const emptyIdentifierForm = (): IdentifierForm => ({
  identifierType: CUSTOMER_IDENTIFIER_TYPE.Email,
  value: '',
});

const RoutingRulesPage: React.FC = () => {
  const queryClient = useQueryClient();
  const { userData, hasPermission } = useAuth();
  // Both write endpoints require the manager role AND Customers: Edit.
  const canConfigure =
    (userData.isManager === true || userData.isSuperAdmin === true) && hasPermission('Customers', 'edit');

  const [customer, setCustomer] = React.useState<CustomerDTO | null>(null);
  const [customerSearch, setCustomerSearch] = React.useState('');
  const [debouncedSearch, setDebouncedSearch] = React.useState('');
  const [ruleOpen, setRuleOpen] = React.useState(false);
  const [ruleForm, setRuleForm] = React.useState<OwnershipForm>(emptyOwnershipForm);
  const [identityOpen, setIdentityOpen] = React.useState(false);
  const [identityForm, setIdentityForm] = React.useState<IdentifierForm>(emptyIdentifierForm);

  React.useEffect(() => {
    const timer = window.setTimeout(() => setDebouncedSearch(customerSearch.trim()), 300);
    return () => window.clearTimeout(timer);
  }, [customerSearch]);

  const customersQuery = useQuery({
    queryKey: ['routing-rules', 'customers', debouncedSearch],
    queryFn: () => customerService.getAll({
      pageNumber: 1,
      pageSize: 25,
      name: debouncedSearch || undefined,
      isActive: true,
    }),
  });

  const usersQuery = useQuery({
    queryKey: ['routing-rules', 'users'],
    // Owner pickers need the whole active roster in one call; the API caps pageSize at 1000.
    queryFn: () => userService.getAll({ pageNumber: 1, pageSize: 500, isActive: true }),
  });

  const profileQuery = useQuery({
    queryKey: ['routing-rules', 'profile', customer?.id],
    queryFn: () => commercialRoutingService.getCustomerProfile(customer!.id),
    enabled: !!customer,
  });

  const selectedScope = scopeSpec(ruleForm.scope);

  const categoriesQuery = useQuery({
    queryKey: ['routing-rules', 'product-categories'],
    queryFn: () => categoryService.getAll({ pageNumber: 1, pageSize: 200, isActive: true }),
    enabled: ruleOpen && selectedScope?.keySource === 'productCategory',
  });

  const branchesQuery = useQuery({
    queryKey: ['routing-rules', 'branches'],
    queryFn: () => businessUnitService.getDropdown(),
    enabled: ruleOpen && selectedScope?.keySource === 'branch',
  });

  /**
   * The fallback owner. Both endpoints existed with no caller, so the only way to set "who gets
   * an inquiry nobody has a rule for" was a database edit — and the sentence above the page
   * ("waits in the unassigned queue for a manager") was the only statement of the default.
   */
  const canSetDefaultOwner =
    (userData.isManager === true || userData.isSuperAdmin === true) && hasPermission('Leads', 'edit');
  const defaultOwnerQuery = useQuery({
    queryKey: ['routing-rules', 'default-owner'],
    queryFn: () => commercialRoutingService.getDefaultOwner(),
  });
  const setDefaultOwner = useMutation({
    mutationFn: (userId: number | null) => commercialRoutingService.setDefaultOwner(userId),
    onSuccess: (result) => {
      queryClient.setQueryData(['routing-rules', 'default-owner'], result);
      queryClient.invalidateQueries({ queryKey: ['lead-owner-options'] });
      toast.success(result.defaultOwnerUserId
        ? `Inquiries no rule claims will go to ${result.name ?? 'the chosen person'}.`
        : 'Fallback owner cleared. Unrouted inquiries will wait for a manager.');
    },
    onError: (error: unknown) => toast.error(presentableErrorMessage(error, 'The fallback owner was not changed.')),
  });

  const userName = React.useCallback((id?: number | null) => {
    if (!id) return '—';
    const match = (usersQuery.data?.items ?? []).find((user) => user.id === id);
    return match ? `${match.firstName} ${match.lastName}`.trim() : `User ${id}`;
  }, [usersQuery.data]);

  const invalidateProfile = () =>
    queryClient.invalidateQueries({ queryKey: ['routing-rules', 'profile', customer?.id] });

  const createRule = useMutation({
    mutationFn: () => commercialRoutingService.createOwnership({
      customerId: customer!.id,
      primaryUserId: Number(ruleForm.primaryUserId),
      backupUserId: ruleForm.backupUserId === '' ? null : Number(ruleForm.backupUserId),
      scope: ruleForm.scope,
      scopeKey: selectedScope?.keyLabel ? ruleForm.scopeKey.trim() : null,
      priority: Number(ruleForm.priority) || 0,
      effectiveFrom: toInstant(ruleForm.effectiveFrom),
      effectiveTo: ruleForm.effectiveTo ? toInstant(ruleForm.effectiveTo) : null,
      source: 'MasterData',
      reason: ruleForm.reason.trim() || null,
    }),
    onSuccess: () => {
      toast.success('Routing rule added');
      setRuleOpen(false);
      void invalidateProfile();
    },
    onError: (error: any) =>
      toast.error(errorText(error, 'The routing rule could not be saved.')),
  });

  /**
   * A detail entered (or confirmed) by a master-data administrator is authoritative by
   * definition, so it is always written as verified at full confidence. The endpoint is an
   * upsert keyed on customer + type + normalised value, so re-saving one that Nexora learned
   * on its own promotes it rather than duplicating it.
   */
  const saveIdentity = useMutation({
    mutationFn: (input: IdentifierForm) => commercialRoutingService.upsertIdentifier({
      customerId: customer!.id,
      identifierType: input.identifierType,
      value: input.value.trim(),
      isVerified: true,
      confidence: 1,
      source: 'MasterData',
    }),
    onSuccess: () => {
      toast.success('Recognition detail confirmed');
      setIdentityOpen(false);
      void invalidateProfile();
    },
    onError: (error: any) =>
      toast.error(errorText(error, 'The recognition detail could not be saved.')),
  });

  const openRuleDialog = () => {
    setRuleForm(emptyOwnershipForm());
    setRuleOpen(true);
  };

  const openIdentityDialog = () => {
    setIdentityForm(emptyIdentifierForm());
    setIdentityOpen(true);
  };

  const needsKey = !!selectedScope?.keyLabel;
  const datesOutOfOrder = !!ruleForm.effectiveTo && ruleForm.effectiveTo <= ruleForm.effectiveFrom;
  const sameOwnerTwice =
    ruleForm.backupUserId !== '' && ruleForm.backupUserId === ruleForm.primaryUserId;
  const ruleReady =
    ruleForm.primaryUserId !== '' &&
    (!needsKey || ruleForm.scopeKey.trim().length > 0) &&
    !!ruleForm.effectiveFrom &&
    !datesOutOfOrder &&
    !sameOwnerTwice;
  const identityReady = identityForm.value.trim().length > 0;

  const ownerships = profileQuery.data?.ownerships ?? [];
  const identifiers = profileQuery.data?.identifiers ?? [];
  const owners = usersQuery.data?.items ?? [];

  return (
    <Box sx={{ p: 3, maxWidth: 1200, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
        <RoutingIcon color="primary" />
        <Typography variant="h4" component="h1" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
          RFQ Routing Rules
        </Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3, maxWidth: 820 }}>
        Decide who picks up an incoming RFQ. Nexora first recognises which customer sent it, then
        applies the most specific rule you have set for that customer. If nothing matches, the RFQ
        waits in the unassigned queue for a manager.
      </Typography>

      <Paper variant="outlined" sx={{ p: 2.5, borderRadius: 3, mb: 3 }}>
        <Typography sx={{ fontWeight: 800, mb: 1 }}>Which rule wins</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1.5 }}>
          Rules are checked in this order and the first one that matches decides the owner. Two rules
          of the same kind are separated by their priority, highest first.
        </Typography>
        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
          {SCOPES.map((scope, index) => (
            <Chip
              key={scope.value}
              size="small"
              label={`${index + 1}. ${scope.label}`}
              variant={index === 0 ? 'filled' : 'outlined'}
              color={index === 0 ? 'primary' : 'default'}
            />
          ))}
        </Stack>
      </Paper>

      <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', mb: 3 }}>
        <Typography sx={{ fontWeight: 800, mb: 0.5 }}>If no rule matches</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2, maxWidth: 820 }}>
          When an inquiry arrives and no rule below claims it, Nexora gives it to this person.
          Leave it empty and unrouted inquiries wait in the Unassigned list for a manager to hand out.
        </Typography>
        {defaultOwnerQuery.isError && (
          <ApiErrorNotice
            error={defaultOwnerQuery.error}
            fallbackMessage="The fallback owner could not be loaded."
            onRetry={() => void defaultOwnerQuery.refetch()}
          />
        )}
        {defaultOwnerQuery.isLoading && <CircularProgress size={22} aria-label="Loading fallback owner" />}
        {defaultOwnerQuery.isSuccess && (canSetDefaultOwner ? (
          <Stack spacing={1} sx={{ maxWidth: 520 }}>
            <TextField
              select
              label="Fallback owner"
              value={defaultOwnerQuery.data.defaultOwnerUserId ?? ''}
              onChange={(event) => setDefaultOwner.mutate(event.target.value === '' ? null : Number(event.target.value))}
              disabled={setDefaultOwner.isPending || usersQuery.isLoading}
              helperText={usersQuery.isError
                ? 'The list of people could not be loaded. Refresh to try again.'
                : setDefaultOwner.isPending ? 'Saving…' : 'Saved as soon as you choose.'}
            >
              <MenuItem value="">Nobody — wait for a manager</MenuItem>
              {owners.map((user) => (
                <MenuItem key={user.id} value={user.id}>
                  {`${user.firstName} ${user.lastName}`.trim() || user.email}
                </MenuItem>
              ))}
            </TextField>
            {/* The server's own verdict: a chosen person routing will not actually use (no rep
                profile, inactive) is worse than nobody, because the manager believes it is handled. */}
            {defaultOwnerQuery.data.defaultOwnerUserId != null && !defaultOwnerQuery.data.isEligible && (
              <Alert severity="warning">{defaultOwnerQuery.data.eligibilityReason}</Alert>
            )}
          </Stack>
        ) : (
          <Stack spacing={0.5}>
            <Typography sx={{ fontWeight: 700 }}>
              {defaultOwnerQuery.data.defaultOwnerUserId != null
                ? `${defaultOwnerQuery.data.name ?? userName(defaultOwnerQuery.data.defaultOwnerUserId)}`
                : 'Nobody — unrouted inquiries wait for a manager'}
            </Typography>
            {defaultOwnerQuery.data.defaultOwnerUserId != null && !defaultOwnerQuery.data.isEligible && (
              <Typography variant="body2" color="warning.main">{defaultOwnerQuery.data.eligibilityReason}</Typography>
            )}
            {/* A read-only control prints why. */}
            <Typography variant="caption" color="text.secondary">
              Only a manager with Can Edit on Leads can change the fallback owner. Ask your administrator if it needs to change.
            </Typography>
          </Stack>
        ))}
      </Paper>

      <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', mb: 3 }}>
        <Typography sx={{ fontWeight: 800, mb: 0.5 }}>Choose a customer</Typography>
        <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
          Routing rules are held against a customer, so start by picking the account you want to
          configure.
        </Typography>
        <Autocomplete
          sx={{ maxWidth: 520 }}
          options={customersQuery.data?.items ?? []}
          value={customer}
          onChange={(_event, next) => setCustomer(next)}
          onInputChange={(_event, next) => setCustomerSearch(next)}
          getOptionLabel={(option) => option.name ?? ''}
          isOptionEqualToValue={(option, value) => option.id === value.id}
          loading={customersQuery.isFetching}
          loadingText="Searching customers…"
          noOptionsText={customersQuery.isError ? 'Customers could not be loaded.' : 'No matching customer'}
          renderInput={(params) => (
            <TextField
              {...params}
              label="Customer"
              placeholder="Start typing a customer name"
            />
          )}
        />
      </Paper>

      {!customer && (
        <Paper sx={{ p: 5, borderRadius: 3, border: '1px dashed', borderColor: 'divider', textAlign: 'center' }}>
          <RoutingIcon color="disabled" sx={{ fontSize: 56, mb: 1.5 }} />
          <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>
            No customer selected yet
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 640, mx: 'auto' }}>
            A routing rule says who should own an RFQ from a particular customer — either for
            everything they send, or only for a product category or branch. Pick a customer above to
            see the rules already in place and add new ones.
          </Typography>
        </Paper>
      )}

      {customer && profileQuery.isError && (
        <ApiErrorNotice
          error={profileQuery.error}
          fallbackMessage={`Routing rules for ${customer.name} could not be loaded.`}
          onRetry={() => void profileQuery.refetch()}
        />
      )}

      {customer && profileQuery.isLoading && (
        <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}><CircularProgress /></Box>
      )}

      {customer && profileQuery.isSuccess && (
        <Stack spacing={3}>
          {/* ── Routing rules ───────────────────────────────────────────── */}
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' }, mb: 2 }}>
              <Box>
                <Typography sx={{ fontWeight: 800 }}>Routing rules for {customer.name}</Typography>
                <Typography variant="body2" color="text.secondary">
                  Who receives an RFQ from this customer, and when that applies.
                </Typography>
              </Box>
              {canConfigure && ownerships.length > 0 && (
                <Button variant="contained" startIcon={<AddIcon />} onClick={openRuleDialog} sx={{ fontWeight: 800, borderRadius: 2, whiteSpace: 'nowrap' }}>
                  Add routing rule
                </Button>
              )}
            </Stack>

            {ownerships.length === 0 ? (
              <Box sx={{ py: 4, textAlign: 'center' }}>
                <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>
                  No routing rule for {customer.name} yet
                </Typography>
                <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 640, mx: 'auto', mb: 3 }}>
                  Without a rule, every RFQ from this customer goes to the unassigned queue and waits
                  for a manager to hand it out. A rule names the sales engineer who should own it —
                  for all of this customer&apos;s work, or just for one product category or branch.
                  Start with a general customer rule if you only want one owner.
                </Typography>
                {canConfigure ? (
                  <Button variant="contained" startIcon={<AddIcon />} onClick={openRuleDialog} sx={{ fontWeight: 800, borderRadius: 2 }}>
                    Add the first routing rule
                  </Button>
                ) : (
                  <Typography variant="body2" color="text.secondary">
                    Ask a manager with Can Edit on Customers to add the first rule.
                  </Typography>
                )}
              </Box>
            ) : (
              <TableContainer>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>Rule</TableCell>
                      <TableCell>Applies to</TableCell>
                      <TableCell>Goes to</TableCell>
                      <TableCell>Backup</TableCell>
                      <TableCell align="right">Priority</TableCell>
                      <TableCell>In force</TableCell>
                      <TableCell>Why</TableCell>
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {ownerships.map((rule: CustomerOwnershipDTO) => {
                      const spec = scopeSpec(rule.scope);
                      const ended = !!rule.effectiveTo && new Date(rule.effectiveTo) <= new Date();
                      return (
                        <TableRow key={rule.id} hover>
                          <TableCell>
                            <Typography variant="body2" sx={{ fontWeight: 700 }}>
                              {spec?.label ?? `Scope ${rule.scope}`}
                            </Typography>
                            <Typography variant="caption" color="text.secondary">
                              {spec?.what}
                            </Typography>
                          </TableCell>
                          <TableCell>{rule.scopeKey || 'Every RFQ from this customer'}</TableCell>
                          <TableCell>{userName(rule.primaryUserId)}</TableCell>
                          <TableCell>{userName(rule.backupUserId)}</TableCell>
                          <TableCell align="right">{rule.priority}</TableCell>
                          <TableCell>
                            <Typography variant="body2">
                              {formatDay(rule.effectiveFrom)} → {rule.effectiveTo ? formatDay(rule.effectiveTo) : 'no end date'}
                            </Typography>
                            {(!rule.isActive || ended) && (
                              <Chip size="small" label={rule.isActive ? 'Ended' : 'Switched off'} sx={{ mt: 0.5 }} />
                            )}
                          </TableCell>
                          <TableCell>
                            <Typography variant="body2">{rule.reason || '—'}</Typography>
                            <Typography variant="caption" color="text.secondary">Added by {rule.source}</Typography>
                          </TableCell>
                        </TableRow>
                      );
                    })}
                  </TableBody>
                </Table>
              </TableContainer>
            )}

            {ownerships.length > 0 && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 2 }}>
                A rule cannot be edited or removed once saved. To stop one applying, give it an end
                date when you create it, or add a higher-priority rule of the same kind.
              </Typography>
            )}
          </Paper>

          {/* ── Recognition details ─────────────────────────────────────── */}
          <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2} sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' }, mb: 2 }}>
              <Box>
                <Typography sx={{ fontWeight: 800 }}>How Nexora recognises {customer.name}</Typography>
                <Typography variant="body2" color="text.secondary">
                  The details on an incoming RFQ that tie it back to this customer.
                </Typography>
              </Box>
              {canConfigure && identifiers.length > 0 && (
                <Button variant="outlined" startIcon={<IdentityIcon />} onClick={openIdentityDialog} sx={{ fontWeight: 800, borderRadius: 2, whiteSpace: 'nowrap' }}>
                  Add recognition detail
                </Button>
              )}
            </Stack>

            {identifiers.length === 0 ? (
              <Box sx={{ py: 4, textAlign: 'center' }}>
                <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>
                  Nexora cannot recognise {customer.name} yet
                </Typography>
                <Typography variant="body2" color="text.secondary" sx={{ maxWidth: 640, mx: 'auto', mb: 3 }}>
                  Routing rules only fire once Nexora knows the RFQ came from this customer. Add at
                  least one detail it can match on — the mailbox they send from, their email domain,
                  their account number, or the company name they use on paperwork.
                </Typography>
                {canConfigure ? (
                  <Button variant="contained" startIcon={<IdentityIcon />} onClick={openIdentityDialog} sx={{ fontWeight: 800, borderRadius: 2 }}>
                    Add the first recognition detail
                  </Button>
                ) : (
                  <Typography variant="body2" color="text.secondary">
                    Ask a manager with Can Edit on Customers to add one.
                  </Typography>
                )}
              </Box>
            ) : (
              <TableContainer>
                <Table size="small">
                  <TableHead>
                    <TableRow>
                      <TableCell>Detail</TableCell>
                      <TableCell>Value</TableCell>
                      <TableCell>Confirmed</TableCell>
                      <TableCell>Added by</TableCell>
                      <TableCell>Seen</TableCell>
                      {canConfigure && <TableCell align="right">Action</TableCell>}
                    </TableRow>
                  </TableHead>
                  <TableBody>
                    {identifiers.map((identifier: CustomerIdentifierDTO) => (
                      <TableRow key={identifier.id} hover>
                        <TableCell>{IDENTIFIER_LABELS[identifier.identifierType] ?? `Type ${identifier.identifierType}`}</TableCell>
                        <TableCell>{identifier.displayValue}</TableCell>
                        <TableCell>
                          {identifier.isVerified
                            ? <Chip size="small" color="success" label="Confirmed" />
                            : <Chip size="small" label={`Suggested (${Math.round(identifier.confidence * 100)}%)`} />}
                        </TableCell>
                        <TableCell>{identifier.source}</TableCell>
                        <TableCell>
                          {identifier.observationCount} time{identifier.observationCount === 1 ? '' : 's'}
                          {identifier.effectiveTo ? ' (no longer used)' : ''}
                        </TableCell>
                        {canConfigure && (
                          <TableCell align="right">
                            {identifier.isVerified ? '—' : (
                              <Button
                                size="small"
                                disabled={saveIdentity.isPending}
                                onClick={() => saveIdentity.mutate({
                                  identifierType: identifier.identifierType,
                                  value: identifier.displayValue,
                                })}
                              >
                                Confirm this detail
                              </Button>
                            )}
                          </TableCell>
                        )}
                      </TableRow>
                    ))}
                  </TableBody>
                </Table>
              </TableContainer>
            )}
          </Paper>
        </Stack>
      )}

      {/* ── Add routing rule ───────────────────────────────────────────── */}
      <Dialog open={ruleOpen} onClose={() => !createRule.isPending && setRuleOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Add a routing rule</DialogTitle>
        <DialogContent>
          <Stack spacing={2.5} sx={{ pt: 1 }}>
            <Typography variant="body2" color="text.secondary">
              This rule decides who owns an RFQ from <strong>{customer?.name}</strong>.
            </Typography>

            <TextField
              select
              fullWidth
              label="When should this rule apply?"
              value={ruleForm.scope}
              onChange={(event) => setRuleForm({ ...ruleForm, scope: Number(event.target.value) as OwnershipScope, scopeKey: '' })}
              helperText={selectedScope?.what}
            >
              {SCOPES.map((scope) => (
                <MenuItem key={scope.value} value={scope.value}>{scope.label}</MenuItem>
              ))}
            </TextField>

            {needsKey && (
              <>
                <Autocomplete
                  freeSolo
                  options={
                    selectedScope?.keySource === 'productCategory'
                      ? (categoriesQuery.data?.items ?? []).map((category) => category.categoryName)
                      : selectedScope?.keySource === 'branch'
                        ? (branchesQuery.data ?? []).map((unit) => unit.businessUnitCode)
                        : []
                  }
                  value={ruleForm.scopeKey}
                  onInputChange={(_event, next) => setRuleForm((form) => ({ ...form, scopeKey: next }))}
                  renderInput={(params) => (
                    <TextField
                      {...params}
                      label={selectedScope?.keyLabel}
                      required
                      helperText={selectedScope?.keyHelp}
                    />
                  )}
                />
                {selectedScope?.dormant && (
                  <Alert severity="info">
                    Incoming RFQs do not carry a {selectedScope.keyLabel?.toLowerCase()} today, so this
                    rule will sit dormant until one is supplied. A customer exception, product
                    category or branch rule takes effect immediately.
                  </Alert>
                )}
              </>
            )}

            <TextField
              select
              fullWidth
              required
              label="Who should own it?"
              value={ruleForm.primaryUserId}
              onChange={(event) => setRuleForm({ ...ruleForm, primaryUserId: Number(event.target.value) })}
              helperText={usersQuery.isError
                ? 'The list of people could not be loaded. Reload the page and try again.'
                : 'The sales engineer the RFQ is assigned to. Must be an active user in this business unit.'}
              error={usersQuery.isError}
            >
              {owners.map((user) => (
                <MenuItem key={user.id} value={user.id}>
                  {`${user.firstName} ${user.lastName}`.trim()}{user.roleName ? ` — ${user.roleName}` : ''}
                </MenuItem>
              ))}
            </TextField>

            <TextField
              select
              fullWidth
              label="Backup owner (optional)"
              value={ruleForm.backupUserId}
              onChange={(event) => setRuleForm({ ...ruleForm, backupUserId: event.target.value === '' ? '' : Number(event.target.value) })}
              helperText={sameOwnerTwice
                ? 'The backup must be someone other than the owner.'
                : 'Used when the owner is unavailable or already at capacity.'}
              error={sameOwnerTwice}
            >
              <MenuItem value="">No backup</MenuItem>
              {owners.map((user) => (
                <MenuItem key={user.id} value={user.id}>
                  {`${user.firstName} ${user.lastName}`.trim()}
                </MenuItem>
              ))}
            </TextField>

            <TextField
              fullWidth
              type="number"
              label="Priority"
              value={ruleForm.priority}
              onChange={(event) => setRuleForm({ ...ruleForm, priority: event.target.value })}
              helperText="Only used to break a tie between two rules of the same kind. Higher wins. Leave at 0 if in doubt."
              slotProps={{ htmlInput: { step: 1 } }}
            />

            <Divider />

            <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
              <TextField
                fullWidth
                type="date"
                required
                label="In force from"
                value={ruleForm.effectiveFrom}
                onChange={(event) => setRuleForm({ ...ruleForm, effectiveFrom: event.target.value })}
                slotProps={{ inputLabel: { shrink: true } }}
              />
              <TextField
                fullWidth
                type="date"
                label="Until (optional)"
                value={ruleForm.effectiveTo}
                onChange={(event) => setRuleForm({ ...ruleForm, effectiveTo: event.target.value })}
                error={datesOutOfOrder}
                helperText={datesOutOfOrder ? 'The end date must be after the start date.' : 'Leave empty for a rule with no end date.'}
                slotProps={{ inputLabel: { shrink: true } }}
              />
            </Stack>

            <TextField
              fullWidth
              label="Reason (optional)"
              value={ruleForm.reason}
              onChange={(event) => setRuleForm({ ...ruleForm, reason: event.target.value })}
              helperText="Recorded with the rule so a colleague can see why it exists."
              multiline
              minRows={2}
              slotProps={{ htmlInput: { maxLength: 500 } }}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={() => setRuleOpen(false)} disabled={createRule.isPending}>Cancel</Button>
          <Button
            variant="contained"
            startIcon={createRule.isPending ? <CircularProgress size={18} color="inherit" /> : <AddIcon />}
            disabled={!ruleReady || createRule.isPending}
            onClick={() => createRule.mutate()}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Save routing rule
          </Button>
        </DialogActions>
      </Dialog>

      {/* ── Add / update recognition detail ────────────────────────────── */}
      <Dialog open={identityOpen} onClose={() => !saveIdentity.isPending && setIdentityOpen(false)} fullWidth maxWidth="sm">
        <DialogTitle sx={{ fontWeight: 800 }}>Add a recognition detail</DialogTitle>
        <DialogContent>
          <Stack spacing={2.5} sx={{ pt: 1 }}>
            <Typography variant="body2" color="text.secondary">
              Nexora will treat an RFQ carrying this detail as coming from <strong>{customer?.name}</strong>.
              A detail added here counts as confirmed, so it is trusted outright. Saving the same
              detail again updates the existing entry instead of duplicating it.
            </Typography>

            <TextField
              select
              fullWidth
              label="Detail type"
              value={identityForm.identifierType}
              onChange={(event) => setIdentityForm({ ...identityForm, identifierType: Number(event.target.value) as CustomerIdentifierType })}
              helperText={IDENTIFIER_TYPES.find((type) => type.value === identityForm.identifierType)?.help}
            >
              {IDENTIFIER_TYPES.map((type) => (
                <MenuItem key={type.value} value={type.value}>{type.label}</MenuItem>
              ))}
            </TextField>

            <TextField
              fullWidth
              required
              label="Value"
              value={identityForm.value}
              onChange={(event) => setIdentityForm({ ...identityForm, value: event.target.value })}
              placeholder={IDENTIFIER_TYPES.find((type) => type.value === identityForm.identifierType)?.placeholder}
              slotProps={{ htmlInput: { maxLength: 256 } }}
            />
          </Stack>
        </DialogContent>
        <DialogActions sx={{ px: 3, pb: 2 }}>
          <Button onClick={() => setIdentityOpen(false)} disabled={saveIdentity.isPending}>Cancel</Button>
          <Button
            variant="contained"
            startIcon={saveIdentity.isPending ? <CircularProgress size={18} color="inherit" /> : <IdentityIcon />}
            disabled={!identityReady || saveIdentity.isPending}
            onClick={() => saveIdentity.mutate(identityForm)}
            sx={{ fontWeight: 800, borderRadius: 2 }}
          >
            Save recognition detail
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
};

export default RoutingRulesPage;
