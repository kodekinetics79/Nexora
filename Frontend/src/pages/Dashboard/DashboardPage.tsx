import { useMemo } from 'react';
import { keepPreviousData, useQuery, useQueryClient } from '@tanstack/react-query';
import { Box } from '@mui/material';
import dayjs from 'dayjs';
import { useAuth } from '../../context/AuthContext';
import { useAppTheme } from '../../context/ThemeContext';
import dashboardService from '../../api/services/dashboardService';
import leadService from '../../api/services/leadService';
import extractionReviewService from '../../api/services/extractionReviewService';
import copilotService from '../../api/services/copilotService';
import { composeClauses, composeOvernightClause, greetingForHour, type BriefingInput } from './briefing';
import BriefingHero from './components/BriefingHero';
import ActionQueue, { type DeadlineRow } from './components/ActionQueue';
import PipelineSnapshot from './components/PipelineSnapshot';
import PipelinePanel from './components/PipelinePanel';
import TrendTiles from './components/TrendTiles';
import AiPulseStrip from './components/AiPulseStrip';
import { asRealDate } from './components/dashboardTheme';

const LIVE = {
  refetchInterval: 60_000,
  placeholderData: keepPreviousData,
} as const;

/**
 * Nexora command center. Three questions, in visual priority:
 *   1. What needs me NOW?         → Action Queue (left, dominant)
 *   2. How is the money flowing?  → Pipeline + 6-month trend (right column)
 *   3. Is the AI working?         → plain-language strip (full width, below)
 * Every section degrades to a quiet skeleton/empty state on failure.
 */
