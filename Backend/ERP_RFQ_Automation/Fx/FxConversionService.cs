using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Fx;

/// <summary>
/// The single conversion authority for the commercial side.
///
/// Design notes
/// ------------
/// * PRECISION follows GeneralLedger exactly: rates are held and rounded at 10dp
///   (GeneralLedgerService.cs:664), money at 2dp away-from-zero (GeneralLedgerService.cs:742).
///   A rate that crosses into the GL therefore cannot change scale on the way.
/// * EFFECTIVE DATING follows the dominant house convention (EffectiveFrom inclusive,
///   nullable EffectiveTo exclusive) used by CommercialFinance and CommercialRouting.
/// * APPROVAL GATING: only FxRateStatuses.Approved rows are visible. A pending rate has no
///   effect on any total.
/// * REPRODUCIBILITY: FxRateSnapshot freezes the rate against a document. Once frozen it is
///   replayed verbatim, so re-dating or correcting an FxRate cannot restate a converted quote.
/// * FAIL CLOSED: every path either produces a rate from approved evidence or reports an
///   explicit reason. Nothing ever falls back to 1, to Currency.ExchangeRate, or to skipping
///   the unconvertible rows and summing the rest.
///
/// Every query filters BusinessUnitId explicitly rather than relying on a global query filter,
/// because Currency (and therefore this module) has no tenant filter registered — see the
/// convention note at the top of Models/ErpRfqAutomationContext.Fx.cs.
/// </summary>
public sealed class FxConversionService : IFxConversionService
{
    private readonly ErpRfqAutomationContext _context;

    public FxConversionService(ErpRfqAutomationContext context) => _context = context;

