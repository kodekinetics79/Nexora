using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Fx;

/// <summary>
/// Raised when a conversion is demanded and cannot be produced from approved evidence.
///
/// Mirrors Procurement/ProcurementContracts.cs ProcurementValidationException: sealed, derived
/// from InvalidOperationException, message-only, and worded as "&lt;observed condition&gt;;
/// &lt;what is required to proceed&gt;." The message IS the contract — it reaches the caller verbatim.
/// </summary>
public sealed class FxConversionException : InvalidOperationException
{
    public FxConversionException(string message) : base(message) { }
}

/// <summary>A monetary amount that knows its own currency. The unit of everything in this module.</summary>
public readonly record struct FxAmount(decimal Amount, long? CurrencyId);

/// <summary>
/// The outcome of resolving a rate for one ordered pair. Either <see cref="Found"/> is true and
/// <see cref="Rate"/> is usable, or <see cref="Reason"/> explains — in words fit to show a user —
/// why no approved rate exists. There is no third state and no silent default of 1.
/// </summary>
public sealed record FxRateResolution(
    bool Found,
    decimal Rate,
    long? FxRateId,
    DateTime RateEffectiveFrom,
    string ResolutionPath,
    string? Reason)
{
    public static FxRateResolution Identity(DateTime asOf) =>
        new(true, 1m, null, asOf, FxResolutionPaths.Identity, null);

    public static FxRateResolution Missing(string reason) =>
        new(false, 0m, null, default, string.Empty, reason);
}

/// <summary>One currency's contribution to a total, with the rate that was applied to it.</summary>
public sealed record FxCurrencyComponent(
    long? CurrencyId,
    string? CurrencyCode,
    decimal Subtotal,
    int RowCount,
    bool Converted,
    decimal? Rate,
    decimal? ConvertedSubtotal,
    string? Reason);

/// <summary>
/// The result of totalling a set of amounts that may span currencies.
///
/// Fail-closed contract: when <see cref="Converted"/> is false, <see cref="Total"/> is null —
/// never a partial or blended figure — and <see cref="UnavailableReason"/> says why in words.
/// <see cref="Components"/> is ALWAYS populated, so a caller that cannot show one number can
/// still show an honest per-currency breakdown. This matches the Procurement read-path
/// philosophy: never throw on a query, but never recommend an uncomparable figure either.
/// </summary>
public sealed record FxTotalResult(
    bool Converted,
    decimal? Total,
    long? TargetCurrencyId,
    string? TargetCurrencyCode,
    string? UnavailableReason,
    IReadOnlyList<FxCurrencyComponent> Components)
{
    public static FxTotalResult Empty(long? targetCurrencyId, string? targetCurrencyCode) =>
        new(true, 0m, targetCurrencyId, targetCurrencyCode, null, Array.Empty<FxCurrencyComponent>());
}

/// <summary>
/// Converts commercial amounts using effective-dated, approval-gated rates, and freezes the rate
/// actually used against the document so the conversion stays reproducible.
/// </summary>
public interface IFxConversionService
{
    /// <summary>The business unit's base currency id, or null when it is absent or ambiguous.</summary>
    Task<long?> ResolveBaseCurrencyIdAsync(long businessUnitId, CancellationToken ct = default);

    /// <summary>
    /// Selects the approved rate in force at <paramref name="asOf"/> for the ordered pair.
    /// Resolution order: identity, direct, inverse, then triangulation via the base currency.
    /// </summary>
    Task<FxRateResolution> ResolveRateAsync(long businessUnitId, long fromCurrencyId, long toCurrencyId,
        DateTime asOf, CancellationToken ct = default);

    /// <summary>Converts a single amount, or explains why it cannot be converted.</summary>
    Task<FxRateResolution> ConvertAsync(long businessUnitId, FxAmount amount, long toCurrencyId,
        DateTime asOf, CancellationToken ct = default);

    /// <summary>
    /// Totals a mixed-currency set into <paramref name="targetCurrencyId"/> (defaulting to the
    /// business unit's base currency). Fails closed: any unconvertible component nulls the total.
    /// </summary>
    Task<FxTotalResult> TotalAsync(long businessUnitId, IReadOnlyCollection<FxAmount> amounts,
        DateTime asOf, long? targetCurrencyId = null, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="TotalAsync"/> but replays any snapshot already frozen against
    /// (<paramref name="documentType"/>, <paramref name="documentId"/>) in preference to a live
    /// lookup, so a document converted yesterday still converts identically today.
    /// </summary>
    Task<FxTotalResult> TotalForDocumentAsync(long businessUnitId, string documentType, long documentId,
        IReadOnlyCollection<FxAmount> amounts, DateTime asOf, long? targetCurrencyId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Freezes the rate for a pair against a document. Idempotent: an existing snapshot is
    /// returned unchanged rather than overwritten, which is what makes the answer stable.
    /// Throws <see cref="FxConversionException"/> when no approved rate exists.
    /// </summary>
    Task<FxRateSnapshot> CaptureSnapshotAsync(long businessUnitId, string documentType, long documentId,
        long fromCurrencyId, long toCurrencyId, DateTime asOf, string capturedBy, CancellationToken ct = default);

    /// <summary>Every rate frozen against a document — the auditor's answer.</summary>
    Task<IReadOnlyList<FxRateSnapshot>> GetSnapshotsAsync(long businessUnitId, string documentType,
        long documentId, CancellationToken ct = default);
}
