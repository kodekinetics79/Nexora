using System.Globalization;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>One offending line in a below-floor check. Delta = floor − price (always &gt; 0).</summary>
public sealed record BelowFloorLine(long RfqItemId, decimal UnitPrice, decimal FloorUnitPrice, decimal Delta);

/// <summary>
/// Result of a below-floor check. Carries the engine preview it was computed
/// from so callers (apply-pricing metric hook) never price the RFQ twice.
/// </summary>
public sealed class BelowFloorCheck
{
    public static readonly BelowFloorCheck Clear = new();

    public List<BelowFloorLine> Lines { get; init; } = new();
    public PricePreview? Preview { get; init; }

    public bool IsBelowFloor => Lines.Count > 0;

    /// <summary>Worst offence as a fraction of the floor (0.12 = 12% below floor).</summary>
    public decimal MaxDeltaPct => Lines.Count == 0
        ? 0m
        : Lines.Max(l => l.FloorUnitPrice > 0 ? l.Delta / l.FloorUnitPrice : 0m);
}

/// <summary>
/// WP-B3 below-floor control. Detects prices under the pricing engine's per-line
/// FloorUnitPrice at the two places money leaves the building (apply-pricing and
/// quote-send) and, instead of proceeding, parks the exact action as a standard
/// <see cref="AgentApproval"/> (ToolName <see cref="ToolName"/>) so the existing
/// approvals inbox + manager-gated approve endpoint govern it. NO new approval
/// entity — the approvals engine is reused verbatim.
///
/// Floors are recomputed via <see cref="IPricingEngine"/> at check time; a line
/// whose floor could not be established (0 / no signals) has NO floor and never
/// blocks. If the RFQ deadline is already inside the tenant's
/// <see cref="SlaPolicy.DeadlineBufferHours"/> when a hold is created, the
/// requester and their manager are alerted immediately (the sweep would be too late).
/// </summary>
public interface IBelowFloorGuard
{
    /// <summary>Compares requested apply-pricing lines against freshly computed floors.</summary>
    /// <exception cref="KeyNotFoundException">RFQ not found in this business unit.</exception>
    Task<BelowFloorCheck> CheckApplyPricingAsync(long rfqId, long businessUnitId, ApplyPricingRequest request, CancellationToken ct);

    /// <summary>
    /// Compares a quote's CURRENT line prices against the floors of its linked RFQ.
    /// Returns <see cref="BelowFloorCheck.Clear"/> when the quote has no RFQ linkage
    /// (nothing to compare against) or does not exist (the send path raises its own 404).
    /// </summary>
    Task<BelowFloorCheck> CheckQuoteSendAsync(long quoteId, long businessUnitId, CancellationToken ct);

    /// <summary>Parks a below-floor apply-pricing request as a pending approval.</summary>
    Task<AgentApproval> CreateApplyPricingHoldAsync(
        long rfqId, long businessUnitId, BelowFloorCheck check,
        long? requestedByUserId, string? requestedBy, CancellationToken ct);

    /// <summary>Parks a below-floor quote send as a pending approval.</summary>
    Task<AgentApproval> CreateSendHoldAsync(
        long quoteId, long businessUnitId, string recipientEmail, string? customSubject, string? customBody,
        BelowFloorCheck check, long? requestedByUserId, string? requestedBy, CancellationToken ct);
}

public sealed class BelowFloorGuard : IBelowFloorGuard
{
    /// <summary>The approvals-engine tool that completes a held action on approve.</summary>
    public const string ToolName = "approve_below_floor_quote";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly ErpRfqAutomationContext _db;
    private readonly IPricingEngine _engine;
    private readonly ISlaNotifications _notifications;
    private readonly ILogger<BelowFloorGuard> _log;

    public BelowFloorGuard(
        ErpRfqAutomationContext db,
        IPricingEngine engine,
        ISlaNotifications notifications,
        ILogger<BelowFloorGuard> log)
    {
        _db = db;
        _engine = engine;
        _notifications = notifications;
        _log = log;
    }

    // ------------------------------------------------------------------ checks

    public async Task<BelowFloorCheck> CheckApplyPricingAsync(
        long rfqId, long businessUnitId, ApplyPricingRequest request, CancellationToken ct)
    {
        if (request?.Lines is not { Count: > 0 }) return BelowFloorCheck.Clear;

        var preview = await _engine.PriceRfqAsync(rfqId, businessUnitId, ct);
        var floorByItem = preview.Lines.Where(l => l.FloorUnitPrice.HasValue)
            .ToDictionary(l => l.RfqItemId, l => l.FloorUnitPrice!.Value);

        // Malformed requests (unknown line, non-positive price, duplicate) are left
        // to the engine's own validation so the caller gets its usual 400 — a hold
        // must only ever park a request that would otherwise have succeeded.
        var seen = new HashSet<long>();
        foreach (var line in request.Lines)
        {
            if (!seen.Add(line.RfqItemId)) return BelowFloorCheck.Clear;
            if (line.UnitPrice <= 0) return BelowFloorCheck.Clear;
            if (!floorByItem.ContainsKey(line.RfqItemId)) return BelowFloorCheck.Clear;
        }

        var offending = request.Lines
            .Where(l => floorByItem[l.RfqItemId] > 0 && l.UnitPrice < floorByItem[l.RfqItemId])
            .Select(l => new BelowFloorLine(
                l.RfqItemId, l.UnitPrice, floorByItem[l.RfqItemId], floorByItem[l.RfqItemId] - l.UnitPrice))
            .ToList();

        return new BelowFloorCheck { Lines = offending, Preview = preview };
    }

