using System.Text;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.HeaderCompletion;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A structured document whose lines were read without a model may still state its closing
/// date in a label the vocabulary does not know. The header text alone is read, and only what
/// the model quotes verbatim from that text is believed.
/// </summary>
public sealed class HeaderCompletionServiceTests
{
    private sealed class ScriptedLlm : ILLMService
    {
        private readonly HeaderCompletionResult? _answer;
        private readonly Exception? _throws;
        public int Calls { get; private set; }
        public string? LastText { get; private set; }
        public AiCallContext? LastContext { get; private set; }
        public AiProviderClass ProviderClass { get; init; } = AiProviderClass.Local;

        public ScriptedLlm(HeaderCompletionResult? answer, Exception? throws = null)
        {
            _answer = answer;
            _throws = throws;
        }

        public Task<LeadExtractionResult?> ExtractLeadDataAsync(string fullText, AiCallContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Header completion must never call the line-item extractor.");

        public Task<BoqDraftResult?> DraftServiceBoqAsync(string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException();

        public Task<HeaderCompletionResult?> CompleteHeaderAsync(string headerText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastText = headerText;
            LastContext = context;
            if (_throws is not null) throw _throws;
            return Task.FromResult(_answer);
        }
    }

    private static readonly HeaderCompletionResult Nothing = new(null, null, null, null, null, null, null, null, null, null, 0.5);

    private static DocumentExtractionInput Input(List<RfqSpreadsheetRow> rows, string? narrative = null) => new()
    {
        BusinessUnitId = 7,
        SourceId = "job:42",
        AttemptNumber = 2,
        SourceDocumentName = "bid.docx",
        ProcessingPath = ExtractionProcessingPath.DeterministicRules,
        IsStructured = true,
        StructuredRows = rows,
        HeaderText = string.Empty,
        LineItemRegions = rows.Select(r => r.ProductName ?? string.Empty).ToList(),
        DocumentNarrative = narrative,
    };

    private static List<RfqSpreadsheetRow> Rows(Dictionary<string, string>? labels = null, string? closing = null)
        => Enumerable.Range(1, 2).Select(n => new RfqSpreadsheetRow
        {
            RowNumber = n + 1,
            ProductName = $"Relay {n}",
            Quantity = "4",
            BidClosingDate = closing,
            UnmappedHeaderLabels = labels ?? new Dictionary<string, string>(StringComparer.Ordinal),
        }).ToList();

    private static HeaderCompletionService Service(ScriptedLlm llm, IAiExternalProviderTrust? trust = null)
        => new(llm, NullLogger<HeaderCompletionService>.Instance, trust);

    [Fact]
    public async Task A_closing_date_and_rfq_number_stated_under_unknown_labels_are_filled_on_every_row()
    {
        var rows = Rows(new Dictionary<string, string> { ["BCD"] = "10/8/2026 3:00 PM", ["Event"] = "Doc60000010028" });
        var llm = new ScriptedLlm(Nothing with
        {
            Rfqno = "Doc60000010028", RfqnoSpan = "Event: Doc60000010028",
            BidClosingDate = "2026-08-10 15:00", BidClosingDateSpan = "BCD: 10/8/2026 3:00 PM",
        });

        var outcome = await Service(llm).CompleteAsync(Input(rows));

        Assert.Equal(new[] { RfqSpreadsheetFields.RfqNo, RfqSpreadsheetFields.BidClosingDate }, outcome.CompletedFields);
        Assert.All(rows, row =>
        {
            Assert.Equal("Doc60000010028", row.RfqNo);
            Assert.Equal("BCD: 10/8/2026 3:00 PM", row.BidClosingDate);
            Assert.Equal(HeaderCompletionService.CompletionConfidence, row.FieldProvenance[RfqSpreadsheetFields.BidClosingDate].Confidence);
            Assert.StartsWith(HeaderCompletionService.ProvenancePrefix, row.FieldProvenance[RfqSpreadsheetFields.RfqNo].Note);
        });
        Assert.Contains("BCD: 10/8/2026 3:00 PM", llm.LastText);
        Assert.DoesNotContain("Relay 1", llm.LastText);
        Assert.Equal("header:job:42:a2", llm.LastContext!.IdempotencyKey);
        Assert.Equal(AiPromptVersions.HeaderCompletion, llm.LastContext.PromptVersion);
    }

    [Fact]
    public async Task A_completed_value_reaches_the_canonical_document_capped_and_annotated()
    {
        var rows = Rows(new Dictionary<string, string> { ["BCD"] = "2026-08-10" });
        var llm = new ScriptedLlm(Nothing with { BidClosingDate = "2026-08-10", BidClosingDateSpan = "BCD: 2026-08-10" });
        await Service(llm).CompleteAsync(Input(rows));

        var import = new CanonicalRfqNormalizer().NormalizeSpreadsheetRows(rows, businessUnitId: 7);
        var document = Assert.Single(import.Documents);

        Assert.Equal(new DateTime(2026, 8, 10), document.BidClosingDate.Value.Date);
        Assert.Equal(HeaderCompletionService.CompletionConfidence, document.BidClosingDate.Confidence);
        Assert.Contains(document.BidClosingDate.Transformations, t => t.StartsWith(HeaderCompletionService.ProvenancePrefix, StringComparison.Ordinal));
        Assert.Equal("row 1", document.BidClosingDate.Evidence[0].Location);
    }

    [Fact]
    public async Task A_span_the_text_does_not_contain_is_dropped()
    {
        var rows = Rows(new Dictionary<string, string> { ["Currency"] = "US Dollar" });
        var llm = new ScriptedLlm(Nothing with { BidClosingDate = "2026-08-10", BidClosingDateSpan = "Due: 10/8/2026" });

        var outcome = await Service(llm).CompleteAsync(Input(rows));

        Assert.Empty(outcome.CompletedFields);
        Assert.Equal(new[] { RfqSpreadsheetFields.BidClosingDate }, outcome.RejectedFields);
        Assert.All(rows, row => Assert.Null(row.BidClosingDate));
    }

    [Fact]
    public async Task A_date_the_span_does_not_support_is_dropped()
    {
        // The span is real, but the model reported a different day than the span states.
        var rows = Rows(new Dictionary<string, string> { ["BCD"] = "10/8/2026" });
        var llm = new ScriptedLlm(Nothing with { BidClosingDate = "2026-10-08", BidClosingDateSpan = "BCD: 10/8/2026" });

        var outcome = await Service(llm).CompleteAsync(Input(rows));

        Assert.Empty(outcome.CompletedFields);
        Assert.All(rows, row => Assert.Null(row.BidClosingDate));
    }

    [Fact]
    public async Task A_document_that_states_its_closing_date_is_never_sent_to_the_model()
    {
        var rows = Rows(new Dictionary<string, string> { ["Currency"] = "USD" }, closing: "2026-08-10");
        rows.ForEach(r => { r.RfqNo = "RFQ-1"; r.DeliveryLocation = "Dammam"; r.AgreementReference = "FA-1"; r.RequiredDeliveryDate = "2026-09-01"; });
        var llm = new ScriptedLlm(Nothing);
        var service = Service(llm);

        Assert.False(service.HasGap(Input(rows)));
        var outcome = await service.CompleteAsync(Input(rows));

        Assert.Equal("nothingMissing", outcome.Skipped);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task A_document_with_no_header_text_is_never_sent_to_the_model()
    {
        var llm = new ScriptedLlm(Nothing);
        var service = Service(llm);
        var input = Input(Rows());

        Assert.False(service.HasGap(input));
        Assert.Equal("noHeaderText", (await service.CompleteAsync(input)).Skipped);
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task The_prose_outside_the_table_is_read_when_there_are_no_labels()
    {
        var rows = Rows();
        var llm = new ScriptedLlm(Nothing with { BidClosingDate = "2026-08-10", BidClosingDateSpan = "quotations must reach us by 10 August 2026" });
        var input = Input(rows, narrative: "Dear supplier, quotations must reach us by 10 August 2026. Regards.");

        Assert.True(Service(llm).HasGap(input));
        var outcome = await Service(llm).CompleteAsync(input);

        Assert.Equal(new[] { RfqSpreadsheetFields.BidClosingDate }, outcome.CompletedFields);
        Assert.Equal("quotations must reach us by 10 August 2026", rows[0].BidClosingDate);
    }

    [Fact]
    public async Task An_unauthorised_external_provider_leaves_the_document_as_read()
    {
        var rows = Rows(new Dictionary<string, string> { ["BCD"] = "2026-08-10" });
        var llm = new ScriptedLlm(Nothing with { BidClosingDate = "2026-08-10", BidClosingDateSpan = "BCD: 2026-08-10" })
        {
            ProviderClass = AiProviderClass.External,
        };

        var outcome = await Service(llm, trust: null).CompleteAsync(Input(rows));

        Assert.StartsWith("providerNotAuthorised", outcome.Skipped);
        Assert.Equal(0, llm.Calls);
        Assert.All(rows, row => Assert.Null(row.BidClosingDate));
    }

    [Fact]
    public async Task A_policy_denial_or_provider_failure_is_not_a_failure_of_the_document()
    {
        var rows = Rows(new Dictionary<string, string> { ["BCD"] = "2026-08-10" });

        var denied = await Service(new ScriptedLlm(null, new AiPolicyDeniedException("policy_disabled"))).CompleteAsync(Input(rows));
        Assert.Equal("policyDenied:policy_disabled", denied.Skipped);

        var failed = await Service(new ScriptedLlm(null, new HttpRequestException("down"))).CompleteAsync(Input(rows));
        Assert.Equal("providerFailed", failed.Skipped);

        Assert.All(rows, row => Assert.Null(row.BidClosingDate));
    }

    [Fact]
    public void The_buyer_name_is_never_completed()
        => Assert.DoesNotContain(RfqSpreadsheetFields.BuyerName, HeaderCompletionService.CompletableFields);
}