    /// <summary>Money scale, identical to GeneralLedgerService.Round.</summary>
    public static decimal RoundMoney(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Rate scale, identical to GeneralLedgerService NormalizeLines.</summary>
    public static decimal RoundRate(decimal value) => decimal.Round(value, 10, MidpointRounding.AwayFromZero);

    public async Task<long?> ResolveBaseCurrencyIdAsync(long businessUnitId, CancellationToken ct = default)
    {
        // Take(2): the "exactly one base currency" rule is enforced only in application code
        // (CurrencyRepository), never by the database. Two rows means the answer is ambiguous,
        // and an ambiguous base currency must not be guessed — the same defensive read used by
        // ProductRepository.cs:683 and CommercialLineResolutionApplicationService.cs:142.
        var bases = await _context.Currencies.AsNoTracking()
            .Where(c => c.BusinessUnitId == businessUnitId && c.IsBaseCurrency == true && c.IsActive == true)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .Take(2)
            .ToArrayAsync(ct);
        return bases.Length == 1 ? bases[0] : null;
    }

    public async Task<FxRateResolution> ResolveRateAsync(long businessUnitId, long fromCurrencyId,
        long toCurrencyId, DateTime asOf, CancellationToken ct = default)
    {
        if (fromCurrencyId == toCurrencyId)
            return FxRateResolution.Identity(asOf);

        var direct = await FindRateAsync(businessUnitId, fromCurrencyId, toCurrencyId, asOf, ct);
        if (direct is not null)
            return new FxRateResolution(true, RoundRate(direct.Rate), direct.Id, direct.EffectiveFrom,
                FxResolutionPaths.Direct, null);

        // Reciprocal. A trading house stores USD->AED and EUR->AED against an AED base; the
        // AED->USD direction is the same evidence read backwards, so it is admissible, but it
        // is recorded as Inverse so an auditor can see the rate was not quoted directly.
        var inverse = await FindRateAsync(businessUnitId, toCurrencyId, fromCurrencyId, asOf, ct);
        if (inverse is not null && inverse.Rate > 0m)
            return new FxRateResolution(true, RoundRate(1m / inverse.Rate), inverse.Id, inverse.EffectiveFrom,
                FxResolutionPaths.Inverse, null);

        // Cross through the base currency, e.g. EUR->AED->USD.
        var baseCurrencyId = await ResolveBaseCurrencyIdAsync(businessUnitId, ct);
        if (baseCurrencyId is not null && baseCurrencyId != fromCurrencyId && baseCurrencyId != toCurrencyId)
        {
            var leg1 = await ResolveDirectOrInverseAsync(businessUnitId, fromCurrencyId, baseCurrencyId.Value, asOf, ct);
            var leg2 = await ResolveDirectOrInverseAsync(businessUnitId, baseCurrencyId.Value, toCurrencyId, asOf, ct);
            if (leg1 is not null && leg2 is not null)
            {
                // The legs are multiplied at full decimal width and rounded once, so the cross
                // rate does not accumulate two roundings.
                var crossed = RoundRate(leg1.Rate * leg2.Rate);
                if (crossed > 0m)
                    return new FxRateResolution(true, crossed, null,
                        leg1.EffectiveFrom > leg2.EffectiveFrom ? leg1.EffectiveFrom : leg2.EffectiveFrom,
                        FxResolutionPaths.Triangulated, null);
            }
        }

        var codes = await CurrencyCodesAsync(businessUnitId, new[] { fromCurrencyId, toCurrencyId }, ct);
        var fromCode = codes.TryGetValue(fromCurrencyId, out var f) ? f : fromCurrencyId.ToString();
        var toCode = codes.TryGetValue(toCurrencyId, out var t) ? t : toCurrencyId.ToString();
        return FxRateResolution.Missing(
            $"No approved {fromCode} to {toCode} exchange rate is effective on {asOf:yyyy-MM-dd}; " +
            "record an approved rate before this amount can be converted.");
    }

    public async Task<FxRateResolution> ConvertAsync(long businessUnitId, FxAmount amount, long toCurrencyId,
        DateTime asOf, CancellationToken ct = default)
    {
        if (amount.CurrencyId is null)
            return FxRateResolution.Missing(
                "An amount carries no currency; an approved FX comparison is required before it can be totalled.");

        var resolution = await ResolveRateAsync(businessUnitId, amount.CurrencyId.Value, toCurrencyId, asOf, ct);
        return resolution;
    }

    public Task<FxTotalResult> TotalAsync(long businessUnitId, IReadOnlyCollection<FxAmount> amounts,
        DateTime asOf, long? targetCurrencyId = null, CancellationToken ct = default)
        => TotalCoreAsync(businessUnitId, amounts, asOf, targetCurrencyId, null, ct);

    public Task<FxTotalResult> TotalForDocumentAsync(long businessUnitId, string documentType, long documentId,
        IReadOnlyCollection<FxAmount> amounts, DateTime asOf, long? targetCurrencyId = null,
        CancellationToken ct = default)
        => TotalCoreAsync(businessUnitId, amounts, asOf, targetCurrencyId, (documentType, documentId), ct);

    private async Task<FxTotalResult> TotalCoreAsync(long businessUnitId, IReadOnlyCollection<FxAmount> amounts,
        DateTime asOf, long? targetCurrencyId, (string Type, long Id)? document, CancellationToken ct)
    {
        var target = targetCurrencyId ?? await ResolveBaseCurrencyIdAsync(businessUnitId, ct);
        if (target is null)
            return new FxTotalResult(false, null, null, null,
                "This business unit has no single active base currency; set exactly one base currency before " +
                "cross-currency totals can be reported.",
                await BareComponentsAsync(businessUnitId, amounts, ct));

        var codeIds = amounts.Where(a => a.CurrencyId.HasValue).Select(a => a.CurrencyId!.Value).ToList();
        codeIds.Add(target.Value);
        var codes = await CurrencyCodesAsync(businessUnitId, codeIds.Distinct().ToArray(), ct);
        var targetCode = codes.TryGetValue(target.Value, out var tc) ? tc : null;

        if (amounts.Count == 0)
            return FxTotalResult.Empty(target, targetCode);

        // Snapshots frozen against this document win over any live rate.
        var snapshots = document is null
            ? new Dictionary<(long, long), FxRateSnapshot>()
            : (await GetSnapshotsAsync(businessUnitId, document.Value.Type, document.Value.Id, ct))
                .ToDictionary(s => (s.FromCurrencyId, s.ToCurrencyId));

        var groups = amounts
            .GroupBy(a => a.CurrencyId)
            .Select(g => new { CurrencyId = g.Key, Subtotal = g.Sum(x => x.Amount), RowCount = g.Count() })
            .OrderBy(g => g.CurrencyId ?? long.MinValue)
            .ToList();

        var components = new List<FxCurrencyComponent>(groups.Count);
        decimal running = 0m;
        var allConverted = true;
        string? firstReason = null;

        foreach (var group in groups)
        {
            var code = group.CurrencyId.HasValue && codes.TryGetValue(group.CurrencyId.Value, out var c) ? c : null;
            var subtotal = RoundMoney(group.Subtotal);

            if (group.CurrencyId is null)
            {
                const string reason =
                    "Some amounts carry no currency; an approved FX comparison is required before they can be totalled.";
                allConverted = false;
                firstReason ??= reason;
                components.Add(new FxCurrencyComponent(null, null, subtotal, group.RowCount, false, null, null, reason));
                continue;
            }

            FxRateResolution resolution;
            if (snapshots.TryGetValue((group.CurrencyId.Value, target.Value), out var snapshot))
                resolution = new FxRateResolution(true, snapshot.Rate, snapshot.FxRateId, snapshot.RateEffectiveFrom,
                    FxResolutionPaths.Snapshot, null);
            else
                resolution = await ResolveRateAsync(businessUnitId, group.CurrencyId.Value, target.Value, asOf, ct);

            if (!resolution.Found)
            {
                allConverted = false;
                firstReason ??= resolution.Reason;
                components.Add(new FxCurrencyComponent(group.CurrencyId, code, subtotal, group.RowCount,
                    false, null, null, resolution.Reason));
                continue;
            }

            // Round once, after applying the rate — the GL rule (Round(debit * rate)).
            var converted = RoundMoney(group.Subtotal * resolution.Rate);
            running += converted;
            components.Add(new FxCurrencyComponent(group.CurrencyId, code, subtotal, group.RowCount,
                true, resolution.Rate, converted, null));
        }

        return allConverted
            ? new FxTotalResult(true, RoundMoney(running), target, targetCode, null, components)
            // Fail closed: no partial total is emitted, but the honest per-currency breakdown is.
            : new FxTotalResult(false, null, target, targetCode, firstReason, components);
    }

    public async Task<FxRateSnapshot> CaptureSnapshotAsync(long businessUnitId, string documentType, long documentId,
        long fromCurrencyId, long toCurrencyId, DateTime asOf, string capturedBy, CancellationToken ct = default)
    {
        documentType = Require(documentType, "document type", 40);
        capturedBy = Require(capturedBy, "authenticated actor", 255);
        if (documentId <= 0) throw new FxConversionException("A valid document id is required to capture an FX snapshot.");

        var existing = await _context.Set<FxRateSnapshot>()
            .FirstOrDefaultAsync(s => s.BusinessUnitId == businessUnitId && s.DocumentType == documentType &&
                s.DocumentId == documentId && s.FromCurrencyId == fromCurrencyId && s.ToCurrencyId == toCurrencyId, ct);
        // Idempotent by design: re-capturing must not move a rate an auditor has already been shown.
        if (existing is not null) return existing;

        var resolution = await ResolveRateAsync(businessUnitId, fromCurrencyId, toCurrencyId, asOf, ct);
        if (!resolution.Found) throw new FxConversionException(resolution.Reason!);

        var snapshot = new FxRateSnapshot
        {
            BusinessUnitId = businessUnitId,
            DocumentType = documentType,
            DocumentId = documentId,
            FromCurrencyId = fromCurrencyId,
            ToCurrencyId = toCurrencyId,
            Rate = RoundRate(resolution.Rate),
            FxRateId = resolution.FxRateId,
            RateEffectiveFrom = resolution.RateEffectiveFrom,
            ResolutionPath = resolution.ResolutionPath,
            AsOf = asOf,
            CapturedOn = DateTime.UtcNow,
            CapturedBy = capturedBy
        };
        _context.Set<FxRateSnapshot>().Add(snapshot);
        await _context.SaveChangesAsync(ct);
        return snapshot;
    }

    public async Task<IReadOnlyList<FxRateSnapshot>> GetSnapshotsAsync(long businessUnitId, string documentType,
        long documentId, CancellationToken ct = default)
        => await _context.Set<FxRateSnapshot>().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.DocumentType == documentType && s.DocumentId == documentId)
            .OrderBy(s => s.FromCurrencyId).ThenBy(s => s.ToCurrencyId)
            .ToListAsync(ct);

