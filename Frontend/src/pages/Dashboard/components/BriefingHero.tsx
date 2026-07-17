import React, { useState } from 'react';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { Box, Chip, IconButton, InputBase, Link, Skeleton, Tooltip, Typography } from '@mui/material';
import {
  AutoAwesome as SparkleIcon,
  Refresh as RefreshIcon,
  Send as SendIcon,
} from '@mui/icons-material';
import dayjs from 'dayjs';
import { useAppTheme } from '../../../context/ThemeContext';
import GlassCard from './GlassCard';
import type { BriefingClause } from '../briefing';

const SUGGESTIONS = ['Which bids close this week?', 'What should I work on first?'];

interface BriefingHeroProps {
  greeting: string;
  clauses: BriefingClause[];
  overnight: BriefingClause | null;
  /** True while any of the briefing's source queries is still on first load. */
  loading: boolean;
  /** True when every source query failed (nothing real to say). */
  allFailed: boolean;
  /** Epoch ms of the last successful core refresh; 0 when none yet. */
  updatedAt: number;
  refreshing: boolean;
  onRefresh: () => void;
}

/** Joins clause nodes as "a, b, and c" with deep links. */
const ClauseSentence: React.FC<{ clauses: BriefingClause[] }> = ({ clauses }) => {
  const parts: React.ReactNode[] = [];
  clauses.forEach((c, i) => {
    if (i > 0) parts.push(i === clauses.length - 1 ? (clauses.length > 2 ? ', and ' : ' and ') : ', ');
    parts.push(
      <Link key={c.key} component={RouterLink} to={c.to} underline="hover" sx={{ fontWeight: 700, color: 'primary.main' }}>
        {c.text}
      </Link>
    );
  });
  return (
    <>
      {'You have '}
      {parts}
      {'.'}
    </>
  );
};

/**
 * Narrative briefing hero: greeting, one plain-language situation sentence
 * composed from real numbers (each clause a deep link), the overnight line,
 * and the "Ask Nexora" entry. Absorbs the page header — carries the h1,
 * the updated-at stamp, and the manual refresh control.
 */
const BriefingHero: React.FC<BriefingHeroProps> = ({
  greeting,
  clauses,
  overnight,
  loading,
  allFailed,
  updatedAt,
  refreshing,
  onRefresh,
}) => {
  const { mode } = useAppTheme();
  const navigate = useNavigate();
  const [question, setQuestion] = useState('');
  const dark = mode === 'dark';

  const ask = (text?: string) => {
    const q = (text ?? question).trim();
    // CopilotPage prefills its input from this key — never auto-sends.
    navigate('/copilot', q ? { state: { initialQuestion: q } } : undefined);
  };

  return (
    <GlassCard label="Daily briefing" sx={{ p: { xs: 2, md: 3 } }}>
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mb: 0.75 }}>
        <Typography variant="overline" sx={{ color: 'text.secondary', letterSpacing: '0.14em', fontWeight: 800 }}>
          Your briefing
        </Typography>
        <Box sx={{ flex: 1 }} />
        {updatedAt > 0 && (
          <Typography variant="caption" sx={{ color: 'text.disabled', display: { xs: 'none', sm: 'block' } }}>
            Updated {dayjs(updatedAt).format('HH:mm')}
          </Typography>
        )}
        <Tooltip title="Refresh now">
          <IconButton
            size="small"
            onClick={onRefresh}
            aria-label="Refresh dashboard"
            sx={{
              border: '1px solid',
              borderColor: 'divider',
              borderRadius: 2,
              '@keyframes dashSpin': { from: { transform: 'rotate(0)' }, to: { transform: 'rotate(360deg)' } },
              '& svg': { animation: refreshing ? 'dashSpin 1s linear infinite' : 'none' },
            }}
          >
            <RefreshIcon sx={{ fontSize: 18 }} />
          </IconButton>
        </Tooltip>
      </Box>

      <Box sx={{ display: 'flex', flexDirection: { xs: 'column', md: 'row' }, gap: { xs: 2, md: 3 }, alignItems: { md: 'flex-end' } }}>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Typography component="h1" variant="h4" sx={{ fontWeight: 800, letterSpacing: '-0.02em', color: 'text.primary', mb: 1 }}>
            {greeting}
          </Typography>

          {loading ? (
            <Box aria-hidden>
              <Skeleton width="88%" height={24} />
              <Skeleton width="52%" height={24} />
            </Box>
          ) : (
            <Typography variant="body1" sx={{ color: 'text.primary', lineHeight: 1.8, maxWidth: 780 }}>
              {clauses.length > 0 ? (
                <ClauseSentence clauses={clauses} />
              ) : allFailed ? (
                'Your briefing is taking a moment — the numbers will appear as soon as we can reach them.'
              ) : (
                'All clear — no deadlines, reviews, or approvals need you right now.'
              )}
              {overnight && (
                <>
                  {' '}
                  <Link component={RouterLink} to={overnight.to} underline="hover" sx={{ color: 'text.secondary', fontWeight: 600 }}>
                    {overnight.text}
                  </Link>
                </>
              )}
            </Typography>
          )}
        </Box>

        <Box sx={{ minWidth: { xs: '100%', md: 340 }, maxWidth: { md: 380 } }}>
          <Box
            component="form"
            role="search"
            aria-label="Ask Nexora"
            onSubmit={(e: React.FormEvent) => {
              e.preventDefault();
              ask();
            }}
            sx={{
              display: 'flex',
              alignItems: 'center',
              gap: 1,
              px: 1.75,
              py: 0.75,
              borderRadius: '999px',
              border: '1px solid',
              borderColor: dark ? 'rgba(255,255,255,0.12)' : 'rgba(15,23,42,0.10)',
              bgcolor: dark ? 'rgba(15, 23, 42, 0.45)' : 'rgba(255, 255, 255, 0.85)',
              transition: 'border-color 0.15s',
              '&:focus-within': { borderColor: 'primary.main' },
            }}
          >
            <SparkleIcon aria-hidden sx={{ fontSize: 18, color: 'primary.main' }} />
            <InputBase
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              placeholder="Ask Nexora anything…"
              inputProps={{ 'aria-label': 'Ask Nexora anything' }}
              sx={{ flex: 1, fontSize: '0.9rem', fontWeight: 500 }}
            />
            <IconButton type="submit" size="small" aria-label="Send question to Nexora" sx={{ color: 'primary.main' }}>
              <SendIcon sx={{ fontSize: 18 }} />
            </IconButton>
          </Box>
          <Box sx={{ display: 'flex', gap: 0.75, mt: 1, flexWrap: 'wrap', justifyContent: { md: 'flex-end' } }}>
            {SUGGESTIONS.map((s) => (
              <Chip key={s} label={s} size="small" variant="outlined" onClick={() => ask(s)} sx={{ fontWeight: 600, borderRadius: 2 }} />
            ))}
          </Box>
        </Box>
      </Box>
    </GlassCard>
  );
};

export default BriefingHero;
