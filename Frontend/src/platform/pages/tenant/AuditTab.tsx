import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Box, ToggleButton, ToggleButtonGroup, Typography } from '@mui/material';
import Stack from '../../components/Flex';
import AuditExplorer from '../../components/AuditExplorer';
import PageSection from '../../components/PageSection';
import { EmptyState, ErrorState, LoadingState } from '../../components/States';
import { SoftChip } from '../../components/StatusChip';
import { fmtDateTime } from '../../components/format';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type { Tenant } from '../../types';

const TIMELINE_LIMIT = 200;

const KIND_TONE: Record<string, 'info' | 'warning' | 'neutral'> = {
  audit: 'info',
  impersonation: 'warning',
  ticket: 'neutral',
};

/**
 * Two readings of the same customer's history. The explorer is the filterable, paged
 * audit log; the timeline is the merged story — audit rows, impersonation sessions and
 * tickets in one ordered list, assembled server-side so the three cannot be stitched
 * together in the wrong order by a client that fetched them separately.
 */
export default function AuditTab({ tenant }: { tenant: Tenant }) {
  const [view, setView] = useState<'explorer' | 'timeline'>('explorer');

  const timelineQuery = useQuery({
    queryKey: platformKeys.tenantTimeline(tenant.id, { limit: TIMELINE_LIMIT }),
    queryFn: () => platformApi.getTenantTimeline(tenant.id, { limit: TIMELINE_LIMIT }),
    enabled: view === 'timeline',
  });

  return (
    <Stack spacing={2}>
      {/*
        This was a second `<Tabs>` INSIDE the tenant page's twelve-tab strip — the only genuine
        tab-within-tab in the product. Two identical strips stacked make the inner one read as a
        third level of navigation, and a reader who clicked "Merged timeline" could not tell
        whether they had changed page or changed filter.

        It is a segmented control now: a filter over one screen, visually distinct from the tab
        strip above it. Same two readings, same state, one level of tabs on the page.
      */}
      <ToggleButtonGroup
        exclusive
        size="small"
        value={view}
        onChange={(_event, next: 'explorer' | 'timeline' | null) => next && setView(next)}
        aria-label="Audit reading"
        sx={{ alignSelf: 'flex-start' }}
      >
        <ToggleButton value="explorer">Audit entries</ToggleButton>
        <ToggleButton value="timeline">Merged timeline</ToggleButton>
      </ToggleButtonGroup>

      {view === 'explorer' ? (
        <AuditExplorer tenantId={tenant.id} height={560} />
      ) : (
        <PageSection
          title="Everything that happened to this customer"
          subtitle="Privileged actions, impersonation sessions and support tickets, in one order."
        >
          {timelineQuery.isLoading ? (
            <LoadingState label="Assembling the timeline…" minHeight={240} />
          ) : timelineQuery.isError ? (
            <ErrorState
              message={platformErrorMessage(timelineQuery.error, 'The timeline could not be read.')}
              onRetry={() => timelineQuery.refetch()}
              minHeight={240}
            />
          ) : (timelineQuery.data?.length ?? 0) === 0 ? (
            <EmptyState title="Nothing recorded" message="No privileged activity has touched this tenant." />
          ) : (
            <Stack spacing={0.5}>
              {(timelineQuery.data ?? []).map((entry) => (
                <Stack
                  key={`${entry.kind}-${entry.id}`}
                  direction={{ xs: 'column', md: 'row' }}
                  spacing={1.5}
                  sx={{ py: 1, borderBottom: '1px solid', borderColor: 'divider' }}
                >
                  <Box sx={{ width: 120, flexShrink: 0 }}>
                    <SoftChip label={entry.kind} tone={KIND_TONE[entry.kind] ?? 'neutral'} dot={false} />
                  </Box>
                  <Box sx={{ flex: 1, minWidth: 0 }}>
                    <Typography variant="body2" sx={{ fontWeight: 700 }}>
                      <Box component="code">{entry.action}</Box>
                    </Typography>
                    {entry.summary && (
                      <Typography variant="caption" color="text.secondary">
                        {entry.summary}
                      </Typography>
                    )}
                  </Box>
                  {entry.result && entry.result !== 'success' && (
                    <SoftChip label={entry.result} tone="error" dot={false} />
                  )}
                  <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
                    {entry.actor ?? '—'} · {fmtDateTime(entry.occurredAtUtc)}
                  </Typography>
                </Stack>
              ))}
            </Stack>
          )}
        </PageSection>
      )}
    </Stack>
  );
}
