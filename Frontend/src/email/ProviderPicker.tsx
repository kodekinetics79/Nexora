import { Alert, AlertTitle, Link, MenuItem, Stack, TextField, Typography } from '@mui/material';
import {
  endpointFor,
  providerWarnings,
  type EmailEndpoint,
  type EmailProviderCapability,
  type MailDirection,
} from './types';

/**
 * Pick a mail provider and have its published settings filled in.
 *
 * <b>This picker is the feature.</b> Every operator who connects a mailbox otherwise goes
 * hunting through a provider's help pages for a hostname, a port and a TLS setting, and a
 * meaningful share of them get the TLS setting wrong — 465-versus-587 is the single most
 * common way a mailbox ends up saved with settings that cannot work. Choosing "GoDaddy"
 * fills in `smtpout.secureserver.net:465` implicit TLS and `imap.secureserver.net:993`
 * without anyone reading anything.
 *
 * The endpoint handed to `onApply` carries its own `useSsl`, taken from the server. The
 * client never derives that flag: the derivation is asymmetric between IMAP and SMTP, and
 * Microsoft 365 is implicit inbound and STARTTLS outbound simultaneously.
 */

export interface ProviderPickerProps {
  providers: EmailProviderCapability[];
  /** Catalogue key of the current selection, or '' for none. */
  value: string;
  /** Which direction the surrounding form is configuring — decides which endpoint is
   *  applied and which warnings are relevant. */
  direction: MailDirection;
  onApply: (provider: EmailProviderCapability, endpoint: EmailEndpoint | null) => void;
  label?: string;
  helperText?: string;
  disabled?: boolean;
}

export default function ProviderPicker({
  providers,
  value,
  direction,
  onApply,
  label = 'Mail provider',
  helperText = 'Choose a provider to fill in its documented server settings.',
  disabled = false,
}: ProviderPickerProps) {
  const selected = providers.find((p) => p.key === value) ?? null;
  const warnings = selected ? providerWarnings(selected, direction) : [];

  return (
    <Stack spacing={1.5}>
      <TextField
        select
        fullWidth
        size="small"
        label={label}
        value={value}
        disabled={disabled}
        onChange={(event) => {
          const provider = providers.find((p) => p.key === event.target.value);
          if (provider) onApply(provider, endpointFor(provider, direction));
        }}
        helperText={helperText}
        slotProps={{ htmlInput: { 'aria-label': label } }}
      >
        {providers.map((provider) => (
          <MenuItem key={provider.key} value={provider.key}>
            {provider.displayName}
          </MenuItem>
        ))}
      </TextField>

      {selected && (
        <Alert severity={warnings.length > 0 ? 'warning' : 'info'} sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>{selected.displayName}</AlertTitle>
          <Typography variant="body2">{selected.guidance}</Typography>

          {/*
            Stated BEFORE the password is typed, not after it has been rejected. Every one
            of these describes a switch that lives at the provider, and every one produces
            a failure that reads exactly like a wrong password.
          */}
          {warnings.length > 0 && (
            <Stack component="ul" spacing={0.5} sx={{ mt: 1, mb: 0, pl: 2.5 }}>
              {warnings.map((warning) => (
                <Typography key={warning} component="li" variant="body2" sx={{ fontWeight: 600 }}>
                  {warning}
                </Typography>
              ))}
            </Stack>
          )}

          {selected.documentationUrl && (
            <Typography variant="caption" sx={{ display: 'block', mt: 1 }}>
              <Link href={selected.documentationUrl} target="_blank" rel="noopener noreferrer">
                {selected.displayName} setup documentation
              </Link>
            </Typography>
          )}
        </Alert>
      )}
    </Stack>
  );
}
