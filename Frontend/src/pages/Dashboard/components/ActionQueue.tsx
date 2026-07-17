import React from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Box, Button, ButtonBase, Chip, Typography } from '@mui/material';
import {
  Alarm as DeadlineIcon,
  AssignmentInd as AssignIcon,
  CheckCircleOutlined as DoneIcon,
  ChevronRight as ChevronIcon,
  CloudUpload as UploadIcon,
  FactCheck as ReviewIcon,
  SmartToy as BotIcon,
} from '@mui/icons-material';
import type { Dayjs } from 'dayjs';
import dayjs from 'dayjs';
import type { AgentApproval } from '../../../api/services/copilotService';
import type { AcceptedLeadResponseDTO } from '../../../api/services/leadService';
import type { NeedsReviewItem } from '../../../api/services/extractionReviewService';
import type { LeadDecisionSummary } from '../../../api/services/dashboardService';
import GlassCard, { CardSkeleton, CardTitle } from './GlassCard';
import { formatCount, formatMoney, humanizeDeadline } from './dashboardTheme';

// ─── Row models ─────────────────────────────────────────────────────────────

export interface DeadlineRow {
  leadId: number;
  rfqno: string | null;
  buyersName: string | null;
  closing: Dayjs;
  overdue: boolean;
}

interface ActionQueueProps {
  deadlines: DeadlineRow[];
  deadlinesReady: boolean;
  review?: { items: NeedsReviewItem[]; totalCount: number };
  approvals?: AgentApproval[];
  unassigned?: { items: AcceptedLeadResponseDTO[]; totalCount: number };
  /** Bid/Review/Skip decorations keyed by leadId; absent when the API 404s. */
  decisions?: Record<string, LeadDecisionSummary>;
  isLoading: boolean;
  /** True when this tenant has no leads at all — drives the first-run empty state. */
  isBrandNew: boolean;
}

// ─── Small pieces ───────────────────────────────────────────────────────────

/** "sendSupplierEmail" / "send_supplier_email" → "Send supplier email". */
const prettifyTool = (name: string): string => {
  const words = name.replace(/[_-]+/g, ' ').replace(/([a-z0-9])([A-Z])/g, '$1 $2').toLowerCase().trim();
  return words ? words.charAt(0).toUpperCase() + words.slice(1) : 'Copilot action';
};

const DecisionChip: React.FC<{ summary?: LeadDecisionSummary }> = ({ summary }) => {
  if (!summary) return null;
  const map = {
    bid: { label: 'Bid', color: 'success' as const },
    review: { label: 'Review', color: 'warning' as const },
    skip: { label: 'Skip', color: 'default' as const },
  };
  const cfg = map[summary.recommendation] ?? map.review;
  return <Chip size="small" label={cfg.label} color={cfg.color} variant="outlined" sx={{ fontWeight: 700, height: 22 }} />;
};

const decisionValueText = (summary?: LeadDecisionSummary): string | null =>
  summary && summary.estimatedValue > 0 ? `worth about ${formatMoney(summary.estimatedValue)}` : null;

interface QueueRowProps {
  to: string;
  icon: React.ReactNode;
  iconColor: string;
  primary: string;
  secondary: string;
  meta?: React.ReactNode;
}

const QueueRow: React.FC<QueueRowProps> = ({ to, icon, iconColor, primary, secondary, meta }) => (
  <ButtonBase
    component={RouterLink}
    to={to}
    focusRipple
    sx={{
      display: 'flex',
      alignItems: 'center',
      gap: 1.5,
      width: '100%',
      textAlign: 'left',
      px: 1.25,
      py: 1,
      borderRadius: 2.5,
      transition: 'background-color 0.15s',
      '&:hover, &:focus-visible': { bgcolor: 'action.hover' },
    }}
  >
    <Box
      aria-hidden
      sx={{
        width: 34,
        height: 34,
        borderRadius: 2.5,
        display: 'grid',
        placeItems: 'center',
        flexShrink: 0,
        color: iconColor,
        bgcolor: `${iconColor}1a`,
        '& svg': { fontSize: 19 },
      }}
    >
      {icon}
    </Box>
    <Box sx={{ flex: 1, minWidth: 0 }}>
      <Typography variant="body2" noWrap sx={{ fontWeight: 700, color: 'text.primary' }}>
        {primary}
      </Typography>
      <Typography variant="caption" noWrap sx={{ color: 'text.secondary', display: 'block' }}>
        {secondary}
      </Typography>
    </Box>
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 0.75, flexShrink: 0 }}>
      {meta}
      <ChevronIcon aria-hidden sx={{ fontSize: 18, color: 'text.disabled' }} />
    </Box>
  </ButtonBase>
);

