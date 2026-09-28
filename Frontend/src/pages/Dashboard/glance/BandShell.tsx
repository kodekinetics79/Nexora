import type { ReactNode } from 'react';
import { Alert, AlertTitle, Box, Button, Link, Paper, Stack, Tooltip, Typography } from '@mui/material';
import { Link as RouterLink, useInRouterContext } from 'react-router-dom';
import { InfoOutlined as HintIcon, LockOutlined as ForbiddenIcon } from '@mui/icons-material';
import dayjs from 'dayjs';
import { glanceCssVariables } from './tokens';
import { NEU_TRANSITION, neuCssVariables, neuInset, neuRaised, neuSlab, neuWell } from './neumorphic';
import RefreshFailedNotice from '../../../components/common/RefreshFailedNotice';
import { SCOPE_UNRESOLVED } from './scopeWords';

/**
 * One band of the glance screen: a clay slab (neumorphic.ts) with its title, its seal, and whatever it draws.
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
  /**
   * `dataUpdatedAt` of a band whose latest background refresh failed while its last good answer is
   * still drawn. The band keeps its rows and says how old they are; only `error` replaces them.
   */
  refreshFailedAt?: number | null;
  /**
   * How to read the band, in one or two sentences. Behind an info key beside the title rather than
   * printed above the chart: the screen has to fit one glance, and a reader needs the key once, not
   * every time they look.
   */
  hint?: string;
  /**
   * The band that asks the reader to act. It wears the brass rim the chosen period key wears, so
   * the one band with work in it is the one the eye finds among six slabs of the same material.
   */
  emphasis?: boolean;
  /** The band's reserved height. It is held in every state, empty included. */
  minHeight?: number;
  index?: number;
  /** Where "details →" opens this band's own deeper page. Absent when there is none for this reader. */
  detailsTo?: string;
}

const sealFreshness = (generatedAt?: string | null): string => {
  if (!generatedAt) return 'freshness not stated';
  const at = dayjs(generatedAt);
  return at.isValid() ? `as of ${at.format('HH:mm')}` : 'freshness not stated';
};

