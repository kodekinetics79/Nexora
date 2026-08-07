import React from 'react';
import { Alert, AlertTitle, Box, Chip, Stack, Typography } from '@mui/material';
import {
  CheckCircle as PassIcon,
  Cancel as FailIcon,
  RemoveCircleOutlined as SkipIcon,
  WarningAmber as WarnIcon,
} from '@mui/icons-material';
import {
  MAIL_STAGE_LABEL,
  type MailConnectionTestResult,
  type MailProbeStatus,
} from './types';

/**
 * The six-stage connection report, rendered identically wherever a mail connection is
 * tested.
 *
 * The value is entirely in showing WHICH stage failed and what to do about it. "Connection
 * failed" is what this replaces: a blocked port, a TLS-mode mismatch, a password the
 * provider refuses and a mailbox with SMTP submission switched off all arrive at a naive
 * console as the same red box, and an operator has no way to tell them apart.
 *
 * `Skipped` is deliberately distinct from `Failed` — reporting six failures when only the
 * first is real sends the operator chasing five phantoms.
 */

const STATUS_STYLE: Record<MailProbeStatus, { icon: React.ReactNode; colour: string }> = {
  Passed: { icon: <PassIcon fontSize="small" />, colour: 'success.main' },
  Failed: { icon: <FailIcon fontSize="small" />, colour: 'error.main' },
  Warning: { icon: <WarnIcon fontSize="small" />, colour: 'warning.main' },
  Skipped: { icon: <SkipIcon fontSize="small" />, colour: 'text.disabled' },
};

export interface ConnectionReportProps {
  result: MailConnectionTestResult;
}

export default function ConnectionReport({ result }: ConnectionReportProps) {
  return (
    <Box sx={{ mt: 2 }} data-testid="mail-connection-report">
      <Alert severity={result.succeeded ? 'success' : 'error'} sx={{ mb: 1.5, borderRadius: 2 }}>
        <AlertTitle sx={{ fontWeight: 800 }}>
          {result.succeeded ? 'Connection successful' : 'Connection failed'}
        </AlertTitle>
        {result.summary}
        <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
          {result.protocol} to {result.host}:{result.port}
          {result.negotiatedSecurity && result.negotiatedSecurity !== 'None'
            ? ` · encrypted with ${result.negotiatedSecurity}`
            : ''}
          {typeof result.inboxMessageCount === 'number'
            ? ` · ${result.inboxMessageCount} message${result.inboxMessageCount === 1 ? '' : 's'} in the mailbox`
            : ''}
        </Typography>
      </Alert>

      {/*
        A successful connection that sent the credential in the clear is NOT a pass worth
        celebrating, so it is surfaced at the top rather than buried in a stage row.
      */}
      {result.credentialsSentInClear && (
        <Alert severity="warning" sx={{ mb: 1.5, borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>The password travelled unencrypted</AlertTitle>
          Anyone on the network path could read it. Switch this connection to an encrypted
          port before saving, and change the password afterwards.
        </Alert>
      )}

      <Stack spacing={0.75}>
        {result.steps.map((step) => {
          const style = STATUS_STYLE[step.status];
          return (
            <Box
              key={step.stage}
              sx={{
                display: 'flex',
                alignItems: 'flex-start',
                gap: 1.25,
                px: 1.5,
                py: 1,
                borderRadius: 2,
                bgcolor: 'action.hover',
              }}
            >
              <Box sx={{ color: style.colour, display: 'flex', pt: '2px' }}>{style.icon}</Box>
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {MAIL_STAGE_LABEL[step.stage] ?? step.stage}
                </Typography>
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                  {step.detail}
                </Typography>
                {step.remedy && (
                  <Typography
                    variant="caption"
                    sx={{ display: 'block', mt: 0.5, fontWeight: 700, color: style.colour }}
                  >
                    {step.remedy}
                  </Typography>
                )}
              </Box>
              {step.elapsedMs > 0 && (
                <Typography variant="caption" color="text.disabled" sx={{ whiteSpace: 'nowrap' }}>
                  {step.elapsedMs} ms
                </Typography>
              )}
            </Box>
          );
        })}
      </Stack>

      {/*
        Chosen by the server from WHAT FAILED, not from the provider alone — an auth failure
        against Microsoft 365 names SMTP submission instead of guessing at the password.
      */}
      {result.providerNotes.length > 0 && (
        <Alert severity="info" sx={{ mt: 1.5, borderRadius: 2 }}>
          {result.providerDisplayName && (
            <AlertTitle sx={{ fontWeight: 800 }}>
              About {result.providerDisplayName}
              <Chip size="small" label={result.tls} sx={{ ml: 1, height: 20 }} />
            </AlertTitle>
          )}
          <Stack component="ul" spacing={0.5} sx={{ m: 0, pl: 2.5 }}>
            {result.providerNotes.map((note) => (
              <Typography key={note} component="li" variant="body2">
                {note}
              </Typography>
            ))}
          </Stack>
        </Alert>
      )}
    </Box>
  );
}
