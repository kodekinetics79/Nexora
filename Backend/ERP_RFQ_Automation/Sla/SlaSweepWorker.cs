using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Periodic SLA sweep (default every 5 minutes). Follows the ExtractionWorker
/// discipline: a fresh DI scope per iteration, every exception logged and
/// swallowed — the loop never dies.
///
/// TENANT SCOPE IS MANDATORY (was not, before). The worker has no HttpContext, so
/// <see cref="TenantRlsCommandInterceptor.ResolveDatabaseRole"/> hands it the
/// BYPASSRLS <c>nexora_pipeline_app</c> role, and with a null tenant the EF global
/// filters (<c>CurrentTenantId == null || ...</c>) are no-ops — BOTH isolation
/// layers off at once. The sweep used to run its entire body that way, holding the
/// boundary together with hand-written BusinessUnitId predicates plus
/// IgnoreQueryFilters(), and one query (the assignee/manager lookup) had no
/// predicate at all. Now: ONE enumeration query resolves the tenant list under the
/// pipeline role, and every subsequent query runs inside a pushed tenant scope, so
/// it executes as <c>nexora_tenant_app</c> under RLS with the EF filters live. The
/// explicit BusinessUnitId predicates are kept as defence in depth.
///
/// Per business unit with any activity it checks:
///  1. Lead bid-closing deadlines  -> warn / critical / overdue alerts
///  2. Accepted-but-unassigned lead aging -> manager alert
///  3. SENT quotes past their validity date, OR 90 days past submission with no
///     customer response (FR-QTM-07) -> IQuoteOutcomeService.ExpireAsync
///  4. SENT quotes gone quiet (stale) -> per-owner daily digest
///  5. Copilot approvals pending too long -> manager escalation
///  6. Supplier orders approaching their committed ship date (FR-SPO-07) -> buyer reminder
///  7. Supplier orders sent but never acknowledged (FR-SPO-07) -> supervisor escalation
///
/// Send-once semantics are enforced by the DATABASE: every alert INSERTs its
/// <see cref="SlaEvent"/> claim BEFORE the email goes out, against the unique
/// (BusinessUnitId, DedupKey) index. On a scaled-out deployment the losing instance
/// takes a 23505 and skips the send instead of mailing the customer twice. A claim
/// whose send then fails is released so the next sweep retries it.
/// </summary>
public sealed class SlaSweepWorker : BackgroundService
{
    /// <summary>Sweep period. Overridable for tests.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    private static readonly DateTime EarliestCommercialDeadline = new(2000, 1, 1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly IBackgroundWorkerHeartbeats? _heartbeats;
    private readonly ILogger<SlaSweepWorker> _log;

    public SlaSweepWorker(
        IServiceScopeFactory scopeFactory,
        ITenantScopeAccessor tenantScope,
        ILogger<SlaSweepWorker> log,
        IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _log = log;
        _heartbeats = heartbeats;
        _heartbeats?.Register(BackgroundWorkerNames.SlaSweep, Period);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SlaSweepWorker starting; period {Period}.", Period);
        _heartbeats?.Beat(BackgroundWorkerNames.SlaSweep, Period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The sweep must never die on an unexpected error (e.g. transient DB issue).
                _log.LogError(ex, "SLA sweep iteration failed; will retry next period.");
            }

            // Beat AFTER the iteration, failed or not: the heartbeat asserts the loop is
            // alive, and a loop that keeps failing is caught by the error logs, not by
            // flipping /ready red on one transient DB blip.
            _heartbeats?.Beat(BackgroundWorkerNames.SlaSweep, Period);

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("SlaSweepWorker stopped.");
    }

    /// <summary>
    /// Resolves the tenant list once, then does ALL per-tenant work inside a pushed
    /// tenant scope. Returns the number of business units swept.
    /// </summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var businessUnits = await ResolveActiveBusinessUnitsAsync(ct);

        foreach (var bu in businessUnits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SweepTenantAsync(bu, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One misbehaving tenant must never block the others.
                _log.LogError(ex, "SLA sweep failed for BU {Bu}; continuing with next tenant.", bu);
            }
        }

