using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Fx;

/// <summary>
/// Lifecycle of an <see cref="FxRate"/> row. Only <see cref="Approved"/> rates are ever
/// used for a conversion: an unapproved rate is invisible to the conversion engine, so a
/// pending or superseded row can never silently change a commercial total.
/// </summary>
public static class FxRateStatuses
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Superseded = "Superseded";
}

/// <summary>How a rate was reached. Recorded on every snapshot so the path is auditable.</summary>
public static class FxResolutionPaths
{
    /// <summary>Same currency on both sides; rate is exactly 1.</summary>
    public const string Identity = "Identity";
    /// <summary>An approved (from -> to) row was found.</summary>
    public const string Direct = "Direct";
    /// <summary>Only the (to -> from) row existed; the reciprocal was used.</summary>
    public const string Inverse = "Inverse";
    /// <summary>Crossed through the business unit's base currency.</summary>
    public const string Triangulated = "Triangulated";
    /// <summary>Replayed from a previously captured <see cref="FxRateSnapshot"/>.</summary>
    public const string Snapshot = "Snapshot";
}

/// <summary>
/// An effective-dated, approval-gated exchange rate for one ordered currency pair inside one
/// business unit.
///
/// Semantics: ONE unit of <see cref="FromCurrencyId"/> equals <see cref="Rate"/> units of
/// <see cref="ToCurrencyId"/>. So with AED as base, the USD -> AED row carries Rate = 3.6725.
///
/// Effective dating follows the house convention (<c>EffectiveFrom</c> inclusive, nullable
/// <c>EffectiveTo</c> exclusive, null meaning "still effective") used by
/// CommercialFinance/FinanceEntities.cs and CommercialRouting. Precision follows the General
/// Ledger: rates are decimal(20,10), matching JournalEntryLine.ExchangeRate, so a rate handed
/// to the GL and a rate used on the commercial side can never disagree by scale.
///
/// Rows are immutable in practice: a correction is a new row that supersedes the old one, which
/// is what makes a historical conversion reproducible.
/// </summary>
[Table("FxRates")]
[Index(nameof(BusinessUnitId), nameof(FromCurrencyId), nameof(ToCurrencyId), nameof(EffectiveFrom),
    IsUnique = true, Name = "UX_FxRates_BU_Pair_EffectiveFrom")]
[Index(nameof(BusinessUnitId), nameof(FromCurrencyId), nameof(ToCurrencyId), nameof(Status),
    Name = "IX_FxRates_BU_Pair_Status")]
public sealed class FxRate
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>The currency being priced. One unit of this equals <see cref="Rate"/> of <see cref="ToCurrencyId"/>.</summary>
    public long FromCurrencyId { get; set; }

    /// <summary>The currency the rate is quoted in.</summary>
    public long ToCurrencyId { get; set; }

    /// <summary>decimal(20,10), same scale as GeneralLedger JournalEntryLine.ExchangeRate. Always &gt; 0.</summary>
    [Precision(20, 10)]
    public decimal Rate { get; set; }

    /// <summary>Inclusive lower bound of the window this rate applies to.</summary>
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Exclusive upper bound; null means the rate is still in force.</summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>Provenance, e.g. "Manual", "CurrencyMasterBackfill", "CentralBank".</summary>
    [MaxLength(60)]
    public string Source { get; set; } = "Manual";

    /// <summary>One of <see cref="FxRateStatuses"/>. Only "Approved" is usable for conversion.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = FxRateStatuses.Pending;

    [MaxLength(255)]
    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedOn { get; set; }

    /// <summary>Optimistic concurrency, matching the GeneralLedger entity envelope.</summary>
    [ConcurrencyCheck]
    public long Version { get; set; } = 1;

    [MaxLength(255)]
    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }
}

/// <summary>
/// The approved snapshot: the rate that was actually applied to a specific commercial document,
/// frozen at the moment of application.
///
/// This is what makes an answer to "what rate was used on this quote?" stable. Once a snapshot
/// exists for (document, pair), the conversion engine replays it verbatim and never re-reads
/// <see cref="FxRate"/> — so correcting or re-dating a rate afterwards cannot retroactively
/// restate a quote that has already been converted, priced or sent.
/// </summary>
[Table("FxRateSnapshots")]
[Index(nameof(BusinessUnitId), nameof(DocumentType), nameof(DocumentId), nameof(FromCurrencyId), nameof(ToCurrencyId),
    IsUnique = true, Name = "UX_FxRateSnapshots_BU_Document_Pair")]
public sealed class FxRateSnapshot
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>Aggregate the snapshot belongs to, e.g. "Quote".</summary>
    [MaxLength(40)]
    public string DocumentType { get; set; } = null!;

    public long DocumentId { get; set; }

    public long FromCurrencyId { get; set; }

    public long ToCurrencyId { get; set; }

    /// <summary>The exact rate applied, at GL scale.</summary>
    [Precision(20, 10)]
    public decimal Rate { get; set; }

    /// <summary>The <see cref="FxRate"/> row this came from; null when the pair was an identity.</summary>
    public long? FxRateId { get; set; }

    /// <summary>EffectiveFrom of the rate row that was in force when the snapshot was taken.</summary>
    public DateTime RateEffectiveFrom { get; set; }

    /// <summary>One of <see cref="FxResolutionPaths"/>.</summary>
    [MaxLength(30)]
    public string ResolutionPath { get; set; } = FxResolutionPaths.Direct;

    /// <summary>The as-of instant the rate was selected for. Replaying with this value reproduces the result.</summary>
    public DateTime AsOf { get; set; }

    public DateTime CapturedOn { get; set; }

    [MaxLength(255)]
    public string CapturedBy { get; set; } = null!;
}
