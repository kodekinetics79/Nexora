using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Reporting;

public interface IGrossMarginService
{
    Task<GrossMarginDTO> GetAsync(long businessUnitId, DateTime from, DateTime to,
        DateTime generatedAt, CancellationToken ct = default);
}

/// <summary>
/// Value-weighted gross margin computed from the immutable quote-time sourcing decision.
///
/// <para><b>Cost basis.</b> <see cref="CustomerQuoteSourcingDecision.SupplierLandedUnitCost"/> is
/// the landed cost the customer price was derived from — <c>price = landed / (1 − margin)</c> in
/// <c>SupplierQuotes/SupplierQuoteCommercialService.cs</c> — and both figures sit on one row in one
/// currency (<c>CurrencyId</c>). That is why cost and revenue are read from the same record rather
/// than joined from two places: they cannot fall out of step.</para>
///
/// <para><b>Re-pricing.</b> The table's only uniqueness is on <c>IdempotencyKey</c>, and its
/// <c>(BusinessUnitId, QuoteItemId, CreatedOn)</c> index is deliberately non-unique — a re-priced
/// line inserts a SECOND decision. Summing them all would double-count the line, so the latest
/// decision per quote line wins.</para>
///
/// <para><b>Fail closed.</b> Every conversion goes through <see cref="IFxConversionService"/>,
/// whose contract nulls the total rather than blending unconvertible currencies. A null total here
/// produces <c>unavailable</c> with the reason verbatim, not a partial number.</para>
/// </summary>
public sealed class GrossMarginService : IGrossMarginService
{
    private readonly ErpRfqAutomationContext _db;

    /// <summary>
    /// Quote statuses that mean the customer accepted. ORDERED is included because a quote that
    /// became an order was accepted first; excluding it would drop the tenant's largest wins from
    /// the sample. This is the same set the pipeline funnel calls "won", so the margin and the
    /// funnel's won value reconcile to the same population instead of quietly disagreeing.
    /// </summary>
    private static readonly (string Code, string Display, long? LegacyId)[] AcceptedStatuses =
    {
        ("ACCEPTED", "Accepted", 44L),
        ("ORDERED", "Ordered", null)
    };

    public const string AcceptedDefinitionText =
        "Quote status ACCEPTED or ORDERED, dated by Quote.OutcomeOn.";

    public GrossMarginService(ErpRfqAutomationContext db) => _db = db;

