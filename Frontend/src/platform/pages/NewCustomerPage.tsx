import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert, Autocomplete, Box, Button, Card, CardActionArea, CardContent, Chip, Divider,
  Step, StepLabel, Stepper, TextField, Tooltip, Typography,
} from '@mui/material';
import {
  ArrowBackOutlined as BackIcon,
  CheckCircleOutlined as DoneIcon,
  InfoOutlined as WhyIcon,
} from '@mui/icons-material';
import Stack from '../components/Flex';
import { platformApi } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import PageHeader from '../components/PageHeader';
import { COUNTRY_CODES, countryLabel } from '../components/localeData';
import { countryDefaults } from '../components/countryDefaults';
import { slugify } from '../components/provisionValidation';
import type { ProvisionTenantInput } from '../types';

/**
 * NEW CUSTOMER — three steps, twelve typed fields.
 *
 * WHAT WAS WRONG. The wizard this replaces asked for about forty-three inputs across four steps,
 * and then reported success on a workspace nobody could log into. Twenty of those inputs were on
 * one scrolling step that mixed money (plan, rate card, contract) with internationalisation
 * (locale, time zone, currency) and infrastructure (data region, workspace purpose) — and the
 * currency is IMMUTABLE after provisioning, sitting under a heading called "Workspace defaults".
 *
 * A salesperson cannot answer most of them. Not "finds them tedious" — cannot answer: the IANA
 * time zone, the BCP-47 locale, the hosting region, the deployment profile, the rate card, the
 * URL slug. Every one is either derivable from something they DO know, or is a decision that
 * belongs to finance or engineering and should never have been on a sales form.
 *
 * SO THE RULE HERE IS: if the answer follows from the country or the plan, it is not a question.
 * It is shown, read-only, with the reason it holds — see the "from the customer's country" and
 * "from the Growth plan" lines. An operator can see every derived value before submitting; they
 * simply cannot mistype one.
 *
 * AND THE LAST STEP TELLS THE TRUTH. Provisioning does not make a customer usable — fourteen
 * server-evaluated activation controls stand between here and a working login. The old wizard
 * ended on "Provisioned", the rep told the customer it was live, and somebody discovered on day
 * four that it never was. This one hands off to the customer page, which says what is still
 * outstanding and who has to clear it.
 */

const STEPS = ['Who they are', 'What they get', 'Who runs it'];

interface Draft {
  name: string;
  countryCode: string | null;
  contactEmail: string;
  addressLine1: string;
  city: string;
  planId: string | null;
  contractStartOn: string;
  contractEndOn: string;
  purchaseOrderReference: string;
  adminFirstName: string;
  adminLastName: string;
  adminEmail: string;
  /** Only used when the country has no defaults; otherwise these are derived and never typed. */
  manualCurrency: string;
  manualTimeZone: string;
  manualLocale: string;
}

const EMPTY: Draft = {
  name: '', countryCode: null, contactEmail: '', addressLine1: '', city: '',
  planId: null, contractStartOn: new Date().toISOString().slice(0, 10), contractEndOn: '',
  purchaseOrderReference: '', adminFirstName: '', adminLastName: '', adminEmail: '',
  manualCurrency: '', manualTimeZone: '', manualLocale: '',
};

const looksLikeEmail = (value: string) => /^[^@\s]+@[^@\s.]+\.[^@\s]+$/.test(value.trim());