    public async Task<BelowFloorCheck> CheckQuoteSendAsync(long quoteId, long businessUnitId, CancellationToken ct)
    {
        var quote = await _db.Quotes.AsNoTracking()
            .Where(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId)
            .Select(q => new
            {
                q.Id,
                q.BusinessUnitId,
                q.Rfqid,
                Items = q.QuoteItems
                    .Where(i => i.RfqitemId != null)
                    .Select(i => new { RfqItemId = i.RfqitemId!.Value, i.UnitPrice })
                    .ToList()
            })
            .FirstOrDefaultAsync(ct);

        // Missing quote → let the send path throw its own KeyNotFoundException;
        // no RFQ linkage / no linked lines → no floors to compare against.
        if (quote is null || quote.Rfqid is null || quote.Items.Count == 0) return BelowFloorCheck.Clear;

        var preview = await _engine.PriceRfqAsync(quote.Rfqid.Value, quote.BusinessUnitId, ct);
        var floorByItem = preview.Lines
            .Where(l => l.FloorUnitPrice > 0)
            .ToDictionary(l => l.RfqItemId, l => l.FloorUnitPrice!.Value);

        var offending = quote.Items
            .Where(i => i.UnitPrice > 0
                        && floorByItem.TryGetValue(i.RfqItemId, out var floor)
                        && i.UnitPrice < floor)
            .Select(i => new BelowFloorLine(
                i.RfqItemId, i.UnitPrice, floorByItem[i.RfqItemId], floorByItem[i.RfqItemId] - i.UnitPrice))
            .ToList();

        return new BelowFloorCheck { Lines = offending, Preview = preview };
    }

    // ------------------------------------------------------------------ holds

    public async Task<AgentApproval> CreateApplyPricingHoldAsync(
        long rfqId, long businessUnitId, BelowFloorCheck check,
        long? requestedByUserId, string? requestedBy, CancellationToken ct)
    {
        var rfqNo = await _db.Rfqs.AsNoTracking()
            .Where(r => r.Id == rfqId && r.BusinessUnitId == businessUnitId)
            .Select(r => r.Rfqno)
            .FirstOrDefaultAsync(ct);
        var label = string.IsNullOrWhiteSpace(rfqNo) ? $"RFQ #{rfqId}" : $"RFQ {rfqNo}";

        var inputJson = JsonSerializer.Serialize(new
        {
            holdType = "apply_pricing",
            rfqId,
            quoteId = (long?)null,
            lines = check.Lines.Select(ToLinePayload)
        }, JsonOpts);

        var approval = await SaveHoldAsync(
            businessUnitId, inputJson, BuildSummary(label, check), requestedByUserId, requestedBy, ct);

        await NotifyIfInsideDeadlineBufferAsync(approval, businessUnitId, rfqId, label, ct);
        return approval;
    }

    public async Task<AgentApproval> CreateSendHoldAsync(
        long quoteId, long businessUnitId, string recipientEmail, string? customSubject, string? customBody,
        BelowFloorCheck check, long? requestedByUserId, string? requestedBy, CancellationToken ct)
    {
        var quote = await _db.Quotes.AsNoTracking()
            .Where(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId)
            .Select(q => new { q.BusinessUnitId, q.QuoteNo, q.Rfqid })
            .FirstAsync(ct);
        var label = $"Quote #{quote.QuoteNo}";

        var inputJson = JsonSerializer.Serialize(new
        {
            holdType = "send_quote",
            quoteId,
            rfqId = quote.Rfqid,
            recipientEmail,
            customSubject,
            customBody,
            lines = check.Lines.Select(ToLinePayload)
        }, JsonOpts);

        var approval = await SaveHoldAsync(
            businessUnitId, inputJson, BuildSummary(label, check), requestedByUserId, requestedBy, ct);

        if (quote.Rfqid.HasValue)
            await NotifyIfInsideDeadlineBufferAsync(approval, businessUnitId, quote.Rfqid.Value, label, ct);
        return approval;
    }

    // ------------------------------------------------------------------ internals

    private static object ToLinePayload(BelowFloorLine l) => new
    {
        rfqItemId = l.RfqItemId,
        unitPrice = l.UnitPrice,
        floorUnitPrice = l.FloorUnitPrice,
        delta = l.Delta
    };

    /// <summary>Plain-language summary, e.g. "Quote #Q-1042: 3 line(s) below floor by up to 12%".</summary>
    private static string BuildSummary(string label, BelowFloorCheck check)
    {
        var pct = (check.MaxDeltaPct * 100m).ToString("0.#", CultureInfo.InvariantCulture);
        return $"{label}: {check.Lines.Count} line(s) below floor by up to {pct}%";
    }

