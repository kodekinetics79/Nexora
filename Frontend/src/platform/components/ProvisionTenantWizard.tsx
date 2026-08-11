import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Autocomplete,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  FormControl,
  FormControlLabel,
  FormLabel,
  Grid,
  IconButton,
  InputAdornment,
  LinearProgress,
  MenuItem,
  Paper,
  Radio,
  RadioGroup,
  Step,
  StepButton,
  Stepper,
  TextField,
  Typography,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  ArrowForward as NextIcon,
  CheckCircleOutlined as SlugFreeIcon,
  Close as CloseIcon,
  DeleteOutlined as DeleteDraftIcon,
  Edit as EditIcon,
  ErrorOutlined as SlugTakenIcon,
  RocketLaunch as ProvisionIcon,
  SaveOutlined as SaveDraftIcon,
  Visibility,
  VisibilityOff,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import Stack from './Flex';
import { platformApi, toProvisionRequestBody } from '../api/client';
import { platformErrorMessage } from '../api/apiError';
import { platformKeys } from '../api/queryKeys';
import { BILLING_MODES } from '../types';
import type { BillingMode, SubmitProvisioningResult } from '../types';
import {
  COUNTRY_CODES,
  CURRENCY_CODES,
  LOCALE_SUGGESTIONS,
  TIME_ZONE_IDS,
  countryLabel,
  resolvedTimeZone,
} from './localeData';
import { PASSWORD_MAX_LENGTH, passwordProblem, readPasswordStrength } from '../../utils/passwordPolicy';
import {
  draftFromRequestBody,
  emptyDraft,
  reviewAdvisories,
  slugify,
  STEP_ADMIN,
  STEP_COMMERCIAL,
  STEP_COMPANY,
  STEP_REVIEW,
  todayIso,
  toProvisionInput,
  validateStep,
  WIZARD_STEPS,
  type DraftField,
  type ProvisionDraft,
} from './provisionValidation';

/**
 * The replay guard sent with every submit.
 *
 * <p>Keyed on the CONTENT of the request, not on the click: an impatient second press, or a
 * retry after a proxy timed out, sends the same key and the server replays the original
 * execution instead of building a second workspace for the same customer. Editing anything
 * produces a different fingerprint and therefore a genuinely new attempt, which is what an
 * operator means when they fix a rejected form and submit again.</p>
 */
function useIdempotencyKey() {
  const issued = useRef<{ fingerprint: string; key: string } | null>(null);
  return (fingerprint: string): string => {
    if (issued.current?.fingerprint === fingerprint) return issued.current.key;
    const key =
      typeof crypto !== 'undefined' && 'randomUUID' in crypto
        ? crypto.randomUUID()
        : `prov-${Date.now()}-${Math.random().toString(36).slice(2, 12)}`;
    issued.current = { fingerprint, key };
    return key;
  };
}

const BILLING_MODE_COPY: Record<BillingMode, { title: string; detail: string }> = {
  Billable: { title: 'Billable', detail: 'Charged against a plan and rate card from the billing start date.' },
  Trial: { title: 'Trial', detail: 'Free until a fixed end date. The end date is mandatory.' },
  Internal: { title: 'Internal', detail: 'Our own workspace — demo, staging or support. Never invoiced.' },
  Partner: { title: 'Partner', detail: 'Reseller or integration partner operating under a separate agreement.' },
};

/** Ties the "this step has problems" summary to the step panel it describes. */
const STEP_ERROR_ID = 'provision-step-errors';

interface Props {
  open: boolean;
  onClose: () => void;
  /**
   * Handed the accepted submit so the caller can watch the execution. Provisioning is
   * durable and asynchronous: nothing has been created yet when this fires.
   */
  onSubmitted: (result: SubmitProvisioningResult) => void;
}

