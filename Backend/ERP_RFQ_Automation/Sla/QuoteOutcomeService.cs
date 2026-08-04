using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;

namespace ERP_RFQ_Automation.Sla;

/// <summary>One selectable outcome reason (governed SetupMaster picklist row).</summary>
public sealed class OutcomeReasonDto
{
    public long Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Records the final fate of a quote (won / lost / expired) plus follow-up
/// stamps. Status transitions are delegated to <see cref="IQuoteService.TransitionStatusAsync"/>
/// (single source of status logic); this service adds the outcome columns,
/// reason resolution, terminal-state immutability, and the system auto-expire path.
///
/// The deduplication agent's ISlaPolicyReader should eventually read
/// <see cref="SlaPolicy"/> via a small adapter — see SLA-WIRING.md.
/// </summary>
public interface IQuoteOutcomeService
{
    /// <summary>
    /// Manual outcome capture. <paramref name="outcome"/> is "won" | "lost" | "expired".
    /// A reason code is required for lost/expired, optional for won.
    /// Terminal quotes are immutable except for manager/admin corrections.
    /// </summary>
    Task<QuoteResponseDTO> SetOutcomeAsync(
        long quoteId, long businessUnitId, string actorEmail,
        string outcome, string? reasonCode = null, string? note = null,
        CancellationToken ct = default);

    /// <summary>
    /// System path (SLA sweep): expires a SENT quote with the given reason
    /// (normally "AUTO_EXPIRED"). No role checks; no RespondedOn stamp.
    /// Returns false when the quote is no longer in SENT (nothing to do).
    /// </summary>
    Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default);

    /// <summary>Stamps RespondedOn (first customer response) if not already set.</summary>
    Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default);

    /// <summary>
    /// Returns the governed "QuoteOutcomeReason" picklist for a BU, seeding the
    /// default rows idempotently on first use.
    /// </summary>
    Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(long businessUnitId, CancellationToken ct = default);
}

