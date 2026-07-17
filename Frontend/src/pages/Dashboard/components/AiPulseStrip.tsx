import React from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Box, ButtonBase, Typography } from '@mui/material';
import {
  AutoAwesome as SparkleIcon,
  FactCheck as ReviewIcon,
  MenuBook as ReadIcon,
  SmartToy as BotIcon,
  Verified as ConfidenceIcon,
} from '@mui/icons-material';
import GlassCard, { CardSkeleton, CardTitle } from './GlassCard';
import { formatCount } from './dashboardTheme';

interface PulseTile {
  key: string;
  icon: React.ReactNode;
  value: string;
  caption: string;
  to?: string;
}

interface AiPulseStripProps {
  /** Documents Nexora has read (every lead starts as a document). */
  documentsRead?: number;
  /** Average extraction confidence 0–100, from the server-side aggregate. */
  avgConfidencePct?: number;
  /** Extraction review queue depth (exact totalCount). */
  reviewQueueDepth?: number;
  /** Copilot actions executed within the recent audit window (last 100 entries). */
  recentActionsTaken?: number;
  auditAvailable: boolean;
  /** Copilot actions currently held for human approval (exact). */
  actionsHeld?: number;
  isLoading: boolean;
}

/**
 * "Is the AI working?" — a compact plain-language strip. Every number is real;
 * tiles whose source failed (or isn't derivable) simply don't render.
 */
const AiPulseStrip: React.FC<AiPulseStripProps> = ({
  documentsRead,
  avgConfidencePct,
  reviewQueueDepth,
  recentActionsTaken,
  auditAvailable,
  actionsHeld,
  isLoading,
}) => {
  const tiles: PulseTile[] = [];

  if (documentsRead !== undefined) {
    tiles.push({
      key: 'read',
      icon: <ReadIcon />,
      value: formatCount(documentsRead),
      caption: documentsRead === 1 ? 'document read for you' : 'documents read for you',
      to: '/procurement/leads/all',
    });
  }
  if (avgConfidencePct !== undefined && documentsRead !== undefined && documentsRead > 0) {
    tiles.push({
      key: 'confidence',
      icon: <ConfidenceIcon />,
      value: `${Math.round(avgConfidencePct)}%`,
      caption: 'average reading confidence',
    });
  }
  if (reviewQueueDepth !== undefined) {
    tiles.push({
      key: 'review',
      icon: <ReviewIcon />,
      value: formatCount(reviewQueueDepth),
      caption: reviewQueueDepth === 1 ? 'document waiting for your check' : 'documents waiting for your check',
      to: '/procurement/extraction/review',
    });
  }
  if (auditAvailable && recentActionsTaken !== undefined) {
    tiles.push({
      key: 'actions',
      icon: <BotIcon />,
      value: formatCount(recentActionsTaken),
      caption: 'recent copilot actions taken',
      to: '/copilot/activity',
    });
  }
  if (actionsHeld !== undefined) {
    tiles.push({
      key: 'held',
      icon: <SparkleIcon />,
      value: formatCount(actionsHeld),
      caption: actionsHeld === 1 ? 'action held for your approval' : 'actions held for your approval',
      to: '/copilot/approvals',
    });
  }

  return (
    <GlassCard label="Is the AI working?">
      <CardTitle
        title="Nexora at work"
        subtitle={
          documentsRead !== undefined && documentsRead > 0
            ? `Nexora has read ${formatCount(documentsRead)} ${documentsRead === 1 ? 'document' : 'documents'} for you and keeps watch around the clock.`
            : 'Nexora reads incoming documents and drafts the busywork for you.'
        }
      />
      {isLoading && tiles.length === 0 ? (
        <CardSkeleton rows={1} rowHeight={72} />
      ) : tiles.length === 0 ? (
        <Typography variant="body2" sx={{ color: 'text.secondary' }}>
          Nothing measured yet — upload a document or connect your inbox and Nexora gets to work.
        </Typography>
      ) : (
        <Box
          sx={{
            display: 'grid',
            gridTemplateColumns: { xs: '1fr 1fr', sm: 'repeat(3, 1fr)', md: `repeat(${Math.min(tiles.length, 5)}, 1fr)` },
            gap: 1,
          }}
        >
          {tiles.map((tile) => {
            const inner = (
              <>
                <Box aria-hidden sx={{ color: 'primary.main', display: 'flex', '& svg': { fontSize: 20 } }}>
                  {tile.icon}
                </Box>
                <Box sx={{ minWidth: 0 }}>
                  <Typography variant="h6" sx={{ fontWeight: 800, lineHeight: 1.2, color: 'text.primary' }}>
                    {tile.value}
                  </Typography>
                  <Typography variant="caption" sx={{ color: 'text.secondary', display: 'block', lineHeight: 1.3 }}>
                    {tile.caption}
                  </Typography>
                </Box>
              </>
            );
            const tileSx = {
              display: 'flex',
              alignItems: 'center',
              gap: 1.25,
              p: 1.5,
              borderRadius: 3,
              textAlign: 'left' as const,
              width: '100%',
              border: '1px solid',
              borderColor: 'divider',
              transition: 'border-color 0.15s, background-color 0.15s',
            };
            return tile.to ? (
              <ButtonBase
                key={tile.key}
                component={RouterLink}
                to={tile.to}
                focusRipple
                sx={{
                  ...tileSx,
                  '&:hover, &:focus-visible': { borderColor: 'primary.main', bgcolor: 'action.hover' },
                }}
              >
                {inner}
              </ButtonBase>
            ) : (
              <Box key={tile.key} sx={tileSx}>
                {inner}
              </Box>
            );
          })}
        </Box>
      )}
    </GlassCard>
  );
};

export default AiPulseStrip;
