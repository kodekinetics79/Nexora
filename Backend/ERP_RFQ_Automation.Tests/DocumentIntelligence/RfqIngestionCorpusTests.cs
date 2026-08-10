using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests.DocumentIntelligence;

/// <summary>
/// The shapes the 120-document sample set never states, read end to end: Word document ->
/// <see cref="DocxTableParser"/> -> <see cref="CanonicalRfqNormalizer"/> -> the
/// <see cref="LeadItemData"/> the persistence layer consumes.
///
/// <para>Every test here fails if its fix is reverted, and the assertion is on what DEPENDS on
/// the value — the field a reviewer is shown, the number a customer would be quoted — not on the
/// value merely surviving a round trip.</para>
/// </summary>
public sealed class RfqIngestionCorpusTests
{
    private static readonly DocxTableParser Parser = new(new NativeSpreadsheetParser());
    private static readonly CanonicalRfqNormalizer Normalizer = new();

    private static IReadOnlyList<RfqSpreadsheetRow> Read(byte[] document, string name = "synthetic.docx")
        => Parser.Parse(document, name);

    private static CanonicalRfqDocument Canonical(byte[] document, string name = "synthetic.docx")
        => Assert.Single(Normalizer.NormalizeSpreadsheetRows(Read(document, name), businessUnitId: 7).Documents);

    // ===================================================================== defect 1: closing date

    /// <summary>
    /// Every spelling a buyer uses for "the date this bid closes" reaches the closing-date field.
    ///
    /// <para>REGRESSION. The header scan took the first alias each field matched ANYWHERE in the
    /// line, so the bare "date" alias matched INSIDE "Bid Closing Date:" and the inner mark
    /// swallowed the outer one. Every spelling but "Deadline:" wrote the closing date into the
    /// received-date field, left BidClosingDate null, and told the reviewer "Bid closing date
    /// needs review" — i.e. missing — on a document that states it plainly. A bid closes and
    /// nobody knew there was a bid.</para>
    /// </summary>
    [Theory]
    [InlineData("Bid Closing Date: 2026-06-15", "2026-06-15")]
    [InlineData("Closing Date: 2026-06-15", "2026-06-15")]
    [InlineData("Due Date: 2026-06-15", "2026-06-15")]
    [InlineData("Submission Date: 2026-06-15", "2026-06-15")]
    [InlineData("Deadline: 2026-06-15", "2026-06-15")]
    [InlineData("Quotation Due: 2026-06-15", "2026-06-15")]
    public void Every_closing_date_spelling_reaches_the_closing_date_field(string header, string expected)
    {
        var row = Assert.Single(Read(SyntheticRfqCorpus.HeaderBlock(header)));

        Assert.Equal(expected, row.BidClosingDate);
        Assert.Null(row.ReceivedDate);
    }

    /// <summary>
    /// The closing date survives a second label on the same line. "RFQ No: 7712  Closing Date: …"
    /// used to yield a received date of the CLOSING date and no closing date at all.
    /// </summary>
    [Fact]
    public void A_closing_date_beside_another_label_is_still_a_closing_date()
    {
        var row = Assert.Single(Read(
            SyntheticRfqCorpus.HeaderBlock("RFQ No: 7712  Closing Date: 2026-09-01 14:00")));

        Assert.Equal("7712", row.RfqNo);
        Assert.Equal("2026-09-01 14:00", row.BidClosingDate);
        Assert.Null(row.ReceivedDate);
    }

    /// <summary>
    /// "Delivery Date" and "Required Delivery Date" are the buyer's requirement, and the label
    /// before them is not part of the value. "Ship To: Dammam  Required Delivery Date: …" used to
    /// produce a delivery location of "Dammam  Required Delivery" and a received date.
    /// </summary>
    [Theory]
    [InlineData("Delivery Date: 2026-10-01", null, "2026-10-01")]
    [InlineData("Required Delivery Date: 2026-10-01", null, "2026-10-01")]
    [InlineData("Ship To: Dammam  Required Delivery Date: 2026-10-01", "Dammam", "2026-10-01")]
    public void A_delivery_date_label_is_read_whole(string header, string? location, string expected)
    {
        var row = Assert.Single(Read(SyntheticRfqCorpus.HeaderBlock(header)));

        Assert.Equal(expected, row.RequiredDeliveryDate);
        Assert.Equal(location, row.DeliveryLocation);
        Assert.Null(row.ReceivedDate);
    }