public sealed class QuoteOutcomeService : IQuoteOutcomeService
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IQuoteService _quoteService;
    private readonly ILogger<QuoteOutcomeService> _logger;
    private readonly ERP_RFQ_Automation.Metrics.IMetricRecorder? _metrics;
    private readonly ERP_RFQ_Automation.Intelligence.Decision.ILeadDecisionService? _decisions;
    private readonly ILifecycleApplicationService? _lifecycle;
    private readonly ISalesApplicationService? _sales;

    // WP-B4: metrics + decision-brief lookup are OPTIONAL passive add-ons
    // (default null) — existing constructions keep compiling, and outcome capture
    // works unchanged when the recorder is not yet registered (WAVEB-WIRING.md).
    public QuoteOutcomeService(
        ErpRfqAutomationContext context,
        IQuoteService quoteService,
        ILogger<QuoteOutcomeService> logger,
        ERP_RFQ_Automation.Metrics.IMetricRecorder? metrics = null,
        ERP_RFQ_Automation.Intelligence.Decision.ILeadDecisionService? decisions = null,
        ILifecycleApplicationService? lifecycle = null,
        ISalesApplicationService? sales = null)
    {
        _context = context;
        _quoteService = quoteService;
        _logger = logger;
        _metrics = metrics;
        _decisions = decisions;
        _lifecycle = lifecycle;
        _sales = sales;
    }

    // Default governed picklist, seeded per-BU on demand (SetupMaster.BusinessUnitId
    // is non-nullable, so shared null-BU rows are not possible — see SLA-WIRING.md).
    private static readonly (string Code, string Label)[] DefaultReasons =
    {
        ("PRICE", "Price too high"),
        ("LEAD_TIME", "Lead time too long"),
        ("NO_STOCK", "Item unavailable"),
        ("LOST_COMPETITOR", "Lost to competitor"),
        ("CUSTOMER_CANCELLED", "Customer cancelled"),
        ("NO_RESPONSE", "No response"),
        ("AUTO_EXPIRED", "Expired automatically"),
        ("OTHER", "Other")
    };

    private static readonly Dictionary<string, string> OutcomeToStatusCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["won"] = "ACCEPTED",
        ["lost"] = "REJECTED",
        ["expired"] = "EXPIRED"
    };

    public async Task<QuoteResponseDTO> SetOutcomeAsync(
        long quoteId, long businessUnitId, string actorEmail,
        string outcome, string? reasonCode = null, string? note = null,
        CancellationToken ct = default)
    {
        if (!OutcomeToStatusCode.TryGetValue(outcome?.Trim() ?? "", out var statusCode))
            throw new ArgumentException($"Invalid outcome '{outcome}'. Expected won, lost or expired.");

        var isWon = statusCode == "ACCEPTED";
        if (!isWon && string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A reason is required when marking a quote as lost or expired.");

        var quote = await _context.Quotes
            .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId, ct)
            ?? throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

        // Revisions-lite chain lock (WP-B4): a quote that has been superseded by a
        // newer revision can no longer take an outcome — the outcome belongs on the
        // latest revision (and, once recorded there, locks the whole chain).
        var supersededBy = await _context.Quotes.AsNoTracking()
            .Where(q => q.RevisionOfQuoteId == quote.Id)
            .Select(q => new { q.QuoteNo, q.RevisionNo })
            .FirstOrDefaultAsync(ct);
        if (supersededBy != null)
            throw new InvalidOperationException(
                $"Quote '{quote.QuoteNo}' has been superseded by revision '{supersededBy.QuoteNo}' " +
                $"(Rev {supersededBy.RevisionNo}). Record the outcome on the latest revision instead.");

        // Terminal-state immutability: once an outcome is recorded the quote is
        // frozen; only a manager/admin may correct it.
        if (quote.OutcomeOn.HasValue || await IsTerminalStatusAsync(quote, ct))
        {
            if (!await IsManagerOrAdminAsync(actorEmail, businessUnitId, ct))
                throw new InvalidOperationException(
                    $"Quote '{quote.QuoteNo}' already has a final outcome. Only a manager or administrator can correct it.");
        }

        await EnsureExpiredStatusSeededAsync(businessUnitId, ct);
        await EnsureReasonsSeededAsync(businessUnitId, ct);

        long? reasonId = null;
        if (!string.IsNullOrWhiteSpace(reasonCode))
        {
            reasonId = await ResolveReasonIdAsync(businessUnitId, reasonCode!, ct)
                ?? throw new ArgumentException($"Unknown outcome reason '{reasonCode}'.");
        }

        var now = DateTime.UtcNow;
        var recordsCustomerResponse = RecordsCustomerResponse(outcome, reasonCode);
        quote.OutcomeOn = now;
        quote.OutcomeReasonId = reasonId;
        quote.OutcomeNote = Truncate(note, 500);
        if (recordsCustomerResponse) quote.RespondedOn ??= now;
        quote.ModifiedBy = actorEmail;
        quote.ModifiedDate = now;
        QuoteResponseDTO dto;
        if (_lifecycle is not null)
        {
            await ExecuteLifecycleTransactionAsync(async () =>
            {
                await _lifecycle.TransitionQuoteInCurrentTransactionAsync(
                    businessUnitId,
                    quoteId,
                    new LifecycleActor(actorEmail, "quote-outcome"),
                    OutcomeCommand(quote, statusCode, reasonCode, note, actorEmail),
                    false,
                    ct);
                await AppendOutcomeActivitiesAsync(quote, outcome, reasonCode, actorEmail, now,
                    recordsCustomerResponse, ct);
            }, ct);
            dto = await _quoteService.GetQuoteAsync(quoteId);
        }
        else
        {
            QuoteResponseDTO? transitioned = null;
            await ExecuteLifecycleTransactionAsync(async () =>
            {
                transitioned = await _quoteService.TransitionStatusAsync(quoteId, statusCode, actorEmail);
                await _context.SaveChangesAsync(ct);
                await AppendOutcomeActivitiesAsync(quote, outcome, reasonCode, actorEmail, now,
                    recordsCustomerResponse, ct);
            }, ct);
            dto = transitioned!;
        }

        dto.SentOn = quote.SentOn;
        dto.RespondedOn = quote.RespondedOn;
        dto.OutcomeOn = quote.OutcomeOn;
        dto.OutcomeReasonId = quote.OutcomeReasonId;
        dto.OutcomeNote = quote.OutcomeNote;

        _logger.LogInformation("Quote {QuoteId} outcome '{Outcome}' recorded by {Actor} (reason {Reason}).",
            quoteId, outcome, actorEmail, reasonCode ?? "-");

        // WP-B4 passive metric (never throws, never blocks the outcome).
        await RecordOutcomeMetricAsync(quote, outcome!.Trim().ToLowerInvariant(), reasonCode, ct);

        return dto;
    }

    public async Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default)
    {
        var quote = await _context.Quotes.FirstOrDefaultAsync(q => q.Id == quoteId, ct);
        if (quote is null) return false;

        // Only a quote still sitting in SENT auto-expires; anything else was
        // already resolved by a human between sweep and execution.
        var sentId = await ResolveStatusIdAsync(quote.BusinessUnitId, "SENT", ct) ?? LegacySentStatusId;
        if (quote.StatusId != sentId || quote.OutcomeOn.HasValue) return false;

        // Revisions-lite chain lock (WP-B4): a superseded quote's fate is carried
        // by its newer revision — the system must not stamp an outcome on it.
        // IgnoreQueryFilters is kept (the sweep may run scoped or unscoped) but the tenant is now
        // chained explicitly: a revision always belongs to the same business unit as the quote it
        // supersedes, so a match from another tenant is never legitimate — it would suppress this
        // tenant's auto-expiry on the strength of a foreign row.
        var superseded = await _context.Quotes.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(q => q.RevisionOfQuoteId == quote.Id && q.BusinessUnitId == quote.BusinessUnitId, ct);
        if (superseded) return false;

        await EnsureExpiredStatusSeededAsync(quote.BusinessUnitId, ct);
        await EnsureReasonsSeededAsync(quote.BusinessUnitId, ct);
        var reasonId = await ResolveReasonIdAsync(quote.BusinessUnitId, reasonCode, ct);

        quote.OutcomeOn = DateTime.UtcNow;
        quote.OutcomeReasonId = reasonId;
        quote.ModifiedBy = "system:sla-sweep";
        quote.ModifiedDate = DateTime.UtcNow;
        // NOTE: RespondedOn deliberately NOT stamped — the customer never responded.
        if (_lifecycle is not null)
        {
            await ExecuteLifecycleTransactionAsync(async () =>
            {
                await _lifecycle.TransitionQuoteInCurrentTransactionAsync(
                    quote.BusinessUnitId,
                    quoteId,
                    new LifecycleActor("system:sla-sweep", "sla-worker"),
                    OutcomeCommand(quote, "EXPIRED", reasonCode, null, "system:sla-sweep"),
                    false,
                    ct);
            }, ct);
        }
        else
        {
            await _quoteService.TransitionStatusAsync(quoteId, "EXPIRED", "system:sla-sweep");
            await _context.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Quote {QuoteId} auto-expired (reason {Reason}).", quoteId, reasonCode);

        // WP-B4 passive metric for the system path too.
        await RecordOutcomeMetricAsync(quote, "expired", reasonCode, ct);

        return true;
    }

    private async Task ExecuteLifecycleTransactionAsync(Func<Task> operation, CancellationToken ct)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);
            await operation();
            await transaction.CommitAsync(ct);
        });
    }

    private static LifecycleTransitionCommand OutcomeCommand(
        Quote quote, string statusCode, string? reasonCode, string? note, string actor)
        => new(
            statusCode,
            quote.LifecycleVersion,
            reasonCode ?? (statusCode is "REJECTED" or "EXPIRED" ? "OUTCOME_RECORDED" : null),
            Truncate(note, 500),
            "quote-outcome",
            Guid.NewGuid().ToString("N"),
            $"quote:{quote.Id}:outcome:{statusCode}",
            $"quote-outcome:{quote.Id}:v{quote.LifecycleVersion}:{statusCode}:{actor.ToLowerInvariant()}");

    /// <summary>
    /// WP-B4 hook (c): appends an "outcome_recorded" MetricEvent with the outcome,
    /// reason and the RecDate→SentOn→OutcomeOn cycle deltas (whole days). The
    /// lead-decision-brief recommendation is included only when it is cheaply
    /// resolvable via ILeadDecisionService.GetSummariesAsync (its batch/cheap
    /// path); anything else — including any lookup failure — just omits it.
    /// Never throws.
    /// </summary>
    private async Task RecordOutcomeMetricAsync(Quote quote, string outcome, string? reasonCode, CancellationToken ct)
    {
        if (_metrics is null) return;

        try
        {
            // RFQ receive date + lead linkage in one projection. Rfq.RecDate is
            // non-nullable but may be a sentinel (< year 2000) — treat as unknown.
            // Tenant chained from the quote: Quote.RfqId is an intra-tenant foreign key, so
            // IgnoreQueryFilters here must not become a licence to read another tenant's RFQ
            // receive date and lead id into this tenant's outcome metric.
            var rfqFacts = quote.Rfqid.HasValue
                ? await _context.Rfqs.AsNoTracking().IgnoreQueryFilters()
                    .Where(r => r.Id == quote.Rfqid.Value && r.BusinessUnitId == quote.BusinessUnitId)
                    .Select(r => new { r.RecDate, r.LeadId })
                    .FirstOrDefaultAsync(ct)
                : null;

            DateTime? recDate = rfqFacts != null && rfqFacts.RecDate.Year >= 2000 ? rfqFacts.RecDate : null;

            static double? Days(DateTime? from, DateTime? to) =>
                from.HasValue && to.HasValue ? Math.Round((to.Value - from.Value).TotalDays, 1) : null;

            string? recommendation = null;
            if (_decisions is not null && rfqFacts?.LeadId is long leadId)
            {
                try
                {
                    var summaries = await _decisions.GetSummariesAsync(new[] { leadId }, quote.BusinessUnitId, ct);
                    if (summaries.TryGetValue(leadId, out var summary)) recommendation = summary.Recommendation;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not cheaply resolvable right now — omit, per spec.
                    _logger.LogDebug(ex, "Outcome metric: decision-brief recommendation lookup skipped for quote {QuoteId}.", quote.Id);
                }
            }

            await _metrics.RecordAsync(quote.BusinessUnitId, ERP_RFQ_Automation.Metrics.MetricEventTypes.OutcomeRecorded, quote.Id, new
            {
                quoteId = quote.Id,
                quoteNo = quote.QuoteNo,
                revisionNo = quote.RevisionNo,
                outcome,
                reasonCode,
                cycle = new
                {
                    recDate,
                    sentOn = quote.SentOn,
                    outcomeOn = quote.OutcomeOn,
                    recToSentDays = Days(recDate, quote.SentOn),
                    sentToOutcomeDays = Days(quote.SentOn, quote.OutcomeOn),
                    recToOutcomeDays = Days(recDate, quote.OutcomeOn)
                },
                decisionBriefRecommendation = recommendation
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The recorder itself never throws; this belt-and-braces guard covers the
            // facts-gathering above. A metric must never break an outcome.
            _logger.LogWarning(ex, "Outcome metric for quote {QuoteId} was dropped.", quote.Id);
        }
    }

    public async Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default)
    {
        var quote = await _context.Quotes
            .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId, ct)
            ?? throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

        if (quote.RespondedOn.HasValue)
        {
            await AppendSalesActivityAsync(quote, CommercialActivityType.CustomerResponded,
                "CUSTOMER_RESPONDED", actorEmail, quote.RespondedOn.Value, ct);
            return;
        }

        var respondedOn = DateTime.UtcNow;
        quote.RespondedOn = respondedOn;
        quote.ModifiedBy = actorEmail;
        quote.ModifiedDate = respondedOn;
        await ExecuteLifecycleTransactionAsync(async () =>
        {
            await _context.SaveChangesAsync(ct);
            await AppendSalesActivityAsync(quote, CommercialActivityType.CustomerResponded,
                "CUSTOMER_RESPONDED", actorEmail, respondedOn, ct);
        }, ct);
    }

    private async Task AppendOutcomeActivitiesAsync(
        Quote quote,
        string outcome,
        string? reasonCode,
        string actor,
        DateTime occurredOn,
        bool recordsCustomerResponse,
        CancellationToken ct)
    {
        if (recordsCustomerResponse)
            await AppendSalesActivityAsync(quote, CommercialActivityType.CustomerResponded,
                "CUSTOMER_RESPONDED", actor, quote.RespondedOn ?? occurredOn, ct);
        var activityType = outcome.Equals("won", StringComparison.OrdinalIgnoreCase)
            ? CommercialActivityType.Won
            : outcome.Equals("lost", StringComparison.OrdinalIgnoreCase)
                ? CommercialActivityType.Lost
                : (CommercialActivityType?)null;
        if (activityType.HasValue)
            await AppendSalesActivityAsync(quote, activityType.Value,
                reasonCode?.Trim() ?? outcome.Trim().ToUpperInvariant(), actor, occurredOn, ct);
    }

    internal static bool RecordsCustomerResponse(string outcome, string? reasonCode) =>
        outcome.Equals("won", StringComparison.OrdinalIgnoreCase) ||
        outcome.Equals("lost", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(reasonCode?.Trim(), "NO_RESPONSE", StringComparison.OrdinalIgnoreCase);

    private async Task AppendSalesActivityAsync(
        Quote quote,
        CommercialActivityType activityType,
        string outcomeCode,
        string actor,
        DateTime occurredOn,
        CancellationToken ct)
    {
        if (_sales is null || !quote.Rfqid.HasValue) return;
        var attribution = await _context.Rfqs.AsNoTracking()
            .Where(rfq => rfq.BusinessUnitId == quote.BusinessUnitId && rfq.Id == quote.Rfqid.Value)
            .Select(rfq => new
            {
                OwnerUserId = rfq.Lead == null ? null : rfq.Lead.AssignTo,
                CustomerId = rfq.Lead == null ? quote.CustomerId : rfq.Lead.CustomerId
            })
            .SingleOrDefaultAsync(ct);
        if (attribution?.OwnerUserId is not > 0) return;

        var eventCode = activityType.ToString().ToUpperInvariant();
        var activityIdentity = activityType is CommercialActivityType.Won or CommercialActivityType.Lost
            ? $"v{quote.LifecycleVersion}:{eventCode}"
            : eventCode;
        await _sales.AppendActivityAsync(quote.BusinessUnitId, new AppendCommercialActivityCommand(
            attribution.OwnerUserId.Value, activityType, "Quote", quote.Id,
            attribution.CustomerId ?? quote.CustomerId, null, occurredOn,
            outcomeCode, $"quote:{quote.Id}:{eventCode.ToLowerInvariant()}", actor,
            $"quote:{quote.Id}:commercial-activity",
            $"quote:{quote.Id}:commercial-activity:{activityIdentity}"), ct);
    }

    public async Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(long businessUnitId, CancellationToken ct = default)
    {
        await EnsureReasonsSeededAsync(businessUnitId, ct);

        return await _context.SetupMasters.AsNoTracking()
            .Where(s => s.SetupType == "QuoteOutcomeReason"
                        && s.BusinessUnitId == businessUnitId
                        && (s.IsActive == true || s.IsActive == null))
            .OrderBy(s => s.SetupId)
            .Select(s => new OutcomeReasonDto
            {
                Id = s.SetupId,
                Code = s.SetupCode ?? "",
                Label = s.Description ?? s.SetupValue
            })
            .ToListAsync(ct);
    }

    // ---------------- seeding (idempotent, per-BU, lazy on first use) ----------------

    private async Task EnsureReasonsSeededAsync(long businessUnitId, CancellationToken ct)
    {
        var existing = await _context.SetupMasters
            .Where(s => s.SetupType == "QuoteOutcomeReason" && s.BusinessUnitId == businessUnitId)
            .Select(s => s.SetupCode)
            .ToListAsync(ct);
        var existingSet = new HashSet<string>(existing.Where(c => c != null)!, StringComparer.OrdinalIgnoreCase);

        var added = false;
        foreach (var (code, label) in DefaultReasons)
        {
            if (existingSet.Contains(code)) continue;
            _context.SetupMasters.Add(new SetupMaster
            {
                SetupType = "QuoteOutcomeReason",
                SetupCode = code,
                SetupValue = label,
                Description = label,
                BusinessUnitId = businessUnitId,
                IsActive = true,
                CreatedBy = "system:sla-seed",
                CreatedOn = DateTime.UtcNow
            });
            added = true;
        }
        if (added) await _context.SaveChangesAsync(ct);
    }

    private async Task EnsureExpiredStatusSeededAsync(long businessUnitId, CancellationToken ct)
    {
        // Create-if-absent: some tenants only carry the legacy DRAFT/SENT/ACCEPTED/
        // REJECTED QuoteStatus rows. Seed EXPIRED for this BU so the outcome flow
        // and TransitionStatusAsync("EXPIRED") always have a target row.
        var exists = await _context.SetupMasters.AnyAsync(
            s => s.SetupType == "QuoteStatus" && s.SetupCode == "EXPIRED"
                 && s.BusinessUnitId == businessUnitId, ct);
        if (exists) return;

        // A row on ANY BU also satisfies the BU-agnostic fallback resolution.
        var anyBu = await _context.SetupMasters.AnyAsync(
            s => s.SetupType == "QuoteStatus" && s.SetupCode == "EXPIRED", ct);
        if (anyBu) return;

        _context.SetupMasters.Add(new SetupMaster
        {
            SetupType = "QuoteStatus",
            SetupCode = "EXPIRED",
            SetupValue = "Expired",
            Description = "Quote expired without a customer decision",
            BusinessUnitId = businessUnitId,
            IsActive = true,
            CreatedBy = "system:sla-seed",
            CreatedOn = DateTime.UtcNow
        });
        await _context.SaveChangesAsync(ct);
    }

    // ---------------- resolution helpers ----------------

    private const long LegacySentStatusId = 43;

    private async Task<long?> ResolveStatusIdAsync(long businessUnitId, string code, CancellationToken ct)
    {
        var row = await _context.SetupMasters.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SetupType == "QuoteStatus" && s.SetupCode == code
                && s.BusinessUnitId == businessUnitId, ct);
        row ??= await _context.SetupMasters.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SetupType == "QuoteStatus" && s.SetupCode == code, ct);
        return row?.SetupId;
    }

    private async Task<long?> ResolveReasonIdAsync(long businessUnitId, string code, CancellationToken ct)
    {
        var row = await _context.SetupMasters.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SetupType == "QuoteOutcomeReason" && s.SetupCode == code
                && s.BusinessUnitId == businessUnitId, ct);
        row ??= await _context.SetupMasters.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SetupType == "QuoteOutcomeReason" && s.SetupCode == code, ct);
        return row?.SetupId;
    }

    private async Task<bool> IsTerminalStatusAsync(Quote quote, CancellationToken ct)
    {
        if (!quote.StatusId.HasValue) return false;
        var status = await _context.SetupMasters.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SetupId == quote.StatusId.Value, ct);
        var code = status?.SetupCode?.ToUpperInvariant();
        return code is "ACCEPTED" or "REJECTED" or "EXPIRED";
    }

    /// <summary>
    /// Role check for terminal-state corrections: caller's Users row → RoleId →
    /// SetupMaster (SetupType "role") whose name contains "admin" or "manager".
    /// </summary>
    private async Task<bool> IsManagerOrAdminAsync(string actorEmail, long businessUnitId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actorEmail)) return false;

        var email = actorEmail.Trim().ToLower();
        var roleId = await _context.Users.AsNoTracking()
            .Where(u => u.Email.ToLower() == email && (u.Buid == null || u.Buid == businessUnitId))
            .Select(u => u.RoleId)
            .FirstOrDefaultAsync(ct);
        if (roleId is null or 0) return false;

        // The Users lookup above already chains the tenant; this one did not, and it is the half
        // that actually decides the privilege. A role id that also exists in another business unit
        // would have resolved to THAT tenant's role name, so a row called "Manager" elsewhere could
        // authorise correcting a frozen outcome here. SetupMaster carries a global query filter, so
        // this only ever mattered on the null-tenant sweep path — which is precisely where this
        // service runs.
        var roleName = await _context.SetupMasters.AsNoTracking()
            .Where(s => s.SetupId == roleId && s.SetupType == "role" && s.BusinessUnitId == businessUnitId)
            .Select(s => s.SetupValue)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(roleName)) return false;

        var lower = roleName.ToLowerInvariant();
        return lower.Contains("admin") || lower.Contains("manager");
    }

    private static string? Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? null : (value.Length <= max ? value : value[..max]);
}
