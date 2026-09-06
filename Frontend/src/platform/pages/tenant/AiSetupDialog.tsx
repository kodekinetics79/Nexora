import { useMemo, useState } from 'react';
import {
  Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, Divider,
  FormControlLabel, Radio, TextField, Typography,
} from '@mui/material';
import Stack from '../../components/Flex';
import type {
  AiCloudEgress, AiExtractionReadinessReport, AiPosture, TenantAiEnablementInput, TenantAiPolicy,
} from '../../types';

/** How permissive a posture is, for comparing an answer against what the plan sells. */
const RANK: Record<AiPosture, number> = { Off: 0, PrivateOnly: 1, ApprovedCloud: 2 };

/** The posture a plan's package provisions a tenant with. */
const PACKAGE_POSTURE: Record<string, AiPosture> = {
  Off: 'Off', Private: 'PrivateOnly', Cloud: 'ApprovedCloud',
};

/**
 * The guided setup.
 *
 * <b>The defect it closes.</b> Turning document reading on took a policy update and a
 * destination grant, behind two dialogs, of which the prominent one was the grant — and a grant
 * alone permits nothing. An operator pressed the button that looked right, watched four controls
 * go green, and the documents still refused. Every field here is a radio or a checkbox, nothing
 * technical is typed, and one Apply issues one audited call.
 *
 * <b>What it does not do.</b> It never asks for an endpoint, a model id, an egress enum or a
 * purpose string. The server takes the destination from the resolved provider descriptor, which
 * is what retires the case-sensitive AllowedModel comparison: one capital letter used to refuse
 * every document a tenant submitted, and now nobody types the value.
 */

/**
 * For turning a token ceiling into the number an operator can actually weigh. It is an
 * ASSUMPTION, labelled as one wherever it is shown — the tenant's real average lives in the
 * ledger and this dialog does not read it.
 */
const TOKENS_PER_DOCUMENT = 18_000;

const PURPOSES: { value: string; label: string; note: string }[] = [
  { value: 'RfqExtraction', label: 'Reading incoming RFQ documents', note: 'The core journey.' },
  { value: 'BoqDraft', label: 'Drafting bills of quantity', note: 'Roughly twice the tokens per document.' },
  { value: 'Agent', label: 'The in-app assistant', note: 'Conversational; not part of the pilot scope.' },
];

/** One answer. Selected state is carried by the border and the radio, never by colour alone. */
function Choice({
  selected, onSelect, disabled, title, children,
}: {
  selected: boolean;
  onSelect: () => void;
  disabled?: boolean;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <FormControlLabel
      disabled={disabled}
      control={(
        <Radio
          checked={selected}
          onChange={onSelect}
          sx={{ alignSelf: 'flex-start', pt: 0.5 }}
        />
      )}
      sx={{
        alignItems: 'flex-start', m: 0, p: 1.25, borderRadius: 1, border: 1,
        borderColor: selected ? 'primary.main' : 'divider',
        bgcolor: selected ? 'action.selected' : undefined,
      }}
      label={(
        <Box>
          <Typography sx={{ fontWeight: 650, fontSize: '0.95rem' }}>{title}</Typography>
          <Typography variant="body2" color="text.secondary">{children}</Typography>
        </Box>
      )}
    />
  );
}

function Question({ number, ask, children }: { number: string; ask: string; children: React.ReactNode }) {
  return (
    <Box>
      <Stack direction="row" spacing={1.5} alignItems="baseline" sx={{ mb: 1 }}>
        <Typography variant="caption" sx={{ fontWeight: 800, color: 'primary.main' }}>{number}</Typography>
        <Typography sx={{ fontWeight: 700 }}>{ask}</Typography>
      </Stack>
      <Stack spacing={1} sx={{ pl: 3.5 }}>{children}</Stack>
    </Box>
  );
}

