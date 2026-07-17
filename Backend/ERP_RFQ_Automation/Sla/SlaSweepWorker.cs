using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Sla;

/// <summary>
/// Periodic SLA sweep (default every 5 minutes). Follows the ExtractionWorker
/// discipline: a fresh DI scope per iteration, every exception logged and
/// swallowed — the loop never dies. The worker runs without a tenant context so
/// global query filters are no-ops; every query below is therefore EXPLICITLY
/// BU-scoped (mirroring how the extraction queue handles cross-tenant work).
///
/// Per business unit with any activity it checks:
///  1. Lead bid-closing deadlines  -> warn / critical / overdue alerts
///  2. Accepted-but-unassigned lead aging -> manager alert
///  3. SENT quotes past their auto-expiry window -> IQuoteOutcomeService.ExpireAsync
///  4. SENT quotes gone quiet (stale) -> per-owner daily digest
///  5. Copilot approvals pending too long -> manager escalation
///
/// Send-once semantics: an SlaEvent row per (BU, EntityType, EntityId, Level) is
/// looked up before any email; the stale digest dedups per (owner, UTC day).
/// </summary>
public sealed class SlaSweepWorker : BackgroundService
{
    /// <summary>Sweep period. Overridable for tests.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SlaSweepWorker> _log;

    public SlaSweepWorker(IServiceScopeFactory scopeFactory, ILogger<SlaSweepWorker> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("SlaSweepWorker starting; period {Period}.", Period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await SweepOnceAsync(scope.ServiceProvider, stoppingToken);
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

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("SlaSweepWorker stopped.");
    }

    internal async Task SweepOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var db = sp.GetRequiredService<ErpRfqAutomationContext>();
        var notifications = sp.GetRequiredService<ISlaNotifications>();
        var outcomes = sp.GetRequiredService<IQuoteOutcomeService>();

        // BUs with any activity: distinct BUs seen on Leads or Quotes.
        var leadBus = await db.Leads.AsNoTracking().IgnoreQueryFilters().Select(l => l.BusinessUnitId).Distinct().ToListAsync(ct);
        var quoteBus = await db.Quotes.AsNoTracking().IgnoreQueryFilters().Select(q => q.BusinessUnitId).Distinct().ToListAsync(ct);
        var businessUnits = leadBus.Union(quoteBus).ToList();

        foreach (var bu in businessUnits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var policy = await db.Set<SlaPolicy>().AsNoTracking().IgnoreQueryFilters()
                                 .FirstOrDefaultAsync(p => p.BusinessUnitId == bu, ct)
                             ?? SlaPolicy.Default(bu);

                await SweepLeadDeadlinesAsync(db, notifications, bu, policy, ct);
                await SweepUnassignedLeadsAsync(db, notifications, bu, policy, ct);
                await SweepQuoteAutoExpiryAsync(db, outcomes, bu, policy, ct);
                await SweepStaleQuotesAsync(db, notifications, bu, policy, ct);
                await SweepPendingApprovalsAsync(db, notifications, bu, policy, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // One misbehaving tenant must never block the others.
                _log.LogError(ex, "SLA sweep failed for BU {Bu}; continuing with next tenant.", bu);
            }
        }
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
        var leads = await db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == bu
                        && l.BidClosingDate != null
                        && l.BidClosingDate.Value.Year >= 2000
                        && (l.LeadStatusId == null || l.LeadStatusId == 24)
                        && l.LeadRejectedReasonId == null
                        && l.BidClosingDate <= horizon)
            .Select(l => new { l.Id, l.Rfqno, l.BidClosingDate, l.AssignTo })
            .ToListAsync(ct);
        if (leads.Count == 0) return;