    private async Task<AgentApproval> SaveHoldAsync(
        long businessUnitId, string inputJson, string summary,
        long? requestedByUserId, string? requestedBy, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var approval = new AgentApproval
        {
            Id = Guid.NewGuid(),
            SessionId = null, // not born from a copilot conversation
            BusinessUnitId = businessUnitId,
            ToolName = ToolName,
            InputJson = inputJson,
            Status = AgentApprovalStatus.Pending,
            Summary = summary,
            RequestedByUserId = requestedByUserId,
            RequestedBy = requestedBy,
            CreatedOn = now,
            UpdatedOn = now
        };
        _db.Set<AgentApproval>().Add(approval);

        // Same audit convention as the orchestrator's guardrail "Held" entries.
        _db.Set<AgentAuditLog>().Add(new AgentAuditLog
        {
            BusinessUnitId = businessUnitId,
            Actor = requestedBy ?? (requestedByUserId?.ToString() ?? "system"),
            ToolName = ToolName,
            Decision = "Held",
            InputJson = inputJson,
            ResultSummary = summary,
            CreatedOn = now
        });

        await _db.SaveChangesAsync(ct);
        return approval;
    }

    /// <summary>
    /// WP-B3 rule 3 (creation-time half): when the RFQ's bid-closing date is
    /// already inside the tenant's DeadlineBufferHours, waiting for the periodic
    /// sweep would burn the remaining runway — alert the requester AND their
    /// manager right now, and stamp the sweep's 'escalated' SlaEvent so it never
    /// double-notifies. Never throws (notification failures must not break the hold).
    /// </summary>
    private async Task NotifyIfInsideDeadlineBufferAsync(
        AgentApproval approval, long businessUnitId, long rfqId, string label, CancellationToken ct)
    {
        try
        {
            var bidClosing = await _db.Rfqs.AsNoTracking().IgnoreQueryFilters()
                .Where(r => r.Id == rfqId && r.BusinessUnitId == businessUnitId)
                .Select(r => r.BidClosingDate)
                .FirstOrDefaultAsync(ct);

            // Real dates only — sentinels (< year 2000) mean "unknown" (extraction convention).
            if (!bidClosing.HasValue || bidClosing.Value.Year < 2000) return;

            var policy = await _db.Set<SlaPolicy>().AsNoTracking().IgnoreQueryFilters()
                             .FirstOrDefaultAsync(p => p.BusinessUnitId == businessUnitId, ct)
                         ?? SlaPolicy.Default(businessUnitId);

            var now = DateTime.UtcNow;
            if (bidClosing.Value.AddHours(-policy.DeadlineBufferHours) > now) return; // still outside the buffer

            // Requester (owner) + their manager (Users.ManagerId).
            var requester = await _db.Users.AsNoTracking()
                .Where(u => (approval.RequestedByUserId != null && u.Id == approval.RequestedByUserId)
                            || (approval.RequestedBy != null && u.Email == approval.RequestedBy))
                .Select(u => new { u.Id, u.Email, u.FirstName, u.ManagerId })
                .FirstOrDefaultAsync(ct);

            var detail =
                $"\"{approval.Summary}\" needs an approval decision, and the bid for {label} closes on " +
                $"{bidClosing.Value:dd MMM yyyy HH:mm} UTC — already inside the {policy.DeadlineBufferHours}-hour " +
                "internal safety buffer. Please approve or reject it right away so the quote can still go out in time.";

            var notified = false;
            if (requester is not null && !string.IsNullOrWhiteSpace(requester.Email))
            {
                notified |= await _notifications.SendDeadlineAlertAsync(
                    requester.Email, requester.FirstName, "escalated", label,
                    $"Below-floor approval is blocking {label} — deadline imminent", detail, businessUnitId, ct);

                if (requester.ManagerId.HasValue)
                {
                    var manager = await _db.Users.AsNoTracking()
                        .Where(u => u.Id == requester.ManagerId.Value)
                        .Select(u => new { u.Email, u.FirstName })
                        .FirstOrDefaultAsync(ct);
                    if (manager is not null && !string.IsNullOrWhiteSpace(manager.Email))
                    {
                        notified |= await _notifications.SendDeadlineAlertAsync(
                            manager.Email, manager.FirstName, "escalated", label,
                            $"Below-floor approval is blocking {label} — deadline imminent", detail, businessUnitId, ct);
                    }
                }
            }

            if (notified)
            {
                // Same (BU, "approval", guid-derived key, "escalated") dedup row the
                // sweep checks before escalating — one escalation per approval, ever.
                var entityKey = BitConverter.ToInt64(approval.Id.ToByteArray(), 0);
                _db.Set<SlaEvent>().Add(new SlaEvent
                {
                    BusinessUnitId = businessUnitId,
                    EntityType = "approval",
                    EntityId = entityKey,
                    Level = "escalated",
                    CreatedOn = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex,
                "Below-floor hold {ApprovalId}: immediate deadline-buffer notification failed (hold itself is intact).",
                approval.Id);
        }
    }
}
