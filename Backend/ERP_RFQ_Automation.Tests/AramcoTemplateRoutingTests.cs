using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Templates;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The Aramco bid list template reads a document into the SAME rows a spreadsheet produces,
/// and those rows take the structured path: normaliser, canonical import, evidence ledger.
/// These tests drive the paid entry point with a model that explodes if called, so they prove
/// both that the template is reached and that what it produces can be cited line by line.
/// </summary>
public sealed class AramcoTemplateRoutingTests
{
    // Numbered by non-blank line so the evidence assertions below can name a row.
    private const string Preamble = """
        MATERIALS E-BIDDING SYSTEM
        Bid Materials List (Low Value Bid)
        2/16/2021 7:31:24 AM
        Vendor Code
        Vendname
        Bidno
        Bid Date
        Bid Close
        2004414
        ALI ZAID AL-QURAISHI&PARTNERS EL
        C001046933
        2/16/2021
        2/28/2021
        Address
        Buyer
        Buyer Tel
        Saudi Arabia
        1G5-Fawzi Alomari
        011-8078850-
        Bid Line
        Item No
        Ship To
        Req Unit
        Req Qty
        Resp Qty
        For Foreign Suppliers, If the delivery type is CIF or DDP, Supplier must attach.
        """;                                                  // 26 lines; the first record starts on line 27

    private static ChunkedExtractionService NewService(ExplodingLlm llm)
        => new(llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());

    private static DocumentExtractionInput Doc(string text, string name = "bid.doc")
        => new() { BusinessUnitId = 1, SourceDocumentName = name, HeaderText = text, ReceivedOn = new DateTime(2021, 2, 16, 8, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public async Task An_Aramco_bid_list_is_extracted_with_no_model_call()
    {
        var text = Preamble + "\n" + string.Join("\n",
            "10", "902017274", "3801", "EA", "176", "KEY:SHAFT,SQUARE", "SHAPE:", "SQUARE;",
            "20", "902017276", "3801", "EA", "89", "SEAL,4 MM DIA");
        var llm = new ExplodingLlm();

        var outcome = await NewService(llm).ExtractUnstructuredAsync(Doc(text));

        Assert.False(llm.WasCalled);
        Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);
        Assert.Null(outcome.AiProviderClass);
        Assert.Equal(ExtractionProcessingPath.DeterministicRules, outcome.ProcessingPath);
        Assert.Equal(2, outcome.ExtractedItemCount);
        Assert.Equal(outcome.ExpectedItemCount, outcome.ExtractedItemCount);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("template", StringComparison.OrdinalIgnoreCase));

        var first = outcome.Result!.Items[0];
        Assert.Equal("902017274", first.ItemMaterialCode);
        Assert.Equal("10", first.LineItemNo);
        Assert.Equal(176, first.Quantity);
        Assert.Equal("EA", first.UnitOfMeasure);
        Assert.Equal("KEY:SHAFT,SQUARE", first.ProductShortName);
        Assert.Contains("SQUARE;", first.ItemText);
        Assert.Equal("3801", first.ExtraFields!["Ship To"]);   // the buyer's plant code, under the buyer's own label

        Assert.Equal("C001046933", outcome.Result.Rfqno);
        Assert.Equal("1G5-Fawzi Alomari", outcome.Result.BuyersName);
        Assert.Equal("2021-02-28", outcome.Result.BidClosingDate);
    }