    public async Task<GrossMarginDTO> GetAsync(long businessUnitId, DateTime from, DateTime to,
        DateTime generatedAt, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (from >= to) throw new ArgumentException("The reporting window is invalid.", nameof(from));

        var result = new GrossMarginDTO
        {
            PeriodFrom = from,
            PeriodTo = to,
            GeneratedAt = generatedAt,
            AcceptedDefinition = AcceptedDefinitionText
        };

        var acceptedStatusIds = await ResolveAcceptedStatusIdsAsync(ct);
        if (acceptedStatusIds.Count == 0)
        {
            result.Reason = "This business unit has no ACCEPTED or ORDERED quote status configured, "
                            + "so no quote can be identified as accepted.";
            return result;
        }

        // Accepted quotes that carry NO acceptance date belong to no period. They are reported, not
        // folded into the current window by a coalesce — that fallback is precisely how a figure
        // starts describing a population nobody chose.
        result.QuotesExcludedForMissingAcceptanceDate = await _db.Quotes.AsNoTracking()
            .CountAsync(q => q.BusinessUnitId == businessUnitId
                             && q.StatusId != null && acceptedStatusIds.Contains(q.StatusId.Value)
                             && q.OutcomeOn == null, ct);

        var acceptedInWindow = _db.Quotes.AsNoTracking()
            .Where(q => q.BusinessUnitId == businessUnitId
                        && q.StatusId != null && acceptedStatusIds.Contains(q.StatusId.Value)
                        && q.OutcomeOn != null && q.OutcomeOn >= from && q.OutcomeOn < to);

        result.AcceptedQuoteLines = await _db.QuoteItems.AsNoTracking()
            .CountAsync(qi => acceptedInWindow.Any(q => q.Id == qi.QuoteId), ct);

        var decisions = await _db.Set<CustomerQuoteSourcingDecision>().AsNoTracking()
            .Where(d => d.BusinessUnitId == businessUnitId
                        && acceptedInWindow.Any(q => q.Id == d.QuoteId))
            .Select(d => new DecisionRow(d.Id, d.QuoteId, d.QuoteItemId, d.Quantity,
                d.SupplierLandedUnitCost, d.CustomerUnitPrice, d.CurrencyId, d.CreatedOn))
            .ToListAsync(ct);

        // One decision per quote line: the latest. See the class remarks on re-pricing.
        var sample = decisions
            .GroupBy(d => d.QuoteItemId)
            .Select(g => g.OrderByDescending(d => d.CreatedOn).ThenByDescending(d => d.Id).First())
            .ToList();

        result.SampleLines = sample.Count;
        result.SampleQuotes = sample.Select(d => d.QuoteId).Distinct().Count();
        result.LinesWithoutSourcingEvidence = Math.Max(0, result.AcceptedQuoteLines - sample.Count);

        if (sample.Count == 0)
        {
            result.Reason = result.AcceptedQuoteLines == 0
                ? "No quote was accepted in this window, so there is nothing to measure."
                : $"None of the {result.AcceptedQuoteLines} accepted quote line(s) in this window "
                  + "carries a sourcing decision, so no price can be traced to the landed cost it was built on.";
            await ApplyCostBasisDisclosureAsync(businessUnitId, result, sample, ct);
            return result;
        }

        var fx = new FxConversionService(_db);
        var totals = await ConvertAsync(fx, businessUnitId, sample, generatedAt, ct);
        result.CurrencyCode = totals.Revenue.TargetCurrencyCode;

        await ApplyCostBasisDisclosureAsync(businessUnitId, result, sample, ct);

        if (totals.Revenue.Total is not { } revenue || totals.Cost.Total is not { } cost)
        {
            result.Reason = totals.Revenue.UnavailableReason ?? totals.Cost.UnavailableReason
                ?? "The sample spans currencies with no approved exchange rate, so it cannot be totalled.";
            return result;
        }

        if (revenue <= 0m)
        {
            result.Reason = "The accepted value in this window totals zero or less, so a margin percentage "
                            + "has no denominator.";
            return result;
        }

        result.RevenueTotal = revenue;
        result.CostTotal = cost;
        result.MarginPercent = Percent(revenue, cost);
        result.Status = GrossMarginStatuses.Available;

        // The comparable subset, when — and only when — the window straddles the correction.
        if (result.CostBasisChangedOn is { } boundary && result.LinesOnPriorCostBasis > 0
            && result.LinesOnCurrentCostBasis > 0)
        {
            var current = sample.Where(d => d.CreatedOn >= boundary).ToList();
            var currentTotals = await ConvertAsync(fx, businessUnitId, current, generatedAt, ct);
            if (currentTotals.Revenue.Total is { } currentRevenue and > 0m
                && currentTotals.Cost.Total is { } currentCost)
            {
                result.MarginPercentCurrentBasisOnly = Percent(currentRevenue, currentCost);
            }
        }

        return result;
    }

    private static decimal Percent(decimal revenue, decimal cost) =>
        Math.Round((revenue - cost) / revenue * 100m, 1, MidpointRounding.AwayFromZero);

    private static async Task<(FxTotalResult Revenue, FxTotalResult Cost)> ConvertAsync(
        FxConversionService fx, long businessUnitId, IReadOnlyList<DecisionRow> rows,
        DateTime asOf, CancellationToken ct)
    {
        var revenue = await fx.TotalAsync(businessUnitId,
            rows.Select(d => new FxAmount(d.CustomerUnitPrice * d.Quantity, d.CurrencyId)).ToArray(), asOf, null, ct);
        var cost = await fx.TotalAsync(businessUnitId,
            rows.Select(d => new FxAmount(d.SupplierLandedUnitCost * d.Quantity, d.CurrencyId)).ToArray(), asOf, null, ct);
        return (revenue, cost);
    }