    /// <summary>
    /// The received date still reads, from the two forms that actually mean it. This is the guard
    /// against "fixing" the closing date by deleting the alias that made it wrong.
    /// </summary>
    [Theory]
    [InlineData("RFQ Date: 2026-05-26")]
    [InlineData("Date: 2026-05-26")]
    public void The_received_date_still_reads(string header)
    {
        var row = Assert.Single(Read(SyntheticRfqCorpus.HeaderBlock(header)));

        Assert.Equal("2026-05-26", row.ReceivedDate);
        Assert.Null(row.BidClosingDate);
    }

    /// <summary>
    /// A label we do not recognise is a visible gap, not a wrong value. "Award Date:" is not a
    /// received date, and reading it as one is worse than reading nothing: the field is required,
    /// so absence stops and asks while a plausible wrong date sails through.
    /// </summary>
    [Fact]
    public void An_unrecognised_date_label_is_not_read_as_the_received_date()
    {
        var row = Assert.Single(Read(SyntheticRfqCorpus.HeaderBlock("Award Date: 2026-05-26")));

        Assert.Null(row.ReceivedDate);
        Assert.Null(row.BidClosingDate);
    }

    /// <summary>
    /// The consequence a reviewer actually sees: a document stating its closing date plainly
    /// produces a closing date, and no "Bid closing date needs review" warning.
    /// </summary>
    [Fact]
    public void A_stated_closing_date_is_not_reported_as_needing_review()
    {
        var rows = Read(SyntheticRfqCorpus.HeaderBlock(
            "RFQ Number: RFQ-C1 Customer: Aramco RFQ Date: 2026-05-26 Bid Closing Date: 2026-06-15"));
        var result = Normalizer.NormalizeSpreadsheetRows(rows, businessUnitId: 7);
        var document = Assert.Single(result.Documents);

        Assert.Equal(new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc), document.BidClosingDate.Value);
        Assert.Equal(new DateTime(2026, 5, 26, 0, 0, 0, DateTimeKind.Utc), document.ReceivedDate.Value);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == "BID_CLOSING_DATE");
    }

    // ===================================================================== defect 5: closing time

    /// <summary>
    /// FR-RFQ-04 requires the closing date AND its time. A tender closing at 14:00 that reaches
    /// the lead as midnight makes a quote submitted at 15:00 look on time — and it is late.
    /// </summary>
    [Fact]
    public void A_stated_closing_time_survives_normalisation_and_the_extraction_contract()
    {
        var document = Canonical(SyntheticRfqCorpus.ClosingTime());

        Assert.Equal(new DateTime(2026, 9, 1, 14, 0, 0, DateTimeKind.Utc), document.BidClosingDate.Value);

        // And it survives the string-typed extraction contract the worker re-reads.
        var rendered = FormatThroughExtractionContract(document);
        Assert.Equal(new DateTime(2026, 9, 1, 14, 0, 0), RfqDateParser.Parse(rendered));
    }

    /// <summary>A document that states no time still lands on midnight, unchanged.</summary>
    [Fact]
    public void A_date_with_no_stated_time_is_unchanged()
    {
        var document = Canonical(SyntheticRfqCorpus.HeaderBlock("Bid Closing Date: 2026-06-15"));

        Assert.Equal(TimeSpan.Zero, document.BidClosingDate.Value.TimeOfDay);
    }

    /// <summary>
    /// A Saudi tender stating its deadline in the Umm al-Qura calendar is stored as the Gregorian
    /// instant and renders back to the buyer's own form.
    /// </summary>
    [Fact]
    public void A_hijri_closing_date_is_read_as_a_real_deadline()
    {
        var document = Canonical(SyntheticRfqCorpus.HijriClosingDate());

        Assert.Equal(CanonicalValueKind.Normalized, document.BidClosingDate.Kind);
        Assert.Equal("1447-03-15", RfqDateParser.ToHijri(document.BidClosingDate.Value));
    }

    // ===================================================================== defect 6: ambiguity

    /// <summary>
    /// "03/04/2026" is 3 April under Gulf convention and 4 March under American. The parser
    /// establishes that it cannot tell; the RFQ path used to throw that away and stamp the value
    /// Confidence 1.0 / Valid with no note anywhere, while the customer-PO path told its reviewer.
    /// </summary>
    [Fact]
    public void An_ambiguous_day_month_date_is_surfaced_rather_than_asserted()
    {
        var rows = Read(SyntheticRfqCorpus.AmbiguousDayMonth());
        var result = Normalizer.NormalizeSpreadsheetRows(rows, businessUnitId: 7);
        var document = Assert.Single(result.Documents);

        // Read day-first, as the Gulf writes it — and reported, not asserted.
        Assert.Equal(new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc), document.ReceivedDate.Value);
        Assert.Equal(ValidationStatus.NeedsReview, document.ReceivedDate.ValidationStatus);
        Assert.True(document.ReceivedDate.Confidence < 1.0m,
            "an ambiguous reading must not carry full confidence");

        var issue = Assert.Single(result.Issues, i => i.Code == "RECEIVED_DATE");
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Contains("ambiguous", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("day-first", issue.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ValidationStatus.NeedsReview, document.ValidationStatus);
    }

    /// <summary>An unambiguous date carries full confidence and raises nothing.</summary>
    [Fact]
    public void An_unambiguous_date_is_still_asserted_with_full_confidence()
    {
        var rows = Read(SyntheticRfqCorpus.HeaderBlock(
            "RFQ Number: RFQ-U1 Customer: Aramco RFQ Date: 15/04/2026"));
        var result = Normalizer.NormalizeSpreadsheetRows(rows, businessUnitId: 7);
        var document = Assert.Single(result.Documents);

        Assert.Equal(new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc), document.ReceivedDate.Value);
        Assert.Equal(1.0m, document.ReceivedDate.Confidence);
        Assert.DoesNotContain(result.Issues, i => i.Code == "RECEIVED_DATE");
    }

    // ===================================================================== defect 3: merged cells

    /// <summary>
    /// A <c>w:gridSpan</c> cell occupies two grid columns but is one element, so a positional read
    /// shifted every column to its right: the quantity landed in the unit column and persisted as
    /// the number 0.
    /// </summary>
    [Fact]
    public void A_horizontally_merged_cell_does_not_shift_the_columns_to_its_right()
    {
        var rows = Read(SyntheticRfqCorpus.HorizontallyMergedCell());

        Assert.Equal(2, rows.Count);
        Assert.Equal("250", rows[0].Quantity);
        Assert.Equal("40", rows[1].Quantity);
        Assert.Equal("EA", rows[1].UnitOfMeasure);
    }

    /// <summary>
    /// A <c>w:vMerge</c> continuation cell is EMPTY in the file; the value belongs to every row the
    /// merge spans. Read positionally, a part number spanning three lines populated the first and
    /// was silently null on the other two.
    /// </summary>
    [Fact]
    public void A_vertically_merged_cell_carries_its_value_down_every_row_it_spans()
    {
        var rows = Read(SyntheticRfqCorpus.VerticallyMergedCell());

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal("P-9", row.ManufacturerPartNumber));
        Assert.Equal(new[] { "10", "20", "30" }, rows.Select(r => r.Quantity));
    }

    // ===================================================================== defect 4: second table

    /// <summary>
    /// A commercial-terms table is not a line-item table. It maps one column, so every row of it
    /// became a phantom line called "Payment Terms", "Incoterms" or "Validity" — each with no
    /// quantity — and <c>Lead.NoOfLineItems</c> then reported the inflated count as if it were a
    /// conservation guarantee.
    /// </summary>
    [Fact]
    public void A_commercial_terms_table_contributes_no_line_items()
    {
        var rows = Read(SyntheticRfqCorpus.LineTableThenCommercialTerms());

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "Ball Valve 2in", "Gate Valve 4in" }, rows.Select(r => r.ProductName));
        Assert.DoesNotContain(rows, r => r.ProductName is "Payment Terms" or "Incoterms" or "Validity");
    }

    /// <summary>
    /// A nested table belongs to itself. Its rows are not the outer table's rows, and its text is
    /// not part of the containing cell's product name.
    /// </summary>
    [Fact]
    public void A_nested_table_neither_adds_lines_nor_pollutes_the_cell_that_holds_it()
    {
        var rows = Read(SyntheticRfqCorpus.NestedSpecificationTable());

        Assert.Equal(2, rows.Count);
        Assert.Equal("Gate Valve 4in", rows[0].ProductName);
        Assert.Equal("12", rows[0].Quantity);
        Assert.DoesNotContain(rows, r => r.Quantity is "999" or "888");
    }

    // ===================================================================== defect 2: quantities

    /// <summary>
    /// Every quantity spelling the sample set never contains. A readable one is read; an
    /// unreadable one is NULL and blocks the line — never the number 0, which reads as a real
    /// demand for nothing and is quoted.
    /// </summary>
    [Theory]
    [InlineData("2,500", 2500)]     // thousands separator
    [InlineData("500 PCS", 500)]    // unit inside the cell
    [InlineData("12.00", 12)]       // whole number written as a decimal
    [InlineData("1 000", 1000)]     // space-grouped
    [InlineData("٥٠٠", 500)]        // Arabic-Indic digits
    [InlineData("10-20", null)]     // a range is not a quantity
    [InlineData("1.234", null)]     // ambiguous: 1234 or 1.234, a thousandfold apart
    [InlineData("2.5", null)]       // fractional: truncating to 2 is a 20% under-quote
    public void A_quantity_is_read_or_refused_but_never_defaulted(string raw, int? expected)
    {
        var document = Assert.Single(Normalizer.NormalizeSpreadsheetRows(
            new[] { QuantityRow(raw) }, businessUnitId: 7).Documents);
        var line = Assert.Single(document.LineItems);

        if (expected is { } quantity)
        {
            Assert.Equal(CanonicalValueKind.Normalized, line.Quantity.Kind);
            Assert.Equal(quantity, line.Quantity.Value);
            Assert.Equal(ValidationStatus.Valid, line.Quantity.ValidationStatus);
        }
        else
        {
            Assert.NotEqual(CanonicalValueKind.Normalized, line.Quantity.Kind);
            Assert.Equal(ValidationStatus.Invalid, line.Quantity.ValidationStatus);
            Assert.Equal(ValidationStatus.Invalid, document.ValidationStatus);
        }
    }

    /// <summary>
    /// The one that matters: an unreadable quantity must not reach the persistence contract as a
    /// number. <c>MapCanonicalItem</c> emitted <c>line.Quantity.Value</c> unguarded, so a failed
    /// parse left the struct at 0 and 0 travelled as a real quantity — while its two neighbours,
    /// unit price and lead time, both tested <c>Kind == Normalized</c> first.
    /// </summary>
    [Fact]
    public async Task An_unreadable_quantity_reaches_persistence_as_null_not_zero()
    {
        var service = new ChunkedExtractionService(
            new StubLlm(), new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

        var outcome = await service.ExtractStructuredAsync(
            new[] { QuantityRow("2,500"), QuantityRow("10-20") }, 7, "quantities.docx");

        var items = outcome.Result!.Items!;
        Assert.Equal(2, items.Count);
        Assert.Equal(2500, items[0].Quantity);
        Assert.Null(items[1].Quantity);
    }

    /// <summary>
    /// The reviewer is told which quantity could not be read and why. "Quantity must be a positive
    /// whole number" on a line that plainly reads "2,500 PCS" tells them nothing they can act on.
    /// </summary>
    [Fact]
    public void An_unreadable_quantity_names_itself_in_the_issue()
    {
        var result = Normalizer.NormalizeSpreadsheetRows(new[] { QuantityRow("1.234") }, businessUnitId: 7);

        var issue = Assert.Single(result.Issues, i => i.Code == "QUANTITY");
        Assert.Contains("1.234", issue.Message, StringComparison.Ordinal);
        Assert.Contains("ambiguous", issue.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The whole document, read the way a buyer wrote it: eight lines, five readable quantities,
    /// three refused, and an Arabic description carried verbatim.
    /// </summary>
    [Fact]
    public void The_quantity_corpus_reads_end_to_end()
    {
        var document = Canonical(SyntheticRfqCorpus.QuantityShapes());

        Assert.Equal("RFQ-Q1", document.RfqNo.Value);
        Assert.Equal("Aramco", document.BuyerName.Value);
        Assert.Equal(8, document.LineItems.Count);

        var readable = document.LineItems
            .Where(l => l.Quantity.Kind == CanonicalValueKind.Normalized)
            .Select(l => l.Quantity.Value)
            .ToList();
        Assert.Equal(new[] { 2500, 500, 12, 1000, 500 }, readable);

        Assert.Equal("صمام كروي", document.LineItems[5].ProductName.Value);
        Assert.Equal("M", document.LineItems[0].UnitOfMeasure.Value);
    }

    // ===================================================================== defect 7: "Delivery"

    /// <summary>
    /// A column headed exactly "Delivery" holds a date far more often than a number of days. Under
    /// lead time it failed the integer parse and was dropped, while the buyer's stated delivery
    /// date stayed null — the value lost with no diagnostic anywhere.
    /// </summary>
    [Fact]
    public void A_delivery_column_is_the_buyers_requirement_not_a_supplier_lead_time()
    {
        var rows = new NativeSpreadsheetParser().ParseGrid(
            new List<IReadOnlyList<string?>>
            {
                new string?[] { "Part No", "Description", "Qty", "Delivery" },
                new string?[] { "P-1", "Ball Valve", "250", "2026-10-01" },
            },
            "delivery.docx",
            "Table 1");

        var row = Assert.Single(rows);
        Assert.Equal("2026-10-01", row.RequiredDeliveryDate);
        Assert.Null(row.LeadTimeDays);
    }

    /// <summary>"Delivery Time" and "Lead Time" are still supplier lead times.</summary>
    [Theory]
    [InlineData("Lead Time")]
    [InlineData("Delivery Time")]
    [InlineData("Delivery Period")]
    public void A_lead_time_column_is_still_a_lead_time(string header)
    {
        var rows = new NativeSpreadsheetParser().ParseGrid(
            new List<IReadOnlyList<string?>>
            {
                new string?[] { "Part No", "Description", "Qty", header },
                new string?[] { "P-1", "Ball Valve", "250", "14" },
            },
            "leadtime.docx",
            "Table 1");

        var row = Assert.Single(rows);
        Assert.Equal("14", row.LeadTimeDays);
        Assert.Null(row.RequiredDeliveryDate);
    }

    // ===================================================================== helpers

    private static RfqSpreadsheetRow QuantityRow(string quantity) => new()
    {
        RowNumber = 2,
        SourceDocumentName = "quantities.docx",
        RfqNo = "RFQ-Q1",
        BuyerName = "Aramco",
        ReceivedDate = "2026-05-26",
        ProductName = "Cable 3 core 95mm",
        Quantity = quantity,
    };

    /// <summary>
    /// Renders the closing date exactly as <c>ChunkedExtractionService</c> hands it to the
    /// extraction contract, so the assertion is on the string the worker actually re-reads.
    /// </summary>
    private static string FormatThroughExtractionContract(CanonicalRfqDocument document)
    {
        var service = new ChunkedExtractionService(
            new StubLlm(), new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());
        var outcome = service.ExtractStructuredAsync(
            new[] { ClosingRow(document) }, 7, "closing.docx").GetAwaiter().GetResult();
        return outcome.Result!.BidClosingDate!;
    }

    private static RfqSpreadsheetRow ClosingRow(CanonicalRfqDocument document) => new()
    {
        RowNumber = 2,
        SourceDocumentName = "closing.docx",
        RfqNo = document.RfqNo.Value,
        BuyerName = document.BuyerName.Value,
        ReceivedDate = "2026-05-26",
        BidClosingDate = document.BidClosingDate.OriginalValue,
        ProductName = "Ball Valve 2in 150#",
        Quantity = "250",
    };
}
