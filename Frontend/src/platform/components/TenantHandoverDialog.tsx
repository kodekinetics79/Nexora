import { useState } from 'react';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Divider,
  Typography,
} from '@mui/material';
import {
  CheckCircleOutlined as DoneIcon,
  ContentCopy as CopyIcon,
  ErrorOutlineOutlined as MissingIcon,
} from '@mui/icons-material';
import Stack from './Flex';
import { fmtDateTime } from './format';
import type { ProvisionTenantResult } from '../types';

interface Props {
  /** The provisioning response. Null closes the dialog. */
  result: ProvisionTenantResult | null;
  onClose: () => void;
}

/**
 * The handover step after a successful provision.
 *
 * This is deliberately a blocking screen rather than a toast. On the password
 * path the generated credential exists in the provisioning response and NOWHERE
 * else — not in the audit log, not retrievable later — so it cannot be delivered
 * in something the operator can miss or dismiss by accident. That is also why
 * the dialog does not close on a backdrop click and why the confirm button stays
 * disabled until the password has actually been copied.
 */
export default function TenantHandoverDialog({ result, onClose }: Props) {
  const [credentialCopied, setCredentialCopied] = useState(false);
  // Announced politely so a screen-reader user learns the copy succeeded; the
  // same text is visible, because a silent icon button that "did something" is
  // no better for sighted users.
  const [copyNotice, setCopyNotice] = useState<string | null>(null);

  const admin = result?.foundingAdmin;
  const generatedPassword = admin?.generatedPassword ?? null;
  const invitation = admin?.invitation ?? null;
  const warnings = result?.billing?.warnings ?? [];
  const baseline = result?.baseline ?? null;

  const handleClose = () => {
    setCredentialCopied(false);
    setCopyNotice(null);
    onClose();
  };

  const copy = (value: string, subject: string, onCopied?: () => void) => {
    const clipboard = navigator.clipboard;
    if (!clipboard) {
      setCopyNotice(`Copying is unavailable in this browser — select the ${subject.toLowerCase()} and copy it manually.`);
      return;
    }
    clipboard
      .writeText(value)
      .then(() => {
        setCopyNotice(`${subject} copied to the clipboard.`);
        onCopied?.();
      })
      .catch(() => setCopyNotice(`Copy failed — select the ${subject.toLowerCase()} and copy it manually.`));
  };

  return (
    <Dialog
      open={result !== null}
      onClose={() => { /* deliberately not dismissible by backdrop — see the close button */ }}
      fullWidth
      maxWidth="sm"
      aria-labelledby="handover-title"
    >
      <DialogTitle id="handover-title" sx={{ fontWeight: 800 }}>
        {result?.tenant.name} is ready
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Alert severity="success" sx={{ borderRadius: 2 }}>
            The workspace is provisioned and its founding administrator account exists.
          </Alert>

          {/* Revenue risks the server flagged. First thing on the screen, because
              a giveaway noticed at handover costs nothing to fix and a giveaway
              noticed at the first invoice costs a quarter. */}
          {warnings.length > 0 && (
            <Alert severity="warning" sx={{ borderRadius: 2 }}>
              <AlertTitle sx={{ fontWeight: 800 }}>
                {warnings.length === 1 ? 'The billing setup has a gap' : `The billing setup has ${warnings.length} gaps`}
              </AlertTitle>
              <Box component="ul" sx={{ m: 0, pl: 2.5 }}>
                {warnings.map((warning) => (
                  <li key={warning}>
                    <Typography variant="body2">{warning}</Typography>
                  </li>
                ))}
              </Box>
            </Alert>
          )}

          <Stack spacing={0.5}>
            <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary' }}>
              Sign-in email
            </Typography>
            <Typography sx={{ fontFamily: 'monospace', fontSize: '0.95rem' }}>{admin?.email}</Typography>
          </Stack>

          {generatedPassword ? (
            <>
              <Stack spacing={0.5}>
                <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary' }} id="handover-password-label">
                  Temporary password
                </Typography>
                <Stack direction="row" spacing={1} alignItems="center">
                  <Typography
                    aria-labelledby="handover-password-label"
                    sx={{
                      fontFamily: 'monospace', fontSize: '1.05rem', fontWeight: 700,
                      px: 1.5, py: 1, borderRadius: 1.5, flex: 1,
                      bgcolor: 'action.hover', wordBreak: 'break-all',
                    }}
                  >
                    {generatedPassword}
                  </Typography>
                  <Button
                    variant="outlined"
                    startIcon={<CopyIcon fontSize="small" />}
                    onClick={() => copy(generatedPassword, 'Password', () => setCredentialCopied(true))}
                    sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}
                  >
                    {credentialCopied ? 'Copied' : 'Copy'}
                  </Button>
                </Stack>
              </Stack>
              <Alert severity="warning" sx={{ borderRadius: 2 }}>
                <AlertTitle sx={{ fontWeight: 800 }}>Shown once — it cannot be retrieved</AlertTitle>
                Only a one-way hash is stored, so nobody can look this up later, including us.
                Send it to {admin?.email} through a secure channel and have them change it on first
                sign-in. If it is lost, the password must be reset instead.
              </Alert>
            </>
          ) : invitation ? (
            <>
              {/*
                The link is served ONLY when the mail did not go out — it is a bearer
                credential, and the server withholds it on the ordinary path. Rendering
                it unconditionally put an empty monospace box under an "Activation link"
                heading and a Copy button that copied `undefined`, on every successful
                invite, alongside a caption claiming a link was there to copy.
              */}
              {invitation.activationUrl ? (
                <Stack spacing={0.5}>
                  <Typography variant="overline" sx={{ fontWeight: 800, color: 'text.secondary' }} id="handover-link-label">
                    Activation link
                  </Typography>
                  <Stack direction="row" spacing={1} alignItems="center">
                    <Typography
                      aria-labelledby="handover-link-label"
                      sx={{
                        fontFamily: 'monospace', fontSize: '0.85rem',
                        px: 1.5, py: 1, borderRadius: 1.5, flex: 1,
                        bgcolor: 'action.hover', wordBreak: 'break-all',
                      }}
                    >
                      {invitation.activationUrl}
                    </Typography>
                    <Button
                      variant="outlined"
                      startIcon={<CopyIcon fontSize="small" />}
                      onClick={() => copy(invitation.activationUrl!, 'Activation link')}
                      sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}
                    >
                      Copy
                    </Button>
                  </Stack>
                </Stack>
              ) : null}
              <Alert severity={invitation.activationUrl ? 'warning' : 'info'} sx={{ borderRadius: 2 }}>
                <AlertTitle sx={{ fontWeight: 800 }}>No password exists yet</AlertTitle>
                {admin?.email} sets their own on this single-use link, so nothing secret passes
                through you. It expires on {fmtDateTime(invitation.expiresAtUtc)}; after that the
                invitation has to be reissued.{' '}
                {invitation.activationUrl
                  ? 'The invitation email was NOT sent, so this link is the only copy — deliver it '
                    + 'yourself through a secure channel, or configure outbound email and resend.'
                  : 'The invitation has been emailed to them; the link itself is deliberately not '
                    + 'shown here. Use Resend from the tenant’s access tab if it does not arrive.'}
              </Alert>
            </>
          ) : (
            <Alert severity="info" sx={{ borderRadius: 2 }}>
              You set the password yourself, so it is not repeated here. Share it with {admin?.email}{' '}
              through a secure channel.
            </Alert>
          )}

          <Divider textAlign="left" sx={{ pt: 0.5 }}>
            <Typography variant="overline" sx={{ fontWeight: 800, letterSpacing: '0.06em' }}>
              Workspace readiness
            </Typography>
          </Divider>

          {baseline ? (
            <Stack spacing={1}>
              <ReadinessRow
                ready={baseline.quoteConfiguration}
                label="Quote template"
                detail={baseline.quoteConfiguration ? 'Configured — the tenant can issue a quote' : 'Not configured — quoting is blocked until it is'}
              />
              <ReadinessRow
                ready={Boolean(baseline.baseCurrency)}
                label="Base currency"
                detail={baseline.baseCurrency ?? 'Not set — pricing has no currency to report in'}
              />
              <ReadinessRow
                ready={baseline.unitsOfMeasure > 0}
                label="Units of measure"
                detail={`${baseline.unitsOfMeasure} seeded`}
              />
              <ReadinessRow ready={baseline.roles > 0} label="Roles" detail={`${baseline.roles} created`} />
              <ReadinessRow
                ready={baseline.permissionGrants > 0}
                label="Permission grants"
                detail={`${baseline.permissionGrants} applied`}
              />
              <ReadinessRow
                ready={Boolean(baseline.leadReferencePrefix)}
                label="Lead reference prefix"
                detail={baseline.leadReferencePrefix ?? 'Not set — lead references fall back to the default'}
              />
            </Stack>
          ) : (
            <Alert severity="info" sx={{ borderRadius: 2 }}>
              The server did not report what it seeded, so this workspace has not been confirmed
              usable. Open the tenant and check its setup before handing it over.
            </Alert>
          )}

          {/* Kept visible as well as announced: a copy action that gives no
              feedback at all is why people paste the wrong thing. */}
          <Box role="status" aria-live="polite" sx={{ minHeight: 20 }}>
            {copyNotice && (
              <Typography variant="caption" sx={{ fontWeight: 700, color: 'text.secondary' }}>
                {copyNotice}
              </Typography>
            )}
          </Box>
        </Stack>
      </DialogContent>
      <DialogActions sx={{ p: 2 }}>
        <Button
          variant="contained"
          onClick={handleClose}
          disabled={Boolean(generatedPassword) && !credentialCopied}
          sx={{ fontWeight: 700, px: 3 }}
        >
          {generatedPassword && !credentialCopied ? 'Copy the password first' : 'Done'}
        </Button>
      </DialogActions>
    </Dialog>
  );
}

function ReadinessRow({ ready, label, detail }: { ready: boolean; label: string; detail: string }) {
  return (
    <Stack direction="row" spacing={1.25} alignItems="flex-start">
      {ready ? (
        <DoneIcon fontSize="small" sx={{ color: 'success.main', mt: 0.2 }} titleAccess="Ready" />
      ) : (
        <MissingIcon fontSize="small" sx={{ color: 'warning.main', mt: 0.2 }} titleAccess="Not ready" />
      )}
      <Box>
        <Typography variant="body2" sx={{ fontWeight: 700 }}>
          {label}
        </Typography>
        <Typography variant="caption" color="text.secondary">
          {detail}
        </Typography>
      </Box>
    </Stack>
  );
}