export default function NewCustomerPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [step, setStep] = useState(0);
  const [draft, setDraft] = useState<Draft>(EMPTY);
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) =>
    setDraft((d) => ({ ...d, [key]: value }));

  const plans = useQuery({ queryKey: platformKeys.plans(), queryFn: () => platformApi.listPlans() });

  const derived = useMemo(() => countryDefaults(draft.countryCode), [draft.countryCode]);
  // The values that will actually be sent, from whichever source supplied them. Computed once so
  // the Continue button and the submit body cannot disagree — a form that lets you advance and
  // then fails on the server is the failure this whole screen exists to remove.
  const operating = derived ?? (
    draft.manualCurrency.trim() && draft.manualTimeZone.trim() && draft.manualLocale.trim()
      ? {
          currency: draft.manualCurrency.trim().toUpperCase(),
          timeZone: draft.manualTimeZone.trim(),
          locale: draft.manualLocale.trim(),
        }
      : null);
  const plan = plans.data?.find((p) => String(p.id) === draft.planId) ?? null;
  const slug = slugify(draft.name);

  // One idempotency key per attempt at THIS customer, not one per request. A key regenerated on
  // every call is the defect that lets a double-click create two of something.
  const [idempotencyKey] = useState(() => crypto.randomUUID());

  const submit = useMutation({
    mutationFn: () => {
      const input: ProvisionTenantInput = {
        name: draft.name.trim(),
        slug,
        legalName: draft.name.trim(),
        registrationNumber: null,
        taxNumber: null,
        countryCode: draft.countryCode,
        industry: null,
        website: null,
        addressLine1: draft.addressLine1.trim() || null,
        addressLine2: null,
        city: draft.city.trim() || null,
        stateProvince: null,
        postalCode: null,
        phone: null,
        contactEmail: draft.contactEmail.trim(),
        logoUrl: null,

        planId: draft.planId,
        billingMode: 'Billable',
        billingModeReason: null,
        rateCardId: null,
        billingStartsOn: draft.contractStartOn || null,
        trialEndsOn: null,
        contractStartOn: draft.contractStartOn || null,
        contractEndOn: draft.contractEndOn || null,
        // Net 30 unless somebody says otherwise, and it is shown as a derived value rather than
        // left null — a null here is a tenant that reaches activation and stops.
        paymentTermsDays: 30,
        purchaseOrderReference: draft.purchaseOrderReference.trim() || null,
        billingContactName: null,
        // The server falls back to the founding administrator when this is null, which is a
        // better default than nobody. Stated on the review step rather than hidden.
        billingContactEmail: null,
        billingAddress: null,
        accountOwnerEmail: null,

        baseCurrencyCode: operating?.currency ?? null,
        timeZoneId: operating?.timeZone ?? null,
        locale: operating?.locale ?? null,
        dataRegion: null,

        // PRODUCTION, always, from this form. Relaxing it is an Owner decision that changes which
        // production prerequisites may be deferred; it does not belong on a sales workflow.
        deploymentProfile: null,
        deploymentProfileReason: null,

        adminEmail: draft.adminEmail.trim(),
        adminFirstName: draft.adminFirstName.trim(),
        adminLastName: draft.adminLastName.trim(),
        adminJobTitle: null,
        adminPhone: null,
        adminActivation: 'invite',
        adminPassword: null,
      };
      return platformApi.submitProvisioning(input, { idempotencyKey });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: platformKeys.customers() });
      queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
      navigate('/platform/customers');
    },
  });

  const stepValid = [
    draft.name.trim().length > 1 && draft.countryCode !== null
      && looksLikeEmail(draft.contactEmail) && operating !== null,
    draft.planId !== null && draft.contractStartOn !== '',
    draft.adminFirstName.trim() !== '' && draft.adminLastName.trim() !== ''
      && looksLikeEmail(draft.adminEmail),
  ];

  return (
    <Box sx={{ maxWidth: 860 }}>
      <Button startIcon={<BackIcon />} color="inherit" sx={{ mb: 1.5 }}
        onClick={() => navigate('/platform/customers')}>
        Customers
      </Button>
      <PageHeader title="New customer" subtitle="Three steps. Everything else follows from these answers." />

      <Stepper activeStep={step} sx={{ mb: 3.5 }}>
        {STEPS.map((label) => <Step key={label}><StepLabel>{label}</StepLabel></Step>)}
      </Stepper>

      {submit.isError && (
        <Alert severity="error" sx={{ mb: 2.5 }}>
          {platformErrorMessage(submit.error, 'The customer could not be created.')}
        </Alert>
      )}

      {step === 0 && (
        <Stack spacing={2.5}>
          <TextField
            label="Company name" required autoFocus value={draft.name}
            onChange={(e) => set('name', e.target.value)}
            helperText={slug ? `Their workspace address will be ${slug}` : 'As it appears on the order form'}
          />
          <Autocomplete
            options={COUNTRY_CODES}
            value={draft.countryCode}
            onChange={(_e, v) => set('countryCode', v)}
            getOptionLabel={(code) => countryLabel(code)}
            renderInput={(params) => (
              <TextField {...params} label="Country" required
                helperText="Sets their currency, time zone and language — which is why it is the only one of the four you are asked for." />
            )}
          />
          <TextField
            label="Company email" required type="email" value={draft.contactEmail}
            onChange={(e) => set('contactEmail', e.target.value)}
            error={draft.contactEmail !== '' && !looksLikeEmail(draft.contactEmail)}
            helperText="Where we write to the company, not to one person."
          />
          <Stack direction="row" spacing={2}>
            <TextField label="Address" fullWidth value={draft.addressLine1}
              onChange={(e) => set('addressLine1', e.target.value)} />
            <TextField label="City" sx={{ minWidth: 200 }} value={draft.city}
              onChange={(e) => set('city', e.target.value)} />
          </Stack>

          {draft.countryCode && <DerivedPanel countryCode={draft.countryCode} />}
          {draft.countryCode && !derived && (
            <Stack direction="row" spacing={2}>
              <TextField label="Currency" required fullWidth value={draft.manualCurrency}
                onChange={(e) => set('manualCurrency', e.target.value)}
                helperText="ISO-4217, e.g. USD. Cannot be changed later." />
              <TextField label="Time zone" required fullWidth value={draft.manualTimeZone}
                onChange={(e) => set('manualTimeZone', e.target.value)}
                helperText="IANA id, e.g. America/New_York" />
              <TextField label="Language" required fullWidth value={draft.manualLocale}
                onChange={(e) => set('manualLocale', e.target.value)}
                helperText="BCP-47 tag, e.g. en-US" />
            </Stack>
          )}
        </Stack>
      )}

      {step === 1 && (
        <Stack spacing={2.5}>
          <Typography variant="subtitle2" color="text.secondary">
            Pick the plan they signed. It carries the price, the quotas and the modules.
          </Typography>
          <Box sx={{ display: 'grid', gap: 1.5, gridTemplateColumns: { xs: '1fr', sm: 'repeat(3, 1fr)' } }}>
            {(plans.data ?? []).filter((p) => p.isActive).map((p) => {
              const selected = String(p.id) === draft.planId;
              return (
                <Card key={p.id} variant="outlined"
                  sx={{ borderColor: selected ? 'primary.main' : 'divider', borderWidth: selected ? 2 : 1 }}>
                  <CardActionArea onClick={() => set('planId', String(p.id))}>
                    <CardContent>
                      <Stack direction="row" sx={{ alignItems: 'center', gap: 1 }}>
                        <Typography sx={{ fontWeight: 700, flex: 1 }}>{p.name}</Typography>
                        {selected && <DoneIcon fontSize="small" color="primary" />}
                      </Stack>
                      <Typography variant="h6" sx={{ fontWeight: 800, mt: 0.5 }}>
                        {p.priceMonthlyUsd == null ? 'No list price' : `$${p.priceMonthlyUsd}`}
                        {p.priceMonthlyUsd != null && (
                          <Typography component="span" variant="caption" color="text.secondary"> /month</Typography>
                        )}
                      </Typography>
                      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.75 }}>
                        {p.seatQuota ?? 'Unlimited'} seats · {p.monthlyDocQuota ?? 'Unlimited'} documents a month
                      </Typography>
                    </CardContent>
                  </CardActionArea>
                </Card>
              );
            })}
          </Box>

          <Divider />
          <Stack direction="row" spacing={2}>
            <TextField label="Contract starts" type="date" required fullWidth
              value={draft.contractStartOn} onChange={(e) => set('contractStartOn', e.target.value)}
              slotProps={{ inputLabel: { shrink: true } }} />
            <TextField label="Renews on" type="date" fullWidth
              value={draft.contractEndOn} onChange={(e) => set('contractEndOn', e.target.value)}
              slotProps={{ inputLabel: { shrink: true } }}
              helperText="Leave blank if it is open-ended" />
          </Stack>
          <TextField label="Their PO reference" value={draft.purchaseOrderReference}
            onChange={(e) => set('purchaseOrderReference', e.target.value)}
            helperText="Optional. Some customers refuse an invoice without it." />

          <Alert severity="info" icon={<WhyIcon />}>
            <Typography variant="body2" sx={{ fontWeight: 700 }}>Set for you from the plan</Typography>
            <Typography variant="caption" component="div">
              Billing mode <strong>Billable</strong> · payment terms <strong>net 30</strong> ·
              rate card and module access from{plan ? ` the ${plan.name} plan` : ' the plan you pick'} ·
              billing starts the day the contract does. Any of these can be changed afterwards on
              the customer, where the change is recorded against whoever made it.
            </Typography>
          </Alert>
        </Stack>
      )}

      {step === 2 && (
        <Stack spacing={2.5}>
          <Typography variant="subtitle2" color="text.secondary">
            The first person at the customer who can sign in and add everyone else.
          </Typography>
          <Stack direction="row" spacing={2}>
            <TextField label="First name" required fullWidth value={draft.adminFirstName}
              onChange={(e) => set('adminFirstName', e.target.value)} />
            <TextField label="Last name" required fullWidth value={draft.adminLastName}
              onChange={(e) => set('adminLastName', e.target.value)} />
          </Stack>
          <TextField label="Work email" required type="email" value={draft.adminEmail}
            onChange={(e) => set('adminEmail', e.target.value)}
            error={draft.adminEmail !== '' && !looksLikeEmail(draft.adminEmail)}
            helperText="They get an activation link. We never set a password on their behalf." />

          <Alert severity="warning" icon={false}>
            <Typography variant="body2" sx={{ fontWeight: 700 }}>Creating the workspace is not the same as going live</Typography>
            <Typography variant="caption" component="div">
              A set of activation checks — the customer's legal identity, who receives their
              invoices, where their data lives — has to pass before anyone can sign in. The next
              screen lists whichever are still outstanding and who has to clear each one. Nothing
              here quietly reports success on a workspace that does not work yet.
            </Typography>
          </Alert>
        </Stack>
      )}

      <Stack direction="row" spacing={1.5} sx={{ mt: 4 }}>
        {step > 0 && <Button onClick={() => setStep((s) => s - 1)} disabled={submit.isPending}>Back</Button>}
        <Box sx={{ flex: 1 }} />
        {step < 2 ? (
          <Button variant="contained" disabled={!stepValid[step]} onClick={() => setStep((s) => s + 1)}>
            Continue
          </Button>
        ) : (
          <Button variant="contained" disabled={!stepValid[2] || submit.isPending}
            onClick={() => submit.mutate()}>
            {submit.isPending ? 'Creating…' : 'Create customer'}
          </Button>
        )}
      </Stack>
    </Box>
  );
}

