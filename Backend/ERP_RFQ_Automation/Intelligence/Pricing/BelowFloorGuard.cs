using System.Globalization;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Fx;
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

    /// <summary>
    /// Lines whose price could NOT be compared to their floor because the two sides are in
    /// different currencies and no approved FX rate joins them. These are not "clear" — an
    /// uncomparable price is precisely the case where money must not leave the building
    /// unreviewed, so they hold the action exactly as a genuine below-floor line does.
    /// </summary>
    public List<string> CurrencyBlockers { get; init; } = new();

    /// <summary>
    /// True when the action must be held. Fail-closed: an unresolvable currency counts, because
    /// "we could not check" and "the check passed" must never be the same answer.
    /// </summary>
    public bool IsBelowFloor => Lines.Count > 0 || CurrencyBlockers.Count > 0;

    /// <summary>True when the hold is due to missing FX evidence rather than a real breach.</summary>
    public bool RequiresFxEvidence => CurrencyBlockers.Count > 0;

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

        // FX fix: ApplyPricingLine carries a bare decimal with no currency (PricingModels.cs), so
        // the submitted prices are only meaningful if every RFQ line being priced shares one
        // currency. When the touched lines span currencies, or a line's currency is unresolved,
        // the request is uncomparable and is held rather than silently accepted.
        var touched = request.Lines.Select(l => l.RfqItemId).ToHashSet();
        var touchedCurrencies = preview.Lines
            .Where(l => touched.Contains(l.RfqItemId))
            .Select(l => string.IsNullOrWhiteSpace(l.Currency) ? null : l.Currency.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        var applyBlockers = new List<string>();
        if (touchedCurrencies.Any(c => c is null))
            applyBlockers.Add("At least one priced line has no resolved currency; an approved FX comparison is " +
                              "required before prices can be applied.");
        else if (touchedCurrencies.Count > 1)
            applyBlockers.Add($"The priced lines span {touchedCurrencies.Count} currencies " +
                              $"({string.Join(", ", touchedCurrencies)}); submitted prices carry no currency of their " +
                              "own, so they cannot be applied without an approved FX comparison.");

        return new BelowFloorCheck { Lines = offending, Preview = preview, CurrencyBlockers = applyBlockers };
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
                q.CurrencyId,
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
        var floorLines = preview.Lines
            .Where(l => l.FloorUnitPrice > 0)
            .ToDictionary(l => l.RfqItemId, l => l);

        // FX fix: the quote line price (denominated by Quote.CurrencyId) used to be compared
        // directly against PriceLine.FloorUnitPrice (denominated by PriceLine.Currency, from the
        // RFQ line). Neither side's currency was read, so a EUR price numerically above a USD
        // floor sailed through the send gate — a fail-OPEN control. Prices are now converted into
        // the floor's currency before the comparison, and any line that cannot be converted
        // becomes a blocker rather than a pass.
        var fx = new FxConversionService(_db);
        // Tenant predicate is explicit and matches the currencyIdByCode lookup two lines below,
        // which always had one. Currencies now carry a global query filter too, but this guard
        // also runs from the SLA sweep under a null tenant context where that filter is a no-op —
        // and the code resolved here decides whether a quote price is compared against the floor
        // in the right currency, so resolving another tenant's Currency row by primary key alone
        // would silently mis-price the send gate.
        var quoteCurrencyCode = quote.CurrencyId is null
            ? null
            : await _db.Currencies.AsNoTracking()
                .Where(c => c.Id == quote.CurrencyId.Value && c.BusinessUnitId == businessUnitId)
                .Select(c => c.Code).FirstOrDefaultAsync(ct);
        var currencyIdByCode = await _db.Currencies.AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId)
            .ToDictionaryAsync(c => c.Code.ToUpperInvariant(), c => c.Id, ct);

        var offending = new List<BelowFloorLine>();
        var blockers = new List<string>();
        var rateCache = new Dictionary<long, decimal?>();

        foreach (var item in quote.Items)
        {
            if (item.UnitPrice <= 0) continue;
            if (!floorLines.TryGetValue(item.RfqItemId, out var floorLine)) continue;
            var floor = floorLine.FloorUnitPrice!.Value;

            var comparablePrice = item.UnitPrice;
            var sameCurrency =
                !string.IsNullOrWhiteSpace(quoteCurrencyCode) &&
                !string.IsNullOrWhiteSpace(floorLine.Currency) &&
                string.Equals(quoteCurrencyCode.Trim(), floorLine.Currency.Trim(), StringComparison.OrdinalIgnoreCase);

            if (!sameCurrency)
            {
                if (quote.CurrencyId is null || string.IsNullOrWhiteSpace(floorLine.Currency) ||
                    !currencyIdByCode.TryGetValue(floorLine.Currency.Trim().ToUpperInvariant(), out var floorCurrencyId))
                {
                    blockers.Add($"Line {item.RfqItemId}: the quote price and the floor are in different or " +
                                 "unidentified currencies; an approved FX rate is required before the floor can be checked.");
                    continue;
                }

                if (!rateCache.TryGetValue(floorCurrencyId, out var rate))
                {
                    var resolution = await fx.ResolveRateAsync(businessUnitId, quote.CurrencyId.Value, floorCurrencyId, DateTime.UtcNow, ct);
                    rate = resolution.Found ? resolution.Rate : (decimal?)null;
                    rateCache[floorCurrencyId] = rate;
                }
                if (rate is null)
                {
                    blockers.Add($"Line {item.RfqItemId}: no approved {quoteCurrencyCode} to {floorLine.Currency} " +
                                 "exchange rate is effective today; the floor cannot be checked.");
                    continue;
                }
                comparablePrice = FxConversionService.RoundMoney(item.UnitPrice * rate.Value);
            }

            if (comparablePrice < floor)
                offending.Add(new BelowFloorLine(item.RfqItemId, comparablePrice, floor, floor - comparablePrice));
        }

        return new BelowFloorCheck { Lines = offending, Preview = preview, CurrencyBlockers = blockers };
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
            // Both lookups chain the tenant, matching SlaSweepWorker.ResolveRequesterManagerAsync.
            // This runs from a background sweep, so the Users global query filter is a no-op here;
            // without the predicate a same-id or same-email hit in another business unit would be
            // handed straight to SendDeadlineAlertAsync below, emailing another tenant's user the
            // summary, label and closing date of this tenant's quote.
            var requester = await _db.Users.AsNoTracking()
                .Where(u => (approval.RequestedByUserId != null && u.Id == approval.RequestedByUserId)
                            || (approval.RequestedBy != null && u.Email == approval.RequestedBy))
                .Where(u => u.Buid == null || u.Buid == businessUnitId)
                .Select(u => new { u.Id, u.Email, u.FirstName, u.ManagerId })
                .FirstOrDefaultAsync(ct);

            var detail =
                $"\"{approval.Summary}\" needs an approval decision, and the bid for {label} closes on " +
                $"{bidClosing.Value:dd MMM yyyy HH:mm} UTC — already inside the {policy.DeadlineBufferHours}-hour " +
                "internal safety buffer. Please approve or reject it right away so the quote can still go out in time.";

            var outcomes = new List<SlaSendResult>();
            if (requester is not null && !string.IsNullOrWhiteSpace(requester.Email))
            {
                outcomes.Add(await _notifications.SendDeadlineAlertAsync(
                    requester.Email, requester.FirstName, "escalated", label,
                    $"Below-floor approval is blocking {label} — deadline imminent", detail, businessUnitId, ct));

                if (requester.ManagerId.HasValue)
                {
                    var manager = await _db.Users.AsNoTracking()
                        .Where(u => u.Id == requester.ManagerId.Value
                                    && (u.Buid == null || u.Buid == businessUnitId))
                        .Select(u => new { u.Email, u.FirstName })
                        .FirstOrDefaultAsync(ct);
                    if (manager is not null && !string.IsNullOrWhiteSpace(manager.Email))
                    {
                        outcomes.Add(await _notifications.SendDeadlineAlertAsync(
                            manager.Email, manager.FirstName, "escalated", label,
                            $"Below-floor approval is blocking {label} — deadline imminent", detail, businessUnitId, ct));
                    }
                }
            }

            // Stamp whenever the provider MAY already hold a copy, not only on a proven send.
            // Withholding the stamp because the outcome was uncertain hands the 5-minute sweep
            // permission to escalate the same approval again — the duplicate this ledger exists
            // to prevent. An outcome of NotSent is the one case where nothing left the building,
            // and there the sweep SHOULD pick it up.
            var accepted = outcomes.FirstOrDefault(x => x.Outcome == SlaSendOutcome.Sent);
            var notified = outcomes.Any(x => x.Outcome != SlaSendOutcome.NotSent);
            if (notified)
            {
                // Same (BU, "approval", guid-derived key, "escalated") dedup row the
                // sweep checks before escalating — one escalation per approval, ever.
                //
                // THE DEDUP KEY IS NOT OPTIONAL. SlaEvent.DedupKey carries the UNIQUE index
                // (UX_SlaEvents_BU_DedupKey) and SlaSweepWorker.TryClaimEventAsync matches on
                // that column ALONE — not on EntityType/EntityId/Level. A row written with the
                // default empty string is therefore invisible to the sweep, which re-sent this
                // exact escalation to the requester's manager on its next 5-minute tick; and
                // because every such row shared the key '', the SECOND below-floor
                // deadline-buffer hold in a tenant collided on 23505 and left a poisoned Added
                // entity on this request-scoped DbContext.
                //
                // Routed through the sweep's own claim insert so the key is built in exactly
                // one place and a genuine collision reads as "already escalated" (detached,
                // null) instead of poisoning the context.
                //
                // The RECIPIENT-LESS key is deliberate here: this path mails the requester and
                // their manager together, and the sweep suppresses on this legacy form as well
                // as on its own recipient-scoped keys, so the two paths still never
                // double-notify (see SlaSweepWorker.TryClaimEventAsync).
                var entityKey = BitConverter.ToInt64(approval.Id.ToByteArray(), 0);
                var claim = await SlaSweepWorker.InsertClaimAsync(
                    _db, businessUnitId, "approval", entityKey, "escalated",
                    SlaEvent.BuildDedupKey("approval", entityKey, "escalated"), ct);

                if (claim is null)
                    _log.LogDebug(
                        "Below-floor hold {ApprovalId}: escalation was already stamped for BU {Bu}; not re-claimed.",
                        approval.Id, businessUnitId);
                else
                    // The stamp is written AFTER the send here, so it records an outcome rather
                    // than claiming one. Accepted evidence when there is any; UNCERTAIN when the
                    // provider may hold a copy it never acknowledged — either way the key is
                    // occupied and the sweep will not send it again.
                    await SlaSweepWorker.SettleClaimAsync(_db, claim,
                        accepted is not null ? SlaEventStatuses.Sent : SlaEventStatuses.Uncertain,
                        accepted is not null ? null : "ACCEPTANCE_EVIDENCE_MISSING",
                        accepted?.Provider, accepted?.AcceptanceReference, ct);
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
