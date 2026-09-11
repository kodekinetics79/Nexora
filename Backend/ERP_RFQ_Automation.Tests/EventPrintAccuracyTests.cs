using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What two real 1,500-line sourcing-portal event prints revealed when their extraction was
/// checked line by line against the documents: the closing date read in the wrong order, the
/// delivery date lost to a weekday, the currency stated once and applied nowhere, the buyer's
/// own line numbers replaced by ours, repeated titles mistaken for labels, and the approved
/// makers ignored. Each case here is one of those, kept small.
/// </summary>
public sealed class EventPrintAccuracyTests
{
    private static readonly DateTime Arrived = new(2026, 9, 10);

    private static RfqSpreadsheetRow Row(int number, string product, string? closing = null, string? currency = null,
        string? customerNo = null, Dictionary<string, string>? extra = null) => new()
    {
        RowNumber = number,
        ProductName = product,
        Quantity = "1",
        UnitOfMeasure = "each",
        BidClosingDate = closing,
        Currency = currency,
        CustomerLineNumber = customerNo,
        UnmappedColumns = extra ?? new Dictionary<string, string>(StringComparer.Ordinal),
    };

    private static CanonicalRfqDocument Normalise(DateTime arrived, params RfqSpreadsheetRow[] rows)
        => Assert.Single(new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, 1, arrived).Documents);

    // ------------------------------------------------------------------- date order

    [Fact]
    public void An_ambiguous_closing_date_already_past_at_arrival_is_read_month_first_and_still_flagged()
    {
        var document = Normalise(Arrived, Row(2, "Relay", closing: "10/8/2026 3:00 PM"));

        Assert.Equal(new DateTime(2026, 10, 8), document.BidClosingDate.Value.Date);
        Assert.Equal(ValidationStatus.NeedsReview, document.BidClosingDate.ValidationStatus);
        Assert.Contains(document.BidClosingDate.Transformations, t => t.StartsWith("read_month_first", StringComparison.Ordinal));
        var issue = Assert.Single(document.Issues, i => i.Code == "BID_CLOSING_DATE");
        Assert.Contains("read month-first (8 October 2026)", issue.Message);
        Assert.Contains(document.BidClosingDate.Transformations, t => t.Contains("already past when the document arrived", StringComparison.Ordinal));
    }

    [Fact]
    public void A_closing_date_that_would_precede_the_documents_own_issue_date_is_read_month_first()
    {
        // Published "8/9/2026", due "9/6/2026", arrived 11 September: read day-first the tender
        // would close on 9 June, three months before it was published on 8 September. Read
        // month-first it was published on 9 August and closed on 6 September — the only order
        // in which the document makes sense, even though both readings are already past.
        var row = Row(2, "Bearing", closing: "9/6/2026 4:00 PM");
        row.ReceivedDate = "8/9/2026 9:48 AM";
        var document = Normalise(new DateTime(2026, 9, 11), row);

        Assert.Equal(new DateTime(2026, 9, 6), document.BidClosingDate.Value.Date);
        Assert.Equal(new DateTime(2026, 8, 9), document.ReceivedDate.Value.Date);
        Assert.Contains(document.BidClosingDate.Transformations, t => t.Contains("issue date", StringComparison.Ordinal));
    }

    [Fact]
    public void A_closing_date_consistent_with_the_issue_date_only_day_first_stays_day_first()
    {
        // Issued 1 March, closing "10/3/2026": day-first (10 March) follows the issue date and
        // month-first (3 October) does too, so the issue date decides nothing; the closing date is
        // still ahead of arrival either way, so day-first stands.
        var row = Row(2, "Bearing", closing: "10/3/2026");
        row.ReceivedDate = "2026-03-01";
        var document = Normalise(new DateTime(2026, 3, 2), row);
        Assert.Equal(new DateTime(2026, 3, 10), document.BidClosingDate.Value.Date);
    }

    [Fact]
    public void An_ambiguous_closing_date_still_ahead_is_read_day_first_as_before()
    {
        var document = Normalise(new DateTime(2026, 7, 1), Row(2, "Relay", closing: "10/8/2026"));

        Assert.Equal(new DateTime(2026, 8, 10), document.BidClosingDate.Value.Date);
        Assert.DoesNotContain(document.BidClosingDate.Transformations, t => t.StartsWith("read_month_first", StringComparison.Ordinal));
    }

    [Fact]
    public void A_closing_date_past_in_both_orders_is_left_day_first()
    {
        // Neither reading can still close: there is no evidence for either order.
        var document = Normalise(new DateTime(2027, 1, 1), Row(2, "Relay", closing: "10/8/2026"));
        Assert.Equal(new DateTime(2026, 8, 10), document.BidClosingDate.Value.Date);
    }

    [Fact]
    public void The_documents_other_ambiguous_dates_follow_the_closing_dates_order()
    {
        var row = Row(2, "Relay", closing: "10/8/2026");
        row.RequiredDeliveryDate = "2/1/2027";
        var document = Normalise(Arrived, row);

        Assert.Equal(new DateTime(2027, 2, 1), document.RequiredDeliveryDate.Value.Date);
    }

    // --------------------------------------------------------------- delivery date format

    [Theory]
    [InlineData("Sat, 2 Jan, 2027")]
    [InlineData("2 Jan, 2027")]
    [InlineData("Saturday, 2 January 2027")]
    public void A_delivery_date_with_a_weekday_or_a_comma_is_read(string raw)
        => Assert.Equal(new DateTime(2027, 1, 2), RfqDateParser.Read(raw).Value);

    // ------------------------------------------------------------------------ currency

    [Theory]
    [InlineData("US Dollar", "USD")]
    [InlineData("Saudi Riyal", "SAR")]
    [InlineData("European Union Euro", "EUR")]
    [InlineData("USD", "USD")]
    public void A_currency_stated_as_a_word_reaches_the_line_as_its_iso_code(string word, string code)
    {
        var document = Normalise(Arrived, Row(2, "Relay", currency: word));
        var line = Assert.Single(document.LineItems);

        Assert.Equal(code, line.Currency.Value);
        if (word != code)
            Assert.Contains(line.Currency.Transformations, t => t.StartsWith("currency_name_to_iso", StringComparison.Ordinal));
    }

    [Fact]
    public void A_currency_word_nobody_knows_is_kept_as_written()
        => Assert.Equal("Galleons", Assert.Single(Normalise(Arrived, Row(2, "Relay", currency: "Galleons")).LineItems).Currency.Value);

    // ------------------------------------------------------------------ line numbering

    [Fact]
    public void The_buyers_own_line_numbers_are_kept_when_every_line_has_one()
    {
        var document = Normalise(Arrived, Row(2, "Relay", customerNo: "8"), Row(3, "Module", customerNo: "9"));

        Assert.Equal(new[] { "8", "9" }, document.LineItems.Select(l => l.LineItemNo.Value));
        Assert.All(document.LineItems, l => Assert.Equal(CanonicalValueKind.Extracted, l.LineItemNo.Kind));
    }

    [Fact]
    public void Our_numbering_is_used_when_the_buyers_is_missing_or_repeats()
    {
        var partial = Normalise(Arrived, Row(2, "Relay", customerNo: "8"), Row(3, "Module"));
        Assert.Equal(new[] { "1", "2" }, partial.LineItems.Select(l => l.LineItemNo.Value));

        var repeated = Normalise(Arrived, Row(2, "Relay", customerNo: "8"), Row(3, "Module", customerNo: "8"));
        Assert.Equal(new[] { "1", "2" }, repeated.LineItems.Select(l => l.LineItemNo.Value));
    }

    // ------------------------------------------------------------ manufacturing part text

    private const string OneMaker =
        "4000020966 - 0060000235 - BENTLY-NEVADA LLC - US 000000005002923306 - 10030469 - BENTLY-NEVADA LLC - US " +
        "ED_PART_NUMBER - AA32307301 PART_NUMBER - AA 323073-01 CUSTOMER_MATERIAL_DESCRIPTION1 - MODULE";

    private const string TwoMakers =
        "4000062813 - 0060001092 - SCHNEIDER ELECTRIC THE NETHERLANDS - NL 000000005002831975 - 10001675 - SCHNEIDER ELECTRIC THE NETHERLANDS - NL " +
        "PART_NUMBER - LV429827 4000062814 - 0060001761 - PEPPERL+FUCHS (AUST) PTY LTD - AU 000000005002831976 - 10001676 - PEPPERL+FUCHS (AUST) PTY LTD - AU PART_NUMBER - NJ2-12GM40-E2";

    [Fact]
    public void One_approved_maker_becomes_the_lines_manufacturer_with_its_part_numbers_beside_it()
    {
        var document = Normalise(Arrived, Row(2, "Module", extra: new() { ["Manufacturing Part Text"] = OneMaker }));
        var line = Assert.Single(document.LineItems);

        Assert.Equal("BENTLY-NEVADA LLC", line.ManufacturerName.Value);
        Assert.Equal(CanonicalValueKind.Extracted, line.ManufacturerName.Kind);
        Assert.Equal(1.0m, line.ManufacturerName.Confidence);
        Assert.Contains(line.ManufacturerName.Transformations, t => t.StartsWith("read_from_manufacturing_part_text", StringComparison.Ordinal));
        Assert.Equal("AA 323073-01", line.ExtraFields!["Manufacturer part numbers"]);
    }

    [Fact]
    public void Several_approved_makers_are_listed_for_the_reviewer_and_none_is_guessed()
    {
        var document = Normalise(Arrived, Row(2, "Breaker", extra: new() { ["Manufacturing Part Text"] = TwoMakers }));
        var line = Assert.Single(document.LineItems);

        Assert.Null(line.ManufacturerName.Value);
        Assert.Equal("SCHNEIDER ELECTRIC THE NETHERLANDS; PEPPERL+FUCHS (AUST) PTY LTD", line.ExtraFields!["Approved manufacturers"]);
        Assert.Equal("LV429827; NJ2-12GM40-E2", line.ExtraFields["Manufacturer part numbers"]);
    }

    [Fact]
    public void What_was_read_from_the_part_text_survives_the_stored_size_cap()
    {
        // Four approved makers: the raw cell alone runs past the 2 KB the line's extra fields
        // may occupy, and the store drops entries from the end. The reading must come first
        // and the raw text must be shortened, or the makers are computed and then lost.
        var fourMakers = string.Join(" ", Enumerable.Range(1, 4).Select(n =>
            $"40000000{n:00} - 00600000{n:00} - MAKER NUMBER {n} INDUSTRIAL COMPANY LIMITED - US 0000000050000000{n:00} - 100000{n:00} - MAKER NUMBER {n} INDUSTRIAL COMPANY LIMITED - US " +
            $"ED_PART_NUMBER - P{n}00X CUSTOMER_MATERIAL_CODE - 0000000050000000{n:00} PART_NUMBER - P{n}-00X CUSTOMER_MATERIAL_DESCRIPTION1 - SOME LONG DESCRIPTION OF THE PART THAT GOES ON AND ON CUSTOMER_MATERIAL_DESCRIPTION2 - MORE"));
        var extra = new Dictionary<string, string>
        {
            ["Material Type"] = "9CAT", ["Manufacturing Part Text"] = fourMakers,
            ["Material PO Text"] = new string('x', 300), ["Hazardous Indicator"] = "No", ["SASO Indicator"] = "No",
        };
        var line = Assert.Single(Normalise(Arrived, Row(2, "Breaker", extra: extra)).LineItems);

        var stored = ERP_RFQ_Automation.Models.ExtraFieldsJson.Serialize(line.ExtraFields!);
        Assert.NotNull(stored);
        Assert.Contains("Approved manufacturers", stored);
        Assert.Contains("Manufacturer part numbers", stored);
        Assert.Equal(new[] { "Approved manufacturers", "Manufacturer part numbers" }, line.ExtraFields!.Keys.Take(2));
        Assert.True(line.ExtraFields["Manufacturing Part Text"].Length <= 604);
    }

    [Fact]
    public void A_manufacturer_the_line_states_is_never_overridden()
    {
        var row = Row(2, "Module", extra: new() { ["Manufacturing Part Text"] = OneMaker });
        row.ManufacturerName = "Bently Nevada (stated)";
        var line = Assert.Single(Normalise(Arrived, row).LineItems);
        Assert.Equal("Bently Nevada (stated)", line.ManufacturerName.Value);
    }

    // ------------------------------------------------------------ repeated titles in a form

    [Fact]
    public void Three_items_with_the_same_title_keep_their_title_and_their_own_numbers()
    {
        var grid = new List<IReadOnlyList<string?>> { new[] { "Name", "Alternative", "Value" } };
        foreach (var number in new[] { 40, 41, 42 })
        {
            grid.Add(new[] { $"{number} OUTLET, SOCKET, FOR PANELBOARD", "", "" });
            grid.Add(new[] { "OUTLET, SOCKET, FOR PANELBOARD", "", "" });
            grid.Add(new[] { "Price", "", "" });
            grid.Add(new[] { "Quantity", "", "1 each" });
            grid.Add(new[] { "Material Number", "", $"00000000200000{number}" });
            grid.Add(new[] { "Remarks", "", "" });
        }

        var rows = new DocxFormBlockParser().Parse(grid, "event.docx", "Table 7");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal("OUTLET, SOCKET, FOR PANELBOARD", row.ProductName));
        Assert.Equal(new[] { "40", "41", "42" }, rows.Select(r => r.CustomerLineNumber));
    }

    // ------------------------------------------------------------------ issue date label

    [Fact]
    public void A_portals_publish_time_is_the_documents_issue_date()
    {
        Assert.Equal(RfqSpreadsheetFields.ReceivedDate, RfqHeaderVocabulary.Builtin.FieldForLabel("Publish time"));
        Assert.Equal(RfqSpreadsheetFields.ReceivedDate, RfqHeaderVocabulary.Builtin.FieldForColumn("Published on"));
    }
}