        return businessUnits.Count;
    }

    /// <summary>
    /// The ONLY query in this worker that runs without a tenant scope (and therefore
    /// under the BYPASSRLS pipeline role): the distinct BUs seen on Leads or Quotes.
    /// It reads nothing but tenant ids.
    ///
    /// <para>Suspended and archived tenants are dropped HERE, inside this scope, for two reasons.
    /// A suspended tenant's staff cannot sign in, so an escalation telling them a bid closes
    /// tomorrow reaches an inbox nobody can act from, and an auto-expiry silently changes the state
    /// of quotes they cannot see. And this is the one scope in the worker with no tenant pushed,
    /// which is a hard requirement of the gate: consulted under a pushed scope its platform read is
    /// refused at column level and fails OPEN, admitting everyone silently
    /// (see <c>ITenantWorkGate</c>).</para>
    ///
    /// <para>Skipping is safe for the ALERTS — they are derived from dates and re-evaluated from
    /// scratch every sweep, so a suspension is a gap in notification, not a lost record. It DEFERS
    /// the state transitions (quote auto-expiry, approval ageing), which are likewise computed from
    /// <c>BidClosingDate</c> and quote timestamps, so the first sweep after reinstatement applies
    /// whatever the dates say by then. Nothing here is a one-shot side effect that a skipped cycle
    /// destroys.</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveActiveBusinessUnitsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var leadBus = await db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Select(l => l.BusinessUnitId).Distinct().ToListAsync(ct);
        var quoteBus = await db.Quotes.AsNoTracking().IgnoreQueryFilters()
            .Select(q => q.BusinessUnitId).Distinct().ToListAsync(ct);
        // FR-SPO-07: procurement is an activity source in its own right. Without this a
        // tenant whose supplier orders were raised against RFQs that never became Leads or
        // Quotes would be enumerated as "no activity", and its ship-date reminders and
        // acknowledgement escalations would never run at all.
        var procurementBus = await db.SupplierPurchaseOrders.AsNoTracking().IgnoreQueryFilters()
            .Select(o => o.BusinessUnitId).Distinct().ToListAsync(ct);
        var businessUnits = leadBus.Union(quoteBus).Union(procurementBus).OrderBy(id => id).ToList();

        // Resolved from THIS scope rather than injected: the worker is a singleton and the gate is
        // scoped, so a constructor dependency would fail startup scope validation.
        var gate = scope.ServiceProvider
            .GetService<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate>();
        if (gate is null || businessUnits.Count == 0) return businessUnits;

        return await gate.FilterServiceableAsync(businessUnits, ct);
    }

    private async Task SweepTenantAsync(long bu, CancellationToken ct)
    {
        using var tenant = _tenantScope.Push(bu);
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<ISlaNotifications>();
        var outcomes = scope.ServiceProvider.GetRequiredService<IQuoteOutcomeService>();

        // Fail closed. If the DbContext did not pick the pushed scope up, every query
        // below would silently run cross-tenant under the bypass role again.
        if (db.ScopedTenantId != bu)
        {
            throw new InvalidOperationException(
                $"SLA sweep refused to run for BU {bu}: the DbContext resolved tenant " +
                $"{db.ScopedTenantId?.ToString() ?? "<none>"}. Tenant scope is mandatory for this worker.");
        }

        var policy = await db.Set<SlaPolicy>().AsNoTracking()
                         .FirstOrDefaultAsync(p => p.BusinessUnitId == bu, ct)
                     ?? SlaPolicy.Default(bu);

        await SweepLeadDeadlinesAsync(db, notifications, bu, policy, ct);
        await SweepUnassignedLeadsAsync(db, notifications, bu, policy, ct);
        await SweepQuoteAutoExpiryAsync(db, outcomes, bu, policy, ct);
        await SweepStaleQuotesAsync(db, notifications, bu, policy, ct);
        await SweepPendingApprovalsAsync(db, notifications, bu, policy, ct);
        await SweepSupplierShipDatesAsync(db, notifications, bu, policy, ct);
        await SweepSupplierAcknowledgementsAsync(db, notifications, bu, policy, ct);
    }

    // ---------------- 1. lead deadlines ----------------

    private async Task SweepLeadDeadlinesAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var horizon = now.AddDays(policy.WarnDaysBeforeClose);

        // Real dates only (sentinels < 2000 are "unknown", mirroring the extraction
        // pipeline's SanitizeDate); open leads = status null or 24 (accepted), never
        // rejected (25 carries a rejected-reason).
        var leads = await OpenLeadDeadlineCandidates(db, bu, horizon)
            .Select(l => new { l.Id, l.Rfqno, l.BidClosingDate, l.AssignTo })
            .ToListAsync(ct);
        if (leads.Count == 0) return;

        // Assignees + their managers, one batch. Explicitly BU-scoped: this query used
        // to have NO business-unit predicate and ran under the bypass role.
        var assigneeIds = leads.Where(l => l.AssignTo.HasValue).Select(l => l.AssignTo!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => assigneeIds.Contains(u.Id) && (u.Buid == null || u.Buid == bu))
            .Select(u => new { u.Id, u.Email, u.FirstName, u.ManagerId })
            .ToListAsync(ct);
        var managerIds = users.Where(u => u.ManagerId.HasValue).Select(u => u.ManagerId!.Value).Distinct().ToList();
        var managers = await db.Users.AsNoTracking()
            .Where(u => managerIds.Contains(u.Id) && (u.Buid == null || u.Buid == bu))
            .Select(u => new { u.Id, u.Email, u.FirstName })
            .ToListAsync(ct);

        foreach (var lead in leads)
        {
            var close = lead.BidClosingDate!.Value;
            var level = close < now ? "overdue"
                : close <= now.AddDays(policy.CriticalDaysBeforeClose) ? "critical"
                : "warn";

            // Alerts need a human to act on them: unassigned leads are covered by
            // the unassigned-aging sweep instead.
            if (!lead.AssignTo.HasValue) continue;
            var assignee = users.FirstOrDefault(u => u.Id == lead.AssignTo.Value);
            if (assignee is null || string.IsNullOrWhiteSpace(assignee.Email)) continue;

            // Claim BEFORE sending: on a scaled-out deployment only one instance wins.
            var claim = await TryClaimEventAsync(db, bu, "lead", lead.Id, level, null, ct);
            if (claim is null) continue;

            var label = string.IsNullOrWhiteSpace(lead.Rfqno) ? $"Lead #{lead.Id}" : $"RFQ {lead.Rfqno}";
            var daysLeft = (int)Math.Ceiling((close - now).TotalDays);
            var (headline, detail) = level switch
            {
                "overdue" => ($"Bid deadline missed — {label}",
                    $"The bid for {label} closed on {close:dd MMM yyyy} and no submission has been recorded. Please review it as soon as possible."),
                "critical" => ($"Bid closes in about {Math.Max(daysLeft, 0)} day(s) — {label}",
                    $"The bid for {label} closes on {close:dd MMM yyyy}. This is the final reminder before the deadline."),
                _ => ($"Bid closes on {close:dd MMM yyyy} — {label}",
                    $"A heads-up that the bid for {label} closes in about {daysLeft} day(s). Make sure the response is on track.")
            };

            var delivered = await SendOrReleaseAsync(db, claim, ct, () =>
                notifications.SendDeadlineAlertAsync(
                    assignee.Email, assignee.FirstName, level, label, headline, detail, bu, ct));
            if (!delivered) continue;

            if (level == "overdue" && assignee.ManagerId.HasValue)
            {
                var manager = managers.FirstOrDefault(m => m.Id == assignee.ManagerId.Value);
                if (manager is not null && !string.IsNullOrWhiteSpace(manager.Email))
                {
                    await notifications.SendDeadlineAlertAsync(manager.Email, manager.FirstName, level, label,
                        $"Bid deadline missed by {assignee.FirstName} — {label}",
                        $"The bid for {label}, assigned to {assignee.FirstName} ({assignee.Email}), closed on {close:dd MMM yyyy} without a recorded submission.",
                        bu, ct);
                }
            }
        }
    }

    internal static IQueryable<Lead> OpenLeadDeadlineCandidates(
        ErpRfqAutomationContext db, long businessUnitId, DateTime horizon)
        => db.Leads.AsNoTracking()
            .Where(lead => lead.BusinessUnitId == businessUnitId
                && lead.BidClosingDate != null
                && lead.BidClosingDate >= EarliestCommercialDeadline
                && lead.BidClosingDate <= horizon
                && (lead.LeadStatusId == null || lead.LeadStatus != null
                    && (lead.LeadStatus.SetupCode == "QUALIFIED"
                        || lead.LeadStatus.SetupValue == "Accepted"
                        || lead.LeadStatus.SetupValue == "Qualified"))
                && lead.LeadRejectedReasonId == null);

    // ---------------- 2. unassigned aging ----------------

    private async Task SweepUnassignedLeadsAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-policy.UnassignedHours);

        // Accepted (24) but never assigned, older than the allowance. Age is
        // measured from acceptance when known (ModifiedDate) else creation.
        var leads = await db.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == bu
                        && l.LeadStatus != null
                        && (l.LeadStatus.SetupCode == "QUALIFIED" || l.LeadStatus.SetupValue == "Accepted" || l.LeadStatus.SetupValue == "Qualified")
                        && l.AssignTo == null
                        && (l.ModifiedDate ?? l.CreatedDate) < cutoff)
            .Select(l => new { l.Id, l.Rfqno })
            .ToListAsync(ct);
        if (leads.Count == 0) return;

        var recipients = await GetManagersAndAdminsAsync(db, bu, ct);
        if (recipients.Count == 0)
        {
            _log.LogWarning("BU {Bu}: {Count} unassigned lead(s) past the SLA but no manager/admin user found to notify.", bu, leads.Count);
            return;
        }

        foreach (var lead in leads)
        {
            // Once per lead ever; distinct EntityType keeps this separate from the
            // deadline "warn" for the same lead (documented in SLA-WIRING.md).
            var claim = await TryClaimEventAsync(db, bu, "lead-unassigned", lead.Id, "warn", null, ct);
            if (claim is null) continue;

            var label = string.IsNullOrWhiteSpace(lead.Rfqno) ? $"Lead #{lead.Id}" : $"RFQ {lead.Rfqno}";
            await SendOrReleaseAsync(db, claim, ct, async () =>
            {
                var anyDelivered = false;
                foreach (var r in recipients)
                {
                    anyDelivered |= await notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "warn", label,
                        $"Lead waiting for an owner — {label}",
                        $"{label} was accepted more than {policy.UnassignedHours} hour(s) ago but nobody has been assigned to it yet. Please assign an owner so it doesn't slip.",
                        bu, ct);
                }
                return anyDelivered;
            });
        }
    }

    // ---------------- 3. quote auto-expiry (FR-QTM-07) ----------------

    /// <summary>
    /// FR-QTM-07 auto-expiry candidates for one tenant: SENT quotes hit by EITHER trigger.
    ///
    /// <para>TRIGGER 1 — the validity date has passed (plus the tenant's grace allowance,
    /// which is 0 by default). This used to read <c>coalesce(ValidUntil, SentOn) + 14
    /// days</c>, so a quote survived a full fortnight PAST the date it told the customer
    /// it was good until.</para>
    ///
    /// <para>TRIGGER 2 — <c>QuoteNoResponseExpiryDays</c> (90) have elapsed since
    /// submission and NO customer response is recorded. This rule did not exist at all:
    /// <c>StaleQuoteDays</c> only ever sent a nudge email. It is what expires a quote that
    /// was sent without a validity date, which trigger 1 can never see.</para>
    ///
    /// <para>A quote whose customer HAS responded is out of scope for trigger 2 — the
    /// requirement's "when no customer response is recorded" is a precondition of that
    /// trigger, not of the validity rule. Trigger 1 stays unconditional: an expired
    /// validity date is expired whatever the customer said.</para>
    ///
    /// <para>Sentinel validity dates (year &lt; 2000, the extraction pipeline's "unknown"
    /// convention, mirrored from <see cref="OpenLeadDeadlineCandidates"/>) are ignored by
    /// trigger 1 so an unparsed date cannot expire a live quote on the spot; such quotes
    /// still fall to trigger 2.</para>
    ///
    /// <para>DECISION REGISTER R7 — an explicitly EXTENDED validity suppresses trigger 2 for as
    /// long as the extended date is still in the future. R7 rules that validity is the user's
    /// and that "automatic expiry still runs against the <i>current</i> validity, so it stops
    /// fighting the user". Gulf tender bid validity is set by the buyer, routinely 90–120 days
    /// and extendable on request: without this clause a rep who reasoned and recorded a hold to
    /// day 120 would still have the bid auto-expired underneath them on day 90, and the live
    /// Etimad pipeline would vanish from the dashboard. Trigger 1 is unaffected and still
    /// expires the quote the moment the EXTENDED date passes, so nothing becomes immortal — and
    /// a validity that was merely set at draft time and never explicitly extended is NOT
    /// covered, so the 90-day silence rule keeps its original reach.</para>
    ///
    /// Exposed as a queryable so both trigger boundaries are testable without a worker host.
    /// </summary>
    internal static IQueryable<Quote> ExpiryCandidates(
        ErpRfqAutomationContext db, long businessUnitId, List<long> sentStatusIds,
        SlaPolicy policy, DateTime now)
    {
        // Hoisted so EF parameterises the day counts instead of re-reading the policy
        // object inside the expression tree.
        var graceDays = policy.QuoteExpiryGraceDays;
        var noResponseDays = policy.QuoteNoResponseExpiryDays;

        return db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == businessUnitId
                        && q.StatusId != null && sentStatusIds.Contains(q.StatusId.Value)
                        && ((q.ValidUntil != null
                             && q.ValidUntil >= EarliestCommercialDeadline
                             && q.ValidUntil.Value.AddDays(graceDays) < now)
                            || (q.SentOn != null
                                && q.RespondedOn == null
                                && q.SentOn.Value.AddDays(noResponseDays) < now
                                && !(q.ValidityExtendedOn != null
                                     && q.ValidUntil != null
                                     && q.ValidUntil >= EarliestCommercialDeadline
                                     && q.ValidUntil.Value.AddDays(graceDays) >= now))));
    }

    private async Task SweepQuoteAutoExpiryAsync(
        ErpRfqAutomationContext db, IQuoteOutcomeService outcomes, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sentStatusIds = await GetStatusIdsAsync(db, "SENT", ct);

        // ONE candidate set for BOTH triggers, and therefore ONE claim per quote below:
        // a quote that trips both rules in the same sweep is still expired exactly once,
        // and a quote already expired by an earlier sweep is never re-claimed.
        var candidates = await ExpiryCandidates(db, bu, sentStatusIds, policy, now)
            .Select(q => q.Id)
            .ToListAsync(ct);

        foreach (var quoteId in candidates)
        {
            // Claim first so two instances cannot both drive ExpireAsync for the same quote.
            var claim = await TryClaimEventAsync(db, bu, "quote", quoteId, "expired", null, ct);
            if (claim is null) continue;

            var expired = false;
            try
            {
                expired = await outcomes.ExpireAsync(quoteId, "AUTO_EXPIRED", ct);
            }
            catch
            {
                await ReleaseEventClaimAsync(db, claim, ct);
                throw;
            }

            if (expired)
                _log.LogInformation("BU {Bu}: quote {QuoteId} auto-expired by SLA sweep.", bu, quoteId);
            else
                await ReleaseEventClaimAsync(db, claim, ct); // resolved by a human, or superseded
        }
    }

    // ---------------- 4. stale quotes (daily per-owner digest) ----------------

    private async Task SweepStaleQuotesAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sentStatusIds = await GetStatusIdsAsync(db, "SENT", ct);

        var stale = await db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == bu
                        && q.StatusId != null && sentStatusIds.Contains(q.StatusId.Value)
                        && q.SentOn != null
                        && q.RespondedOn == null
                        && q.SentOn.Value.AddDays(policy.StaleQuoteDays) < now)
            .Select(q => new { q.Id, q.QuoteNo, q.SentOn, q.CreatedBy, CustomerName = q.Customer != null ? q.Customer.Name : null })
            .ToListAsync(ct);
        if (stale.Count == 0) return;

        // Owner resolution: Quote.CreatedBy is a free-text identity. Match a BU user
        // by email first, then by "First Last" display name; unresolvable owners are
        // logged and skipped (no guessable recipient).
        var buUsers = await db.Users.AsNoTracking()
            .Where(u => u.Buid == bu && u.IsActive != false)
            .Select(u => new { u.Id, u.Email, u.FirstName, u.LastName })
            .ToListAsync(ct);

        var byOwner = stale
            .Select(q => new
            {
                Quote = q,
                Owner = buUsers.FirstOrDefault(u =>
                    string.Equals(u.Email, q.CreatedBy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals($"{u.FirstName} {u.LastName}", q.CreatedBy, StringComparison.OrdinalIgnoreCase))
            })
            .Where(x => x.Owner != null)
            .GroupBy(x => x.Owner!.Id);

        var todayUtc = now.Date;
        foreach (var group in byOwner)
        {
            var owner = buUsers.First(u => u.Id == group.Key);

            // Daily dedup: one digest per (owner, UTC day). The day is part of the
            // unique DedupKey, so the claim itself is the dedup — no read-then-write race.
            var claim = await TryClaimEventAsync(
                db, bu, "quote-stale-digest", owner.Id, "stale", todayUtc, ct);
            if (claim is null) continue;

            var lines = group.Select(x => new StaleQuoteDigestLine
            {
                QuoteNo = x.Quote.QuoteNo,
                CustomerName = x.Quote.CustomerName,
                SentOn = x.Quote.SentOn,
                DaysWaiting = SlaComputed.DaysSinceSent(x.Quote.SentOn, now) ?? 0
            }).OrderByDescending(l => l.DaysWaiting).ToList();

            await SendOrReleaseAsync(db, claim, ct, () =>
                notifications.SendStaleQuotesDigestAsync(owner.Email, owner.FirstName, lines, bu, ct));
        }

        var unresolved = stale.Count(q => !buUsers.Any(u =>
            string.Equals(u.Email, q.CreatedBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals($"{u.FirstName} {u.LastName}", q.CreatedBy, StringComparison.OrdinalIgnoreCase)));
        if (unresolved > 0)
            _log.LogWarning("BU {Bu}: {Count} stale quote(s) whose CreatedBy did not match a user; digest skipped for those.", bu, unresolved);
    }

    // ---------------- 5. pending approval escalation (WP-B3 escalation clock) ----------------

    /// <summary>
    /// Escalates approvals still pending at
    ///   min(CreatedOn + ApprovalEscalationHours, BidClosingDate − DeadlineBufferHours)
    /// — the deadline term applies to holds that resolve to an RFQ with a real
    /// bid-closing date (below-floor quote holds); approvals with no resolvable
    /// deadline use the plain age rule, exactly as before. The requester's own
    /// manager (Users.ManagerId) is notified first; when no manager can be
    /// resolved the tenant's managers/admins are the fallback audience. Send-once
    /// via the (BU, "approval", guid-key, "escalated") SlaEvent claim — the below-floor
    /// guard stamps the same event when it escalates at creation time (deadline
    /// already inside the buffer), so the two paths never double-notify.
    /// </summary>
    private async Task SweepPendingApprovalsAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var pending = await db.Set<AgentApproval>().AsNoTracking()
            .Where(a => a.BusinessUnitId == bu && a.Status == AgentApprovalStatus.Pending)
            .Select(a => new { a.Id, a.ToolName, a.Summary, a.CreatedOn, a.RequestedByUserId, a.RequestedBy, a.InputJson })
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        List<Recipient>? fallbackRecipients = null; // lazy — only fetched when needed

        foreach (var approval in pending)
        {
            // Escalation moment: age rule, pulled earlier by the RFQ deadline buffer
            // when the hold carries one.
            var escalateAt = approval.CreatedOn.AddHours(policy.ApprovalEscalationHours);
            var deadline = await ResolveApprovalDeadlineAsync(db, bu, approval.ToolName, approval.InputJson, ct);
            if (deadline.HasValue)
            {
                var bufferEdge = deadline.Value.AddHours(-policy.DeadlineBufferHours);
                if (bufferEdge < escalateAt) escalateAt = bufferEdge;
            }
            if (now < escalateAt) continue;

            // SlaEvent.EntityId is a bigint; approval ids are Guids, so a stable
            // 64-bit key is derived from the Guid's first 8 bytes (dedup only —
            // never used to look the approval back up).
            var entityKey = BitConverter.ToInt64(approval.Id.ToByteArray(), 0);

            // Requester's manager first (Users.ManagerId); managers/admins as fallback.
            var recipients = await ResolveRequesterManagerAsync(db, bu, approval.RequestedByUserId, approval.RequestedBy, ct);
            if (recipients.Count == 0)
            {
                fallbackRecipients ??= await GetManagersAndAdminsAsync(db, bu, ct);
                recipients = fallbackRecipients;
            }
            if (recipients.Count == 0) continue; // nobody to tell — retry next sweep

            var claim = await TryClaimEventAsync(db, bu, "approval", entityKey, "escalated", null, ct);
            if (claim is null) continue;

            var label = $"Copilot approval: {approval.ToolName}";
            var deadlineNote = deadline.HasValue
                ? $" The linked RFQ's bid closes on {deadline.Value:dd MMM yyyy HH:mm} UTC, inside the {policy.DeadlineBufferHours}-hour safety buffer."
                : $" It has been pending for more than {policy.ApprovalEscalationHours} hour(s).";

            await SendOrReleaseAsync(db, claim, ct, async () =>
            {
                var anyDelivered = false;
                foreach (var r in recipients)
                {
                    anyDelivered |= await notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "escalated", label,
                        "A copilot action has been waiting for approval",
                        $"\"{approval.Summary ?? approval.ToolName}\" (requested by {approval.RequestedBy ?? "unknown"}) has been pending since " +
                        $"{approval.CreatedOn:dd MMM yyyy HH:mm} UTC.{deadlineNote} Please approve or reject it.",
                        bu, ct);
                }
                return anyDelivered;
            });
        }
    }

    /// <summary>
    /// The external deadline behind an approval, when one exists: below-floor
    /// holds (WP-B3) carry rfqId/quoteId in their InputJson → RFQ.BidClosingDate
    /// (real dates only, year ≥ 2000 — sentinel convention). Any parse/lookup
    /// problem simply means "no deadline".
    /// </summary>
    private async Task<DateTime?> ResolveApprovalDeadlineAsync(
        ErpRfqAutomationContext db, long bu, string toolName, string? inputJson, CancellationToken ct)
    {
        if (toolName != "approve_below_floor_quote" || string.IsNullOrWhiteSpace(inputJson)) return null;

        try
        {
            long? rfqId = null;
            using (var doc = System.Text.Json.JsonDocument.Parse(inputJson))
            {
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

                if (root.TryGetProperty("rfqId", out var rfqEl) && rfqEl.TryGetInt64(out var rid))
                    rfqId = rid;
                else if (root.TryGetProperty("quoteId", out var quoteEl) && quoteEl.TryGetInt64(out var qid))
                {
                    rfqId = await db.Quotes.AsNoTracking()
                        .Where(q => q.Id == qid && q.BusinessUnitId == bu)
                        .Select(q => q.Rfqid)
                        .FirstOrDefaultAsync(ct);
                }
            }
            if (rfqId is null) return null;

            var bidClosing = await db.Rfqs.AsNoTracking()
                .Where(r => r.Id == rfqId.Value && r.BusinessUnitId == bu)
                .Select(r => r.BidClosingDate)
                .FirstOrDefaultAsync(ct);

            return bidClosing.HasValue && bidClosing.Value.Year >= 2000 ? bidClosing : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "BU {Bu}: could not resolve a deadline for a pending approval; using the age rule.", bu);
            return null;
        }
    }

    /// <summary>The requester's manager (via Users.ManagerId), as a 0/1-element recipient list.</summary>
    private static async Task<List<Recipient>> ResolveRequesterManagerAsync(
        ErpRfqAutomationContext db, long bu, long? requestedByUserId, string? requestedBy, CancellationToken ct)
    {
        // Requester by id first, then by email (AgentToolContext.UserName is the email claim).
        var requester = await db.Users.AsNoTracking()
            .Where(u => (requestedByUserId != null && u.Id == requestedByUserId)
                        || (requestedBy != null && u.Email == requestedBy))
            .Where(u => u.Buid == null || u.Buid == bu)
            .Select(u => new { u.ManagerId })
            .FirstOrDefaultAsync(ct);
        if (requester?.ManagerId is null) return new List<Recipient>();

        var manager = await db.Users.AsNoTracking()
            .Where(u => u.Id == requester.ManagerId.Value && u.IsActive != false
                        && (u.Buid == null || u.Buid == bu))
            .Select(u => new { u.Id, u.Email, u.FirstName })
            .FirstOrDefaultAsync(ct);

        return manager is null || string.IsNullOrWhiteSpace(manager.Email)
            ? new List<Recipient>()
            : new List<Recipient> { new(manager.Id, manager.Email, manager.FirstName) };
    }

    // ---------------- 6/7. supplier order chasing (FR-SPO-07) ----------------
    //
    // Both candidate sets are isolated as static IQueryables, like ExpiryCandidates above,
    // so each boundary is testable without standing up a worker host — and so a schema move
    // touches two queries rather than the claim/notify/business-day logic below.

    /// <summary>Statuses at which a shipment reminder is pointless — the goods have moved,
    /// or the order is dead. Everything else still has a ship date worth chasing.</summary>
    private static readonly string[] ShipmentSettledStatuses =
    {
        SupplierPurchaseOrderStatuses.Shipped,
        SupplierPurchaseOrderStatuses.PartiallyReceived,
        SupplierPurchaseOrderStatuses.Received,
        SupplierPurchaseOrderStatuses.Closed,
        SupplierPurchaseOrderStatuses.Cancelled
    };

    /// <summary>Statuses meaning "this order is with the supplier, awaiting their answer".
    /// ISSUED is included because it is the legacy word that conflated Approved and Sent
    /// (see <see cref="SupplierPurchaseOrderStatuses"/>); excluding it would silently exempt
    /// every order raised before the vocabulary was split.</summary>
    private static readonly string[] WithSupplierStatuses =
    {
        SupplierPurchaseOrderStatuses.Sent,
        SupplierPurchaseOrderStatuses.Issued
    };

    /// <summary>
    /// FR-SPO-07 reminder candidates: supplier orders whose committed ship date falls in
    /// [today, horizon], where horizon is the date <c>SupplierShipDateReminderDays</c>
    /// WORKING days out (<see cref="BusinessCalendar.AddBusinessDays"/>). Expressing the
    /// window as a pre-computed date keeps the business-day rule out of SQL while still
    /// letting the database do the filtering on an indexable range.
    ///
    /// <para>"Before it passes" is the <c>&gt;= today</c> term: this sweep is a heads-up, not
    /// a lateness alert. An order whose ship date has already gone by is a different
    /// requirement (delivery performance) and is deliberately not claimed here — claiming it
    /// would burn the send-once key and silently suppress the real overdue alert when one is
    /// built.</para>
    ///
    /// <para>Shipped/received/closed/cancelled orders are excluded because there is nothing
    /// left to chase, and so are REJECTED acknowledgements: a supplier who refused the order
    /// is not going to ship it, and reminding the buyer about a date the supplier has already
    /// disowned trains people to ignore the mailbox. A NULL acknowledgement still gets the
    /// reminder — an unanswered order carrying a committed date is exactly the case worth
    /// chasing.</para>
    /// </summary>
    internal static IQueryable<SupplierPurchaseOrder> ShipDateReminderCandidates(
        ErpRfqAutomationContext db, long businessUnitId, DateOnly today, DateOnly horizon)
        => db.SupplierPurchaseOrders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId
                        && o.CommittedShipDate != null
                        && o.CommittedShipDate >= today
                        && o.CommittedShipDate <= horizon
                        && !ShipmentSettledStatuses.Contains(o.Status)
                        && (o.AcknowledgementStatus == null
                            || o.AcknowledgementStatus != SupplierAcknowledgementStatuses.Rejected));

    private async Task SweepSupplierShipDatesAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        // Non-positive means the tenant has not configured a reminder lead time. Zero would still
        // match orders shipping today, which is a reminder nobody can act on.
        if (policy.SupplierShipDateReminderDays <= 0) return;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = BusinessCalendar.AddBusinessDays(today, policy.SupplierShipDateReminderDays);

        var orders = await ShipDateReminderCandidates(db, bu, today, horizon)
            .Select(o => new
            {
                o.Id, o.PurchaseOrderNumber, o.CommittedShipDate,
                o.ApprovedByUserId, o.ApprovedBy, o.CreatedBy, o.ModifiedBy
            })
            .ToListAsync(ct);
        if (orders.Count == 0) return;

        var buUsers = await GetBusinessUnitUsersAsync(db, bu, ct);
        var unresolved = 0;

        foreach (var order in orders)
        {
            var buyer = ResolveBuyer(buUsers, order.ApprovedByUserId, order.ApprovedBy, order.CreatedBy, order.ModifiedBy);
            if (buyer is null) { unresolved++; continue; }

            var shipDate = order.CommittedShipDate!.Value;

            // The claim key carries the SHIP DATE, not just the order id. One reminder per
            // (order, committed date): a second sweep on the same date is blocked by the
            // unique (BusinessUnitId, DedupKey) index, but a supplier who counters with a NEW
            // date produces a new key and therefore a fresh reminder — which is the whole
            // point, since the date the buyer was warned about no longer holds.
            var claim = await TryClaimEventAsync(db, bu, "supplier-order-ship", order.Id, "warn",
                shipDate.ToDateTime(TimeOnly.MinValue), ct);
            if (claim is null) continue;

            var label = $"Purchase order {order.PurchaseOrderNumber}";
            var workingDays = BusinessCalendar.BusinessDaysUntil(today, shipDate);
            var whenPhrase = workingDays == 0
                ? "today"
                : $"in {workingDays} working day{(workingDays == 1 ? "" : "s")}";

            await SendOrReleaseAsync(db, claim, ct, () =>
                notifications.SendDeadlineAlertAsync(buyer.Email, buyer.FirstName, "warn", label,
                    $"Supplier ships on {shipDate:dd MMM yyyy} — {label}",
                    $"The supplier committed to ship {label} on {shipDate:dd MMM yyyy}, which is {whenPhrase} " +
                    "(weekends excluded). Confirm the shipment is on track, or chase the supplier now while there is " +
                    "still time to re-plan.",
                    bu, ct));
        }

        if (unresolved > 0)
            _log.LogWarning(
                "BU {Bu}: {Count} supplier order(s) approaching their committed ship date have no resolvable buyer; reminder skipped for those.",
                bu, unresolved);
    }

    /// <summary>
    /// FR-SPO-07 escalation candidates: orders sitting with the supplier (SENT, or legacy
    /// ISSUED) that carry no acknowledgement at all.
    ///
    /// <para>CLOCK START. <c>coalesce(SentToSupplierOn, ApprovedOn, CreatedOn)</c>. Dispatch is
    /// what the supplier is late against, and <c>SentToSupplierOn</c> is stamped when the order
    /// is issued, so it is the authoritative clock. It replaced an earlier
    /// <c>coalesce(ApprovedOn, CreatedOn)</c> approximation that charged the supplier every day
    /// the order sat approved but unsent — an order approved on Monday and dispatched on Thursday
    /// was three days overdue before the supplier had seen it.</para>
    ///
    /// <para>The fallbacks remain for rows predating that column. <c>ModifiedOn</c> is
    /// deliberately not among them: any later edit moves it, which would silently restart the
    /// supplier's clock.</para>
    ///
    /// <para><paramref name="calendarCutoff"/> is <c>now − window</c> in plain calendar time.
    /// Working time elapsed is never GREATER than calendar time elapsed, so this is a strict
    /// superset of what the business-day rule can select; the exact Friday–Saturday-aware
    /// test then runs per row. That keeps the weekend maths out of SQL without dragging the
    /// whole table into memory.</para>
    /// </summary>
    internal static IQueryable<SupplierPurchaseOrder> AcknowledgementEscalationCandidates(
        ErpRfqAutomationContext db, long businessUnitId, DateTime calendarCutoff)
        => db.SupplierPurchaseOrders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId
                        && WithSupplierStatuses.Contains(o.Status)
                        && o.AcknowledgementStatus == null
                        && o.AcknowledgedOn == null
                        && (o.SentToSupplierOn ?? o.ApprovedOn ?? o.CreatedOn) <= calendarCutoff);

    private async Task SweepSupplierAcknowledgementsAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        // A non-positive window is "not configured", not "escalate instantly". The columns were
        // added to existing SlaPolicy rows with a backfill, and a tenant can still key zero in;
        // reading either as a zero-hour deadline would escalate every dispatched order on the
        // first sweep, which is how an alerting channel gets muted permanently.
        if (policy.SupplierAckEscalationHours <= 0) return;

        var now = DateTime.UtcNow;
        var window = TimeSpan.FromHours(policy.SupplierAckEscalationHours);

        var orders = await AcknowledgementEscalationCandidates(db, bu, now - window)
            .Select(o => new
            {
                o.Id, o.PurchaseOrderNumber, o.ApprovedByUserId, o.ApprovedBy,
                // Dispatch, not internal approval, is what the supplier is late against.
                SentOn = o.SentToSupplierOn ?? o.ApprovedOn ?? o.CreatedOn
            })
            .ToListAsync(ct);
        if (orders.Count == 0) return;

        List<Recipient>? fallbackRecipients = null; // lazy — only fetched when needed

        foreach (var order in orders)
        {
            var sentOn = order.SentOn;

            // The business-day gate. A calendar clock would fire this on a Friday or Saturday,
            // into an office nobody is sitting in, and would charge the supplier a weekend they
            // could not have worked.
            var escalateAt = BusinessCalendar.AddBusinessTime(sentOn, window);
            if (now < escalateAt) continue;

            // Same escalation ladder as the copilot-approval sweep: the approver's own manager
            // first (Users.ManagerId), then the tenant's managers/admins selected by RoleRank.
            var recipients = await ResolveRequesterManagerAsync(db, bu, order.ApprovedByUserId, order.ApprovedBy, ct);
            if (recipients.Count == 0)
            {
                fallbackRecipients ??= await GetManagersAndAdminsAsync(db, bu, ct);
                recipients = fallbackRecipients;
            }
            if (recipients.Count == 0) continue; // nobody to tell — retry next sweep

            var claim = await TryClaimEventAsync(db, bu, "supplier-order-ack", order.Id, "escalated", null, ct);
            if (claim is null) continue;

            var label = $"Purchase order {order.PurchaseOrderNumber}";
            await SendOrReleaseAsync(db, claim, ct, async () =>
            {
                var anyDelivered = false;
                foreach (var r in recipients)
                {
                    anyDelivered |= await notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "escalated", label,
                        $"Supplier has not acknowledged an order — {label}",
                        $"{label} went to the supplier on {sentOn:dd MMM yyyy HH:mm} UTC and there is still no " +
                        $"acknowledgement after {policy.SupplierAckEscalationHours} working hour(s) (weekends excluded). " +
                        "Nothing confirms the supplier has accepted this order, so neither the ship date nor the customer " +
                        "promise behind it can be relied on. Please chase the supplier or reassign the order.",
                        bu, ct);
                }
                return anyDelivered;
            });
        }
    }

    /// <summary>Active users of the tenant, for free-text owner matching.</summary>
    private static async Task<List<BuyerCandidate>> GetBusinessUnitUsersAsync(
        ErpRfqAutomationContext db, long bu, CancellationToken ct)
        => await db.Users.AsNoTracking()
            .Where(u => u.Buid == bu && u.IsActive != false)
            .Select(u => new BuyerCandidate(u.Id, u.Email, u.FirstName, u.LastName))
            .ToListAsync(ct);

    private sealed record BuyerCandidate(long Id, string Email, string FirstName, string LastName);

    /// <summary>
    /// The buyer to remind: the recorded approver's user id first (the only exact key on the
    /// order), then the free-text approver/creator/last-editor identities matched against BU
    /// users by email or "First Last" — the same resolution the stale-quote digest uses,
    /// because these columns are the same kind of free-text identity. Null when nobody
    /// matches: a reminder with a guessed recipient is worse than no reminder.
    /// </summary>
    private static BuyerCandidate? ResolveBuyer(
        List<BuyerCandidate> buUsers, long? approvedByUserId, params string?[] identities)
    {
        if (approvedByUserId is { } id)
        {
            var byId = buUsers.FirstOrDefault(u => u.Id == id);
            if (byId is not null && !string.IsNullOrWhiteSpace(byId.Email)) return byId;
        }

        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity)) continue;
            var match = buUsers.FirstOrDefault(u =>
                string.Equals(u.Email, identity, StringComparison.OrdinalIgnoreCase)
                || string.Equals($"{u.FirstName} {u.LastName}", identity, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrWhiteSpace(match.Email)) return match;
        }

        return null;
    }

    // ---------------- shared helpers ----------------

    private static async Task<List<long>> GetStatusIdsAsync(ErpRfqAutomationContext db, string code, CancellationToken ct)
    {
        // SetupMaster ids carrying this QuoteStatus code + the legacy fallback id, so
        // pre-SetupMaster tenants are still swept correctly. Now tenant-scoped: a status
        // id belonging to a DIFFERENT tenant could never legitimately match this
        // tenant's quotes anyway.
        var ids = await db.SetupMasters.AsNoTracking()
            .Where(s => s.SetupType == "QuoteStatus" && s.SetupCode == code)
            .Select(s => s.SetupId)
            .ToListAsync(ct);
        if (code == "SENT" && !ids.Contains(43)) ids.Add(43);
        return ids;
    }

    private sealed record Recipient(long Id, string Email, string FirstName);

    /// <summary>Active BU users whose role name (SetupType "role") contains manager/admin.</summary>
    private static async Task<List<Recipient>> GetManagersAndAdminsAsync(
        ErpRfqAutomationContext db, long bu, CancellationToken ct)
    {
        var rows = await db.Users.AsNoTracking()
            .Where(u => u.Buid == bu && u.IsActive != false && u.RoleId != null)
            // RC-1: was the case-SENSITIVE literal "role"; production stores 'Role', so this
            // join matched nothing and SLA escalations silently had no recipients.
            .Join(db.SetupMasters.AsNoTracking().Where(ERP_RFQ_Automation.Authorization.SetupTypes.IsRoleRow),
                u => u.RoleId, s => s.SetupId,
                (u, s) => new { u.Id, u.Email, u.FirstName, s.RoleRank })
            .ToListAsync(ct);

        // Escalation recipients are selected by RoleRank, not by a substring of the role name.
        // Post-backfill this is the same set of people, and it can no longer be changed by a rename.
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Email) &&
                        r.RoleRank >= ERP_RFQ_Automation.Authorization.RoleRanks.Manager)
            .Select(r => new Recipient(r.Id, r.Email, r.FirstName))
            .ToList();
    }

    /// <summary>
    /// Inserts the send-once claim. Returns the claim when THIS instance won, or null
    /// when the (BusinessUnitId, DedupKey) unique index rejected it — meaning the alert
    /// was already produced, either by an earlier sweep or by a concurrent instance.
    ///
    /// The lookup below is a fast path only: on a steady-state sweep almost every
    /// candidate has already been alerted, and an indexed read is much cheaper than a
    /// failed INSERT plus rollback. It is NOT the correctness boundary — two instances
    /// can both pass it, which is exactly the defect this class had. The unique index
    /// is what makes the send happen once.
    /// </summary>
    internal static async Task<SlaEvent?> TryClaimEventAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level,
        DateTime? dayUtc, CancellationToken ct)
    {
        var dedupKey = SlaEvent.BuildDedupKey(entityType, entityId, level, dayUtc);
        var alreadyClaimed = await db.Set<SlaEvent>().AsNoTracking()
            .AnyAsync(e => e.BusinessUnitId == bu && e.DedupKey == dedupKey, ct);
        if (alreadyClaimed) return null;

        return await InsertClaimAsync(db, bu, entityType, entityId, level, dedupKey, ct);
    }

    /// <summary>
    /// The authoritative half of the claim: INSERT, and treat a unique violation as
    /// "another instance owns this alert". Separate from the fast path above so the
    /// true interleaving — two instances that BOTH found nothing and both insert — is
    /// directly reachable and testable.
    /// </summary>
    internal static async Task<SlaEvent?> InsertClaimAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level,
        string dedupKey, CancellationToken ct)
    {
        var entity = new SlaEvent
        {
            BusinessUnitId = bu,
            EntityType = entityType,
            EntityId = entityId,
            Level = level,
            DedupKey = dedupKey,
            CreatedOn = DateTime.UtcNow
        };

        db.Set<SlaEvent>().Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            return entity;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Detach so the poisoned entry cannot re-enter a later SaveChanges.
            db.Entry(entity).State = EntityState.Detached;
            return null;
        }
    }

    /// <summary>
    /// Gives the claim back so a later sweep can retry. Uses ExecuteDelete so it never
    /// flushes unrelated tracked work sitting in the shared DbContext.
    /// </summary>
    internal static async Task ReleaseEventClaimAsync(
        ErpRfqAutomationContext db, SlaEvent claim, CancellationToken ct)
    {
        db.Entry(claim).State = EntityState.Detached;
        await db.Set<SlaEvent>().Where(e => e.Id == claim.Id).ExecuteDeleteAsync(ct);
    }

    /// <summary>Runs the send; releases the claim when nothing was delivered so the next
    /// sweep retries instead of silently swallowing the alert.</summary>
    private async Task<bool> SendOrReleaseAsync(
        ErpRfqAutomationContext db, SlaEvent claim, CancellationToken ct, Func<Task<bool>> send)
    {
        bool delivered;
        try
        {
            delivered = await send();
        }
        catch
        {
            await ReleaseEventClaimAsync(db, claim, ct);
            throw;
        }

        if (!delivered)
        {
            await ReleaseEventClaimAsync(db, claim, ct);
            _log.LogWarning(
                "SLA alert {EntityType}/{EntityId}/{Level} for BU {Bu} was not delivered; claim released for retry.",
                claim.EntityType, claim.EntityId, claim.Level, claim.BusinessUnitId);
        }
        return delivered;
    }

    /// <summary>
    /// PostgreSQL 23505 (unique_violation) and its SQLite equivalent, detected without
    /// taking a compile-time dependency on either provider's exception type.
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? error = exception; error is not null; error = error.InnerException)
        {
            if (error is DbException dbError
                && string.Equals(dbError.SqlState, "23505", StringComparison.Ordinal))
                return true;
            if (error.Message.Contains("duplicate key value violates unique constraint", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
