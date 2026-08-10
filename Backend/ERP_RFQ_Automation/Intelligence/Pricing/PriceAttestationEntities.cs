using System.ComponentModel.DataAnnotations.Schema;

namespace ERP_RFQ_Automation.Intelligence.Pricing;

/// <summary>
/// The two places a quoted price is allowed to come from (Decision Register R5).
/// Anything else is not a provenance the rep may declare.
/// </summary>
public static class PriceAttestationSources
{
    /// <summary>The price was given to the rep by their sales manager / sales lead.</summary>
    public const string SalesManager = "SALES_MANAGER";

    /// <summary>The price was taken from a supplier's own quotation.</summary>
    public const string SupplierQuote = "SUPPLIER_QUOTE";

    public static readonly IReadOnlyList<string> All = new[] { SalesManager, SupplierQuote };

    public static bool IsKnown(string? source) =>
        source is SalesManager or SupplierQuote;

    /// <summary>Plain commercial wording for messages and audit summaries.</summary>
    public static string Describe(string? source) => source switch
    {
        SalesManager => "sales manager",
        SupplierQuote => "supplier quote",
        _ => "unknown source"
    };
}

/// <summary>
/// R5 price-provenance attestation: the rep's recorded confirmation, taken BEFORE a quote
/// is emailed, of where the prices on it came from — together with the exact per-line
/// prices as they stood at that moment.
///
/// This is the pre-release gate. It replaces the BRD's FR-QTM-04 LOA approver workflow
/// (approved deviation) and closes the fail-open hole in
/// <see cref="BelowFloorGuard"/>: a floor that cannot be established never blocks, so a
/// first-time item used to leave the building with nothing recorded at all. An attestation
/// is required on EVERY send, including revisions, whether or not a floor exists.
///
/// The snapshot in <see cref="Lines"/> is what makes the attestation honest. Validity is
/// decided by comparing the recorded prices against the quote's CURRENT prices, never by
/// comparing timestamps — otherwise "confirm, then edit the price, then send" passes.
/// </summary>
[Table("QuotePriceAttestations")]
public sealed class QuotePriceAttestation
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>The quote this confirmation covers. A revision is a different Quote row and
    /// therefore starts with no attestation of its own — which is the intent.</summary>
    public long QuoteId { get; set; }

    /// <summary>SALES_MANAGER or SUPPLIER_QUOTE (see <see cref="PriceAttestationSources"/>).</summary>
    public string Source { get; set; } = null!;

    /// <summary>The supplier quote reference, or the manager's name — free text, required.</summary>
    public string SourceReference { get; set; } = null!;

    /// <summary>
    /// SHA-256 over the quote currency plus every (quote item id, unit price) pair at
    /// confirmation time. Lets the send gate decide validity in one indexed read; the row
    /// set in <see cref="Lines"/> is the human-readable record of the same facts.
    /// </summary>
    public string LineFingerprint { get; set; } = null!;

    /// <summary>Quote currency at confirmation — a currency swap changes the money even
    /// when every number is untouched, so it is part of what was confirmed.</summary>
    public long? CurrencyId { get; set; }

    public int LineCount { get; set; }

    public long? ConfirmedByUserId { get; set; }

    /// <summary>Who confirmed, as the authenticated caller identified themselves.</summary>
    public string ConfirmedBy { get; set; } = null!;

    public DateTime ConfirmedOn { get; set; }

    public ICollection<QuotePriceAttestationLine> Lines { get; set; } = new List<QuotePriceAttestationLine>();
}

/// <summary>
/// One quote line exactly as it was priced at the moment of confirmation. Immutable by
/// convention — a later price change produces a NEW attestation, it never edits this one.
/// </summary>
[Table("QuotePriceAttestationLines")]
public sealed class QuotePriceAttestationLine
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public long AttestationId { get; set; }

    public long QuoteItemId { get; set; }

    /// <summary>The RFQ line this quote line answers, when the quote is linked to an RFQ.</summary>
    public long? RfqItemId { get; set; }

    /// <summary>Line description as printed at confirmation time, so the record reads on its own.</summary>
    public string? ItemDescription { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public QuotePriceAttestation Attestation { get; set; } = null!;
}