/// <summary>What an adversarial review of the accuracy fixes found, each pinned before it was fixed.</summary>
public sealed class EventPrintAccuracyReviewTests
{
    private static readonly System.Text.Encoding Utf8 = System.Text.Encoding.UTF8;
    private static byte[] Csv(params string[] lines) => Utf8.GetBytes(string.Join("\r\n", lines));

    [Fact]
    public void A_model_column_beside_a_part_number_column_never_becomes_the_part_number()
    {
        var rows = new NativeSpreadsheetParser().ParseCsv(Csv(
            "Item,Equipment,Model,Part No,Qty",
            "1,Pump CR64,GRUNDFOS CR64,96123456-01,4"), "spares.csv");
        var row = Assert.Single(rows);

        Assert.Equal("96123456-01", row.ManufacturerPartNumber);
        Assert.Equal("GRUNDFOS CR64", row.UnmappedColumns["Model"]);
    }

    [Theory]
    [InlineData("Lot")]
    [InlineData("Per")]
    [InlineData("Nil")]
    [InlineData("TBD")]
    public void A_three_letter_word_in_a_currency_cell_is_not_a_currency_code(string word)
        => Assert.Null(CurrencyNames.ToIsoCode(word));

    [Theory]
    [InlineData("USD", "USD")]
    [InlineData("kwd", "KWD")]
    [InlineData("AED", "AED")]
    public void A_real_iso_code_passes_as_itself(string word, string code)
        => Assert.Equal(code, CurrencyNames.ToIsoCode(word));

