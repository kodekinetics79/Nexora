import React from 'react';
import { Box, Stack, Typography } from '@mui/material';
import ViewTabs from '../../components/layout/ViewTabs';
import GlanceStrip from './GlanceStrip';

/**
 * The one frame every Inbox tab sits in — Needs you, Documents to check, Inbound mail, Upload
 * documents.
 *
 * Owner 2026-09-28: the four tabs each drew their own header at their own width, so the tab strip
 * jumped on every click, the title changed name and size, and the same verb was a different button
 * on each screen. Here the header ("Inbox" and today's figures), the width and the tab strip are
 * identical on all four; a tab changes only what is under the strip.
 *
 * The toolbar row under the strip is the one place a tab puts its own things — what it holds on
 * the left (a count, a search, filters) and its actions on the right. It keeps its height when a
 * tab has nothing to put in it, so the content always starts at the same line.
 */
interface InboxFrameProps {
  /** One plain line on what this tab holds, e.g. "6 documents to check". Announced politely. */
  summary?: React.ReactNode;
  /** Colour the summary as a warning (a queue could not be read). */
  summaryWarning?: boolean;
  /** Search or filters, after the summary. */
  tools?: React.ReactNode;
  /** This tab's actions, right-aligned. */
  actions?: React.ReactNode;
  children: React.ReactNode;
}

export const INBOX_CARD_SX = { borderRadius: 3, overflow: 'hidden' } as const;

const InboxFrame: React.FC<InboxFrameProps> = ({ summary, summaryWarning = false, tools, actions, children }) => (
  <Box sx={{ p: { xs: 1, sm: 2 }, maxWidth: 1440, mx: 'auto' }}>
    <Stack
      direction={{ xs: 'column', md: 'row' }}
      sx={{ alignItems: { xs: 'flex-start', md: 'flex-end' }, justifyContent: 'space-between', gap: 2, mb: 1.5, minHeight: { md: 52 } }}
    >
      <Typography variant="h4" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.02em', lineHeight: 1.1 }}>
        Inbox
      </Typography>
      <GlanceStrip />
    </Stack>

    <ViewTabs primaryKey="inbox" ariaLabel="Inbox views" />

    <Stack
      direction="row"
      sx={{ alignItems: 'center', gap: 1.5, flexWrap: 'wrap', rowGap: 1, minHeight: 40, mt: -0.5, mb: 1.5 }}
    >
      {summary != null && (
        <Typography
          aria-live="polite"
          sx={{
            fontSize: '0.95rem',
            color: summaryWarning ? 'warning.dark' : 'text.secondary',
            fontWeight: summaryWarning ? 600 : 500,
          }}
        >
          {summary}
        </Typography>
      )}
      {tools}
      <Box sx={{ flex: 1 }} />
      {actions && <Stack direction="row" sx={{ alignItems: 'center', gap: 1 }}>{actions}</Stack>}
    </Stack>

    {children}
  </Box>
);

export default InboxFrame;
