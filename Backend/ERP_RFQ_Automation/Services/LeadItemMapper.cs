using System.Globalization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Services.Uom;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// The ONE extracted-line-item → <see cref="LeadItem"/> mapper.
///
/// It replaces four byte-for-byte copies of the same 25-assignment object initialiser
/// (EmailService, FolderService, ManualUploadService, ExtractionWorker). They had already
/// drifted — the worker clamped confidences and rejected sentinel dates while the other three
/// did not — and, more expensively, every field-level fix landed on one door and reached a
/// quarter of the rows. The unit-of-measure canonicalisation below is exactly such a fix:
/// there is now one assignment to change, and it covers every ingestion door.
///
/// Door-specific behaviour that is genuinely NOT shared stays with the door: each one parses
/// dates from a different set of document conventions, so <c>parseDate</c> is passed in.
/// </summary>
public static class LeadItemMapper
{
    /// <summary>
    /// Maps one extracted item. <paramref name="parseDate"/> is the calling door's date
    /// reader; <paramref name="leadId"/> is set for doors that persist against a saved lead
    /// and left null for doors that attach through the navigation collection.
    /// </summary>
    public static LeadItem Map(LeadItemData source, Func<string?, DateTime?> parseDate, long? leadId = null)
    {
        var item = new LeadItem
        {
            CompanyRef = Truncate(source.CompanyRef, 100),
            CustomerAccountPortalId = Truncate(source.CustomerAccountPortalId, 100),
            CustomerRfqno = Truncate(source.CustomerRfqno, 100),
            ItemMaterialCode = Truncate(source.ItemMaterialCode, 100),
            CommodityProduct = Truncate(source.CommodityProduct, 200),
            BuyerName = Truncate(source.BuyerName, 200),
            LineItemNo = Truncate(source.LineItemNo, 50),
            ProductShortName = Truncate(source.ProductShortName, 1000),
            Alternative = Truncate(source.Alternative, 100),
            ProductShortDescription = Truncate(source.ProductShortDescription, 1000),
            Currency = Truncate(source.Currency, 10),

            // THE single unit-of-measure assignment in the ingestion pipeline. The extractor is
            // told to transcribe the customer's own wording, so five spellings of one unit
            // arrive here; the canonicaliser settles the spelling and REFUSES to settle
            // packaging ("Pallet") or form factor ("length"), which stay verbatim for review.
            // Null in stays null out — a missing unit is never defaulted to a count.
            UnitOfMeasure = Truncate(UomCanonicalizer.CanonicalizeForStorage(source.UnitOfMeasure), 100),

            UnitPrice = source.UnitPrice,

            // NULL in stays NULL out. The `?? 0` that used to be here was the last step of the
            // silent-zero path: the extractors already refuse to invent a quantity — the
            // conversational prompt leaves it null when the sender stated none, the model reader
            // quarantines a non-positive value to null, and the canonical normalizer now emits
            // null for "2,500" it could not read — and this one coalesce turned every one of
            // those back into a real demand for zero units, on the single write path shared by
            // all four ingestion doors.
            // Every ingestion door converges here, including deterministic parsers that do
            // not pass through the model client's quarantine. Zero and negative demand are
            // not quoteable quantities; preserve the line and represent the unresolved value
            // as NULL so review and promotion gates can distinguish it from a real quantity.
            Quantity = source.Quantity is > 0 ? source.Quantity : null,
            StorageLocation = Truncate(source.StorageLocation, 100),
            ManufacturerName = Truncate(source.ManufacturerName, 200),
            ManufacturerPartNumber = Truncate(source.ManufacturerPartNumber, 100),
            AlternateProductName = Truncate(source.AlternateProductName, 200),
            AlternatePartNumber = Truncate(source.AlternatePartNumber, 100),
            ItemText = Truncate(source.ItemText, 2000),
            MaterialPotext = Truncate(source.MaterialPotext, 2000),
            LeadTime = int.TryParse(source.LeadTime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leadTime)
                ? leadTime
                : null,
            // Sentinel/OCR-noise dates (0001-01-01) parse "successfully" but are not dates.
            ReceivedDate = SanitizeDate(parseDate(source.ReceivedDate)),
            BidClosingDateLine = SanitizeDate(parseDate(source.BidClosingDateLine)),
            // NULL when the extractor supplied no per-line confidence — not 0. A stored 0
            // asserts "measured as zero", which is a claim; absence is the truth, and the
            // review UI treats null as "no score" rather than rendering a red 0%. This
            // column already has form: 2,920 of 2,966 production rows carry 0.88 — a
            // hand-typed literal from a retired parser, not a measurement. The repair is
            // to stop writing anything we did not actually receive.
            Aiconfidence = ClampConfidence(source.ItemConfidence),
            // The extractor is asked for, and returns, the customer's unmapped columns (Plant
            // Code, Incoterms, Project, Cost Center …) — the columns carrying the buyer's own
            // commercial context. Captured verbatim, bounded by ExtraFieldsJson.
            ExtraFields = ExtraFieldsJson.Serialize(source.ExtraFields)
        };

        if (leadId.HasValue) item.LeadId = leadId.Value;
        return item;
    }

    /// <summary>
    /// Bounded copy that MARKS the cut, so a reviewer can tell a truncated value from a short
    /// one instead of silently reading a clipped part number as complete.
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    private static DateTime? SanitizeDate(DateTime? value) => value is { Year: >= 2000 } ? value : null;

    private static decimal? ClampConfidence(double? confidence)
        => confidence is null ? null : (decimal)Math.Clamp(confidence.Value, 0d, 1d);
}