    [Fact]
    public void A_column_whose_value_differs_per_line_is_never_sent_as_header_text()
    {
        var rows = new[]
        {
            new RfqSpreadsheetRow { RowNumber = 2, ProductName = "Valve", Quantity = "4", UnmappedColumns = new() { ["Cust Ref"] = "PO-77821", ["Cut-off"] = "12/12/2026" } },
            new RfqSpreadsheetRow { RowNumber = 3, ProductName = "Gasket", Quantity = "9", UnmappedColumns = new() { ["Cust Ref"] = "PO-77822", ["Cut-off"] = "12/12/2026" } },
        };

        var text = ERP_RFQ_Automation.Extraction.HeaderCompletion.HeaderCompletionService.ComposeHeaderText(rows, null);

        Assert.Contains("Cut-off: 12/12/2026", text);
        Assert.DoesNotContain("Cust Ref", text);
        Assert.DoesNotContain("PO-77821", text);
    }

    [Theory]
    [InlineData("Quotation Request 20260910120000.docx", null)]
    [InlineData("Export 202609101200.xlsx", null)]
    [InlineData("RFP 6000000031 - Switchgear Package.docx", "6000000031")]
    [InlineData("RFP - 60000010028 - 1 of 3.docx", "60000010028")]
    public void A_timestamp_in_a_file_name_is_not_an_rfq_number(string fileName, string? expected)
        => Assert.Equal(expected, DocxTableParser.RfqNumberFromFileName(fileName));

    [Fact]
    public void The_arrival_date_travels_from_the_job_to_the_date_order_rule()
    {
        // Received 20 February, closing "11/03/2026": day-first (11 March) is still ahead of
        // arrival, so it stands — whatever day the extraction happens to run on.
        var rows = new[] { new RfqSpreadsheetRow { RowNumber = 2, ProductName = "Valve", Quantity = "4", BidClosingDate = "11/03/2026" } };
        var extractor = new ERP_RFQ_Automation.Extraction.ChunkedExtractionService(
            new ERP_RFQ_Automation.Tests.Support.StubLlm(), new CanonicalRfqNormalizer(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ERP_RFQ_Automation.Extraction.ChunkedExtractionService>.Instance);

        var outcome = extractor.ExtractStructuredAsync(rows, 1, "bid.csv", receivedOn: new DateTime(2026, 2, 20)).GetAwaiter().GetResult();

        Assert.Equal(new DateTime(2026, 3, 11), outcome.CanonicalImport!.Documents[0].BidClosingDate.Value.Date);
    }
}