export default function ProvisionTenantWizard({ open, onClose, onSubmitted }: Props) {
  const queryClient = useQueryClient();
  const { enqueueSnackbar } = useSnackbar();
  const idempotencyKeyFor = useIdempotencyKey();

  const [draft, setDraft] = useState<ProvisionDraft>(() => freshDraft());
  const [step, setStep] = useState(STEP_COMPANY);
  // A field shows its error once the operator has left it, or once they have
  // tried to leave the step — not while they are still part-way through typing.
  const [touched, setTouched] = useState<Set<DraftField>>(new Set());
  const [attempted, setAttempted] = useState<Set<number>>(new Set());
  const [showPassword, setShowPassword] = useState(false);
  const [slugEdited, setSlugEdited] = useState(false);
  const [legalNameEdited, setLegalNameEdited] = useState(false);
  // The saved draft this session is editing, if any. The version travels back on save so
  // two tabs open on one draft cannot silently overwrite each other.
  const [draftRef, setDraftRef] = useState<{ id: string; version: number } | null>(null);
  const [resumeOpen, setResumeOpen] = useState(false);
  // Debounced separately from `draft.slug` so the availability call fires when typing
  // pauses rather than on every keystroke.
  const [slugProbe, setSlugProbe] = useState('');

  // Changing step swaps the entire panel. Without moving focus, a screen reader
  // and a keyboard user both stay parked on the Next button with no indication
  // that the content behind them is now a different form.
  const panelRef = useRef<HTMLDivElement>(null);
  const focusedStep = useRef(step);
  useEffect(() => {
    if (focusedStep.current === step) return;
    focusedStep.current = step;
    panelRef.current?.focus();
  }, [step]);

  useEffect(() => {
    if (!open) return;
    setDraft(freshDraft());
    setStep(STEP_COMPANY);
    setTouched(new Set());
    setAttempted(new Set());
    setShowPassword(false);
    setSlugEdited(false);
    setLegalNameEdited(false);
    setDraftRef(null);
    setResumeOpen(false);
  }, [open]);

  useEffect(() => {
    const timer = setTimeout(() => setSlugProbe(draft.slug.trim()), 400);
    return () => clearTimeout(timer);
  }, [draft.slug]);

  const { data: plans } = useQuery({
    queryKey: platformKeys.plans(),
    queryFn: () => platformApi.listPlans(),
    enabled: open,
  });

  const { data: rateCards } = useQuery({
    queryKey: platformKeys.rateCards(),
    queryFn: () => platformApi.listRateCards(),
    enabled: open,
  });

  const { data: drafts } = useQuery({
    queryKey: platformKeys.provisioningDrafts(),
    queryFn: () => platformApi.listProvisioningDrafts(),
    enabled: open,
  });

  // Asked as the operator types, because the address is only free to change now: after
  // provisioning it is permanent on the business unit code and every reference derived
  // from it.
  const slugCheck = useQuery({
    queryKey: platformKeys.slugCheck(slugProbe),
    queryFn: () => platformApi.checkSlug({ slug: slugProbe }),
    enabled: open && slugProbe.length >= 2,
    staleTime: 30_000,
  });

  const submitMutation = useMutation({
    mutationFn: () => {
      const body = toProvisionRequestBody(toProvisionInput(draft));
      return platformApi.submitProvisioning(toProvisionInput(draft), {
        idempotencyKey: idempotencyKeyFor(JSON.stringify(body)),
        fromDraftId: draftRef?.id ?? null,
      });
    },
    onSuccess: (result) => {
      queryClient.invalidateQueries({ queryKey: platformKeys.tenants() });
      queryClient.invalidateQueries({ queryKey: platformKeys.overview() });
      queryClient.invalidateQueries({ queryKey: platformKeys.provisioningDrafts() });
      onSubmitted(result);
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'Provisioning was not accepted'), { variant: 'error' }),
  });

  const saveDraftMutation = useMutation({
    mutationFn: () => {
      const payload = toProvisionRequestBody(toProvisionInput(draft));
      const name = draft.name.trim() || draft.slug.trim() || 'Untitled workspace';
      return draftRef
        ? platformApi.updateProvisioningDraft(draftRef.id, name, payload, draftRef.version)
        : platformApi.createProvisioningDraft(name, payload);
    },
    onSuccess: (saved) => {
      setDraftRef({ id: saved.id, version: saved.version });
      queryClient.invalidateQueries({ queryKey: platformKeys.provisioningDrafts() });
      enqueueSnackbar(`Draft “${saved.name}” saved`, { variant: 'success' });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The draft could not be saved'), { variant: 'error' }),
  });

  const loadDraftMutation = useMutation({
    mutationFn: (id: string) => platformApi.getProvisioningDraft(id),
    onSuccess: (loaded) => {
      if (!loaded.payload) {
        enqueueSnackbar('That draft carries no payload to restore.', { variant: 'warning' });
        return;
      }
      setDraft(draftFromRequestBody(loaded.payload));
      setDraftRef({ id: loaded.id, version: loaded.version });
      setStep(STEP_COMPANY);
      setTouched(new Set());
      setAttempted(new Set());
      // Both mirrors are treated as taken over: the saved values are the operator's, and
      // re-deriving them from the name would silently rewrite what they chose.
      setSlugEdited(true);
      setLegalNameEdited(true);
      setResumeOpen(false);
      enqueueSnackbar(`Resumed “${loaded.name}”`, { variant: 'success' });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The draft could not be loaded'), { variant: 'error' }),
  });

  const deleteDraftMutation = useMutation({
    mutationFn: (id: string) => platformApi.deleteProvisioningDraft(id),
    onSuccess: (_result, id) => {
      if (draftRef?.id === id) setDraftRef(null);
      queryClient.invalidateQueries({ queryKey: platformKeys.provisioningDrafts() });
    },
    onError: (error) =>
      enqueueSnackbar(platformErrorMessage(error, 'The draft could not be discarded'), { variant: 'error' }),
  });

  const provisionMutation = submitMutation;

  const errors = useMemo(() => validateStep(step, draft), [step, draft]);
  const errorCount = Object.keys(errors).length;
  const stepAttempted = attempted.has(step);

  const set = (patch: Partial<ProvisionDraft>) => setDraft((current) => ({ ...current, ...patch }));
  const markTouched = (field: DraftField) =>
    setTouched((current) => (current.has(field) ? current : new Set(current).add(field)));

  const errorFor = (field: DraftField): string | undefined =>
    touched.has(field) || stepAttempted ? errors[field] : undefined;

  /** Shared wiring so every field is labelled, error-linked and blur-validated the same way. */
  const field = (name: DraftField, label: string, extra?: { helper?: string; required?: boolean }) => {
    const message = errorFor(name);
    return {
      label,
      required: extra?.required,
      value: draft[name] as string,
      onChange: (event: { target: { value: string } }) => set({ [name]: event.target.value } as Partial<ProvisionDraft>),
      onBlur: () => markTouched(name),
      error: Boolean(message),
      // A space rather than undefined: without it the helper line only exists
      // once a field is invalid, and every neighbouring field jumps down the
      // moment an error appears mid-form.
      helperText: message ?? extra?.helper ?? ' ',
      fullWidth: true,
    };
  };

  const goTo = (next: number) => {
    if (next > step) {
      // Forward movement is the gate: an invalid step cannot be left behind,
      // whether the operator uses Next or clicks ahead in the stepper.
      const blocking = validateStep(step, draft);
      if (Object.keys(blocking).length > 0) {
        setAttempted((current) => new Set(current).add(step));
        return;
      }
    }
    setStep(next);
  };

  const canProvision = Object.keys(validateStep(STEP_REVIEW, draft)).length === 0;
  const advisories = step === STEP_REVIEW ? reviewAdvisories(draft) : [];

  const planLabel = (id: string) => {
    const plan = (plans ?? []).find((p) => p.id === id);
    return plan ? `${plan.name} (${plan.code})` : '—';
  };
  const rateCardLabel = (id: string) => {
    const card = (rateCards ?? []).find((c) => c.id === id);
    return card ? `${card.code} · ${card.currency}` : '—';
  };

  const passwordReading = readPasswordStrength(draft.adminPassword);

  // Only trusted while the answer describes what is currently in the box; a stale reply
  // from two keystroke ago claiming "available" is worse than saying nothing.
  const slugAvailability = slugProbe === draft.slug.trim() && !slugCheck.isFetching ? slugCheck.data : undefined;
  const slugTaken = slugAvailability != null && !slugAvailability.isAvailable;
  const slugHelper = slugAvailability
    ? slugAvailability.isAvailable
      ? `${slugAvailability.slug ?? draft.slug} is available.`
      : (slugAvailability.message ?? 'This address is not available.')
    : 'Used in the workspace address and the tenant id. Permanent once provisioned.';

  const openDrafts = (drafts ?? []).filter((entry) => entry.submittedExecutionId === null);

  return (
    <Dialog
      open={open}
      onClose={() => (provisionMutation.isPending ? undefined : onClose())}
      fullWidth
      maxWidth="md"
      aria-labelledby="provision-wizard-title"
      slotProps={{ paper: { sx: { borderRadius: 3 } } }}
    >
      <DialogTitle sx={{ fontWeight: 800, pr: 7 }}>
        {/* The id sits on the heading text alone: putting it on the whole title
            would fold the subtitle and the close button into the dialog's name. */}
        <Box component="span" id="provision-wizard-title">
          Create a company workspace
        </Box>
        <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 500, mt: 0.5 }}>
          Everything downstream — invoices, quotes, users, support — inherits what is entered here.
        </Typography>
        <IconButton
          onClick={onClose}
          disabled={provisionMutation.isPending}
          aria-label="Cancel tenant creation"
          sx={{ position: 'absolute', right: 12, top: 12 }}
        >
          <CloseIcon />
        </IconButton>
      </DialogTitle>

      <Box sx={{ px: 3, pt: 1, pb: 2 }}>
        <Stepper nonLinear activeStep={step} alternativeLabel>
          {WIZARD_STEPS.map((definition, index) => (
            <Step key={definition.key} completed={index < step}>
              <StepButton
                onClick={() => goTo(index)}
                disabled={provisionMutation.isPending}
                sx={{ '& .MuiStepLabel-label': { fontWeight: index === step ? 800 : 600 } }}
              >
                {definition.label}
              </StepButton>
            </Step>
          ))}
        </Stepper>
      </Box>

      <DialogContent
        dividers
        ref={panelRef}
        tabIndex={-1}
        aria-label={WIZARD_STEPS[step].label}
        sx={{ minHeight: 420, '&:focus': { outline: 'none' } }}
      >
        {stepAttempted && errorCount > 0 && step !== STEP_REVIEW && (
          <Alert id={STEP_ERROR_ID} role="alert" severity="error" sx={{ mb: 2.5, borderRadius: 2 }}>
            {errorCount === 1
              ? 'One field on this step needs attention before you can continue.'
              : `${errorCount} fields on this step need attention before you can continue.`}
          </Alert>
        )}

        {step === STEP_COMPANY && (
          <Stack spacing={2.5}>
            {openDrafts.length > 0 && (
              <Alert
                severity="info"
                sx={{ borderRadius: 2 }}
                action={
                  <Button color="inherit" size="small" onClick={() => setResumeOpen((o) => !o)} sx={{ fontWeight: 700 }}>
                    {resumeOpen ? 'Hide' : 'Resume one'}
                  </Button>
                }
              >
                You have {openDrafts.length} saved {openDrafts.length === 1 ? 'draft' : 'drafts'} that were never
                submitted.
              </Alert>
            )}
            {resumeOpen && (
              <Paper variant="outlined" sx={{ borderRadius: 2, p: 1 }}>
                <Stack spacing={0.5}>
                  {openDrafts.map((entry) => (
                    <Stack key={entry.id} direction="row" alignItems="center" spacing={1} sx={{ px: 1, py: 0.75 }}>
                      <Box sx={{ flex: 1, minWidth: 0 }}>
                        <Typography variant="body2" sx={{ fontWeight: 700 }}>
                          {entry.name}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                          Saved {new Date(entry.updatedOn).toLocaleString()} · {entry.ownerEmail}
                        </Typography>
                      </Box>
                      <Button
                        size="small"
                        onClick={() => loadDraftMutation.mutate(entry.id)}
                        disabled={loadDraftMutation.isPending}
                        sx={{ fontWeight: 700 }}
                      >
                        Resume
                      </Button>
                      <IconButton
                        size="small"
                        color="error"
                        aria-label={`Discard draft ${entry.name}`}
                        onClick={() => deleteDraftMutation.mutate(entry.id)}
                        disabled={deleteDraftMutation.isPending}
                      >
                        <DeleteDraftIcon fontSize="small" />
                      </IconButton>
                    </Stack>
                  ))}
                </Stack>
              </Paper>
            )}
            <SectionHeading
              title="Who is this company?"
              detail="The registered identity behind the workspace. It is what appears on invoices, quotes and every document the customer sends out."
            />
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('name', 'Organization name', { required: true, helper: 'The trading name people will recognise.' })}
                  onChange={(event) => {
                    const value = event.target.value;
                    set({
                      name: value,
                      // Mirrored until the operator takes them over, so the common
                      // case needs no typing and the exceptions stay editable.
                      ...(slugEdited ? {} : { slug: slugify(value) }),
                      ...(legalNameEdited ? {} : { legalName: value }),
                    });
                  }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('slug', 'Workspace slug', { required: true, helper: slugHelper })}
                  onChange={(event) => {
                    setSlugEdited(true);
                    set({ slug: event.target.value });
                  }}
                  error={Boolean(errorFor('slug')) || slugTaken}
                  helperText={errorFor('slug') ?? slugHelper}
                  slotProps={{
                    input: {
                      endAdornment: slugAvailability ? (
                        <InputAdornment position="end">
                          {slugAvailability.isAvailable ? (
                            <SlugFreeIcon fontSize="small" color="success" />
                          ) : (
                            <SlugTakenIcon fontSize="small" color="error" />
                          )}
                        </InputAdornment>
                      ) : undefined,
                    },
                  }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('legalName', 'Registered legal name', { required: true, helper: 'The name on the certificate of incorporation.' })}
                  onChange={(event) => {
                    setLegalNameEdited(true);
                    set({ legalName: event.target.value });
                  }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('industry', 'Industry', { helper: 'Optional. Drives nothing yet; useful for segmentation.' })} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('registrationNumber', 'Company registration number', { helper: 'Optional, but expected on invoices in most jurisdictions.' })} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('taxNumber', 'Tax / VAT number', { helper: 'Optional. Without it, tax-compliant invoicing may not be possible.' })} />
              </Grid>
            </Grid>

            <SectionHeading title="Where do we reach them?" />
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('contactEmail', 'Company contact email', { required: true, helper: 'The general address for this account, not the administrator’s.' })}
                  type="email"
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('phone', 'Phone', { helper: 'Include the country code.' })} type="tel" />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('website', 'Website')} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('logoUrl', 'Logo URL', { helper: 'Optional https:// link used on the customer’s documents.' })} />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <TextField {...field('addressLine1', 'Address line 1', { required: true })} />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <TextField {...field('addressLine2', 'Address line 2')} />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField {...field('city', 'City', { required: true })} />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField {...field('stateProvince', 'State / province')} />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <TextField {...field('postalCode', 'Postal code')} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <Autocomplete
                  options={COUNTRY_CODES}
                  value={draft.countryCode || null}
                  onChange={(_event, value) => {
                    markTouched('countryCode');
                    set({ countryCode: value ?? '' });
                  }}
                  onBlur={() => markTouched('countryCode')}
                  getOptionLabel={(option) => countryLabel(option)}
                  isOptionEqualToValue={(option, value) => option === value}
                  renderInput={(params) => (
                    <TextField
                      {...params}
                      label="Country of registration"
                      required
                      error={Boolean(errorFor('countryCode'))}
                      helperText={errorFor('countryCode') ?? 'Determines tax treatment and data-residency defaults.'}
                    />
                  )}
                />
              </Grid>
            </Grid>
          </Stack>
        )}

        {step === STEP_COMMERCIAL && (
          <Stack spacing={2.5}>
            <SectionHeading
              title="How does this tenant pay?"
              detail="Anything other than Billable is service given away. The platform records who decided that, and why."
            />

            <FormControl>
              <FormLabel id="billing-mode-label" sx={{ fontWeight: 700, mb: 1 }}>
                Billing mode
              </FormLabel>
              <RadioGroup
                aria-labelledby="billing-mode-label"
                value={draft.billingMode}
                onChange={(event) => {
                  const mode = event.target.value as BillingMode;
                  // Billing starts on day one for a paying tenant unless the operator
                  // says otherwise. Moving off Billable drops that default so a tenant
                  // nobody invoices does not carry a billing start date it never uses —
                  // but a date the operator actually chose is left alone.
                  const untouchedDefault = draft.billingStartsOn === todayIso();
                  set({
                    billingMode: mode,
                    billingStartsOn:
                      mode === 'Billable'
                        ? draft.billingStartsOn || todayIso()
                        : untouchedDefault
                          ? ''
                          : draft.billingStartsOn,
                  });
                }}
              >
                <Grid container spacing={1.5}>
                  {BILLING_MODES.map((mode) => {
                    const selected = draft.billingMode === mode;
                    return (
                      <Grid size={{ xs: 12, sm: 6 }} key={mode}>
                        <Paper
                          variant="outlined"
                          sx={{
                            p: 1.5,
                            borderRadius: 2,
                            borderColor: selected ? 'primary.main' : 'divider',
                            bgcolor: selected ? 'action.hover' : 'transparent',
                            transition: 'border-color 0.15s ease, background-color 0.15s ease',
                          }}
                        >
                          <FormControlLabel
                            value={mode}
                            control={<Radio />}
                            sx={{ alignItems: 'flex-start', m: 0 }}
                            label={
                              <Box sx={{ pt: 0.75 }}>
                                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                                  {BILLING_MODE_COPY[mode].title}
                                </Typography>
                                <Typography variant="caption" color="text.secondary">
                                  {BILLING_MODE_COPY[mode].detail}
                                </Typography>
                              </Box>
                            }
                          />
                        </Paper>
                      </Grid>
                    );
                  })}
                </Grid>
              </RadioGroup>
            </FormControl>

            {draft.billingMode !== 'Billable' && (
              <TextField
                {...field('billingModeReason', `Why is this tenant ${draft.billingMode} and not billable?`, {
                  required: true,
                  helper: 'Recorded against the tenant. Name the agreement, the sponsor, or the ticket.',
                })}
                multiline
                minRows={2}
              />
            )}

            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('planId', 'Plan', {
                    required: draft.billingMode === 'Billable',
                    helper:
                      draft.billingMode === 'Billable'
                        ? 'Sets the quotas, scheduling weight and price for this tenant.'
                        : 'Optional for a non-billable tenant, but still sets quotas.',
                  })}
                  select
                >
                  <MenuItem value="">No plan</MenuItem>
                  {(plans ?? []).map((plan) => (
                    <MenuItem key={plan.id} value={plan.id}>
                      {plan.name} ({plan.code})
                    </MenuItem>
                  ))}
                </TextField>
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('rateCardId', 'Rate card', {
                    helper: 'Prices metered usage. Without one, usage is recorded but never billed.',
                  })}
                  select
                >
                  <MenuItem value="">No rate card</MenuItem>
                  {(rateCards ?? []).map((card) => (
                    <MenuItem key={card.id} value={card.id}>
                      {card.code} · {card.currency}
                      {card.isActive ? '' : ' (inactive)'}
                    </MenuItem>
                  ))}
                </TextField>
              </Grid>

              {draft.billingMode === 'Trial' && (
                <Grid size={{ xs: 12, md: 6 }}>
                  <TextField
                    {...field('trialEndsOn', 'Trial ends on', {
                      required: true,
                      helper: 'Mandatory. A trial with no end date is an unlimited giveaway.',
                    })}
                    type="date"
                    slotProps={{ inputLabel: { shrink: true }, htmlInput: { min: todayIso() } }}
                  />
                </Grid>
              )}
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('billingStartsOn', 'Billing starts on', {
                    required: draft.billingMode === 'Billable',
                    helper: draft.billingMode === 'Billable' ? 'The first day of the first billable period.' : 'Optional for a non-billable tenant.',
                  })}
                  type="date"
                  slotProps={{ inputLabel: { shrink: true } }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('contractStartOn', 'Contract starts on')} type="date" slotProps={{ inputLabel: { shrink: true } }} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('contractEndOn', 'Contract ends on')} type="date" slotProps={{ inputLabel: { shrink: true } }} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('paymentTermsDays', 'Payment terms (days)', { helper: 'Days from invoice to due date, e.g. 30.' })}
                  type="number"
                  slotProps={{ htmlInput: { min: 0, max: 365, step: 1 } }}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('purchaseOrderReference', 'Customer PO reference', { helper: 'Quoted on invoices when the customer requires it.' })} />
              </Grid>
            </Grid>

            <SectionHeading title="Who is billed, and who owns the account?" />
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('billingContactName', 'Billing contact name', { required: draft.billingMode === 'Billable' })} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('billingContactEmail', 'Billing contact email', { required: draft.billingMode === 'Billable' })}
                  type="email"
                  slotProps={{
                    input: {
                      endAdornment: draft.contactEmail && draft.contactEmail !== draft.billingContactEmail ? (
                        <InputAdornment position="end">
                          <Button size="small" onClick={() => set({ billingContactEmail: draft.contactEmail })}>
                            Use company
                          </Button>
                        </InputAdornment>
                      ) : undefined,
                    },
                  }}
                />
              </Grid>
              <Grid size={{ xs: 12 }}>
                <TextField
                  {...field('billingAddress', 'Billing address', { helper: 'Only when invoices go somewhere other than the company address.' })}
                  multiline
                  minRows={2}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('accountOwnerEmail', 'Account owner email (internal)', {
                    required: true,
                    // Says EMAIL in the label and the helper because the field only accepts one.
                    // It used to read "Account owner (internal)" / "The person here who answers for
                    // this account commercially", so an operator typed a name, and the only hint
                    // that a name was wrong was "Enter a valid email address" appearing after the
                    // fact on a field that had just asked them to name somebody.
                    helper: 'Work email of the person here who answers for this account commercially.',
                  })}
                  type="email"
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('dataRegion', 'Data region', { helper: 'Where this tenant’s data is stored, if the deployment is regionalised.' })} />
              </Grid>
            </Grid>

            <SectionHeading title="Workspace defaults" detail="Applied to every document and schedule inside the workspace." />
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 4 }}>
                <Autocomplete
                  options={CURRENCY_CODES}
                  value={draft.baseCurrencyCode || null}
                  onChange={(_event, value) => {
                    markTouched('baseCurrencyCode');
                    set({ baseCurrencyCode: value ?? '' });
                  }}
                  onBlur={() => markTouched('baseCurrencyCode')}
                  isOptionEqualToValue={(option, value) => option === value}
                  renderInput={(params) => (
                    <TextField
                      {...params}
                      label="Base currency"
                      required
                      error={Boolean(errorFor('baseCurrencyCode'))}
                      helperText={errorFor('baseCurrencyCode') ?? 'The currency the tenant quotes and reports in.'}
                    />
                  )}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Autocomplete
                  options={TIME_ZONE_IDS}
                  value={draft.timeZoneId || null}
                  onChange={(_event, value) => {
                    markTouched('timeZoneId');
                    set({ timeZoneId: value ?? '' });
                  }}
                  onBlur={() => markTouched('timeZoneId')}
                  isOptionEqualToValue={(option, value) => option === value}
                  renderInput={(params) => (
                    <TextField
                      {...params}
                      label="Time zone"
                      required
                      error={Boolean(errorFor('timeZoneId'))}
                      helperText={errorFor('timeZoneId') ?? 'Deadlines and SLA clocks are read in this zone.'}
                    />
                  )}
                />
              </Grid>
              <Grid size={{ xs: 12, md: 4 }}>
                <Autocomplete
                  // freeSolo: the suggestion list is the common cases, not the
                  // permitted set — any valid BCP-47 tag is accepted.
                  freeSolo
                  options={LOCALE_SUGGESTIONS}
                  value={draft.locale}
                  onChange={(_event, value) => set({ locale: value ?? '' })}
                  inputValue={draft.locale}
                  onInputChange={(_event, value) => set({ locale: value })}
                  onBlur={() => markTouched('locale')}
                  renderInput={(params) => (
                    <TextField
                      {...params}
                      label="Language & region"
                      required
                      error={Boolean(errorFor('locale'))}
                      helperText={errorFor('locale') ?? 'BCP-47 tag, e.g. en-US. Formats dates and numbers.'}
                    />
                  )}
                />
              </Grid>
            </Grid>
          </Stack>
        )}

        {step === STEP_ADMIN && (
          <Stack spacing={2.5}>
            <SectionHeading
              title="Who runs this workspace?"
              detail="This person receives full authority over the workspace and creates the customer’s own users from there. A tenant provisioned without one cannot be logged into at all."
            />
            <Grid container spacing={2}>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('adminFirstName', 'First name', { required: true })} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('adminLastName', 'Last name', { required: true })} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField
                  {...field('adminEmail', 'Work email', {
                    required: true,
                    helper: 'Also their sign-in username. One address belongs to one workspace.',
                  })}
                  type="email"
                />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('adminJobTitle', 'Job title')} />
              </Grid>
              <Grid size={{ xs: 12, md: 6 }}>
                <TextField {...field('adminPhone', 'Phone')} type="tel" />
              </Grid>
            </Grid>

            <Divider />

            <FormControl>
              <FormLabel id="activation-label" sx={{ fontWeight: 700, mb: 1 }}>
                How does this administrator get their first credential?
              </FormLabel>
              <RadioGroup
                aria-labelledby="activation-label"
                value={draft.adminActivation}
                onChange={(event) => set({ adminActivation: event.target.value as ProvisionDraft['adminActivation'] })}
              >
                <FormControlLabel
                  value="invite"
                  control={<Radio />}
                  sx={{ alignItems: 'flex-start', mb: 1 }}
                  label={
                    <Box sx={{ pt: 0.75 }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>
                        Send an activation link
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        No password is created. They open a single-use link and choose their own, so no
                        secret ever passes through you.
                      </Typography>
                    </Box>
                  }
                />
                <FormControlLabel
                  value="password"
                  control={<Radio />}
                  sx={{ alignItems: 'flex-start' }}
                  label={
                    <Box sx={{ pt: 0.75 }}>
                      <Typography variant="body2" sx={{ fontWeight: 700 }}>
                        Set a password now
                      </Typography>
                      <Typography variant="caption" color="text.secondary">
                        For customers who cannot receive our mail. You hand the credential over yourself,
                        through a secure channel.
                      </Typography>
                    </Box>
                  }
                />
              </RadioGroup>
            </FormControl>

            {draft.adminActivation === 'password' && (
              <Stack spacing={1.5}>
                <TextField
                  {...field('adminPassword', 'Initial password', {
                    helper: passwordProblem(draft.adminPassword) ?? 'Leave blank and one is generated for you — shown once, on the next screen.',
                  })}
                  type={showPassword ? 'text' : 'password'}
                  autoComplete="new-password"
                  slotProps={{
                    htmlInput: { maxLength: PASSWORD_MAX_LENGTH },
                    input: {
                      endAdornment: (
                        <InputAdornment position="end">
                          <IconButton
                            onClick={() => setShowPassword((visible) => !visible)}
                            edge="end"
                            size="small"
                            aria-label={showPassword ? 'Hide password' : 'Show password'}
                            aria-pressed={showPassword}
                          >
                            {showPassword ? <VisibilityOff fontSize="small" /> : <Visibility fontSize="small" />}
                          </IconButton>
                        </InputAdornment>
                      ),
                    },
                  }}
                />
                {draft.adminPassword.length > 0 && (
                  <Box>
                    <LinearProgress
                      variant="determinate"
                      value={passwordReading.score}
                      color={passwordReading.strength === 'weak' ? 'error' : passwordReading.strength === 'fair' ? 'warning' : 'success'}
                      sx={{ height: 6, borderRadius: 3 }}
                    />
                    <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>
                      {passwordReading.label}
                    </Typography>
                  </Box>
                )}
              </Stack>
            )}

            {draft.adminActivation === 'invite' && (
              <Alert severity="info" sx={{ borderRadius: 2 }}>
                The activation link and its expiry are shown on the next screen, so you can pass them on
                yourself if the customer’s mail server rejects ours.
              </Alert>
            )}
          </Stack>
        )}

        {step === STEP_REVIEW && (
          <Stack spacing={2.5}>
            <SectionHeading
              title="Check this before it becomes real"
              detail="Submitting queues eight steps that create the workspace, its baseline configuration and its first administrator. Each commits separately and you watch them run — nothing here can be un-created afterwards without a destructive lifecycle operation."
            />

            <ReviewSection title="Company identity" onEdit={() => setStep(STEP_COMPANY)}>
              <ReviewRow label="Organization" value={draft.name} />
              <ReviewRow label="Legal name" value={draft.legalName} />
              <ReviewRow label="Slug" value={draft.slug} />
              <ReviewRow label="Registration no." value={draft.registrationNumber} />
              <ReviewRow label="Tax / VAT no." value={draft.taxNumber} />
              <ReviewRow label="Industry" value={draft.industry} />
              <ReviewRow label="Country" value={countryLabel(draft.countryCode)} />
              <ReviewRow label="Contact email" value={draft.contactEmail} />
              <ReviewRow label="Phone" value={draft.phone} />
              <ReviewRow label="Website" value={draft.website} />
              <ReviewRow label="Logo URL" value={draft.logoUrl} />
              <ReviewRow
                label="Address"
                value={[draft.addressLine1, draft.addressLine2, draft.city, draft.stateProvince, draft.postalCode]
                  .filter((part) => part.trim().length > 0)
                  .join(', ')}
              />
            </ReviewSection>

            <ReviewSection title="Commercial terms" onEdit={() => setStep(STEP_COMMERCIAL)}>
              <ReviewRow
                label="Billing mode"
                value={draft.billingMode}
                emphasis={draft.billingMode === 'Billable' ? 'none' : 'warning'}
              />
              {draft.billingMode !== 'Billable' && <ReviewRow label="Reason" value={draft.billingModeReason} />}
              <ReviewRow label="Plan" value={draft.planId ? planLabel(draft.planId) : ''} />
              <ReviewRow label="Rate card" value={draft.rateCardId ? rateCardLabel(draft.rateCardId) : ''} />
              {draft.billingMode === 'Trial' && (
                <ReviewRow label="Trial ends" value={draft.trialEndsOn} emphasis="warning" />
              )}
              <ReviewRow label="Billing starts" value={draft.billingStartsOn} />
              <ReviewRow label="Contract" value={[draft.contractStartOn, draft.contractEndOn].filter(Boolean).join(' → ')} />
              <ReviewRow label="Payment terms" value={draft.paymentTermsDays ? `${draft.paymentTermsDays} days` : ''} />
              <ReviewRow label="PO reference" value={draft.purchaseOrderReference} />
              <ReviewRow label="Billing contact" value={[draft.billingContactName, draft.billingContactEmail].filter(Boolean).join(' · ')} />
              <ReviewRow label="Billing address" value={draft.billingAddress} />
              <ReviewRow label="Account owner" value={draft.accountOwnerEmail} />
              <ReviewRow label="Base currency" value={draft.baseCurrencyCode} />
              <ReviewRow label="Time zone" value={draft.timeZoneId} />
              <ReviewRow label="Language & region" value={draft.locale} />
              <ReviewRow label="Data region" value={draft.dataRegion} />
            </ReviewSection>

            <ReviewSection title="Founding administrator" onEdit={() => setStep(STEP_ADMIN)}>
              <ReviewRow label="Name" value={`${draft.adminFirstName} ${draft.adminLastName}`.trim()} />
              <ReviewRow label="Work email" value={draft.adminEmail} />
              <ReviewRow label="Job title" value={draft.adminJobTitle} />
              <ReviewRow label="Phone" value={draft.adminPhone} />
              <ReviewRow
                label="First credential"
                value={
                  draft.adminActivation === 'invite'
                    ? 'Activation link — they choose their own password'
                    : draft.adminPassword.length > 0
                      ? 'Password set by you'
                      : 'Password generated by the server, shown once'
                }
              />
            </ReviewSection>

            {advisories.length > 0 && (
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                <AlertTitle sx={{ fontWeight: 800 }}>Worth a second look</AlertTitle>
                <Box component="ul" sx={{ m: 0, pl: 2.5 }}>
                  {advisories.map((note) => (
                    <li key={note}>
                      <Typography variant="body2">{note}</Typography>
                    </li>
                  ))}
                </Box>
              </Alert>
            )}

            {!canProvision && (
              <Alert role="alert" severity="error" sx={{ borderRadius: 2 }}>
                Some earlier steps are incomplete. Use the stepper above to go back and finish them.
              </Alert>
            )}
          </Stack>
        )}
      </DialogContent>

      <DialogActions sx={{ p: 2, gap: 1 }}>
        <Button onClick={onClose} color="inherit" disabled={provisionMutation.isPending}>
          Cancel
        </Button>
        <Button
          onClick={() => saveDraftMutation.mutate()}
          startIcon={<SaveDraftIcon />}
          // A draft is worth saving from the first field: the alternative is an operator
          // holding a half-collected company profile in a browser tab they cannot close.
          disabled={draft.name.trim().length === 0 && draft.slug.trim().length === 0}
          color="inherit"
          sx={{ fontWeight: 700 }}
        >
          {saveDraftMutation.isPending ? 'Saving…' : draftRef ? 'Update draft' : 'Save draft'}
        </Button>
        <Box sx={{ flex: 1 }} />
        <Button
          onClick={() => setStep((current) => Math.max(STEP_COMPANY, current - 1))}
          startIcon={<BackIcon />}
          disabled={step === STEP_COMPANY || provisionMutation.isPending}
          color="inherit"
        >
          Back
        </Button>
        {step < STEP_REVIEW ? (
          <Button variant="contained" endIcon={<NextIcon />} onClick={() => goTo(step + 1)} sx={{ fontWeight: 700, px: 3 }}>
            Next
          </Button>
        ) : (
          <Button
            variant="contained"
            startIcon={<ProvisionIcon />}
            onClick={() => provisionMutation.mutate()}
            // The slug refusal blocks here too: submitting a taken address produces a 400
            // the operator has to read, having already confirmed everything else.
            disabled={!canProvision || slugTaken || provisionMutation.isPending}
            sx={{ fontWeight: 700, px: 3 }}
          >
            {provisionMutation.isPending ? 'Submitting…' : 'Create workspace'}
          </Button>
        )}
      </DialogActions>
    </Dialog>
  );
}