    /// <summary>
    /// Splits the sample across the input-tax-recoverability correction and writes the disclosure.
    ///
    /// <para>The boundary is read from the tenant governance ledger, not guessed: the policy write
    /// path records a <c>COMMERCIAL_POLICY_UPDATED</c> event carrying a before/after snapshot
    /// (<c>OrderToCash/CommercialMatchingPolicyService.cs</c>). Only events where the recoverable
    /// percent ACTUALLY moved count — using the policy row's <c>ModifiedOn</c> instead would flag a
    /// basis change every time someone adjusted a price tolerance, and a disclosure that fires on
    /// everything is one people learn to skip.</para>
    ///
    /// <para>Decision rows carry no basis stamp of their own — R20 ratified a full recompute before
    /// pilot rather than a calculation-version column — so <c>CreatedOn</c> against the ledger
    /// timestamp is the only evidence available, and the note says exactly that.</para>
    /// </summary>
    private async Task ApplyCostBasisDisclosureAsync(long businessUnitId, GrossMarginDTO result,
        IReadOnlyList<DecisionRow> sample, CancellationToken ct)
    {
        var boundary = await ResolveCostBasisChangeAsync(businessUnitId, ct);
        result.CostBasisChangedOn = boundary;

        if (boundary is null)
        {
            result.LinesOnCurrentCostBasis = sample.Count;
            result.CostBasisNote = sample.Count == 0
                ? null
                : "All sampled landed costs were recorded under the input-tax recoverability currently "
                  + "in force; the tenant governance ledger records no change to it.";
            return;
        }

        result.LinesOnPriorCostBasis = sample.Count(d => d.CreatedOn < boundary.Value);
        result.LinesOnCurrentCostBasis = sample.Count - result.LinesOnPriorCostBasis;

        if (sample.Count == 0) return;

        result.CostBasisNote = result.LinesOnPriorCostBasis == 0
            ? $"Input-tax recoverability changed on {boundary.Value:dd MMM yyyy}; every sampled landed "
              + "cost was recorded after it, so the figure rests on a single basis."
            : result.LinesOnCurrentCostBasis == 0
                ? $"Input-tax recoverability changed on {boundary.Value:dd MMM yyyy}; every sampled landed "
                  + "cost was recorded BEFORE it, so this figure is stated on the superseded basis and is "
                  + "not comparable with periods after that date."
                : $"This figure blends two cost bases. Input-tax recoverability changed on "
                  + $"{boundary.Value:dd MMM yyyy}: {result.LinesOnPriorCostBasis} sampled line(s) were "
                  + $"priced before it and {result.LinesOnCurrentCostBasis} after. Sourcing decisions carry "
                  + "no basis stamp, so the split is inferred from when each decision was recorded. Use the "
                  + "current-basis figure for comparison until the historical recompute has run.";
    }

    private async Task<DateTime?> ResolveCostBasisChangeAsync(long businessUnitId, CancellationToken ct)
    {
        var events = await _db.TenantGovernanceAuditEvents.AsNoTracking()
            .Where(e => e.BusinessUnitId == businessUnitId
                        && e.AggregateType == nameof(CommercialMatchingPolicy)
                        && e.Action == CommercialMatchingPolicyService.ActionPolicyUpdated)
            .OrderByDescending(e => e.OccurredOn)
            .Select(e => new { e.OccurredOn, e.EvidenceJson })
            .ToListAsync(ct);

        foreach (var entry in events)
        {
            if (ChangedRecoverablePercent(entry.EvidenceJson)) return entry.OccurredOn;
        }

        return null;
    }

    /// <summary>
    /// True when this ledger entry actually moved the recoverable percent. A malformed or
    /// unrecognised payload returns false: an unreadable evidence blob is not evidence of a change,
    /// and inventing one would put a false disclosure on the board.
    /// </summary>
    internal static bool ChangedRecoverablePercent(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson)) return false;

        try
        {
            using var document = JsonDocument.Parse(evidenceJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("before", out var before)
                || before.ValueKind != JsonValueKind.Object) return false;
            if (!document.RootElement.TryGetProperty("after", out var after)
                || after.ValueKind != JsonValueKind.Object) return false;

            const string field = nameof(CommercialMatchingPolicy.SupplierInputTaxRecoverablePercent);
            if (!before.TryGetProperty(field, out var beforeValue)
                || !after.TryGetProperty(field, out var afterValue)) return false;
            if (beforeValue.ValueKind != JsonValueKind.Number || afterValue.ValueKind != JsonValueKind.Number)
                return false;

            return beforeValue.GetDecimal() != afterValue.GetDecimal();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Mirrors <c>DashboardRepository.ResolveStatusIdsAsync</c>: SetupCode first, case-insensitive
    /// display value second, plus the documented legacy id for tenants that predate the
    /// <c>QuoteStatus</c> rows. Kept case-folded on both sides because production stores mixed case.
    /// </summary>
    private async Task<List<long>> ResolveAcceptedStatusIdsAsync(CancellationToken ct)
    {
        var ids = new List<long>();

        foreach (var status in AcceptedStatuses)
        {
            var code = status.Code;
            var displayLower = status.Display.ToLowerInvariant();

            var matches = await _db.SetupMasters.AsNoTracking()
                .Where(s => s.SetupType.ToLower() == "quotestatus"
                            && (s.SetupCode == code || s.SetupValue.ToLower() == displayLower))
                .Select(s => s.SetupId)
                .ToListAsync(ct);

            ids.AddRange(matches);
            if (status.LegacyId is { } legacy) ids.Add(legacy);
        }

        return ids.Distinct().ToList();
    }

    private sealed record DecisionRow(long Id, long QuoteId, long QuoteItemId, decimal Quantity,
        decimal SupplierLandedUnitCost, decimal CustomerUnitPrice, long CurrencyId, DateTime CreatedOn);
}