export default function AiSetupDialog({
  open, tenantName, policy, readiness, busy, onClose, onApply,
}: {
  open: boolean;
  tenantName: string;
  policy: TenantAiPolicy;
  readiness: AiExtractionReadinessReport | undefined;
  busy: boolean;
  onClose: () => void;
  onApply: (input: TenantAiEnablementInput) => void;
}) {
  const provider = readiness?.resolvedProvider;
  const localAvailable = provider?.providerClass === 'Local';

  // Pre-selected only where the tenant already HAS an answer. A tenant that is enabled with no
  // external consent and no local destination is the unfinished state this dialog exists for,
  // and pre-ticking a posture there would be the tool making the customer's decision — the
  // egressing one, at that — and calling it a default.
  const [posture, setPosture] = useState<AiPosture | null>(
    policy.externalProcessingAllowed ? 'ApprovedCloud'
      : !policy.isEnabled ? 'Off'
        : localAvailable ? 'PrivateOnly'
          : null,
  );
  const [egress, setEgress] = useState<AiCloudEgress>(
    policy.egressPolicy === 'FullDocument' ? 'FullDocument' : 'RedactedFieldsOnly',
  );
  const [purposes, setPurposes] = useState<string[]>(
    policy.allowedPurposes.length > 0 ? policy.allowedPurposes : ['RfqExtraction'],
  );
  const [capped, setCapped] = useState(policy.monthlyHardTokenLimit !== null);
  const [ceiling, setCeiling] = useState(String(policy.monthlyHardTokenLimit ?? 2_000_000));
  const [justification, setJustification] = useState('');
  const [deviationReason, setDeviationReason] = useState(policy.planDeviationReason ?? '');

  const cloud = posture === 'ApprovedCloud';
  const ceilingTokens = Number(ceiling);
  const ceilingValid = Number.isFinite(ceilingTokens) && ceilingTokens > 0;
  const documents = ceilingValid ? Math.floor(ceilingTokens / TOKENS_PER_DOCUMENT) : 0;

  /**
   * Whether this answer gives the tenant more than its plan sells. Mirrored from the server, which
   * remains the authority and refuses the write regardless — this exists so the operator is asked
   * for the approval in the form, rather than meeting a refusal after pressing Apply.
   *
   * Only ever MORE permissive counts. Tightening never needs a signature.
   */
  const beyondPlan = useMemo(() => {
    const sold = policy.planAiPackage ? PACKAGE_POSTURE[policy.planAiPackage] : null;
    if (sold === null || sold === undefined || posture === null) return [];
    const over: string[] = [];
    if (RANK[posture] > RANK[sold]) {
      over.push(`Reads documents with more than the ${policy.planAiPackageName ?? 'plan'} package sells.`);
    }
    if (posture !== 'Off' && !policy.planAiAllowanceUnlimited
      && policy.planAiMonthlyTokenAllowance !== null) {
      if (!capped) over.push('Has no monthly ceiling, against the allowance the plan sells.');
      else if (ceilingValid && ceilingTokens > policy.planAiMonthlyTokenAllowance) {
        over.push(`Has a ceiling above the ${policy.planAiMonthlyTokenAllowance.toLocaleString()} tokens the plan sells.`);
      }
    }
    return over;
  }, [posture, capped, ceilingValid, ceilingTokens, policy]);

  const problem = useMemo(() => {
    if (posture === null) return 'Choose what this tenant\u2019s documents may be read by.';
    if (beyondPlan.length > 0 && deviationReason.trim().length < 15) {
      return 'This goes beyond the plan — say who approved it (15 characters or more).';
    }
    if (posture !== 'Off' && purposes.length === 0) return 'Say what AI may be used for.';
    if (posture !== 'Off' && capped && !ceilingValid) return 'A monthly ceiling must be a number above zero.';
    if (justification.trim().length < 5) return 'A justification of at least 5 characters is required.';
    return null;
  }, [posture, purposes.length, capped, ceilingValid, justification, beyondPlan.length, deviationReason]);

  const toggle = (value: string) => setPurposes(
    (current) => current.includes(value) ? current.filter((x) => x !== value) : [...current, value],
  );

  const apply = () => onApply({
    posture: posture!,
    cloudEgress: cloud ? egress : null,
    purposes: posture === 'Off' ? [] : purposes,
    monthlyHardTokenLimit: posture === 'Off' || !capped ? null : ceilingTokens,
    noMonthlyCeiling: posture !== 'Off' && !capped,
    grantExpiresOn: null,
    version: policy.version,
    justification: justification.trim(),
    planDeviationReason: beyondPlan.length > 0 ? deviationReason.trim() : null,
  });

  return (
    <Dialog open={open} onClose={() => !busy && onClose()} fullWidth maxWidth="sm">
      <DialogTitle sx={{ pb: 0.5 }}>
        AI setup — {tenantName}
        <Typography variant="body2" color="text.secondary">
          Owner authority. Version-checked, attributed, and written to the platform audit trail.
        </Typography>
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={3}>
          {/* Printed beside the choice, not buried in a plans screen: an operator deciding this
              needs to know what the customer actually bought, in the words the package defines
              for itself. */}
          {policy.planAiPackage && (
            <Alert severity="info" icon={false} sx={{ py: 0.5 }}>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>
                Plan {policy.planCode} sells {policy.planAiPackageName}
                {policy.planAiAllowanceUnlimited
                  ? ' · uncapped'
                  : policy.planAiMonthlyTokenAllowance !== null
                    ? ` · ${policy.planAiMonthlyTokenAllowance.toLocaleString()} tokens/month`
                    : ' · no allowance decided'}
              </Typography>
              <Typography variant="body2" color="text.secondary">{policy.planAiPackageMeans}</Typography>
            </Alert>
          )}

          <Question number="Q1" ask={`What may ${tenantName}'s documents be read by?`}>
            <Choice title="Nothing — AI off" selected={posture === 'Off'} onSelect={() => setPosture('Off')}>
              Documents are handled by people only. Extraction, BOQ drafting and the assistant are
              all unavailable.
            </Choice>
            <Choice
              title="A private model — nothing leaves their infrastructure"
              selected={posture === 'PrivateOnly'}
              disabled={!localAvailable}
              onSelect={() => setPosture('PrivateOnly')}
            >
              {localAvailable
                ? 'Runs on the customer’s own deployment. No egress, and no external approval needed.'
                : `Not available here: this installation is pointed at ${provider?.endpoint ?? 'an off-host endpoint'}.`}
            </Choice>
            <Choice
              title="An approved cloud provider"
              selected={cloud}
              onSelect={() => setPosture('ApprovedCloud')}
            >
              {provider?.provider ?? 'The configured provider'} · {provider?.model ?? 'its model'}.
              Needs the customer’s written consent, recorded below.
            </Choice>
          </Question>

          {cloud && (
            <Question number="Q1b" ask="What may be sent to that provider?">
              <Choice
                title="Redacted fields only"
                selected={egress === 'RedactedFieldsOnly'}
                onSelect={() => setEgress('RedactedFieldsOnly')}
              >
                Safest. Scanned and PDF RFQs will extract little or nothing — the model never sees
                the page.
              </Choice>
              <Choice
                title="The whole document"
                selected={egress === 'FullDocument'}
                onSelect={() => setEgress('FullDocument')}
              >
                <Box component="span" sx={{ color: 'error.main', fontWeight: 650 }}>
                  The customer’s document text leaves their infrastructure.
                </Box>{' '}
                Required for scanned and PDF RFQs, which is most of them.
              </Choice>
            </Question>
          )}

          {posture !== null && posture !== 'Off' && (
            <>
              <Question number="Q2" ask="What should it be used for?">
                {PURPOSES.map((item) => (
                  <FormControlLabel
                    key={item.value}
                    sx={{
                      alignItems: 'flex-start', m: 0, p: 1.25, borderRadius: 1, border: 1,
                      borderColor: purposes.includes(item.value) ? 'primary.main' : 'divider',
                    }}
                    control={(
                      <Checkbox
                        checked={purposes.includes(item.value)}
                        onChange={() => toggle(item.value)}
                        sx={{ alignSelf: 'flex-start', pt: 0.5 }}
                      />
                    )}
                    label={(
                      <Box>
                        <Typography sx={{ fontWeight: 650, fontSize: '0.95rem' }}>{item.label}</Typography>
                        <Typography variant="body2" color="text.secondary">{item.note}</Typography>
                      </Box>
                    )}
                  />
                ))}
              </Question>

              <Question number="Q3" ask="How much AI should this tenant get each month?">
                <Choice title="A monthly ceiling" selected={capped} onSelect={() => setCapped(true)}>
                  {ceilingValid
                    ? `About ${documents.toLocaleString()} document${documents === 1 ? '' : 's'} a month, `
                      + 'at an assumed 18,000 tokens each.'
                    : 'Enter a number above zero.'}
                </Choice>
                {capped && (
                  <TextField
                    label="Monthly token ceiling"
                    type="number"
                    size="small"
                    value={ceiling}
                    onChange={(event) => setCeiling(event.target.value)}
                    sx={{ maxWidth: 260, ml: 4 }}
                    slotProps={{ htmlInput: { min: 1, 'aria-label': 'Monthly token ceiling' } }}
                  />
                )}
                <Choice title="No ceiling" selected={!capped} onSelect={() => setCapped(false)}>
                  <Box component="span" sx={{ color: 'warning.main', fontWeight: 650 }}>
                    Unlimited spend.
                  </Box>{' '}
                  The tenant will show a standing warning until somebody sets a number.
                </Choice>
              </Question>
            </>
          )}

          <Divider />

          <Box>
            <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, letterSpacing: '.06em' }}>
              THIS WILL
            </Typography>
            <Box component="ul" sx={{ m: 0, pl: 2.5, '& li': { mb: 0.5 } }}>
              {posture === 'Off' && <li>Turn AI processing off for {tenantName} entirely.</li>}
              {posture === 'PrivateOnly' && (
                <li>Turn AI processing on, with no external processing of any kind.</li>
              )}
              {cloud && (
                <>
                  <li>Turn AI processing on and allow external processing.</li>
                  <li>
                    Allow{' '}
                    <strong>{egress === 'FullDocument' ? 'whole documents' : 'redacted fields only'}</strong>{' '}
                    to be sent to {provider?.endpoint}.
                  </li>
                  <li>Authorize {provider?.provider} / {provider?.model} for {purposes.join(', ')}.</li>
                </>
              )}
              {posture !== 'Off' && (
                <li>
                  {capped && ceilingValid
                    ? `Set the monthly ceiling to ${ceilingTokens.toLocaleString()} tokens — about `
                      + `${documents.toLocaleString()} document${documents === 1 ? '' : 's'} a month.`
                    : 'Leave AI spend uncapped.'}
                </li>
              )}
            </Box>
          </Box>

          {beyondPlan.length > 0 && (
            <Alert severity="warning">
              <Typography variant="body2" sx={{ fontWeight: 700 }}>Beyond this tenant's plan</Typography>
              <Box component="ul" sx={{ m: 0, pl: 2.5 }}>
                {beyondPlan.map((line) => <li key={line}>{line}</li>)}
              </Box>
              <TextField
                label="Who approved going beyond the plan?"
                placeholder="The person who agreed it, and the reference."
                value={deviationReason}
                onChange={(event) => setDeviationReason(event.target.value)}
                required
                fullWidth
                size="small"
                sx={{ mt: 1.5 }}
              />
            </Alert>
          )}

          <TextField
            label="Justification / approval reference"
            placeholder="Signed DPA ref, the email that approved it, or a ticket."
            value={justification}
            onChange={(event) => setJustification(event.target.value)}
            required
            multiline
            minRows={2}
            helperText="Written to the audit trail, and onto the grant where the customer can be shown it."
          />

          {/* Stated, not silently dropped: this dialog does not set them, and pretending it does
              would be the same kind of half-answer the whole change exists to remove. */}
          <Alert severity="info" sx={{ py: 0.5 }}>
            Residency and retention stay as they are — {policy.dataResidency} · {policy.retentionDays} days.
            Change those in the policy editor below.
          </Alert>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose} disabled={busy}>Cancel</Button>
        <Button variant="contained" onClick={apply} disabled={busy || problem !== null}>
          {busy ? 'Applying…' : 'Apply and re-check'}
        </Button>
      </DialogActions>
      {problem && (
        <Typography variant="caption" color="text.secondary" sx={{ px: 3, pb: 1.5, textAlign: 'right' }}>
          {problem}
        </Typography>
      )}
    </Dialog>
  );
}
