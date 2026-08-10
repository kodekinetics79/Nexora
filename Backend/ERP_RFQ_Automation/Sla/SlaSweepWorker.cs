using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.InboundLogistics;
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
///  8. Inbound shipments that have passed the committed ship date without departing (FR-MAS-04)
///     -> buyer alert
///  9. Inbound shipments whose derived Material Available Date misses the customer's required
///     delivery date (FR-MAS-04) -> buyer and sales alert
///
/// Send-once semantics are enforced by the DATABASE: every alert INSERTs its
/// <see cref="SlaEvent"/> claim BEFORE the email goes out, against the unique
/// (BusinessUnitId, DedupKey) index. On a scaled-out deployment the losing instance
/// takes a 23505 and skips the send instead of mailing the customer twice.
///
/// <para>ONE CLAIM PER RECIPIENT, and RELEASE ONLY WHEN NOTHING WAS SENT. The claim used to
/// cover a whole audience and to be DELETED whenever the send did not report success — where
/// success only meant "nothing threw". SMTP that took the message body and then dropped the
/// connection before its 250 therefore deleted the claim, and five minutes later the next
/// sweep mailed the supervisor the same escalation again with no record of the first attempt;
/// a provider that returned nothing at all was read as delivered, so the claim was kept
/// forever and the alert was never sent. Both are fixed here: the recipient is part of the
/// dedup key, "delivered" requires a receipt naming a provider AND an acceptance reference,
/// an ambiguous failure settles the claim UNCERTAIN and is never re-sent, and releasing is a
/// status transition that leaves the audit row behind
/// (<see cref="SlaEventStatuses"/>, <see cref="SendOnceAsync"/>).</para>
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
        await SweepInboundShipDateBreachesAsync(db, notifications, bu, ct);
        await SweepInboundDeliveryRiskAsync(db, notifications, bu, ct);
        await SweepUndecidedRfqLinesAsync(db, notifications, bu, policy, ct);
        await SweepSupplierResponseOverdueAsync(db, notifications, bu, ct);
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

            // Claim BEFORE sending: on a scaled-out deployment only one instance wins.
            var delivered = await SendOnceAsync(db, bu, "lead", lead.Id, level, null, assignee.Email, ct,
                () => notifications.SendDeadlineAlertAsync(
                    assignee.Email, assignee.FirstName, level, label, headline, detail, bu, ct));
            if (!delivered) continue;

            if (level == "overdue" && assignee.ManagerId.HasValue)
            {
                var manager = managers.FirstOrDefault(m => m.Id == assignee.ManagerId.Value);
                if (manager is not null && !string.IsNullOrWhiteSpace(manager.Email))
                {
                    // The manager copy used to have NO claim and its result was discarded, so a
                    // sweep five minutes later mailed it again, and again, for as long as the lead
                    // stayed overdue. It is the same (lead, level) alert addressed to a different
                    // person, which the recipient-scoped key now expresses directly.
                    await SendOnceAsync(db, bu, "lead", lead.Id, level, null, manager.Email, ct,
                        () => notifications.SendDeadlineAlertAsync(manager.Email, manager.FirstName, level, label,
                            $"Bid deadline missed by {assignee.FirstName} — {label}",
                            $"The bid for {label}, assigned to {assignee.FirstName} ({assignee.Email}), closed on {close:dd MMM yyyy} without a recorded submission.",
                            bu, ct));
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
            // Once per lead per recipient; distinct EntityType keeps this separate from the
            // deadline "warn" for the same lead (documented in SLA-WIRING.md).
            var label = string.IsNullOrWhiteSpace(lead.Rfqno) ? $"Lead #{lead.Id}" : $"RFQ {lead.Rfqno}";
            await SendOnceToEachAsync(db, bu, "lead-unassigned", lead.Id, "warn", null, recipients, ct,
                r => notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "warn", label,
                    $"Lead waiting for an owner — {label}",
                    $"{label} was accepted more than {policy.UnassignedHours} hour(s) ago but nobody has been assigned to it yet. Please assign an owner so it doesn't slip.",
                    bu, ct));
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

        // A non-positive silence window DISABLES trigger 2 rather than firing it on everything.
        // With noResponseDays = 0 the clause below reduces to "SentOn < now", which is true of
        // every quote ever sent, so one bad policy row would expire the entire open quote book on
        // the next tick — with no reopen path (LifecyclePolicy.QuoteReopenable is empty). The
        // value is not one the domain admits: SlaController clamps writes to 1-365 and the code
        // default is 90, so a zero can only arrive from a backfill, which is exactly what
        // 20260809163319 did. That data is corrected by migration; this guard is the structural
        // fix, and it matches the fail-safe the two supplier sweeps already apply to their own
        // non-positive policy values.
        var noResponseEnabled = noResponseDays > 0;

        return db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == businessUnitId
                        && q.StatusId != null && sentStatusIds.Contains(q.StatusId.Value)
                        && ((q.ValidUntil != null
                             && q.ValidUntil >= EarliestCommercialDeadline
                             && q.ValidUntil.Value.AddDays(graceDays) < now)
                            || (noResponseEnabled
                                && q.SentOn != null
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

            var lines = group.Select(x => new StaleQuoteDigestLine
            {
                QuoteNo = x.Quote.QuoteNo,
                CustomerName = x.Quote.CustomerName,
                SentOn = x.Quote.SentOn,
                DaysWaiting = SlaComputed.DaysSinceSent(x.Quote.SentOn, now) ?? 0
            }).OrderByDescending(l => l.DaysWaiting).ToList();

            // Daily dedup: one digest per (owner, UTC day). The day is part of the
            // unique DedupKey, so the claim itself is the dedup — no read-then-write race.
            await SendOnceAsync(db, bu, "quote-stale-digest", owner.Id, "stale", todayUtc, owner.Email, ct,
                () => notifications.SendStaleQuotesDigestAsync(owner.Email, owner.FirstName, lines, bu, ct));
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

            var label = $"Copilot approval: {approval.ToolName}";
            var deadlineNote = deadline.HasValue
                ? $" The linked RFQ's bid closes on {deadline.Value:dd MMM yyyy HH:mm} UTC, inside the {policy.DeadlineBufferHours}-hour safety buffer."
                : $" It has been pending for more than {policy.ApprovalEscalationHours} hour(s).";

            await SendOnceToEachAsync(db, bu, "approval", entityKey, "escalated", null, recipients, ct,
                r => notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "escalated", label,
                    "A copilot action has been waiting for approval",
                    $"\"{approval.Summary ?? approval.ToolName}\" (requested by {approval.RequestedBy ?? "unknown"}) has been pending since " +
                    $"{approval.CreatedOn:dd MMM yyyy HH:mm} UTC.{deadlineNote} Please approve or reject it.",
                    bu, ct));
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
            var label = $"Purchase order {order.PurchaseOrderNumber}";
            var workingDays = BusinessCalendar.BusinessDaysUntil(today, shipDate);
            var whenPhrase = workingDays == 0
                ? "today"
                : $"in {workingDays} working day{(workingDays == 1 ? "" : "s")}";

            await SendOnceAsync(db, bu, "supplier-order-ship", order.Id, "warn",
                shipDate.ToDateTime(TimeOnly.MinValue), buyer.Email, ct,
                () => notifications.SendDeadlineAlertAsync(buyer.Email, buyer.FirstName, "warn", label,
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

            var label = $"Purchase order {order.PurchaseOrderNumber}";
            await SendOnceToEachAsync(db, bu, "supplier-order-ack", order.Id, "escalated", null, recipients, ct,
                r => notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "escalated", label,
                    $"Supplier has not acknowledged an order — {label}",
                    $"{label} went to the supplier on {sentOn:dd MMM yyyy HH:mm} UTC and there is still no " +
                    $"acknowledgement after {policy.SupplierAckEscalationHours} working hour(s) (weekends excluded). " +
                    "Nothing confirms the supplier has accepted this order, so neither the ship date nor the customer " +
                    "promise behind it can be relied on. Please chase the supplier or reassign the order.",
                    bu, ct));
        }
    }

    // ---------------- 8/9. inbound shipment chasing (FR-MAS-04) ----------------
    //
    // Both of these are FACTS, not thresholds, and so neither introduces a new SlaPolicy column.
    // That is deliberate: the standing lesson from Gate 4 is that a new SLA column backfilled with
    // zero makes every existing row instantly overdue, and the first sweep after deployment mails
    // the entire order book into managers' inboxes — after which the channel is filtered and every
    // real alert is lost with it. "The committed ship date has passed and nothing has left the
    // factory" and "the derived availability date is after the date the customer asked for" need no
    // tenant-configured tolerance to be true, so they are asked directly.

    /// <summary>
    /// FR-MAS-04, breach half. Supplier orders whose committed ship date has ALREADY passed.
    ///
    /// <para>The mirror image of <see cref="ShipDateReminderCandidates"/>, which deliberately stops
    /// at the ship date because it is a heads-up rather than a lateness alert and said so: "claiming
    /// it would burn the send-once key and silently suppress the real overdue alert when one is
    /// built". This is that alert, and it claims a different key.</para>
    ///
    /// <para>Same exclusions as the reminder — shipped/received/closed/cancelled orders have nothing
    /// left to chase, and an order the supplier REJECTED is not going to ship, so reminding a buyer
    /// about a date the supplier has already disowned trains people to ignore the mailbox.</para>
    /// </summary>
    internal static IQueryable<SupplierPurchaseOrder> ShipDateBreachCandidates(
        ErpRfqAutomationContext db, long businessUnitId, DateOnly today)
        => db.SupplierPurchaseOrders.AsNoTracking()
            .Where(o => o.BusinessUnitId == businessUnitId
                        && o.CommittedShipDate != null
                        && o.CommittedShipDate < today
                        && !ShipmentSettledStatuses.Contains(o.Status)
                        && (o.AcknowledgementStatus == null
                            || o.AcknowledgementStatus != SupplierAcknowledgementStatuses.Rejected));

    private async Task SweepInboundShipDateBreachesAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var orders = await ShipDateBreachCandidates(db, bu, today)
            .Select(o => new
            {
                o.Id, o.PurchaseOrderNumber, o.CommittedShipDate,
                o.ApprovedByUserId, o.ApprovedBy, o.CreatedBy, o.ModifiedBy
            })
            .ToListAsync(ct);
        if (orders.Count == 0) return;

        var orderIds = orders.Select(o => o.Id).ToList();
        var orderedByOrder = await db.SupplierPurchaseOrderLines.AsNoTracking()
            .Where(l => l.BusinessUnitId == bu && orderIds.Contains(l.SupplierPurchaseOrderId))
            .GroupBy(l => l.SupplierPurchaseOrderId)
            .Select(g => new { OrderId = g.Key, Quantity = g.Sum(l => l.OrderedQuantity) })
            .ToDictionaryAsync(x => x.OrderId, x => x.Quantity, ct);

        // What has physically left the factory. A shipment still sitting at READY_AT_FACTORY has
        // not shipped, whatever its paperwork says, and a cancelled one never will.
        var departedByOrder = await db.SupplierShipmentLines.AsNoTracking()
            .Join(db.SupplierShipments.AsNoTracking()
                    .Where(s => s.BusinessUnitId == bu
                                && orderIds.Contains(s.SupplierPurchaseOrderId)
                                && s.Milestone != InboundShipmentMilestones.Cancelled
                                && s.DepartedOriginOn != null),
                line => line.SupplierShipmentId, shipment => shipment.Id,
                (line, shipment) => new { shipment.SupplierPurchaseOrderId, line.ShippedQuantity })
            .GroupBy(x => x.SupplierPurchaseOrderId)
            .Select(g => new { OrderId = g.Key, Quantity = g.Sum(x => x.ShippedQuantity) })
            .ToDictionaryAsync(x => x.OrderId, x => x.Quantity, ct);

        var buUsers = await GetBusinessUnitUsersAsync(db, bu, ct);
        var unresolved = 0;

        foreach (var order in orders)
        {
            var ordered = orderedByOrder.GetValueOrDefault(order.Id);
            var departed = departedByOrder.GetValueOrDefault(order.Id);
            // Fully departed by the committed date is the supplier keeping their word, late
            // milestones notwithstanding. Only a shortfall is a breach.
            if (ordered > 0m && departed >= ordered) continue;

            var buyer = ResolveBuyer(buUsers, order.ApprovedByUserId, order.ApprovedBy, order.CreatedBy, order.ModifiedBy);
            if (buyer is null) { unresolved++; continue; }

            var shipDate = order.CommittedShipDate!.Value;
            // Keyed on the committed date, so a supplier who re-commits to a NEW date earns one
            // fresh alert if they miss that one too, rather than being silently forgiven.
            var label = $"Purchase order {order.PurchaseOrderNumber}";
            var outstanding = ordered - departed;
            await SendOnceAsync(db, bu, "inbound-shipment-late", order.Id, "overdue",
                shipDate.ToDateTime(TimeOnly.MinValue), buyer.Email, ct,
                () => notifications.SendDeadlineAlertAsync(buyer.Email, buyer.FirstName, "overdue", label,
                    $"Supplier missed the committed ship date — {label}",
                    $"The supplier committed to ship {label} on {shipDate:dd MMM yyyy}, and "
                    + (departed <= 0m
                        ? "nothing has been recorded as having left origin."
                        : $"{outstanding:0.####} of {ordered:0.####} units are still not recorded as having left origin.")
                    + " Any customer delivery date resting on this order is now unsupported. Chase the "
                    + "supplier for a departure date, or re-plan the promise behind it.",
                    bu, ct));
        }

        if (unresolved > 0)
            _log.LogWarning(
                "BU {Bu}: {Count} supplier order(s) past their committed ship date have no resolvable buyer; alert skipped for those.",
                bu, unresolved);
    }

    /// <summary>
    /// FR-MAS-04, risk half. Shipments still in flight whose derived Material Available Date lands
    /// AFTER the earliest date a customer asked for on the demand they cover.
    ///
    /// <para>A shipment with no derivable date is deliberately NOT alerted on. It is a real problem
    /// — usually an unset lead-time policy or a missing ETA — but it is a different problem with a
    /// different fix, and inventing an alert for it here would fire on every shipment in a tenant
    /// that has not configured its policy yet. The shipment screen states the reason in place of the
    /// date, which is where someone can act on it.</para>
    /// </summary>
    internal static IQueryable<SupplierShipment> DeliveryRiskCandidates(
        ErpRfqAutomationContext db, long businessUnitId)
        => db.SupplierShipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId
                        && s.MaterialAvailableDate != null
                        && s.Milestone != InboundShipmentMilestones.Cancelled
                        && s.Milestone != InboundShipmentMilestones.ReceivedAtWarehouse);

    private async Task SweepInboundDeliveryRiskAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, CancellationToken ct)
    {
        var shipments = await DeliveryRiskCandidates(db, bu)
            .Select(s => new
            {
                s.Id, s.ShipmentNumber, s.SupplierPurchaseOrderId, s.MaterialAvailableDate,
                s.MaterialAvailableBasisKind, s.Milestone
            })
            .ToListAsync(ct);
        if (shipments.Count == 0) return;

        var shipmentIds = shipments.Select(s => s.Id).ToList();

        // The customer date each shipment is measured against: the EARLIEST required date across the
        // RFQ lines its purchase order lines were raised to cover. Earliest, not latest — a shipment
        // is at risk the moment it misses the tightest promise resting on it.
        var requiredByShipment = await db.SupplierShipmentLines.AsNoTracking()
            .Where(line => line.BusinessUnitId == bu && shipmentIds.Contains(line.SupplierShipmentId))
            .Join(db.SupplierPurchaseOrderLines.AsNoTracking().Where(l => l.BusinessUnitId == bu),
                line => line.SupplierPurchaseOrderLineId, orderLine => orderLine.Id,
                (line, orderLine) => new { line.SupplierShipmentId, orderLine.RfqItemId })
            .Join(db.Rfqitems.AsNoTracking().Where(i => i.RequiredDesiredDate != null),
                x => x.RfqItemId, item => item.Id,
                (x, item) => new { x.SupplierShipmentId, Required = item.RequiredDesiredDate!.Value })
            .GroupBy(x => x.SupplierShipmentId)
            .Select(g => new { ShipmentId = g.Key, Required = g.Min(x => x.Required) })
            .ToDictionaryAsync(x => x.ShipmentId, x => x.Required, ct);
        if (requiredByShipment.Count == 0) return;

        var orderNumbers = await db.SupplierPurchaseOrders.AsNoTracking()
            .Where(o => o.BusinessUnitId == bu
                        && shipments.Select(s => s.SupplierPurchaseOrderId).Contains(o.Id))
            .Select(o => new
            {
                o.Id, o.PurchaseOrderNumber, o.ApprovedByUserId, o.ApprovedBy, o.CreatedBy, o.ModifiedBy
            })
            .ToDictionaryAsync(x => x.Id, ct);

        var buUsers = await GetBusinessUnitUsersAsync(db, bu, ct);
        List<Recipient>? fallbackRecipients = null;
        var unresolved = 0;

        foreach (var shipment in shipments)
        {
            if (!requiredByShipment.TryGetValue(shipment.Id, out var requiredOn)) continue;
            var required = DateOnly.FromDateTime(requiredOn);
            var available = shipment.MaterialAvailableDate!.Value;
            if (available <= required) continue;

            var order = orderNumbers.GetValueOrDefault(shipment.SupplierPurchaseOrderId);
            var buyer = order is null
                ? null
                : ResolveBuyer(buUsers, order.ApprovedByUserId, order.ApprovedBy, order.CreatedBy, order.ModifiedBy);

            // FR-MAS-04 names BOTH the buyer and sales staff. The buyer can chase the supplier; only
            // sales can renegotiate the customer date, and a shipment that will land late is useless
            // information if it reaches nobody who can talk to the customer.
            var recipients = new List<Recipient>();
            if (buyer is not null) recipients.Add(new Recipient(buyer.Id, buyer.Email, buyer.FirstName));
            fallbackRecipients ??= await GetManagersAndAdminsAsync(db, bu, ct);
            foreach (var manager in fallbackRecipients)
                if (recipients.All(r => r.Id != manager.Id))
                    recipients.Add(manager);
            if (recipients.Count == 0) { unresolved++; continue; }

            // Keyed on the AVAILABLE date: an ETA that slips again produces a new derived date and
            // therefore one further alert, while a re-run of the sweep against the same date does
            // not. Without the date in the key a shipment that slipped twice would be silent the
            // second time, which is the slip that actually loses the order.
            var lateBy = available.DayNumber - required.DayNumber;
            var label = $"Shipment {shipment.ShipmentNumber}";
            await SendOnceToEachAsync(db, bu, "inbound-shipment-risk", shipment.Id, "critical",
                available.ToDateTime(TimeOnly.MinValue), recipients, ct,
                recipient => notifications.SendDeadlineAlertAsync(recipient.Email,
                    recipient.FirstName, "critical", label,
                    $"Inbound shipment puts a customer delivery date at risk — {label}",
                    $"{label} on purchase order {order?.PurchaseOrderNumber ?? "(unknown)"} is currently "
                    + $"{shipment.Milestone.Replace('_', ' ').ToLowerInvariant()}. Its material available "
                    + $"date works out at {available:dd MMM yyyy}, which is {lateBy} day(s) after the "
                    + $"{required:dd MMM yyyy} the customer required. That date is derived from the "
                    + $"{(shipment.MaterialAvailableBasisKind ?? "ETA").Replace('_', ' ').ToLowerInvariant()} "
                    + "plus the customs-clearance and putaway lead times set for this business unit. "
                    + "Chase the shipment, re-plan the customer promise, or cover the line from stock.",
                    bu, ct));
        }

        if (unresolved > 0)
            _log.LogWarning(
                "BU {Bu}: {Count} at-risk inbound shipment(s) had no resolvable recipient; alert skipped for those.",
                bu, unresolved);
    }

    // ---------------- 10. undecided RFQ lines near close (FR-SBF-01 a + b) ----------------

    /// <summary>
    /// FR-SBF-01 reminder candidates: RFQs whose bid closing date falls in [today, horizon] and
    /// that still carry at least one line with <c>ParticipationDecision = Pending</c>.
    ///
    /// <para><b>Why this is decision-aware and the lead-deadline sweep is not.</b> Sweep 1 chases a
    /// LEAD whose bid closes soon, whatever state its lines are in — it cannot tell an RFQ that has
    /// been fully worked from one nobody has opened. FR-SBF-01 asks for two different things: a
    /// reminder for pending Quote/No-Quote decisions, and a reminder for RFQs approaching closure
    /// WITHOUT a decision. Both are the same fact — an undecided line running out of time — and
    /// this is the query that can see it, because <c>Rfqitem.ParticipationDecision</c> is where the
    /// decision actually lives.</para>
    ///
    /// <para><b>Why it does not fire on everything.</b> An RFQ where every line has been decided is
    /// excluded even though its deadline is just as close, which is the whole point: the alert
    /// carries the information "there is work outstanding here", and an alert that fired on every
    /// RFQ near close would carry none. Sentinel dates (year &lt; 2000, the extraction pipeline's
    /// "unknown" convention) are excluded for the same reason they are in
    /// <see cref="OpenLeadDeadlineCandidates"/>.</para>
    /// </summary>
    internal static IQueryable<Rfq> UndecidedRfqCandidates(
        ErpRfqAutomationContext db, long businessUnitId, DateTime today, DateTime horizon)
        => db.Rfqs.AsNoTracking()
            .Where(rfq => rfq.BusinessUnitId == businessUnitId
                          && rfq.BidClosingDate != null
                          && rfq.BidClosingDate >= EarliestCommercialDeadline
                          && rfq.BidClosingDate >= today
                          && rfq.BidClosingDate <= horizon
                          // Rfqitem carries NO tenant column of its own — it is scoped through its
                          // parent RFQ, which is why the certification test lists RFQItems among the
                          // parent-derived child tables. The predicate on rfq above is therefore the
                          // tenant boundary for this subquery.
                          && db.Rfqitems.Any(line => line.Rfqid == rfq.Id
                                                     && line.ParticipationDecision == Rfqitem.ParticipationPending));

    private async Task SweepUndecidedRfqLinesAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        // Non-positive means the tenant has not configured this reminder (register R12).
        if (policy.QuoteDecisionReminderDays <= 0) return;

        var now = DateTime.UtcNow;
        var today = now.Date;
        var horizon = BusinessCalendar
            .AddBusinessDays(DateOnly.FromDateTime(today), policy.QuoteDecisionReminderDays)
            .ToDateTime(TimeOnly.MaxValue);

        var rfqs = await UndecidedRfqCandidates(db, bu, today, horizon)
            .Select(rfq => new { rfq.Id, rfq.Rfqno, rfq.BidClosingDate, rfq.LeadId, rfq.CreatedBy, rfq.ModifiedBy })
            .ToListAsync(ct);
        if (rfqs.Count == 0) return;

        var rfqIds = rfqs.Select(r => r.Id).ToList();
        var pendingByRfq = await db.Rfqitems.AsNoTracking()
            // rfqIds came from a BU-scoped query, and Rfqitem has no tenant column to test; the
            // re-assertion on the parent keeps the boundary explicit anyway.
            .Where(line => rfqIds.Contains(line.Rfqid)
                           && line.Rfq.BusinessUnitId == bu
                           && line.ParticipationDecision == Rfqitem.ParticipationPending)
            .GroupBy(line => line.Rfqid)
            .Select(g => new { RfqId = g.Key, Pending = g.Count() })
            .ToDictionaryAsync(x => x.RfqId, x => x.Pending, ct);

        // Owner resolution: the originating lead's assignee is the exact key when there is one;
        // otherwise the RFQ's free-text creator/editor, matched the same way every other sweep in
        // this worker matches them. Never a guessed recipient.
        var leadIds = rfqs.Where(r => r.LeadId.HasValue).Select(r => r.LeadId!.Value).Distinct().ToList();
        var assigneeByLead = leadIds.Count == 0
            ? new Dictionary<long, long?>()
            : await db.Leads.AsNoTracking()
                .Where(l => l.BusinessUnitId == bu && leadIds.Contains(l.Id))
                .Select(l => new { l.Id, l.AssignTo })
                .ToDictionaryAsync(x => x.Id, x => x.AssignTo, ct);

        var buUsers = await GetBusinessUnitUsersAsync(db, bu, ct);
        var unresolved = 0;

        foreach (var rfq in rfqs)
        {
            var pending = pendingByRfq.GetValueOrDefault(rfq.Id);
            if (pending == 0) continue;

            long? assignee = rfq.LeadId.HasValue ? assigneeByLead.GetValueOrDefault(rfq.LeadId.Value) : null;
            var owner = ResolveBuyer(buUsers, assignee, rfq.CreatedBy, rfq.ModifiedBy);
            if (owner is null) { unresolved++; continue; }

            var closing = rfq.BidClosingDate!.Value;

            // Keyed on the CLOSING DATE, not just the RFQ: a buyer who extends the deadline earns
            // one fresh reminder against the new date, and a re-sweep against the same date does not.
            var label = string.IsNullOrWhiteSpace(rfq.Rfqno) ? $"RFQ #{rfq.Id}" : $"RFQ {rfq.Rfqno}";
            var workingDays = BusinessCalendar.BusinessDaysUntil(
                DateOnly.FromDateTime(now.Date), DateOnly.FromDateTime(closing.Date));
            var whenPhrase = workingDays == 0
                ? "today"
                : $"in {workingDays} working day{(workingDays == 1 ? "" : "s")}";

            await SendOnceAsync(db, bu, "rfq-undecided-lines", rfq.Id, "warn", closing.Date, owner.Email, ct,
                () => notifications.SendDeadlineAlertAsync(owner.Email, owner.FirstName, "warn", label,
                    $"{pending} line(s) still undecided — {label}",
                    $"{label} closes on {closing:dd MMM yyyy}, which is {whenPhrase} (weekends excluded), and "
                    + $"{pending} line(s) still carry no Quote or No-Quote decision. An undecided line is not "
                    + "quoted and is not declined — it simply goes unanswered, and the buyer sees a partial "
                    + "response. Decide each line, or record the reason it is being declined.",
                    bu, ct));
        }

        if (unresolved > 0)
            _log.LogWarning(
                "BU {Bu}: {Count} RFQ(s) with undecided lines near close have no resolvable owner; reminder skipped for those.",
                bu, unresolved);
    }

    // ---------------- 11. supplier RFQ response overdue (FR-SBF-01 c) ----------------

    /// <summary>
    /// FR-SBF-01 escalation candidates: supplier solicitations that were dispatched, carry a
    /// response deadline the buyer committed to, and have passed it with no response.
    ///
    /// <para><b>No new policy column, deliberately.</b> "The buyer told this supplier to respond by
    /// a date, and that date has gone" is a FACT, not a tolerance — the same reasoning as the two
    /// FR-MAS-04 sweeps above, and the same reason there is no column here to backfill wrongly. A
    /// solicitation with no <c>DueOn</c> is silent: the buyer set no deadline, so there is nothing
    /// to be late against, and inventing one would fire this alert on every supplier RFQ in a tenant
    /// that does not use deadlines.</para>
    ///
    /// <para>Responded, declined and expired solicitations are excluded — the supplier has answered,
    /// or the record has already been resolved. Chasing those trains people to ignore the mailbox.</para>
    /// </summary>
    internal static IQueryable<SupplierSolicitation> SupplierResponseOverdueCandidates(
        ErpRfqAutomationContext db, long businessUnitId, DateTime now)
        => db.Set<SupplierSolicitation>().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId
                        && s.Status == SolicitationStatus.Sent
                        && s.RespondedOn == null
                        && s.DueOn != null
                        && s.DueOn >= EarliestCommercialDeadline
                        && s.DueOn < now);

    private async Task SweepSupplierResponseOverdueAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var solicitations = await SupplierResponseOverdueCandidates(db, bu, now)
            .Select(s => new { s.Id, s.RfqId, s.SupplierId, s.SupplierRfqNumber, s.DueOn, s.SentOn })
            .ToListAsync(ct);
        if (solicitations.Count == 0) return;

        var rfqIds = solicitations.Select(s => s.RfqId).Distinct().ToList();
        var rfqs = await db.Rfqs.AsNoTracking()
            .Where(r => r.BusinessUnitId == bu && rfqIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Rfqno, r.LeadId, r.CreatedBy, r.ModifiedBy, r.BidClosingDate })
            .ToDictionaryAsync(x => x.Id, ct);

        var supplierIds = solicitations.Select(s => s.SupplierId).Distinct().ToList();
        var supplierNames = await db.Suppliers.AsNoTracking()
            .Where(s => (s.Buid == null || s.Buid == bu) && supplierIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var leadIds = rfqs.Values.Where(r => r.LeadId.HasValue).Select(r => r.LeadId!.Value).Distinct().ToList();
        var assigneeByLead = leadIds.Count == 0
            ? new Dictionary<long, long?>()
            : await db.Leads.AsNoTracking()
                .Where(l => l.BusinessUnitId == bu && leadIds.Contains(l.Id))
                .Select(l => new { l.Id, l.AssignTo })
                .ToDictionaryAsync(x => x.Id, x => x.AssignTo, ct);

        var buUsers = await GetBusinessUnitUsersAsync(db, bu, ct);
        var unresolved = 0;

        foreach (var solicitation in solicitations)
        {
            if (!rfqs.TryGetValue(solicitation.RfqId, out var rfq)) continue;

            long? assignee = rfq.LeadId.HasValue ? assigneeByLead.GetValueOrDefault(rfq.LeadId.Value) : null;
            var owner = ResolveBuyer(buUsers, assignee, rfq.CreatedBy, rfq.ModifiedBy);
            if (owner is null) { unresolved++; continue; }

            var dueOn = solicitation.DueOn!.Value;

            // Keyed on the due date, so a buyer who re-issues with a later deadline earns one
            // further alert if that one is missed too.
            var supplierName = supplierNames.GetValueOrDefault(solicitation.SupplierId) ?? "the supplier";
            var rfqLabel = string.IsNullOrWhiteSpace(rfq.Rfqno) ? $"RFQ #{rfq.Id}" : $"RFQ {rfq.Rfqno}";
            var label = string.IsNullOrWhiteSpace(solicitation.SupplierRfqNumber)
                ? $"{rfqLabel} — {supplierName}"
                : $"Supplier RFQ {solicitation.SupplierRfqNumber}";

            var bidNote = rfq.BidClosingDate is { } closing && closing >= EarliestCommercialDeadline
                ? $" The customer's bid for {rfqLabel} closes on {closing:dd MMM yyyy}."
                : string.Empty;

            await SendOnceAsync(db, bu, "supplier-response-overdue", solicitation.Id, "overdue",
                dueOn.Date, owner.Email, ct,
                () => notifications.SendDeadlineAlertAsync(owner.Email, owner.FirstName, "overdue", label,
                    $"Supplier quote is overdue — {label}",
                    $"{supplierName} was asked to respond to {rfqLabel} by {dueOn:dd MMM yyyy} and nothing has "
                    + $"been received. The request went out on {solicitation.SentOn:dd MMM yyyy}.{bidNote} "
                    + "Chase the supplier, or source the line elsewhere while there is still time to price it.",
                    bu, ct));
        }

        if (unresolved > 0)
            _log.LogWarning(
                "BU {Bu}: {Count} overdue supplier solicitation(s) have no resolvable owner; alert skipped for those.",
                bu, unresolved);
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
        => await TryClaimEventAsync(db, bu, entityType, entityId, level, dayUtc, recipient: null, ct);

    /// <summary>
    /// The recipient-scoped claim. <paramref name="recipient"/> becomes part of the dedup key,
    /// so an escalation to three people is three claims that succeed or fail independently:
    /// the second recipient's transport failure can no longer keep a claim that silences the
    /// third, and one recipient's success can no longer release a claim that mails the first
    /// person again on the next sweep. Pass null only for a claim that gates a non-email
    /// action (the quote auto-expiry).
    ///
    /// <para>MIGRATION WINDOW. Rows written before the recipient joined the key carry the
    /// bare <c>{EntityType}:{EntityId}:{Level}</c> form, and the recipient they were mailed to
    /// was never recorded, so it cannot be reconstructed by any backfill. The legacy key is
    /// therefore ALSO consulted as a suppression: an alert that already went out under the old
    /// semantics is not re-sent to anybody. It suppresses in one direction only — it can cost a
    /// copy to a second recipient of a pre-existing alert, never produce a duplicate — and it
    /// decays on its own, because nothing writes the legacy form after this deploy.</para>
    /// </summary>
    internal static async Task<SlaEvent?> TryClaimEventAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level,
        DateTime? dayUtc, string? recipient, CancellationToken ct)
    {
        var normalizedRecipient = SlaEvent.NormalizeRecipient(recipient);
        var dedupKey = SlaEvent.BuildDedupKey(entityType, entityId, level, dayUtc, normalizedRecipient);
        var legacyKey = normalizedRecipient is null
            ? dedupKey
            : SlaEvent.BuildDedupKey(entityType, entityId, level, dayUtc);

        // Released claims do not count: RELEASED means "definitely not sent", which is exactly
        // the state a later sweep is supposed to retry. This mirrors the partial unique index,
        // which is the enforcement.
        var alreadyClaimed = await db.Set<SlaEvent>().AsNoTracking()
            .AnyAsync(e => e.BusinessUnitId == bu
                           && (e.DedupKey == dedupKey || e.DedupKey == legacyKey)
                           && e.Status != SlaEventStatuses.Released, ct);
        if (alreadyClaimed) return null;

        return await InsertClaimAsync(db, bu, entityType, entityId, level, dedupKey, normalizedRecipient, ct);
    }

    /// <summary>
    /// The authoritative half of the claim: INSERT, and treat a unique violation as
    /// "another instance owns this alert". Separate from the fast path above so the
    /// true interleaving — two instances that BOTH found nothing and both insert — is
    /// directly reachable and testable.
    /// </summary>
    internal static Task<SlaEvent?> InsertClaimAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level,
        string dedupKey, CancellationToken ct)
        => InsertClaimAsync(db, bu, entityType, entityId, level, dedupKey, recipient: null, ct);

    internal static async Task<SlaEvent?> InsertClaimAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level,
        string dedupKey, string? recipient, CancellationToken ct)
    {
        var entity = new SlaEvent
        {
            BusinessUnitId = bu,
            EntityType = entityType,
            EntityId = entityId,
            Level = level,
            DedupKey = dedupKey,
            Recipient = SlaEvent.NormalizeRecipient(recipient),
            Status = SlaEventStatuses.Claimed,
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
    /// Gives the claim back so a later sweep can retry — for an outcome that is DEFINITELY
    /// NOT SENT. It is a status transition, never a delete: the row is the audit trail as
    /// well as the dedup ledger, and deleting it destroyed the only evidence that an attempt
    /// was made at all. The partial unique index excludes RELEASED, so the key is free again.
    ///
    /// <para>Uses ExecuteUpdate so it never flushes unrelated tracked work sitting in the
    /// shared DbContext, and detaches the claim first so the tracked copy cannot write the
    /// pre-release values back over it in a later SaveChanges.</para>
    /// </summary>
    internal static Task ReleaseEventClaimAsync(
        ErpRfqAutomationContext db, SlaEvent claim, CancellationToken ct)
        => SettleClaimAsync(db, claim, SlaEventStatuses.Released, "NOT_SENT", null, null, ct);

    /// <summary>
    /// Records how a claim ended. UNCERTAIN and SENT both KEEP the key, so neither is ever
    /// sent again; only RELEASED frees it.
    /// </summary>
    internal static async Task SettleClaimAsync(
        ErpRfqAutomationContext db, SlaEvent claim, string status, string? reason,
        string? provider, string? acceptanceReference, CancellationToken ct)
    {
        var settledOn = DateTime.UtcNow;
        db.Entry(claim).State = EntityState.Detached;
        await db.Set<SlaEvent>().Where(e => e.Id == claim.Id)
            .ExecuteUpdateAsync(set => set
                .SetProperty(e => e.Status, status)
                .SetProperty(e => e.OutcomeReason, reason)
                .SetProperty(e => e.Provider, provider)
                .SetProperty(e => e.AcceptanceReference, acceptanceReference)
                .SetProperty(e => e.SettledOn, settledOn), ct);

        // Keep the caller's copy honest — the sweep logs off it after this returns.
        claim.Status = status;
        claim.OutcomeReason = reason;
        claim.Provider = provider;
        claim.AcceptanceReference = acceptanceReference;
        claim.SettledOn = settledOn;
    }

    /// <summary>
    /// Claims ONE recipient's copy of an alert and sends it, then records what the transport
    /// said. Returns true only when the provider produced acceptance evidence.
    ///
    /// <para>The three outcomes are not "success and failure". <b>NotSent</b> releases the
    /// claim, because the provider demonstrably never had the message. <b>Sent</b> keeps it
    /// with the acceptance evidence. <b>Uncertain</b> also keeps it: the provider may already
    /// have the message, and re-sending an escalation that a supervisor has already read —
    /// possibly now saying something different — is the one failure this path must never
    /// produce. A missed escalation is recoverable from the ledger; a duplicate is not
    /// recoverable from the supervisor's inbox.</para>
    ///
    /// <para>An exception ESCAPING the send is uncertain for the same reason, including a
    /// cancellation on shutdown: the transport was entered and we cannot see how far it got.
    /// (<see cref="ISlaNotifications"/> does not throw, so this is the belt to its braces.)</para>
    /// </summary>
    private async Task<bool> SendOnceAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level,
        DateTime? dayUtc, string? recipientEmail, CancellationToken ct, Func<Task<SlaSendResult>> send)
    {
        if (string.IsNullOrWhiteSpace(recipientEmail)) return false;

        var claim = await TryClaimEventAsync(db, bu, entityType, entityId, level, dayUtc, recipientEmail, ct);
        if (claim is null) return false;

        SlaSendResult result;
        try
        {
            result = await send();
        }
        catch (Exception ex)
        {
            await SettleClaimAsync(db, claim, SlaEventStatuses.Uncertain,
                $"SEND_THREW:{ex.GetType().Name}", null, null, ct);
            _log.LogError(ex,
                "SLA alert {EntityType}/{EntityId}/{Level} to {Recipient} for BU {Bu} threw inside the transport; "
                + "delivery is UNCERTAIN and it will NOT be re-sent.",
                entityType, entityId, level, recipientEmail, bu);
            throw;
        }

        switch (result.Outcome)
        {
            case SlaSendOutcome.Sent:
                await SettleClaimAsync(db, claim, SlaEventStatuses.Sent, null,
                    result.Provider, result.AcceptanceReference, ct);
                return true;

            case SlaSendOutcome.Uncertain:
                await SettleClaimAsync(db, claim, SlaEventStatuses.Uncertain, result.Reason, null, null, ct);
                _log.LogError(
                    "SLA alert {EntityType}/{EntityId}/{Level} to {Recipient} for BU {Bu} has an UNCERTAIN outcome "
                    + "({Reason}); the claim is kept so it is never re-sent. Check the provider before resending by hand.",
                    entityType, entityId, level, recipientEmail, bu, result.Reason);
                return false;

            default:
                await SettleClaimAsync(db, claim, SlaEventStatuses.Released, result.Reason, null, null, ct);
                _log.LogWarning(
                    "SLA alert {EntityType}/{EntityId}/{Level} to {Recipient} for BU {Bu} was definitely not sent "
                    + "({Reason}); claim released for retry.",
                    entityType, entityId, level, recipientEmail, bu, result.Reason);
                return false;
        }
    }

    /// <summary>
    /// One claim PER RECIPIENT for an alert with an audience. Returns true when at least one
    /// copy was accepted, but every recipient's claim is settled on its own evidence — which
    /// is the point: `anyDelivered |= ...` under a single shared claim meant one failure
    /// either silenced the people who had not been told yet, or released a claim that mailed
    /// the person who HAD been told a second time.
    /// </summary>
    private async Task<bool> SendOnceToEachAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level,
        DateTime? dayUtc, IEnumerable<Recipient> recipients, CancellationToken ct,
        Func<Recipient, Task<SlaSendResult>> send)
    {
        var anyDelivered = false;
        foreach (var recipient in recipients)
            anyDelivered |= await SendOnceAsync(db, bu, entityType, entityId, level, dayUtc,
                recipient.Email, ct, () => send(recipient));
        return anyDelivered;
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