        // Assignees + their managers, one batch.
        var assigneeIds = leads.Where(l => l.AssignTo.HasValue).Select(l => l.AssignTo!.Value).Distinct().ToList();
        var users = await db.Users.AsNoTracking()
            .Where(u => assigneeIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.FirstName, u.ManagerId })
            .ToListAsync(ct);
        var managerIds = users.Where(u => u.ManagerId.HasValue).Select(u => u.ManagerId!.Value).Distinct().ToList();
        var managers = await db.Users.AsNoTracking()
            .Where(u => managerIds.Contains(u.Id))
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

            if (await EventExistsAsync(db, bu, "lead", lead.Id, level, ct)) continue;

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

            await notifications.SendDeadlineAlertAsync(assignee.Email, assignee.FirstName, level, label, headline, detail, bu, ct);

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

            await RecordEventAsync(db, bu, "lead", lead.Id, level, ct);
        }
    }

    // ---------------- 2. unassigned aging ----------------

    private async Task SweepUnassignedLeadsAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-policy.UnassignedHours);

        // Accepted (24) but never assigned, older than the allowance. Age is
        // measured from acceptance when known (ModifiedDate) else creation.
        var leads = await db.Leads.AsNoTracking().IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == bu
                        && l.LeadStatusId == 24
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
            if (await EventExistsAsync(db, bu, "lead-unassigned", lead.Id, "warn", ct)) continue;

            var label = string.IsNullOrWhiteSpace(lead.Rfqno) ? $"Lead #{lead.Id}" : $"RFQ {lead.Rfqno}";
            foreach (var r in recipients)
            {
                await notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "warn", label,
                    $"Lead waiting for an owner — {label}",
                    $"{label} was accepted more than {policy.UnassignedHours} hour(s) ago but nobody has been assigned to it yet. Please assign an owner so it doesn't slip.",
                    bu, ct);
            }

            await RecordEventAsync(db, bu, "lead-unassigned", lead.Id, "warn", ct);
        }
    }

    // ---------------- 3. quote auto-expiry ----------------

    private async Task SweepQuoteAutoExpiryAsync(
        ErpRfqAutomationContext db, IQuoteOutcomeService outcomes, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sentStatusIds = await GetStatusIdsAsync(db, "SENT", ct);

        var candidates = await db.Quotes.AsNoTracking().IgnoreQueryFilters()
            .Where(q => q.BusinessUnitId == bu
                        && q.StatusId != null && sentStatusIds.Contains(q.StatusId.Value)
                        && (q.ValidUntil ?? q.SentOn) != null
                        && (q.ValidUntil ?? q.SentOn)!.Value.AddDays(policy.QuoteAutoExpireDays) < now)
            .Select(q => q.Id)
            .ToListAsync(ct);

        foreach (var quoteId in candidates)
        {
            if (await EventExistsAsync(db, bu, "quote", quoteId, "expired", ct)) continue;

            var expired = await outcomes.ExpireAsync(quoteId, "AUTO_EXPIRED", ct);
            if (expired)
            {
                await RecordEventAsync(db, bu, "quote", quoteId, "expired", ct);
                _log.LogInformation("BU {Bu}: quote {QuoteId} auto-expired by SLA sweep.", bu, quoteId);
            }
        }
    }

    // ---------------- 4. stale quotes (daily per-owner digest) ----------------

    private async Task SweepStaleQuotesAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sentStatusIds = await GetStatusIdsAsync(db, "SENT", ct);

        var stale = await db.Quotes.AsNoTracking().IgnoreQueryFilters()
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

            // Daily dedup: one digest per (owner, UTC day) — an SlaEvent with
            // EntityType "quote-stale-digest" and EntityId = owner user id created today.
            var sentToday = await db.Set<SlaEvent>().IgnoreQueryFilters().AnyAsync(e =>
                e.BusinessUnitId == bu && e.EntityType == "quote-stale-digest"
                && e.EntityId == owner.Id && e.Level == "stale"
                && e.CreatedOn >= todayUtc, ct);
            if (sentToday) continue;

            var lines = group.Select(x => new StaleQuoteDigestLine
            {
                QuoteNo = x.Quote.QuoteNo,
                CustomerName = x.Quote.CustomerName,
                SentOn = x.Quote.SentOn,
                DaysWaiting = SlaComputed.DaysSinceSent(x.Quote.SentOn, now) ?? 0
            }).OrderByDescending(l => l.DaysWaiting).ToList();

            var sent = await notifications.SendStaleQuotesDigestAsync(owner.Email, owner.FirstName, lines, bu, ct);
            if (sent)
                await RecordEventAsync(db, bu, "quote-stale-digest", owner.Id, "stale", ct);
        }

        var unresolved = stale.Count(q => !buUsers.Any(u =>
            string.Equals(u.Email, q.CreatedBy, StringComparison.OrdinalIgnoreCase)
            || string.Equals($"{u.FirstName} {u.LastName}", q.CreatedBy, StringComparison.OrdinalIgnoreCase)));
        if (unresolved > 0)
            _log.LogWarning("BU {Bu}: {Count} stale quote(s) whose CreatedBy did not match a user; digest skipped for those.", bu, unresolved);
    }

    // ---------------- 5. pending approval escalation ----------------

    private async Task SweepPendingApprovalsAsync(
        ErpRfqAutomationContext db, ISlaNotifications notifications, long bu, SlaPolicy policy, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddHours(-policy.ApprovalEscalationHours);

        var pending = await db.Set<AgentApproval>().AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.BusinessUnitId == bu
                        && a.Status == AgentApprovalStatus.Pending
                        && a.CreatedOn < cutoff)
            .Select(a => new { a.Id, a.ToolName, a.Summary, a.CreatedOn })
            .ToListAsync(ct);
        if (pending.Count == 0) return;

        var recipients = await GetManagersAndAdminsAsync(db, bu, ct);
        if (recipients.Count == 0) return;

        foreach (var approval in pending)
        {
            // SlaEvent.EntityId is a bigint; approval ids are Guids, so a stable
            // 64-bit key is derived from the Guid's first 8 bytes (dedup only —
            // never used to look the approval back up).
            var entityKey = BitConverter.ToInt64(approval.Id.ToByteArray(), 0);
            if (await EventExistsAsync(db, bu, "approval", entityKey, "escalated", ct)) continue;

            var label = $"Copilot approval: {approval.ToolName}";
            foreach (var r in recipients)
            {
                await notifications.SendDeadlineAlertAsync(r.Email, r.FirstName, "escalated", label,
                    "A copilot action has been waiting for approval",
                    $"\"{approval.Summary ?? approval.ToolName}\" has been pending since {approval.CreatedOn:dd MMM yyyy HH:mm} UTC (more than {policy.ApprovalEscalationHours} hour(s)). Please approve or reject it.",
                    bu, ct);
            }

            await RecordEventAsync(db, bu, "approval", entityKey, "escalated", ct);
        }
    }

    // ---------------- shared helpers ----------------

    private static async Task<List<long>> GetStatusIdsAsync(ErpRfqAutomationContext db, string code, CancellationToken ct)
    {
        // All SetupMaster ids carrying this QuoteStatus code (any BU) + the legacy
        // fallback id, so pre-SetupMaster tenants are still swept correctly.
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
            .Join(db.SetupMasters.AsNoTracking().Where(s => s.SetupType == "role"),
                u => u.RoleId, s => s.SetupId,
                (u, s) => new { u.Id, u.Email, u.FirstName, RoleName = s.SetupValue })
            .ToListAsync(ct);

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Email) && r.RoleName != null &&
                        (r.RoleName.ToLowerInvariant().Contains("manager") || r.RoleName.ToLowerInvariant().Contains("admin")))
            .Select(r => new Recipient(r.Id, r.Email, r.FirstName))
            .ToList();
    }

    private static Task<bool> EventExistsAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level, CancellationToken ct)
        => db.Set<SlaEvent>().IgnoreQueryFilters().AnyAsync(e =>
            e.BusinessUnitId == bu && e.EntityType == entityType && e.EntityId == entityId && e.Level == level, ct);

    private static async Task RecordEventAsync(
        ErpRfqAutomationContext db, long bu, string entityType, long entityId, string level, CancellationToken ct)
    {
        db.Set<SlaEvent>().Add(new SlaEvent
        {
            BusinessUnitId = bu,
            EntityType = entityType,
            EntityId = entityId,
            Level = level,
            CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
