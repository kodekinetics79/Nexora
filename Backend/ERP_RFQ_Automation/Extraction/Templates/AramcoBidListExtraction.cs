using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Services.Interfaces;

namespace ERP_RFQ_Automation.Extraction.Templates;

/// <summary>
/// Turns a parsed Aramco bid list into the extraction outcome the rest of the pipeline already
/// understands — with no model call, no tokens, and no chunking.
///
/// <para>The mapping is deliberately dull. Every value is copied from a column the document
/// actually printed; nothing is inferred, defaulted or standardised. Confidence is 1.0 because
/// the parser refuses anything it cannot read exactly, so a value that reaches here was read,
/// not guessed — and a confidence below 1 on deterministic output would be a lie that costs a
/// reviewer's attention.</para>
/// </summary>
public static class AramcoBidListExtraction
{
    /// <summary>Deterministic reads are certain by construction. See the class remarks.</summary>
    private const double Certain = 1.0d;

    /// <summary>
    /// Builds the outcome, or null when the document is not an Aramco bid list or the parser
    /// refused it. Null means "fall through to the model", never "no items".
    /// </summary>
    public static ChunkedExtractionOutcome? TryExtract(
        string? documentText, string sourceDocumentName, out string? rejection)
    {
        rejection = null;
        if (!AramcoBidListParser.Recognises(documentText)) return null;

        var bid = AramcoBidListParser.Parse(documentText);
        if (!bid.IsTrustworthy)
        {
            // The caller logs this and falls through to the model. A refusal is a routing
            // decision, not a failure: the document still gets read, just not for free.
            rejection = bid.Rejection;
            return null;
        }

        var items = bid.Lines.Select(Map).ToList();

        var result = new LeadExtractionResult(
            bid.Bidno, Certain,                       // Rfqno — the customer's own bid number
            bid.Buyer, Certain,                       // BuyersName
            bid.BidDate?.ToString("yyyy-MM-dd"), Certain,
            bid.BidClose?.ToString("yyyy-MM-dd"), Certain,
            null, 0, null, 0, null, 0,
            $"Read deterministically from an Aramco bid materials list ({items.Count} line(s)).", Certain,
            null, 0, null, 0, null, 0,
            Certain,
            items,
            InquiryType: "product");

        return new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = result,
            ExpectedItemCount = items.Count,
            ExtractedItemCount = items.Count,
            // No provider was consulted, so the ledger must not claim one. This is what makes
            // the Trust Center able to tell a customer, truthfully, that their bid list never
            // left the building.
            AiProviderClass = null,
            ProcessingPath = ExtractionProcessingPath.DeterministicRules,
            Diagnostics =
            [
                $"Aramco bid list template: {items.Count} line item(s) read without a model call."
            ]
        };
    }

    private static LeadItemData Map(AramcoBidLine line)
    {
        // Quantity is int? downstream. Aramco states whole units; a fraction would be a shape
        // this template does not claim to understand, so it becomes null and routes the LINE to
        // review rather than being truncated into a different number.
        int? quantity = decimal.Truncate(line.ReqQty) == line.ReqQty && line.ReqQty <= int.MaxValue
            ? (int)line.ReqQty
            : null;

        // The first line is the sheet's own short noun; the remainder is the specification
        // block. Both are kept — the reviewer prices against the specification.
        var split = line.Description.Split('\n', 2);
        var shortName = split[0];
        var fullDescription = line.Description;

        return new LeadItemData(
            null, 0,                                   // CompanyRef
            null, 0,                                   // CustomerAccountPortalId
            null, 0,                                   // CustomerRfqno
            line.ItemNo, Certain,                      // ItemMaterialCode — Aramco material number
            null, 0,                                   // CommodityProduct
            null, 0,                                   // BuyerName
            line.BidLine, Certain,                     // LineItemNo — the sheet's own numbering
            shortName, Certain,                        // ProductShortName
            null, 0,                                   // Alternative
            fullDescription, Certain,                  // ProductShortDescription
            null, 0,                                   // Currency — the sheet states none
            line.ReqUnit, Certain,                     // UnitOfMeasure — verbatim, never standardised
            null, 0,                                   // UnitPrice — a bid asks for it, never states it
            quantity, quantity is null ? 0 : Certain,  // Quantity
            string.IsNullOrEmpty(line.ShipTo) ? null : line.ShipTo, // StorageLocation — plant code
            string.IsNullOrEmpty(line.ShipTo) ? 0 : Certain,
            null, 0,                                   // ManufacturerName
            null, 0,                                   // ManufacturerPartNumber
            null, 0,                                   // AlternateProductName
            null, 0,                                   // AlternatePartNumber
            null, 0,                                   // ItemText
            null, 0,                                   // MaterialPotext
            null, 0,                                   // LeadTime
            null, 0,                                   // ReceivedDate
            null, 0,                                   // BidClosingDateLine
            Certain);                                  // ItemConfidence
    }
}
