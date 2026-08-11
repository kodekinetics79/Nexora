import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  Grid,
  Table,
  TableBody,
  TableCell,
  TableRow,
  Typography,
} from '@mui/material';
import {
  ConfirmationNumberOutlined as TicketIcon,
  HistoryOutlined as AuditIcon,
  LoginOutlined as ImpersonationIcon,
  WarningAmberRounded as WarningIcon,
} from '@mui/icons-material';
import Stack from '../../components/Flex';
import PageSection from '../../components/PageSection';
import StatTile from '../../components/StatTile';
import { EmptyState, ErrorState, LoadingState, TilesSkeleton } from '../../components/States';
import { Dash, SoftChip } from '../../components/StatusChip';
import { fmtDate, fmtDateTime, fmtNumber, fmtRelative } from '../../components/format';
import { SEVERITY_TONE, STATUS_TONE } from '../../components/SupportTicketDialog';
import { countryLabel } from '../../components/localeData';
import { isTrialExpired } from '../../components/provisionValidation';
import { commercialConfigurationState } from '../../components/commercialTermsValidation';
import { platformApi } from '../../api/client';
import { platformErrorMessage } from '../../api/apiError';
import { platformKeys } from '../../api/queryKeys';
import type { Tenant, TenantOffboardingStatus } from '../../types';

const orDash = (value: string | number | null | undefined) =>
  value === null || value === undefined || String(value).trim().length === 0 ? <Dash /> : String(value);

interface Props {
  tenant: Tenant;
  /** Read once by the page shell so the banners and the Lifecycle tab agree. */
  offboarding: TenantOffboardingStatus | undefined;
  /** True when the offboarding read FAILED, which is not the same as a tenant with no clock. */
  offboardingUnavailable?: boolean;
  onOpenTab: (tab: string) => void;
  canViewSupport: boolean;
}

/**
 * The one screen that answers "what is going on with this customer".
 *
 * <p>Three conditions lead the page rather than sitting as a row among thirty, because
 * each is money or data leaving the building unnoticed: a trial that has expired and is
 * still being served, commercial terms nobody set, and a deletion whose clock is running.</p>
 */