    [Fact]
    public async Task Every_value_cites_the_line_it_was_printed_on()
    {
        // This is what was missing. The template returned lead items with no canonical import,
        // the ledger wrote no evidence for the door, and the Decide screen marked every line
        // "no source document on file" — a perfectly read bid that could not be quoted.
        var text = Preamble + "\n" + string.Join("\n",
            "10", "902017274", "3801", "EA", "176", "KEY:SHAFT,SQUARE", "SHAPE:", "SQUARE;");

        var outcome = await NewService(new ExplodingLlm()).ExtractUnstructuredAsync(Doc(text));

        var document = Assert.Single(outcome.CanonicalImport!.Documents);
        var line = Assert.Single(document.LineItems);
        Assert.Equal("'Bid Materials List'!A11", Assert.Single(document.RfqNo.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A18", Assert.Single(document.BuyerName.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A13", Assert.Single(document.BidClosingDate.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A27", Assert.Single(line.LineItemNo.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A28", Assert.Single(line.CustomerMaterialCode.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A30", Assert.Single(line.UnitOfMeasure.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A31", Assert.Single(line.Quantity.Evidence).Location);
        Assert.Equal("176", Assert.Single(line.Quantity.Evidence).RawValue);
        Assert.Equal("'Bid Materials List'!A32", Assert.Single(line.ProductName.Evidence).Location);
        Assert.Equal(ValidationStatus.Valid, line.ValidationStatus);
    }

    [Fact]
    public async Task A_record_without_a_ship_to_still_cites_its_unit_and_quantity()
    {
        var text = Preamble + "\n" + string.Join("\n", "10", "902017274", "M", "2.5", "CABLE,ELEC");

        var outcome = await NewService(new ExplodingLlm()).ExtractUnstructuredAsync(Doc(text));

        var line = Assert.Single(Assert.Single(outcome.CanonicalImport!.Documents).LineItems);
        Assert.Equal("'Bid Materials List'!A29", Assert.Single(line.UnitOfMeasure.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A30", Assert.Single(line.Quantity.Evidence).Location);
        Assert.Equal("'Bid Materials List'!A31", Assert.Single(line.ProductName.Evidence).Location);
        var item = Assert.Single(outcome.Result!.Items);
        Assert.Equal(2.5m, item.Quantity);            // fractional quantities are preserved exactly
        Assert.Null(item.ExtraFields);                 // nothing invented for the blank Ship To
    }

    [Fact]
    public void The_buyer_is_the_person_named_under_Buyer_not_the_address()
    {
        // "Address | Buyer | Buyer Tel" is one label block; reading two lines past "Buyer"
        // landed on the address, and every lead from this door named "Saudi Arabia" as its buyer.
        var text = Preamble + "\n" + string.Join("\n", "10", "902017274", "3801", "EA", "5", "ITEM");

        var bid = AramcoBidListParser.Parse(text);

        Assert.Equal("1G5-Fawzi Alomari", bid.Buyer);
        Assert.Equal(18, bid.Rows!.Buyer);
        Assert.Equal(11, bid.Rows.Bidno);
        Assert.Equal(20, bid.Rows.ColumnHeader);
    }

    [Fact]
    public void A_document_that_is_not_an_Aramco_bid_list_routes_to_the_model_silently()
    {
        var rows = AramcoBidListExtraction.TryReadRows(
            "Please quote 5 EA of ABC-123.", "email_body.txt", out var rejection);

        Assert.Null(rows);
        // Not a refusal — it was simply never ours. Nothing to warn about.
        Assert.Null(rejection);
    }

    [Fact]
    public void An_Aramco_document_the_template_cannot_read_routes_to_the_model_LOUDLY()
    {
        // A layout change at the sender, or a defect here. Either is worth knowing about
        // before the bill arrives, so the rejection is surfaced rather than swallowed.
        var text = Preamble + "\n" + string.Join("\n",
            "10", "902017274", "3801", "176", "EA", "KEY:SHAFT");   // unit and quantity swapped

        var rows = AramcoBidListExtraction.TryReadRows(text, "bid.doc", out var rejection);

        Assert.Null(rows);
        Assert.NotNull(rejection);
        Assert.Contains("neither a unit of measure nor a plant code", rejection);
    }

    [Fact]
    public async Task Deterministic_reads_are_certain_and_say_so()
    {
        var text = Preamble + "\n" + string.Join("\n", "10", "902017274", "3801", "EA", "5", "ITEM");

        var outcome = await NewService(new ExplodingLlm()).ExtractUnstructuredAsync(Doc(text));

        var line = Assert.Single(outcome.Result!.Items);
        Assert.Equal(1.0d, line.ItemConfidence);
        Assert.Equal(1.0d, line.QuantityConfidence);
        Assert.Equal(1.0d, outcome.Result.OverallConfidence);
    }

    private sealed class ExplodingLlm : ILLMService
    {
        public bool WasCalled { get; private set; }
        public AiProviderClass ProviderClass => AiProviderClass.External;

        public Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("The model must not be called for a document the template can read.");
        }

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<BoqDraftResult?>(null);
    }
}