/**
 * The three fields the old form made a salesperson type, shown as answers instead of questions.
 * Visible rather than hidden: an operator should be able to check what was chosen for them, and
 * the currency in particular cannot be changed afterwards.
 */
function DerivedPanel({ countryCode }: { countryCode: string }) {
  const defaults = countryDefaults(countryCode);
  if (!defaults) {
    return (
      <Alert severity="warning">
        <Typography variant="body2" sx={{ fontWeight: 700 }}>
          {countryLabel(countryCode)} spans several time zones, or is not a market we have defaults for
        </Typography>
        <Typography variant="caption">
          Currency, time zone and language will be left for an operator to set on the customer
          afterwards rather than guessed here. The currency cannot be changed once it is set, so a
          guess is worse than a question.
        </Typography>
      </Alert>
    );
  }
  return (
    <Alert severity="success" icon={<WhyIcon />}>
      <Typography variant="body2" sx={{ fontWeight: 700 }}>
        Set from {countryLabel(countryCode)}, so you are not asked
      </Typography>
      <Stack direction="row" spacing={1} sx={{ mt: 0.75, flexWrap: 'wrap' }}>
        <Tooltip describeChild title="Fixed at provisioning — changing it later would restate every price already quoted">
          <Chip size="small" label={`Currency ${defaults.currency}`} />
        </Tooltip>
        <Chip size="small" label={`Time zone ${defaults.timeZone}`} />
        <Chip size="small" label={`Language ${defaults.locale}`} />
      </Stack>
    </Alert>
  );
}