export default function OverviewTab({ tenant, offboarding, offboardingUnavailable, onOpenTab, canViewSupport }: Props) {
  const navigate = useNavigate();

  const summaryQuery = useQuery({
    queryKey: platformKeys.tenantOperations(tenant.id),
    queryFn: () => platformApi.getTenantOperationsSummary(tenant.id),
  });

  const trialExpired = isTrialExpired(tenant.billingMode, tenant.trialEndsOn);
  const commercialState = commercialConfigurationState(tenant);

  const summary = summaryQuery.data;

  return (
    <Stack spacing={2.5}>
      {trialExpired && (
        <Alert severity="error" icon={<WarningIcon />} sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>
            This trial ended {tenant.trialEndsOn ? fmtDate(tenant.trialEndsOn) : 'in the past'}
          </AlertTitle>
          The workspace is still being served for free. Convert it to a billable plan on the Commercial tab, or
          suspend it on Lifecycle.
        </Alert>
      )}

      {commercialState !== 'complete' && (
        <Alert
          severity="error"
          icon={<WarningIcon />}
          sx={{ borderRadius: 2 }}
          action={
            <Button color="inherit" size="small" onClick={() => onOpenTab('commercial')} sx={{ fontWeight: 700 }}>
              Fix it
            </Button>
          }
        >
          <AlertTitle sx={{ fontWeight: 800 }}>Commercial configuration required</AlertTitle>
          {commercialState === 'plan-missing'
            ? 'This tenant is Billable or on Trial with no plan, so it is priced by nobody and charged for nothing.'
            : 'This tenant is not billable and no reason is recorded — free service nobody signed for.'}
        </Alert>
      )}

      {/* Absence of a banner used to mean two different things: "no deletion is scheduled" and
          "we could not find out". A tenant with a running purge clock therefore looked entirely
          normal whenever the offboarding read failed. */}
      {offboardingUnavailable && (
        <Alert severity="warning" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>Offboarding state unknown</AlertTitle>
          The retention and purge record for this tenant could not be read, so this page cannot
          tell you whether a deletion is scheduled. Open the Lifecycle tab before acting on it.
        </Alert>
      )}

      {offboarding?.stage === 'PendingDeletion' && (
        <Alert
          severity="warning"
          icon={<WarningIcon />}
          sx={{ borderRadius: 2 }}
          action={
            <Button color="inherit" size="small" onClick={() => onOpenTab('lifecycle')} sx={{ fontWeight: 700 }}>
              Review
            </Button>
          }
        >
          <AlertTitle sx={{ fontWeight: 800 }}>Deletion scheduled</AlertTitle>
          {offboarding.isPurgeEligible
            ? 'The retention window has elapsed. This customer can be purged now.'
            : `${offboarding.daysUntilPurgeEligible ?? '—'} day(s) remain before a purge is permitted${offboarding.purgeEligibleOn ? ` (${fmtDate(offboarding.purgeEligibleOn)})` : ''}.`}{' '}
          {offboarding.deletionReason ?? ''}
        </Alert>
      )}

      {offboarding?.stage === 'Purged' && (
        <Alert severity="error" sx={{ borderRadius: 2 }}>
          <AlertTitle sx={{ fontWeight: 800 }}>This tenant has been purged</AlertTitle>
          What you are looking at is a tombstone. The business records were destroyed
          {offboarding.purgedOn ? ` on ${fmtDate(offboarding.purgedOn)}` : ''}.
        </Alert>
      )}

      {summaryQuery.isLoading ? (
        <TilesSkeleton count={4} />
      ) : summaryQuery.isError || !summary ? (
        <ErrorState
          message={platformErrorMessage(summaryQuery.error, 'The operations summary could not be read.')}
          onRetry={() => summaryQuery.refetch()}
          minHeight={200}
        />
      ) : (
        <>
          <Box sx={{ display: 'grid', gridTemplateColumns: { xs: '1fr', sm: '1fr 1fr', lg: 'repeat(4, 1fr)' }, gap: 2.5 }}>
            <StatTile
              label="Open tickets"
              value={fmtNumber(summary.support.openTicketCount)}
              icon={<TicketIcon />}
              color="#6366f1"
              caption={
                summary.support.unassignedOpenTicketCount > 0
                  ? `${summary.support.unassignedOpenTicketCount} unassigned`
                  : 'all assigned'
              }
            />
            <StatTile
              label="Oldest open ticket"
              value={
                summary.support.oldestOpenTicketCreatedAtUtc
                  ? fmtRelative(summary.support.oldestOpenTicketCreatedAtUtc)
                  : '—'
              }
              icon={<TicketIcon />}
              color="#f59e0b"
              caption="the one that embarrasses the desk"
            />
            <StatTile
              label="Privileged actions (30d)"
              value={fmtNumber(summary.audit.entryCountLast30Days)}
              icon={<AuditIcon />}
              color="#0ea5e9"
              caption={
                summary.audit.failureCountLast30Days > 0
                  ? `${fmtNumber(summary.audit.failureCountLast30Days)} failed`
                  : 'no failures'
              }
            />
            <StatTile
              label="Impersonation sessions"
              value={fmtNumber(summary.impersonation.activeSessionCount)}
              icon={<ImpersonationIcon />}
              color={summary.impersonation.activeSessionCount > 0 ? '#ef4444' : '#10b981'}
              caption={`${fmtNumber(summary.impersonation.sessionCountLast30Days)} in the last 30 days`}
            />
          </Box>

          <Grid container spacing={2.5}>
            <Grid size={{ xs: 12, lg: 7 }}>
              <PageSection
                title="Recent tickets"
                actions={
                  canViewSupport ? (
                    <Button size="small" onClick={() => onOpenTab('support')} sx={{ fontWeight: 700 }}>
                      Open the desk
                    </Button>
                  ) : undefined
                }
              >
                {summary.support.recentTickets.length === 0 ? (
                  summary.support.openTicketCount > 0 && !canViewSupport ? (
                    <EmptyState
                      title="Ticket details restricted"
                      message="Your role can see ticket counts, but customer support content requires tenant administration access."
                      minHeight={140}
                    />
                  ) : summary.support.openTicketCount > 0 ? (
                    // "Never raised one" was shown even with a non-zero open-ticket count on the
                    // stat tile directly above it — the page contradicting itself on the same screen.
                    <EmptyState
                      title="Open tickets are not in this list"
                      message={`This customer has ${fmtNumber(summary.support.openTicketCount)} open ticket(s), none of them recent enough to appear here. Open the desk to see them.`}
                      minHeight={140}
                    />
                  ) : (
                    <EmptyState title="No tickets" message="This customer has never raised one." minHeight={140} />
                  )
                ) : (
                  <Box sx={{ overflowX: 'auto' }}>
                    <Table size="small">
                      <TableBody>
                        {summary.support.recentTickets.slice(0, 6).map((ticket) => (
                          <TableRow key={ticket.id} hover>
                            <TableCell width={90}>
                              <SoftChip
                                label={ticket.severity}
                                tone={SEVERITY_TONE[ticket.severity] ?? 'neutral'}
                                dot={false}
                              />
                            </TableCell>
                            <TableCell>
                              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                                #{ticket.id} {ticket.subject}
                              </Typography>
                            </TableCell>
                            <TableCell width={110}>
                              <SoftChip label={ticket.status} tone={STATUS_TONE[ticket.status] ?? 'neutral'} />
                            </TableCell>
                            <TableCell width={110}>
                              <Typography variant="caption" color="text.secondary">
                                {fmtRelative(ticket.updatedAtUtc)}
                              </Typography>
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </Box>
                )}
              </PageSection>

              <PageSection
                title="Recent privileged activity"
                sx={{ mt: 2.5 }}
                actions={
                  <Button size="small" onClick={() => onOpenTab('audit')} sx={{ fontWeight: 700 }}>
                    Open the explorer
                  </Button>
                }
              >
                {summary.audit.recentEntries.length === 0 ? (
                  <EmptyState title="Nothing recorded" message="No privileged action has touched this tenant." minHeight={140} />
                ) : (
                  <Box sx={{ overflowX: 'auto' }}>
                    <Table size="small">
                      <TableBody>
                        {summary.audit.recentEntries.slice(0, 8).map((entry) => (
                          <TableRow key={entry.id} hover>
                            <TableCell>
                              <Box component="code" sx={{ fontSize: 12.5, fontWeight: 700 }}>
                                {entry.action}
                              </Box>
                            </TableCell>
                            <TableCell>
                              <Typography variant="caption" color="text.secondary">
                                {entry.actor}
                              </Typography>
                            </TableCell>
                            <TableCell width={110}>
                              <SoftChip
                                label={entry.result}
                                tone={entry.result === 'success' ? 'success' : 'error'}
                                dot={false}
                              />
                            </TableCell>
                            <TableCell width={150}>
                              <Typography variant="caption" color="text.secondary">
                                {fmtDateTime(entry.occurredAtUtc)}
                              </Typography>
                            </TableCell>
                          </TableRow>
                        ))}
                      </TableBody>
                    </Table>
                  </Box>
                )}
              </PageSection>
            </Grid>

            <Grid size={{ xs: 12, lg: 5 }}>
              <PageSection title="Registry">
                <Stack spacing={1.25}>
                  <Row label="Tenant id">{tenant.id}</Row>
                  <Row label="Slug">{tenant.slug}</Row>
                  <Row label="Business unit">{orDash(summary.lifecycle.primaryBusinessUnitId)}</Row>
                  <Row label="Plan">{orDash(tenant.planCode)}</Row>
                  <Row label="Status reason">{orDash(tenant.statusReason)}</Row>
                  <Row label="Created">{tenant.createdAt ? fmtDateTime(tenant.createdAt) : <Dash />}</Row>
                  <Row label="Last modified">
                    {summary.lifecycle.modifiedOn
                      ? `${fmtDateTime(summary.lifecycle.modifiedOn)} · ${summary.lifecycle.modifiedBy ?? '—'}`
                      : '—'}
                  </Row>
                  <Row label="Legal name">{orDash(tenant.legalName)}</Row>
                  <Row label="Country">{tenant.countryCode ? countryLabel(tenant.countryCode) : <Dash />}</Row>
                  <Row label="Contact">{orDash(tenant.contactEmail)}</Row>
                  <Row label="Base currency">{orDash(tenant.baseCurrencyCode)}</Row>
                  <Row label="Time zone">{orDash(tenant.timeZoneId)}</Row>
                  <Row label="Data region">{orDash(tenant.dataRegion)}</Row>
                </Stack>
              </PageSection>

              <PageSection
                title="Impersonation"
                subtitle="Who has entered this customer's account, and whether they said why."
                sx={{ mt: 2.5 }}
              >
                {summary.impersonation.sessions.length === 0 ? (
                  <EmptyState title="Never impersonated" minHeight={120} />
                ) : (
                  <Stack spacing={1.25}>
                    {summary.impersonation.sessions.slice(0, 5).map((session) => (
                      <Box key={session.jti}>
                        <Stack direction="row" alignItems="center" spacing={1}>
                          <SoftChip
                            label={session.status}
                            tone={session.status === 'active' ? 'error' : session.status === 'revoked' ? 'warning' : 'neutral'}
                          />
                          <Typography variant="body2" sx={{ fontWeight: 700, flex: 1, minWidth: 0 }}>
                            {session.actorEmail ?? `operator ${session.actorPlatformUserId}`}
                          </Typography>
                          <Typography variant="caption" color="text.secondary">
                            {fmtRelative(session.issuedAtUtc)}
                          </Typography>
                        </Stack>
                        <Typography variant="caption" color="text.secondary">
                          {session.reason}
                        </Typography>
                        {session.linkedTicketIds.length === 0 ? (
                          // An empty list is a finding, not a blank: somebody entered a
                          // customer's account without recording which work sent them there.
                          <Typography variant="caption" color="warning.main" sx={{ display: 'block', fontWeight: 700 }}>
                            No ticket linked to this session.
                          </Typography>
                        ) : (
                          <Stack direction="row" spacing={0.5} sx={{ mt: 0.5 }}>
                            {session.linkedTicketIds.map((id) => (
                              <Chip key={id} size="small" label={`#${id}`} sx={{ fontWeight: 700 }} />
                            ))}
                          </Stack>
                        )}
                      </Box>
                    ))}
                  </Stack>
                )}
              </PageSection>

              <PageSection title="Elsewhere" sx={{ mt: 2.5 }}>
                <Stack spacing={1.5}>
                  <Button
                    variant="outlined"
                    onClick={() => navigate(`/platform/pipeline?tenant=${tenant.id}`)}
                    sx={{ fontWeight: 700 }}
                  >
                    Extraction jobs
                  </Button>
                  <Button
                    variant="outlined"
                    onClick={() => navigate(`/platform/audit?tenant=${tenant.id}`)}
                    sx={{ fontWeight: 700 }}
                  >
                    Full audit explorer
                  </Button>
                </Stack>
              </PageSection>
            </Grid>
          </Grid>
        </>
      )}

      {summaryQuery.isFetching && !summaryQuery.isLoading && (
        <LoadingState label="Refreshing…" minHeight={40} />
      )}
    </Stack>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Stack direction={{ xs: 'column', sm: 'row' }} justifyContent="space-between" spacing={1}>
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
      <Box sx={{ fontWeight: 700, fontSize: 14, textAlign: { sm: 'right' }, overflowWrap: 'anywhere' }}>
        {children}
      </Box>
    </Stack>
  );
}