export default function BandShell({
  title, seal, children, step, hint, emphasis = false, loading = false, error = null, forbidden = null, onRetry, refreshFailedAt = null, minHeight = 240, index = 0, detailsTo,
}: BandShellProps) {
  // A band rendered outside the app (a test, a preview) still shows the link, as a plain href.
  const inRouter = useInRouterContext();
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
          sx={{ minHeight: minHeight - 100, display: 'grid', placeItems: 'center' }}
        >
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>Loading {title.toLowerCase()}…</Typography>
        </Box>
      );
    }
    if (refreshFailedAt !== null) {
      return <>{<RefreshFailedNotice updatedAt={refreshFailedAt} sx={{ mb: 1 }} />}{children}</>;
    }
    return children;
  })();

  return (
    <Paper
      component="section"
      variant="outlined"
      className="nx-neu nx-enter"
      data-decorative-motion="true"
      style={{ animationDelay: `${Math.min(index, 8) * 40}ms` }}
      aria-label={title}
      aria-busy={loading || undefined}
      sx={(theme) => ({
        // The series tokens are declared on the band itself rather than globally, so any chart a
        // band draws inherits them and a band rendered on its own — in a test, or in a page that
        // does not mount the whole screen — still paints in the validated palette.
        ...glanceCssVariables(theme.palette.mode),
        ...neuCssVariables(theme.palette.mode),
        ...neuSlab(theme.palette.mode, 8),
        ...(emphasis ? { boxShadow: `0 0 0 1.5px color-mix(in srgb, var(--nx-glance-seal-ink) 70%, transparent), ${neuRaised(theme.palette.mode, 8)}` } : {}),
        ...NEU_TRANSITION,
        p: 1.5,
        // Pixel radii on purpose (the theme multiplies sx numbers by 12): the tray inside is
        // 28 − 12 padding = 16px, so the two corners stay concentric instead of pinching.
        borderRadius: '28px',
        // Bands sit in a grid on the dashboard; filling the cell keeps a row's slabs one height.
        height: '100%',
        // The band reads its own width, not the window's, so its insides adapt to the cell.
        containerType: 'inline-size',
        // No hover lift: a band is not a button, and lifting it said it was.
        minHeight,
        display: 'flex',
        flexDirection: 'column',
      })}
    >
      {/*
        Title on one line, seal on the next. A band is a third of the screen wide, so the seal
        cannot share the title's line without truncating one of them; stacked, both stay whole and
        the seal still sits in the same place on every band.
      */}
      <Stack spacing={0.5} sx={{ alignItems: 'flex-start', mb: 1 }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', minWidth: 0, maxWidth: '100%' }}>
          {step && (
            <Typography
              aria-hidden
              sx={(theme) => ({
                // The step numeral sits in a small pressed dimple, so the sentence the screen reads
                // as (1 → 6) is something the eye can count down the left edge.
                boxShadow: neuRaised(theme.palette.mode, 3),
                alignSelf: 'center',
                width: 26, height: 26, borderRadius: '50%', flexShrink: 0,
                display: 'grid', placeItems: 'center',
                fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700,
                fontSize: 12, color: 'var(--nx-glance-seal-ink)', fontVariantNumeric: 'tabular-nums',
              })}
            >
              {step}
            </Typography>
          )}
          <Typography
            component="h2"
            sx={{ fontWeight: 700, fontSize: 16, lineHeight: 1.25, textWrap: 'balance' }}
          >
            {title}
          </Typography>
          {detailsTo && (
            <Link
              {...(inRouter ? { component: RouterLink, to: detailsTo } : { href: detailsTo })}
              aria-label={`${title}: details`}
              underline="hover"
              sx={{ fontSize: 12, fontWeight: 700, color: 'var(--nx-glance-seal-ink)', whiteSpace: 'nowrap', flexShrink: 0 }}
            >
              details →
            </Link>
          )}
        </Stack>
        <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center', maxWidth: '100%', minWidth: 0 }}>
        <Tooltip title={sealExplanation} placement="top-end">
          <Box
            data-testid="band-seal"
            data-governed={seal.governed ? 'true' : 'false'}
            role="note"
            aria-label={`${sealText}. ${sealExplanation}`}
            sx={(theme) => ({
              flexShrink: 0,
              px: 1.25, py: 0.375,
              borderRadius: 999,
              border: '1px solid',
              borderColor: 'var(--nx-glance-seal-rim)',
              backgroundColor: seal.governed ? 'var(--nx-glance-seal-ground)' : 'transparent',
              // Governed = pressed in, like the period key that governs it. A fixed window is a
              // flat outline: raised, it read as a button, and it is not one.
              boxShadow: seal.governed ? neuInset(theme.palette.mode, 3) : 'none',
              color: 'var(--nx-glance-seal-ink)',
              fontSize: 11,
              fontWeight: seal.governed ? 700 : 600,
              letterSpacing: '0.01em',
              fontVariantNumeric: 'tabular-nums',
              whiteSpace: 'nowrap',
              overflow: 'hidden',
              textOverflow: 'ellipsis',
              maxWidth: '100%',
              // In a narrow cell the seal wraps rather than hiding its window behind an ellipsis.
              '@container (max-width: 360px)': { whiteSpace: 'normal' },
            })}
          >
            {sealText}
          </Box>
        </Tooltip>
          {hint && (
            <Tooltip title={hint} placement="top-start">
              <Box
                component="button"
                type="button"
                aria-label={`How to read this: ${hint}`}
                sx={{
                  display: 'inline-grid', placeItems: 'center', flexShrink: 0,
                  width: 24, height: 24, p: 0, border: 0, borderRadius: '50%',
                  background: 'none', color: 'text.secondary', cursor: 'help',
                  '&:focus-visible': { outline: '2px solid', outlineColor: 'var(--nx-glance-seal-ink)' },
                }}
              >
                <HintIcon sx={{ fontSize: 16 }} />
              </Box>
            </Tooltip>
          )}
        </Stack>
      </Stack>
      {/*
        The band's content is pressed INTO the slab: a raised object holding a sunken tray is the
        signature shape of the material, and it keeps charts on a calm ground of their own.
      */}
      <Box
        sx={(theme) => ({
          ...neuWell(theme.palette.mode, 4),
          borderRadius: '16px',
          px: 1.5, py: 1,
          flexGrow: 1, minWidth: 0, display: 'flex', flexDirection: 'column',
        })}
      >
        {body}
      </Box>
    </Paper>
  );
}
