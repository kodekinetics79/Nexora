using System.Globalization;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Extraction.Templates;

/// <summary>
/// Turns a parsed Aramco bid list into the rows the spreadsheet door already understands —
/// with no model call, no tokens, and no chunking.
///
/// <para>The mapping is deliberately dull. Every value is copied from a column the document
/// actually printed; nothing is inferred, defaulted or standardised. From here the rows take
/// exactly the path a CSV or a Word table takes: the normaliser reads them, the evidence
/// ledger records where each value sat, and the Decide screen can cite the source of every
/// line. They used to become lead items directly, which left the ledger empty for this door
/// and every line marked "no source document on file" — unquotable, on a document that was
/// read perfectly.</para>
/// </summary>
public static class AramcoBidListExtraction
{
    /// <summary>The page name the ledger files this document's cells under.</summary>
    public const string WorksheetName = "Bid Materials List";

    /// <summary>
    /// The buyer's own column labels, in print order. They travel on every row so the review
    /// screen and the spelling learner see the document's vocabulary, not ours.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> Headers = new Dictionary<int, string>
    {
        [1] = "Bid Line", [2] = "Item No", [3] = "Ship To", [4] = "Req Unit", [5] = "Req Qty", [6] = "Description"
    };

    private static readonly IReadOnlyDictionary<string, int> FieldColumns = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [RfqSpreadsheetFields.CustomerMaterialCode] = 2,
        [RfqSpreadsheetFields.UnitOfMeasure] = 4,
        [RfqSpreadsheetFields.Quantity] = 5,
        [RfqSpreadsheetFields.ProductName] = 6,
        [RfqSpreadsheetFields.ItemText] = 6
    };

    /// <summary>
    /// Reads the document into rows, or returns null when it is not an Aramco bid list or the
    /// parser refused it. Null means "fall through to the model", never "no items".
    /// </summary>
    public static IReadOnlyList<RfqSpreadsheetRow>? TryReadRows(
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

        return bid.Lines.Select(line => Map(bid, line, sourceDocumentName)).ToList();
    }

    private static RfqSpreadsheetRow Map(AramcoBidList bid, AramcoBidLine line, string sourceDocumentName)
    {
        var rows = line.Rows ?? new AramcoBidLineRows(0, 0, 0, 0, 0, 0);
        var header = bid.Rows ?? new AramcoBidListRows(0, 0, 0, 0, 0);

        // The first line is the sheet's own short noun; the whole block is the specification
        // the reviewer prices against. Both are kept, as the Word form-block door keeps them.
        var shortName = line.Description.Split('\n', 2)[0];

        var addresses = new Dictionary<string, string>(StringComparer.Ordinal);
        Cite(addresses, RfqSpreadsheetFields.RfqNo, header.Bidno);
        Cite(addresses, RfqSpreadsheetFields.BuyerName, header.Buyer);
        Cite(addresses, RfqSpreadsheetFields.ReceivedDate, header.BidDate);
        Cite(addresses, RfqSpreadsheetFields.BidClosingDate, header.BidClose);
        Cite(addresses, "row", rows.BidLine);                     // the line's own number
        Cite(addresses, RfqSpreadsheetFields.CustomerMaterialCode, rows.ItemNo);
        Cite(addresses, RfqSpreadsheetFields.UnitOfMeasure, rows.ReqUnit);
        Cite(addresses, RfqSpreadsheetFields.Quantity, rows.ReqQty);
        Cite(addresses, RfqSpreadsheetFields.ProductName, rows.Description);
        Cite(addresses, RfqSpreadsheetFields.ItemText, rows.Description);

        var unmapped = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(line.ShipTo)) unmapped["Ship To"] = line.ShipTo;   // the buyer's plant code

        return new RfqSpreadsheetRow
        {
            RowNumber = Math.Max(1, rows.BidLine),
            SourceDocumentName = sourceDocumentName,
            WorksheetName = WorksheetName,
            HeaderRowNumber = Math.Max(1, header.ColumnHeader),
            HeadersByColumn = new Dictionary<int, string>(Headers),
            FieldColumnNumbers = new Dictionary<string, int>(FieldColumns, StringComparer.Ordinal),
            FieldSourceAddresses = addresses,
            UnmappedColumns = unmapped,
            CustomerLineNumber = line.BidLine,
            RfqNo = bid.Bidno,                                    // the customer's own bid number
            BuyerName = bid.Buyer,
            ReceivedDate = bid.BidDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            BidClosingDate = bid.BidClose?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            CustomerMaterialCode = line.ItemNo,                   // Aramco material number
            UnitOfMeasure = line.ReqUnit,                         // verbatim, never standardised
            Quantity = line.ReqQtyText ?? line.ReqQty.ToString(CultureInfo.InvariantCulture),
            ProductName = shortName,
            ItemText = line.Description
        };
    }

    /// <summary>
    /// A value's address is the line it was printed on: column A of the one-column page the
    /// document's text is, at that 1-based line. Nothing is cited for a value the document
    /// did not print.
    /// </summary>
    private static void Cite(Dictionary<string, string> addresses, string field, int row)
    {
        if (row > 0) addresses[field] = NativeSpreadsheetParser.QualifyAddress(WorksheetName, 1, row);
    }
}
