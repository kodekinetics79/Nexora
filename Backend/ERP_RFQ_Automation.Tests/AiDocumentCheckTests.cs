using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Anchoring;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// P0 #6 — AI-read PDFs gave wrong quantities and codes, dropped lines silently, and every line
/// they produced was MISSING_SOURCE. These tests replay the model's ACTUAL wrong answers from the
/// 2026-09-28 audit (leads 14, 18, 20, 27, 28) against the ACTUAL documents, read by the PDF
/// reader exactly as production now reads them, and assert what reaches the lead. No model is
/// called: the model's answer is scripted, because the defect was never the model — it was that
/// nothing checked the answer against the page.
/// </summary>
public sealed class AiDocumentCheckTests
{
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ai-pdf");

    private static ChunkedExtractionService NewService(ILLMService llm)
        => new(llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

    // ---- the reader --------------------------------------------------------------------------

    [Fact]
    public async Task The_PDF_reader_keeps_a_SABIC_quantity_beside_its_currency_and_the_product_number_on_its_own_line()
    {
        // X2. Page.Text glued the page into one run-on line, so "6" (order quantity), "SAR", the
        // product number "978596", "EA" and "1" (price per unit) ran together — the model took the 1.
        var input = await ReadPdfAsync("sabic-bid-5500804759.pdf", "RFx 5500804759.msg");
        var text = string.Join('\n', input.LineItemRegions);

        Assert.Contains("\nCIRCUIT: TYP CONTROL BOARD 6 SAR\n", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^978596 EA\s+1$", text);
        Assert.StartsWith("[[page 1]]", input.LineItemRegions[0], StringComparison.Ordinal);
        Assert.True(input.RegionsCoverWholeDocument);
    }

    [Fact]
    public async Task The_PDF_reader_keeps_the_SAP_item_number_apart_from_the_material_number_and_reads_line_by_line()
    {
        // X2. "00010201195514" and "40KVAPRIMARY" came from glued page text.
        var input = await ReadPdfAsync("masa-rfq-9500202307.pdf", "MaSa RFQ No 9500202307 Ref MKA-26-133.pdf");
        var text = string.Join('\n', input.LineItemRegions);

        Assert.Contains("\n00010 201195514 TRANSFORMER:STEP UP,400  TO 480VAC,40KVA\n", text, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^1\s+each$", text);
        Assert.Contains("\nPRIMARY VOLTS: 400  VAC ( 3 PHASE + NEUTRAL ).\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("00010201195514", text, StringComparison.Ordinal);
        Assert.DoesNotContain("40KVAPRIMARY", text, StringComparison.Ordinal);
        // One region per page, each opening with its page marker — never 20 pages of "header".
        Assert.Equal(13, input.LineItemRegions.Count);
        Assert.All(input.LineItemRegions, region => Assert.StartsWith("[[page ", region, StringComparison.Ordinal));
        Assert.DoesNotContain("[[page", input.HeaderText, StringComparison.Ordinal);
    }

    // ---- the four quote-critical values, on the real prints ----------------------------------

    [Fact]
    public async Task SABIC_5500804759_asks_for_six_control_boards_not_one()
    {
        // Lead 27, verbatim: quantity 1 (the price-per-unit "1" after "978596 EA").
        var modelAnswer = Line(
            lineNo: "1", description: "CIRCUIT: TYPE CONTROL BOARD, APPLICATION MEDIUM VOLTAGE VACUUM CONTACTOR",
            maker: "CUTLER-HAMMER EATON", mpn: "2147A58G03", quantity: 1, unit: "EA", material: "978596",
            extras: new() { ["Taxes/Duties"] = "0,00" });
        var input = await ReadPdfAsync("sabic-bid-5500804759.pdf", "RFx 5500804759.msg");

        var line = Assert.Single((await ExtractAsync(input, modelAnswer)).Result!.Items);

        Assert.Equal(6m, line.Quantity);
        Assert.Equal("EA", line.UnitOfMeasure);
        Assert.Equal("978596", line.ItemMaterialCode);
        Assert.Equal("1", line.LineItemNo);
        Assert.True(line.EvidenceFromDocumentCheck);
        var quantity = Assert.Single(line.VerifiedEvidence!, e => e.FieldName == "Quantity");
        Assert.Equal("6", quantity.RawValue);
        Assert.StartsWith("Page 1, line ", quantity.SourceAddress, StringComparison.Ordinal);
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "ItemMaterialCode" && e.RawValue == "978596");
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "UnitOfMeasure" && e.RawValue == "EA");
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "ManufacturerPartNumber" && e.RawValue == "2147A58G03");
    }

    [Fact]
    public async Task SABIC_5500813867_carries_product_number_1637334_not_a_code_composed_from_the_short_text()
    {
        // Lead 18, verbatim: ItemMaterialCode "MATLFLDLGHT-ELCTRC" = item category "MATL" +
        // the short text. The buyer's product number 1637334 was dropped entirely.
        var modelAnswer = Line(
            lineNo: "1",
            description: "LED , POWER: 150 W, POTENTIAL: 100 - 277 V, MATERIAL: ALUMINIUM CASING, LENS COLOR: ANTI FOG YELLOWISH , CLASSIFICATION: IP65",
            maker: "ECOALY", mpn: "EA-F150WBM65", quantity: 43, unit: "EA", material: "MATLFLDLGHT-ELCTRC");
        var input = InputFromText(Fixture("sabic-bid-5500813867.txt"), "BID5500813867.PDF");

        var line = Assert.Single((await ExtractAsync(input, modelAnswer)).Result!.Items);

        Assert.Equal("1637334", line.ItemMaterialCode);
        Assert.NotEqual("MATLFLDLGHT-ELCTRC", line.ItemMaterialCode);
        Assert.Equal(43m, line.Quantity);
        Assert.Equal("EA", line.UnitOfMeasure);
        Assert.Equal("1", line.LineItemNo);
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "ItemMaterialCode" && e.RawValue == "1637334");
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "Quantity" && e.RawValue == "43");
    }

    [Fact]
    public async Task Marafiq_6000595208_asks_for_two_assemblies_and_no_invented_RFQ_Quantity_column()
    {
        // Lead 28, verbatim: quantity 1, glued code "00010201140757", and ExtraFields
        // {"Unit": "Assembley", "RFQ Quantity": "1"} — a value the document does not state.
        var modelAnswer = Line(
            lineNo: "1", description: "ASSY CNTRL;LMT-TRQU SWTCH,15-30NM,AUMA 2 Assembley",
            maker: "AUMA", mpn: "559 0204MD 98683", quantity: 1, unit: "Assembley", material: "00010201140757",
            extras: new() { ["Unit"] = "Assembley", ["RFQ Quantity"] = "1" });
        var input = InputFromText(Fixture("marafiq-rfq-6000595208-p1-2.txt"),
            "Fw_ Marafiq RFQ No 6000595208, Ref No HFE-26-204..msg");

        var outcome = await ExtractAsync(input, modelAnswer);
        var line = Assert.Single(outcome.Result!.Items);

        Assert.Equal(2m, line.Quantity);
        Assert.Equal("Assembley", line.UnitOfMeasure);
        Assert.Equal("201140757", line.ItemMaterialCode);
        Assert.Equal("00010", line.LineItemNo);
        Assert.True(line.ExtraFields is null || !line.ExtraFields.ContainsKey("RFQ Quantity"));
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "Quantity" && e.RawValue == "2");
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "ManufacturerPartNumber" && e.RawValue == "559 0204MD 98683");
        Assert.Contains(outcome.Diagnostics, d => d.Contains("1 value(s) corrected", StringComparison.Ordinal)
                                                  || d.Contains("value(s) corrected", StringComparison.Ordinal));
        // HT-08: the RFQ number the buyer requires on the quote is in the file name only.
        Assert.Equal("6000595208", outcome.Result.Rfqno);
    }

    [Fact]
    public async Task MaSa_9500202307_is_one_transformer_line_00010_material_201195514_not_two_glued_copies()
    {
        // Lead 20, verbatim: the same line twice — once numbered by the glued code, once "1".
        var first = Line(lineNo: "00010201195514", description: "TRANSFORMER:STEP UP,400 TO 480VAC,40KVA",
            quantity: 1, unit: "EA", material: "00010201195514",
            extras: new() { ["Country of Origin"] = "ANY", ["Manufacturer/Brand"] = "ANY" });
        var echo = Line(lineNo: "1", description: "TRANSFORMER:STEP UP,400 TO 480VAC,40KVA",
            quantity: 1, unit: "EA", material: "00010201195514",
            extras: new() { ["Unit"] = "each", ["RFQ Quantity"] = "1" });
        var input = await ReadPdfAsync("masa-rfq-9500202307.pdf", "MaSa RFQ No 9500202307 Ref MKA-26-133.pdf");
        var llm = new StubLlm(
            Result([first], rfqNo: "MKA-26-133"), Result([echo]), Result([]), Result([]), Result([]), Result([]))
        { MaxOutputTokens = 4096 };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(input);

        var line = Assert.Single(outcome.Result!.Items);
        Assert.Equal("00010", line.LineItemNo);
        Assert.Equal("201195514", line.ItemMaterialCode);
        Assert.Equal(1m, line.Quantity);
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "LineItemNo" && e.RawValue == "00010");
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "Quantity" && e.RawValue == "1");
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "UnitOfMeasure" && e.RawValue == "each");
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        // HT-08: the file name's RFQ number wins over the collective number the page prints.
        Assert.Equal("9500202307", outcome.Result.Rfqno);
        Assert.Equal("MKA-26-133", outcome.Result.OpportunityNo);
    }

    [Fact]
    public async Task Aramco_6000000034_keeps_the_buyers_line_numbers_8_and_9_not_positions_1_and_2()
    {
        // Lead 14 (X9): the model numbered the buyer's lines 8, 9, 35 as 1, 2, 3.
        var eight = Line(lineNo: "1", description: "BASE: LAMP, 230 VAC, FOR INDI", maker: "ABB BV - NL",
            mpn: "GHG4171801R0001", quantity: 1, unit: "EA", material: "000000002000162772",
            extras: new() { ["Material Type"] = "9CAT", ["Material Number"] = "000000002000162772" });
        var nine = Line(lineNo: "2", description: "BATTERY, LEAD ACID, STATIONARY, 200 AH", maker: "EXIDE CORPORTION - US",
            mpn: "N200", quantity: 1, unit: "EA", material: "000000002000165737");
        var input = InputFromText(Fixture("aramco-rfp-6000000034-p1-4.txt"), "RFP 6000000034 (2).pdf");
        var llm = new StubLlm(Result([eight, nine])) { MaxOutputTokens = 8192 };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(input);

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(new[] { "8", "9" }, outcome.Result!.Items.Select(i => i.LineItemNo).ToArray());
        Assert.All(outcome.Result.Items, line =>
        {
            Assert.Equal(1m, line.Quantity);
            Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "Quantity");
            Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "ItemMaterialCode");
        });
        Assert.Equal("6000000034", outcome.Result.Rfqno);
    }

    [Fact]
    public async Task A_value_the_page_does_not_print_is_cleared_never_kept_and_never_invented()
    {
        var text = "[[page 1]]\nRFQ 1234\nItem 1 Gate valve DN50 PN16 stainless\nFlanged ends, lever operated\n";
        var modelAnswer = Line(lineNo: "7", description: "Gate valve DN50 PN16 stainless", quantity: 5, unit: "EA",
            material: "GV-5050-X");

        var line = Assert.Single((await ExtractAsync(InputFromText(text, "enquiry.pdf"), modelAnswer)).Result!.Items);

        Assert.Null(line.Quantity);
        Assert.Null(line.UnitOfMeasure);
        Assert.Null(line.ItemMaterialCode);
        Assert.Null(line.LineItemNo);
        // What IS printed is cited: the description, and nothing else.
        var evidence = Assert.Single(line.VerifiedEvidence!);
        Assert.Equal("ProductShortDescription", evidence.FieldName);
    }

    [Fact]
    public async Task A_line_nothing_on_the_page_identifies_keeps_its_description_and_loses_its_numbers()
    {
        var text = "[[page 1]]\nPlease quote the attached list.\n";
        var modelAnswer = Line(lineNo: "1", description: "Hydraulic pump HP-200", quantity: 3, unit: "EA", material: "HP200");

        var line = Assert.Single((await ExtractAsync(InputFromText(text, "note.pdf"), modelAnswer)).Result!.Items);

        Assert.Equal("Hydraulic pump HP-200", line.ProductShortDescription);
        Assert.Null(line.Quantity);
        Assert.Null(line.ItemMaterialCode);
        Assert.Null(line.VerifiedEvidence);
        Assert.True(line.EvidenceFromDocumentCheck);
    }

    [Fact]
    public async Task A_quantity_the_page_prints_twice_differently_is_cleared_rather_than_guessed()
    {
        var text = "[[page 1]]\nItem 1 Gate valve DN50 PN16 stainless\nQuantity 4 EA\nQuantity 6 EA\n";
        var modelAnswer = Line(lineNo: "1", description: "Gate valve DN50 PN16 stainless", quantity: 5, unit: "EA");

        var line = Assert.Single((await ExtractAsync(InputFromText(text, "enquiry.pdf"), modelAnswer)).Result!.Items);

        Assert.Null(line.Quantity);
    }

    [Theory]
    [InlineData("Quantity 1,250 PCS", 1250)]
    [InlineData("Qty: 2.5 M", 2.5)]
    [InlineData("12     each", 12)]
    public async Task Quantities_match_through_separators_decimals_and_unit_spellings(string printed, double expected)
    {
        var text = $"[[page 1]]\nItem 1 Cable armoured 4C x 16 sq mm\n{printed}\n";
        var modelAnswer = Line(lineNo: "1", description: "Cable armoured 4C x 16 sq mm",
            quantity: (decimal)expected, unit: printed.Contains("each") ? "EA" : printed.Split(' ').Last());

        var line = Assert.Single((await ExtractAsync(InputFromText(text, "cable.pdf"), modelAnswer)).Result!.Items);

        Assert.Equal((decimal)expected, line.Quantity);
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "Quantity");
        Assert.Contains(line.VerifiedEvidence!, e => e.FieldName == "UnitOfMeasure");
    }

    // ---- partial reads -----------------------------------------------------------------------

    [Fact]
    public async Task A_part_that_could_not_be_read_makes_the_read_partial_with_the_numbers()
    {
        // X1. Lead 14: 47 of 48 calls cut off, 3 of 42 lines back, job "Succeeded", no warning.
        // One page per call here; the page holding line 9 fails.
        var eight = Line(lineNo: "8", description: "BASE, LAMP, 230 VAC, FOR INDICATOR LIGHT", quantity: 1, unit: "each",
            material: "000000002000162772");
        var input = InputFromText(Fixture("aramco-rfp-6000000034-p1-4.txt"), "RFP 6000000034 (2).pdf");
        var llm = new StubLlm(Result([]), Result([eight]), null, Result([])) { MaxOutputTokens = 2048 };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(input);

        Assert.Equal(4, llm.CallCount);
        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.True(PartialReadFact.TryParse(outcome.ReviewReason, out var fact));
        Assert.Equal(1, fact.LinesRead);
        Assert.Equal(2, fact.LinesExpected);
        Assert.Equal(1, fact.PartsUnread);
        Assert.StartsWith("Read 1 of about 2 lines;", outcome.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_that_print_more_lines_than_came_back_make_the_read_partial_even_when_every_call_succeeded()
    {
        var eight = Line(lineNo: "8", description: "BASE, LAMP, 230 VAC, FOR INDICATOR LIGHT", quantity: 1, unit: "each");
        var input = InputFromText(Fixture("aramco-rfp-6000000034-p1-4.txt"), "RFP 6000000034 (2).pdf");
        var llm = new StubLlm(Result([eight])) { MaxOutputTokens = 8192 };

        var outcome = await NewService(llm).ExtractUnstructuredAsync(input);

        Assert.Equal(ExtractionOutcomeStatus.NeedsReview, outcome.Status);
        Assert.True(PartialReadFact.TryParse(outcome.ReviewReason, out var fact));
        Assert.Equal(new PartialReadFact(1, 2, 0), fact);
    }

    [Fact]
    public async Task A_complete_read_says_nothing_about_being_partial()
    {
        var modelAnswer = Line(lineNo: "1", description: "CIRCUIT: TYPE CONTROL BOARD, APPLICATION MEDIUM VOLTAGE VACUUM CONTACTOR",
            quantity: 6, unit: "EA", material: "978596");
        var outcome = await ExtractAsync(await ReadPdfAsync("sabic-bid-5500804759.pdf", "RFx 5500804759.msg"), modelAnswer);

        Assert.False(PartialReadFact.TryParse(outcome.ReviewReason, out _));
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
    }

    [Theory]
    [InlineData(3, 42, 28, "Read 3 of about 42 lines; 28 part(s) of the document could not be read. Check the document before quoting.")]
    [InlineData(1, null, 2, "Read 1 line; 2 part(s) of the document could not be read. Check the document before quoting.")]
    [InlineData(5, 7, 0, "Read 5 of about 7 lines; some lines the document prints were not returned. Check the document before quoting.")]
    public void The_partial_read_sentence_round_trips_even_inside_joined_review_reasons(
        int read, int? expected, int parts, string sentence)
    {
        var fact = new PartialReadFact(read, expected, parts);
        Assert.Equal(sentence, fact.Describe());

        Assert.True(PartialReadFact.TryParse($"OCR was incomplete; {sentence} (2 chunk(s) failed to extract.); other", out var parsed));
        Assert.Equal(fact, parsed);
        Assert.False(PartialReadFact.TryParse("2 chunk(s) failed to extract.", out _));
    }

    // ---- a checked model line is checkable, never self-verified -------------------------------

    [Fact]
    public void A_model_line_whose_values_were_found_on_the_page_still_goes_to_a_person()
    {
        // With evidence on every quote-critical value, a clean, confident model read used to be
        // eligible for automatic verification. Found on the page is NEEDS_CHECK, not VERIFIED.
        var line = Line(lineNo: "1", description: "Gate valve", quantity: 6, unit: "EA", material: "978596") with
        {
            VerifiedEvidence =
            [
                new LeadItemEvidenceData("ItemMaterialCode", "978596", "978596", "Page 1, line 58", 1m),
                new LeadItemEvidenceData("Quantity", "6", "6", "Page 1, line 55", 1m),
                new LeadItemEvidenceData("UnitOfMeasure", "EA", "EA", "Page 1, line 58", 1m)
            ]
        };
        var result = Result([line]) with { OverallConfidence = 0.99 };
        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok, Result = result, ExpectedItemCount = 1, ExtractedItemCount = 1,
            ProcessingPath = ExtractionProcessingPath.LocalModel
        };

        Assert.Equal(LeadPersister.AutoVerification.HighConfidence,
            LeadPersister.DecideAutoVerification(outcome, [result], 17, minConfidence: 0.85m));

        var checkedLine = result with { Items = [line with { EvidenceFromDocumentCheck = true }] };
        Assert.Equal(LeadPersister.AutoVerification.None,
            LeadPersister.DecideAutoVerification(outcome.WithResult(checkedLine), [checkedLine], 17, minConfidence: 0.85m));
    }

    [Fact]
    public void Addresses_name_the_page_and_the_line()
    {
        var text = AnchorText.Build(["[[page 2]]\nfirst\nsecond", "third"]);
        Assert.Equal("Page 2, line 2", text.Address(text.Text.IndexOf("second", StringComparison.Ordinal)));
        Assert.Equal("Page 2, line 3", text.Address(text.Text.IndexOf("third", StringComparison.Ordinal)));
        Assert.Equal("Line 4", AnchorText.Build(["a\nb", "c\nd"]).Address(9));
        Assert.Equal("x\ny", AnchorText.WithoutPageMarkers("[[page 1]]\nx\n[[page 2]]\ny"));
    }

    // ---- harness -----------------------------------------------------------------------------

    private static async Task<ChunkedExtractionOutcome> ExtractAsync(DocumentExtractionInput input, LeadItemData modelAnswer)
    {
        var responses = Enumerable.Range(0, 40).Select(i => (LeadExtractionResult?)(i == 0 ? Result([modelAnswer]) : Result([])))
            .ToArray();
        return await NewService(new StubLlm(responses) { MaxOutputTokens = 8192 }).ExtractUnstructuredAsync(input);
    }

    private static LeadExtractionResult Result(List<LeadItemData> items, string? rfqNo = "RFQ-1")
        => Ext.Result(items, 0.9) with { Rfqno = rfqNo };

    private static LeadItemData Line(
        string? lineNo, string description, decimal? quantity, string? unit, string? material = null,
        string? maker = null, string? mpn = null, Dictionary<string, string>? extras = null)
        => Ext.Item(0.95, name: null) with
        {
            LineItemNo = lineNo,
            ProductShortDescription = description,
            Quantity = quantity,
            UnitOfMeasure = unit,
            ItemMaterialCode = material,
            ManufacturerName = maker,
            ManufacturerPartNumber = mpn,
            ExtraFields = extras
        };

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(FixtureDirectory, name));

    /// <summary>The unstructured input the production reader builds from this text (see ProductionDocumentReader.Unstructured).</summary>
    private static DocumentExtractionInput InputFromText(string text, string name)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0).ToList();
        var (header, regions) = PdfPageRegions.Split(lines, 20);
        return new DocumentExtractionInput
        {
            BusinessUnitId = 1,
            SourceDocumentName = name,
            HeaderText = header,
            LineItemRegions = regions,
            RegionsCoverWholeDocument = true
        };
    }

    private static async Task<DocumentExtractionInput> ReadPdfAsync(string fixture, string fileName)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(FixtureDirectory, fixture));
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new TestEnvironment(AppContext.BaseDirectory),
            new MemoryStorage(bytes));
        return await reader.ReadAsync(new ExtractionJob
        {
            Id = 27, BusinessUnitId = 7, StoragePath = "memory://evidence/object",
            ContentHash = new string('d', 64), FileName = fileName, FileType = "pdf"
        });
    }

    private sealed class MemoryStorage(byte[] content) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    private sealed class TestEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "ERP_RFQ_Automation.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
    }
}