/** Sensible starting point: the operator's own zone, today, and a billable posture. */
function freshDraft(): ProvisionDraft {
  return emptyDraft({ timeZoneId: resolvedTimeZone(), billingStartsOn: todayIso() });
}

function SectionHeading({ title, detail }: { title: string; detail?: string }) {
  return (
    <Box>
      <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>
        {title}
      </Typography>
      {detail && (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 0.25 }}>
          {detail}
        </Typography>
      )}
    </Box>
  );
}

function ReviewSection({ title, onEdit, children }: { title: string; onEdit: () => void; children: ReactNode }) {
  return (
    <Paper variant="outlined" sx={{ borderRadius: 2, p: 2 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1.5 }}>
        <Typography variant="overline" sx={{ fontWeight: 800, letterSpacing: '0.06em' }}>
          {title}
        </Typography>
        <Button size="small" startIcon={<EditIcon fontSize="small" />} onClick={onEdit}>
          Edit
        </Button>
      </Stack>
      <Stack spacing={0.75}>{children}</Stack>
    </Paper>
  );
}

function ReviewRow({ label, value, emphasis = 'none' }: { label: string; value: string; emphasis?: 'none' | 'warning' }) {
  const empty = !value || value.trim().length === 0;
  return (
    <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" spacing={1}>
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
      {empty ? (
        // Not recorded is shown as an em dash, never as a guess or a blank line.
        <Typography variant="body2" color="text.disabled" sx={{ fontWeight: 700 }}>
          —
        </Typography>
      ) : emphasis === 'warning' ? (
        <Chip size="small" label={value} color="warning" variant="outlined" sx={{ fontWeight: 700 }} />
      ) : (
        <Typography variant="body2" sx={{ fontWeight: 700, overflowWrap: 'anywhere', textAlign: 'right' }}>
          {value}
        </Typography>
      )}
    </Stack>
  );
}