    /// <summary>
    /// The effective-dated selection. EffectiveFrom is inclusive, EffectiveTo exclusive, and only
    /// approved rows are visible. When windows overlap the latest EffectiveFrom wins, with Id as a
    /// deterministic tiebreak so the same question always returns the same answer.
    /// </summary>
    private Task<FxRate?> FindRateAsync(long businessUnitId, long fromCurrencyId, long toCurrencyId,
        DateTime asOf, CancellationToken ct)
        => _context.Set<FxRate>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId
                        && r.FromCurrencyId == fromCurrencyId
                        && r.ToCurrencyId == toCurrencyId
                        && r.Status == FxRateStatuses.Approved
                        && r.Rate > 0m
                        && r.EffectiveFrom <= asOf
                        && (r.EffectiveTo == null || r.EffectiveTo > asOf))
            .OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

    private sealed record Leg(decimal Rate, DateTime EffectiveFrom);

    private async Task<Leg?> ResolveDirectOrInverseAsync(long businessUnitId, long fromCurrencyId,
        long toCurrencyId, DateTime asOf, CancellationToken ct)
    {
        if (fromCurrencyId == toCurrencyId) return new Leg(1m, asOf);
        var direct = await FindRateAsync(businessUnitId, fromCurrencyId, toCurrencyId, asOf, ct);
        if (direct is not null) return new Leg(direct.Rate, direct.EffectiveFrom);
        var inverse = await FindRateAsync(businessUnitId, toCurrencyId, fromCurrencyId, asOf, ct);
        return inverse is not null && inverse.Rate > 0m ? new Leg(1m / inverse.Rate, inverse.EffectiveFrom) : null;
    }

    private async Task<Dictionary<long, string>> CurrencyCodesAsync(long businessUnitId, IReadOnlyCollection<long> ids,
        CancellationToken ct)
        => ids.Count == 0
            ? new Dictionary<long, string>()
            : await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId && ids.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

    private async Task<IReadOnlyList<FxCurrencyComponent>> BareComponentsAsync(long businessUnitId,
        IReadOnlyCollection<FxAmount> amounts, CancellationToken ct)
    {
        var codes = await CurrencyCodesAsync(businessUnitId,
            amounts.Where(a => a.CurrencyId.HasValue).Select(a => a.CurrencyId!.Value).Distinct().ToArray(), ct);
        return amounts.GroupBy(a => a.CurrencyId)
            .OrderBy(g => g.Key ?? long.MinValue)
            .Select(g => new FxCurrencyComponent(g.Key,
                g.Key.HasValue && codes.TryGetValue(g.Key.Value, out var c) ? c : null,
                RoundMoney(g.Sum(x => x.Amount)), g.Count(), false, null, null, null))
            .ToList();
    }

    private static string Require(string value, string field, int max)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Length > max)
            throw new FxConversionException($"A valid {field} is required.");
        return value;
    }
}

/// <summary>Document type tokens used for FX snapshots.</summary>
public static class FxDocumentTypes
{
    public const string Quote = "Quote";
    public const string Order = "Order";
    public const string Rfq = "Rfq";
}