const SectionHeader: React.FC<{ title: string; count: number; viewAllTo: string }> = ({ title, count, viewAllTo }) => (
  <Box sx={{ display: 'flex', alignItems: 'center', gap: 1, mt: 1.5, mb: 0.5, px: 0.5 }}>
    <Typography component="h3" variant="caption" sx={{ fontWeight: 800, color: 'text.secondary', letterSpacing: '0.06em', textTransform: 'uppercase' }}>
      {title}
    </Typography>
    <Chip size="small" label={formatCount(count)} sx={{ height: 18, fontWeight: 700, fontSize: '0.68rem' }} />
    <Box sx={{ flex: 1 }} />
    <Button component={RouterLink} to={viewAllTo} size="small" sx={{ fontWeight: 700, fontSize: '0.72rem', minWidth: 0 }}>
      View all
    </Button>
  </Box>
);

// ─── Main card ──────────────────────────────────────────────────────────────

/**
 * "What needs me NOW?" — every row is a real pending item with a one-click
 * deep link to the exact screen that resolves it. Sections whose source
 * endpoint failed simply don't render; the card never breaks the page.
 */
const ActionQueue: React.FC<ActionQueueProps> = ({
  deadlines,
  deadlinesReady,
  review,
  approvals,
  unassigned,
  decisions,
  isLoading,
  isBrandNew,
}) => {
  const reviewItems = review?.items ?? [];
  const approvalItems = approvals ?? [];
  const unassignedItems = unassigned?.items ?? [];

  const totalNeedingYou =
    deadlines.length + (review?.totalCount ?? 0) + approvalItems.length + (unassigned?.totalCount ?? 0);

  const anySource = deadlinesReady || review !== undefined || approvals !== undefined || unassigned !== undefined;
  const hasRows =
    deadlines.length > 0 || reviewItems.length > 0 || approvalItems.length > 0 || unassignedItems.length > 0;

  return (
    <GlassCard label="What needs you now" sx={{ height: '100%' }}>
      <CardTitle
        title="Needs you now"
        subtitle="The shortest path through today"
        action={
          totalNeedingYou > 0 ? (
            <Chip
              label={`${formatCount(totalNeedingYou)} open`}
              color="primary"
              size="small"
              sx={{ fontWeight: 800 }}
            />
          ) : undefined
        }
      />

      {isLoading && !anySource ? (
        <CardSkeleton rows={5} rowHeight={48} />
      ) : !hasRows ? (
        <Box sx={{ textAlign: 'center', py: 6, px: 2 }}>
          {isBrandNew ? (
            <>
              <UploadIcon aria-hidden sx={{ fontSize: 40, color: 'text.disabled', mb: 1 }} />
              <Typography variant="h6" sx={{ fontWeight: 800, color: 'text.primary' }}>
                No leads yet
              </Typography>
              <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5, mb: 2.5 }}>
                Connect your inbox or upload documents — Nexora reads them and lines up your first actions here.
              </Typography>
              <Box sx={{ display: 'flex', gap: 1.5, justifyContent: 'center', flexWrap: 'wrap' }}>
                <Button component={RouterLink} to="/procurement/leads/manual-upload" variant="contained" sx={{ fontWeight: 700, borderRadius: 2.5 }}>
                  Upload documents
                </Button>
                <Button component={RouterLink} to="/copilot" variant="outlined" sx={{ fontWeight: 700, borderRadius: 2.5 }}>
                  Ask Nexora
                </Button>
              </Box>
            </>
          ) : (
            <>
              <DoneIcon aria-hidden sx={{ fontSize: 40, color: 'success.main', mb: 1 }} />
              <Typography variant="h6" sx={{ fontWeight: 800, color: 'text.primary' }}>
                You're all caught up
              </Typography>
              <Typography variant="body2" sx={{ color: 'text.secondary', mt: 0.5 }}>
                Nothing is waiting on you right now — Nexora will surface the next thing here.
              </Typography>
            </>
          )}
        </Box>
      ) : (
        <Box sx={{ display: 'flex', flexDirection: 'column' }}>
          {deadlines.length > 0 && (
            <>
              <SectionHeader title="Bid deadlines" count={deadlines.length} viewAllTo="/procurement/leads/all" />
              {deadlines.map((row) => {
                const summary = decisions?.[String(row.leadId)];
                const valueText = decisionValueText(summary);
                return (
                  <QueueRow
                    key={`deadline-${row.leadId}`}
                    to={`/procurement/leads/view/${row.leadId}`}
                    icon={<DeadlineIcon />}
                    iconColor={row.overdue ? '#d03b3b' : '#c98500'}
                    primary={`${row.rfqno || 'Lead'} — ${row.buyersName || 'Unknown buyer'}`}
                    secondary={[humanizeDeadline(row.closing), valueText].filter(Boolean).join(' · ')}
                    meta={<DecisionChip summary={summary} />}
                  />
                );
              })}
            </>
          )}

          {reviewItems.length > 0 && review && (
            <>
              <SectionHeader title="Documents to check" count={review.totalCount} viewAllTo="/procurement/extraction/review" />
              {reviewItems.map((item) => {
                const summary = decisions?.[String(item.id)];
                const valueText = decisionValueText(summary);
                const why = item.reviewReason || 'Nexora wasn’t fully sure about this one — takes a minute to confirm';
                return (
                  <QueueRow
                    key={`review-${item.id}`}
                    to={`/procurement/extraction/review/${item.id}`}
                    icon={<ReviewIcon />}
                    iconColor="#2a78d6"
                    primary={`${item.rfqno || 'Document'} — ${item.buyersName || 'Unknown buyer'}`}
                    secondary={[why, valueText].filter(Boolean).join(' · ')}
                    meta={<DecisionChip summary={summary} />}
                  />
                );
              })}
            </>
          )}

          {approvalItems.length > 0 && (
            <>
              <SectionHeader title="Copilot needs a yes" count={approvalItems.length} viewAllTo="/copilot/approvals" />
              {approvalItems.slice(0, 5).map((a) => (
                <QueueRow
                  key={`approval-${a.id}`}
                  to="/copilot/approvals"
                  icon={<BotIcon />}
                  iconColor="#4a3aa7"
                  primary={a.summary || prettifyTool(a.toolName)}
                  secondary={`Waiting for your approval · asked ${dayjs(a.requestedOn).format('MMM D, HH:mm')}`}
                />
              ))}
            </>
          )}

          {unassignedItems.length > 0 && unassigned && (
            <>
              <SectionHeader title="Leads without an owner" count={unassigned.totalCount} viewAllTo="/procurement/leads/outstanding" />
              {unassignedItems.map((lead) => (
                <QueueRow
                  key={`unassigned-${lead.id}`}
                  to="/procurement/leads/outstanding"
                  icon={<AssignIcon />}
                  iconColor="#199e70"
                  primary={`${lead.rfqno || 'Lead'} — ${lead.buyersName || 'Unknown buyer'}`}
                  secondary="Accepted but nobody owns it yet — assign it so it keeps moving"
                />
              ))}
            </>
          )}
        </Box>
      )}
    </GlassCard>
  );
};

export default ActionQueue;
