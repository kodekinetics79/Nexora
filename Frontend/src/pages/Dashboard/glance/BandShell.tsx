import type { ReactNode } from 'react';
import { Alert, AlertTitle, Box, Button, Paper, Stack, Tooltip, Typography } from '@mui/material';
import { LockOutlined as ForbiddenIcon } from '@mui/icons-material';
import dayjs from 'dayjs';
import { glanceCssVariables } from './tokens';
import { SCOPE_UNRESOLVED } from './scopeWords';

/**
 * One band of the glance screen: a glass slab with its title, its seal, and whatever it draws.
 *
 * Every band on the screen is the same object so the reader learns it once. The one part they have
 * to learn is the seal, top-right, in identical position and typography on every band: whose
 * numbers, over what window, as of when. Three facts that differ per band because each band is its
 * own aggregate with its own scope and its own freshness — there is no composite endpoint and no
 * single "as of" for the screen.
 *
 * The seal is FILLED when the period control governs that band and OUTLINED when the band's window
 * is fixed by the server (the deadline board's urgency buckets, the six-month series). That is why
 * this screen has no global date picker: a control that silently governs four bands out of seven is
 * a lie, and filled-vs-outlined is legible from across the room, before a word of it is read.
 *
 * The shell owns two of the four states a band can be in — error and forbidden — because they look
 * the same wherever they happen and because a band that failed must not blank its neighbours. It
 * deliberately does NOT own the empty state: "nothing happened yet" is drawn by each band inside
 * its own axis and labels, at full height, so nothing reflows when the first record arrives.
 */
export interface BandSeal {
  /** The reader's words from `scopeWords()`. Null renders SCOPE_UNRESOLVED rather than a wire word. */
  scope: string | null;
  /** The window this band actually covers, e.g. "1 Jan – 30 Jan" or "Next 14 days". */
  window: string;
  /** The server's `generatedAt`. Null renders "freshness not stated" — never a guessed clock. */
  generatedAt?: string | null;
  /** True when the screen's period control governs this band's window. */
  governed: boolean;
}

export interface BandShellProps {
  title: string;
  seal: BandSeal;
  children: ReactNode;
  /** The band's own numeral in the screen's sentence, e.g. "2". Read out with the title. */
  step?: string;
  loading?: boolean;
  /** The server's reason for the failure. Presence of this renders the error state. */
  error?: string | null;
  /** The server's own sentence about why this reader may not see the band. */
  forbidden?: string | null;
  onRetry?: () => void;
  /** The band's reserved height. It is held in every state, empty included. */
  minHeight?: number;
  index?: number;
}

const sealFreshness = (generatedAt?: string | null): string => {
  if (!generatedAt) return 'freshness not stated';
  const at = dayjs(generatedAt);
  return at.isValid() ? `as of ${at.format('HH:mm')}` : 'freshness not stated';
};

export default function BandShell({
  title, seal, children, step, loading = false, error = null, forbidden = null, onRetry, minHeight = 260, index = 0,
}: BandShellProps) {
  const scopeText = seal.scope ?? SCOPE_UNRESOLVED;
  const sealText = `${scopeText} · ${seal.window} · ${sealFreshness(seal.generatedAt)}`;
  const sealExplanation = seal.governed
    ? 'This band follows the period you choose above.'
    : 'This band has its own fixed window, set by the server. The period control does not change it.';

  const body = (() => {
    if (forbidden) {
      // Not an error and not an empty: the server is answering, and its answer is that these
      // numbers are not this reader's to see. Its sentence, calm, no retry to offer.
      return (
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'flex-start', px: 1, py: 3, maxWidth: 560 }}>
          <ForbiddenIcon fontSize="small" sx={{ color: 'text.secondary', mt: '2px' }} />
          <Typography variant="body2" sx={{ color: 'text.secondary', lineHeight: 1.5 }}>{forbidden}</Typography>
        </Stack>
      );
    }
    if (error) {
      return (
        <Alert
          severity="error"
          sx={{ mt: 1 }}
          action={onRetry ? <Button color="inherit" size="small" onClick={onRetry}>Retry</Button> : undefined}
        >
          <AlertTitle>We could not load this</AlertTitle>
          {error}
        </Alert>
      );
    }
    if (loading) {
      return (
        <Box
          role="status"
          sx={{
            mt: 1, borderRadius: 2, minHeight: minHeight - 72,
            border: '1px dashed', borderColor: 'divider',
            display: 'grid', placeItems: 'center',
          }}
        >
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>Loading {title.toLowerCase()}…</Typography>
        </Box>
      );
    }
    return children;
  })();

  return (
    <Paper
      component="section"
      variant="outlined"
      className="nx-glass nx-enter"
      data-decorative-motion="true"
      style={{ animationDelay: `${Math.min(index, 8) * 40}ms` }}
      aria-label={title}
      aria-busy={loading || undefined}
      sx={(theme) => ({
        // The series tokens are declared on the band itself rather than globally, so any chart a
        // band draws inherits them and a band rendered on its own — in a test, or in a page that
        // does not mount the whole screen — still paints in the validated palette.
        ...glanceCssVariables(theme.palette.mode),
        p: { xs: 2, md: 2.5 },
        borderRadius: 3,
        minHeight,
        display: 'flex',
        flexDirection: 'column',
      })}
    >
      <Stack
        direction={{ xs: 'column', sm: 'row' }}
        spacing={1}
        sx={{ alignItems: { sm: 'flex-start' }, justifyContent: 'space-between', mb: 1.5 }}
      >
        <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', minWidth: 0 }}>
          {step && (
            <Typography
              aria-hidden
              sx={{
                fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
                fontSize: 14, color: 'var(--nx-glance-seal-ink)', fontVariantNumeric: 'tabular-nums',
              }}
            >
              {step}
            </Typography>
          )}
          <Typography component="h2" sx={{ fontWeight: 800, fontSize: { xs: 16, md: 18 }, lineHeight: 1.25 }}>
            {title}
          </Typography>
        </Stack>
        <Tooltip title={sealExplanation} placement="top-end">
          <Box
            data-testid="band-seal"
            data-governed={seal.governed ? 'true' : 'false'}
            aria-label={`${sealText}. ${sealExplanation}`}
            sx={{
              flexShrink: 0,
              alignSelf: { xs: 'flex-start', sm: 'auto' },
              px: 1, py: 0.375,
              borderRadius: 999,
              border: '1px solid',
              borderColor: 'var(--nx-glance-seal-rim)',
              backgroundColor: seal.governed ? 'var(--nx-glance-seal-ground)' : 'transparent',
              color: 'var(--nx-glance-seal-ink)',
              fontSize: 12,
              fontWeight: seal.governed ? 700 : 600,
              letterSpacing: '0.01em',
              fontVariantNumeric: 'tabular-nums',
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              maxWidth: { xs: '100%', sm: 340 },
            }}
          >
            {sealText}
          </Box>
        </Tooltip>
      </Stack>
      <Box sx={{ flexGrow: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>{body}</Box>
    </Paper>
  );
}