export default function DashboardPage() {
  const { userData } = useAuth();
  const { mode, primaryColor } = useAppTheme();
  const queryClient = useQueryClient();
  const businessUnitId = userData?.businessUnitId || 1;

  // ── Live data (60s auto-refresh; refetches never flash skeletons) ──
  const core = useQuery({
    queryKey: ['dashboard', 'core', businessUnitId],
    queryFn: () => dashboardService.getDashboard(businessUnitId),
    ...LIVE,
  });
  const leadStats = useQuery({
    queryKey: ['dashboard', 'lead-stats'],
    queryFn: dashboardService.getLeadStats,
    ...LIVE,
  });
  const rfqStats = useQuery({
    queryKey: ['dashboard', 'rfq-stats'],
    queryFn: dashboardService.getRfqStats,
    ...LIVE,
  });
  const quoteStats = useQuery({
    queryKey: ['dashboard', 'quote-stats'],
    queryFn: dashboardService.getQuoteStats,
    ...LIVE,
  });
  const orderStats = useQuery({
    queryKey: ['dashboard', 'order-stats'],
    queryFn: dashboardService.getOrderStats,
    ...LIVE,
  });
  const needsReview = useQuery({
    queryKey: ['dashboard', 'needs-review'],
    queryFn: () => extractionReviewService.getNeedsReview({ pageNumber: 1, pageSize: 5 }),
    ...LIVE,
  });
  const recentLeads = useQuery({
    queryKey: ['dashboard', 'recent-leads'],
    queryFn: () => leadService.getAll({ pageNumber: 1, pageSize: 50 }),
    ...LIVE,
  });
  const approvals = useQuery({
    queryKey: ['dashboard', 'approvals'],
    queryFn: () => copilotService.getApprovals('pending'),
    ...LIVE,
  });
  const audit = useQuery({
    queryKey: ['dashboard', 'audit'],
    queryFn: () => copilotService.getAudit(100),
    ...LIVE,
  });
  const unassigned = useQuery({
    queryKey: ['dashboard', 'unassigned'],
    queryFn: () =>
      leadService.getOutstandingLeads({ pageNumber: 1, pageSize: 5, excludeAssigned: true, businessUnitId }),
    ...LIVE,
  });

  // ── Bid deadlines: next 72h + freshly overdue (last 7 days), real dates only ──
  const deadlineRows = useMemo<DeadlineRow[]>(() => {
    const items = recentLeads.data?.items ?? [];
    const now = dayjs();
    const horizon = now.add(72, 'hour');
    const overdueFloor = now.subtract(7, 'day');
    return items
      .filter((lead) => !lead.isRejected)
      .flatMap((lead) => {
        const closing = asRealDate(lead.bidClosingDate);
        if (!closing || closing.isBefore(overdueFloor) || closing.isAfter(horizon)) return [];
        return [
          {
            leadId: lead.id,
            rfqno: lead.rfqno || null,
            buyersName: lead.buyersName || null,
            closing,
            overdue: closing.isBefore(now),
          },
        ];
      })
      .sort((a, b) => a.closing.valueOf() - b.closing.valueOf())
      .slice(0, 8);
  }, [recentLeads.data]);

  // ── Bid/Review/Skip decorations (in-flight API — silently absent on 404) ──
  const decisionLeadIds = useMemo(() => {
    const ids = new Set<number>();
    deadlineRows.forEach((row) => ids.add(row.leadId));
    (needsReview.data?.items ?? []).forEach((item) => ids.add(item.id));
    return Array.from(ids).slice(0, 100);
  }, [deadlineRows, needsReview.data]);

  const decisions = useQuery({
    queryKey: ['dashboard', 'decisions', decisionLeadIds],
    queryFn: () => dashboardService.getDecisionSummaries(decisionLeadIds),
    enabled: decisionLeadIds.length > 0,
    retry: false,
    refetchInterval: 60_000,
    placeholderData: keepPreviousData,
  });

  // ── Derived view state ──
  const stats = core.data?.stats;
  const avgConfidencePct = core.data?.operationalHealth.find((r) => r.subject === 'AI Accuracy')?.a;
  const actionsTakenRecently = audit.data?.filter((a) => a.decision?.toLowerCase() === 'executed').length;
  const isBrandNew = core.isSuccess && stats !== undefined && stats.totalLeads === 0;
  const isRefreshing =
    core.isFetching || needsReview.isFetching || approvals.isFetching || recentLeads.isFetching;

  // ── Narrative briefing: clauses only from queries that succeeded, counts > 0 ──
  const briefingInput = useMemo<BriefingInput>(() => {
    const leads = recentLeads.data?.items;
    const dayAgo = dayjs().subtract(24, 'hour');
    return {
      deadlineCount: recentLeads.isSuccess ? deadlineRows.filter((r) => !r.overdue).length : null,
      needsReviewCount: needsReview.isSuccess && needsReview.data ? needsReview.data.totalCount : null,
      pendingQuoteCount: quoteStats.isSuccess && quoteStats.data ? quoteStats.data.pendingQuotes : null,
      totalQuotedAmount: quoteStats.isSuccess && quoteStats.data ? quoteStats.data.totalQuotedAmount : null,
      pendingApprovalCount: approvals.isSuccess && approvals.data ? approvals.data.length : null,
      overnightDocCount:
        recentLeads.isSuccess && leads
          ? leads.filter((l) => {
              const created = asRealDate(l.createdDate ?? l.recDate);
              return created !== null && created.isAfter(dayAgo);
            }).length
          : null,
    };
  }, [
    recentLeads.isSuccess,
    recentLeads.data,
    deadlineRows,
    needsReview.isSuccess,
    needsReview.data,
    quoteStats.isSuccess,
    quoteStats.data,
    approvals.isSuccess,
    approvals.data,
  ]);

  const briefingClauses = useMemo(() => composeClauses(briefingInput), [briefingInput]);
  const overnightClause = useMemo(() => composeOvernightClause(briefingInput), [briefingInput]);
  const briefingLoading =
    recentLeads.isLoading || needsReview.isLoading || quoteStats.isLoading || approvals.isLoading;
  const briefingAllFailed =
    recentLeads.isError && needsReview.isError && quoteStats.isError && approvals.isError;
  const greeting = greetingForHour(dayjs().hour(), userData?.userName);

  const refreshAll = () => queryClient.invalidateQueries({ queryKey: ['dashboard'] });

  return (
    <Box sx={{ position: 'relative', maxWidth: 1440, mx: 'auto', p: { xs: 1, md: 2 } }}>
      {/* Ambient wash behind the glass — subtle in both themes. */}
      <Box
        aria-hidden
        sx={{
          position: 'absolute',
          inset: -24,
          zIndex: 0,
          pointerEvents: 'none',
          background:
            mode === 'dark'
              ? `radial-gradient(640px 320px at 12% -4%, ${primaryColor}2e, transparent 70%),
                 radial-gradient(560px 300px at 96% 12%, #0ea5e926, transparent 70%)`
              : `radial-gradient(640px 320px at 12% -4%, ${primaryColor}1f, transparent 70%),
                 radial-gradient(560px 300px at 96% 12%, #0ea5e91a, transparent 70%)`,
        }}
      />

      <Box sx={{ position: 'relative', zIndex: 1, display: 'flex', flexDirection: 'column', gap: 2 }}>
        {/* Narrative briefing hero — greeting, situation sentence, Ask Nexora */}
        <BriefingHero
          greeting={greeting}
          clauses={briefingClauses}
          overnight={overnightClause}
          loading={briefingLoading}
          allFailed={briefingAllFailed}
          updatedAt={core.dataUpdatedAt}
          refreshing={isRefreshing}
          onRefresh={refreshAll}
        />

        {/* Bento grid */}
        <Box
          sx={{
            display: 'grid',
            gap: 2,
            gridTemplateColumns: { xs: '1fr', lg: 'repeat(12, 1fr)' },
            alignItems: 'stretch',
          }}
        >
          <Box sx={{ gridColumn: { xs: 'auto', lg: 'span 7' }, minWidth: 0 }}>
            <ActionQueue
              deadlines={deadlineRows}
              deadlinesReady={recentLeads.isSuccess}
              review={needsReview.data ? { items: needsReview.data.items, totalCount: needsReview.data.totalCount } : undefined}
              approvals={approvals.data}
              unassigned={unassigned.data ? { items: unassigned.data.items, totalCount: unassigned.data.totalCount } : undefined}
              decisions={decisions.data?.summaries}
              isLoading={needsReview.isLoading || recentLeads.isLoading}
              isBrandNew={isBrandNew}
            />
          </Box>

          <Box sx={{ gridColumn: { xs: 'auto', lg: 'span 5' }, minWidth: 0, display: 'flex', flexDirection: 'column', gap: 2 }}>
            <PipelineSnapshot
              leadStats={leadStats.data}
              rfqStats={rfqStats.data}
              quoteStats={quoteStats.data}
              orderStats={orderStats.data}
              isLoading={leadStats.isLoading || rfqStats.isLoading || quoteStats.isLoading || orderStats.isLoading}
            />
            <TrendTiles
              volumeTrend={core.data?.volumeTrend}
              rfqsTrend={stats?.rfqsTrend}
              ordersTrend={stats?.ordersTrend}
              isLoading={core.isLoading}
            />
          </Box>

          <Box sx={{ gridColumn: { xs: 'auto', lg: '1 / -1' }, minWidth: 0 }}>
            <AiPulseStrip
              documentsRead={stats?.totalLeads}
              avgConfidencePct={avgConfidencePct}
              reviewQueueDepth={needsReview.data?.totalCount}
              recentActionsTaken={actionsTakenRecently}
              auditAvailable={audit.isSuccess}
              actionsHeld={approvals.data?.length}
              isLoading={core.isLoading && needsReview.isLoading}
            />
          </Box>

          {/* WP-B2: pipeline analytics — additive card below the existing bento. */}
          <Box sx={{ gridColumn: { xs: 'auto', lg: '1 / -1' }, minWidth: 0 }}>
            <PipelinePanel />
          </Box>
        </Box>
      </Box>
    </Box>
  );
}
