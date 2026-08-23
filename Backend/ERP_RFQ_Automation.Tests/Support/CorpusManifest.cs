using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Interfaces;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// Typed view of <c>Corpus/corpus-manifest.json</c> — the ground truth the corpus acceptance
/// tests assert against, and the script the stubbed <see cref="ILLMService"/> answers from.
/// </summary>
public sealed class CorpusManifest
{
    private static readonly Lazy<CorpusManifest> Instance = new(() =>
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Corpus", "corpus-manifest.json");
        var manifest = JsonSerializer.Deserialize<CorpusManifest>(File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            });
        return manifest ?? throw new InvalidOperationException("corpus-manifest.json is empty.");
    });

    public static CorpusManifest Load() => Instance.Value;

    public static byte[] Bytes(string fileName)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Corpus", fileName));

    public static string PathOf(string fileName)
        => Path.Combine(AppContext.BaseDirectory, "Corpus", fileName);

    [JsonPropertyName("emails")] public List<CorpusEmail> Emails { get; set; } = new();
    [JsonPropertyName("documents")] public List<CorpusDocument> Documents { get; set; } = new();
    [JsonPropertyName("llm")] public List<CorpusLlmScript> Llm { get; set; } = new();

    public CorpusEmail Email(string file) => Emails.Single(e => e.File == file);
    public CorpusDocument Document(string file) => Documents.Single(d => d.File == file);

    /// <summary>The scripted extraction answer for a document text: the entry ALL of whose
    /// markers appear in the text, most specific (most markers) first. Null = no script, which
    /// a journey test treats as "this text was never meant to reach the LLM".</summary>
    public CorpusLlmScript? ScriptFor(string fullText)
        => Llm.OrderByDescending(s => s.Markers.Count)
              .FirstOrDefault(s => s.Markers.All(m =>
                  fullText.Contains(m, StringComparison.OrdinalIgnoreCase)));
}

public sealed class CorpusEmail
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("messageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("expectedTriageOutcome")] public string ExpectedTriageOutcome { get; set; } = "";
    [JsonPropertyName("expectedTriageReasons")] public List<string> ExpectedTriageReasons { get; set; } = new();
    [JsonPropertyName("expectedParseStatus")] public string ExpectedParseStatus { get; set; } = "";
    [JsonPropertyName("expectedJobCount")] public int ExpectedJobCount { get; set; }
    [JsonPropertyName("expectedSkippedAttachments")] public List<string> ExpectedSkippedAttachments { get; set; } = new();
    [JsonPropertyName("expectedDisposition")] public string ExpectedDisposition { get; set; } = "";
    [JsonPropertyName("expectedThreadContinuation")] public bool? ExpectedThreadContinuation { get; set; }
}

public sealed class CorpusDocument
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("expectedInspection")] public string ExpectedInspection { get; set; } = "";
    [JsonPropertyName("expectedReaderOutcome")] public string ExpectedReaderOutcome { get; set; } = "";
    [JsonPropertyName("expectedStructuredRows")] public int? ExpectedStructuredRows { get; set; }
    [JsonPropertyName("expectedTextMarkers")] public List<string> ExpectedTextMarkers { get; set; } = new();
    [JsonPropertyName("expectedFailureContains")] public string? ExpectedFailureContains { get; set; }
}

public sealed class CorpusLlmScript
{
    [JsonPropertyName("markers")] public List<string> Markers { get; set; } = new();
    [JsonPropertyName("rfqNumber")] public string RfqNumber { get; set; } = "";
    [JsonPropertyName("buyerName")] public string BuyerName { get; set; } = "";
    [JsonPropertyName("buyerEmail")] public string? BuyerEmail { get; set; }
    [JsonPropertyName("lines")] public List<CorpusLlmLine> Lines { get; set; } = new();

    public LeadExtractionResult ToResult()
        => new(
            Rfqno: RfqNumber, RfqnoConfidence: 0.95,
            BuyersName: BuyerName, BuyersNameConfidence: 0.95,
            RecDate: "2026-08-10", RecDateConfidence: 0.9,
            BidClosingDate: "2026-09-15", BidClosingDateConfidence: 0.9,
            BiddingDecision: null, BiddingDecisionConfidence: 0,
            AcknowledgmentDate: null, AcknowledgmentDateConfidence: 0,
            SubDate: null, SubDateConfidence: 0,
            HeaderRemarks: null, HeaderRemarksConfidence: 0,
            OpportunityNo: null, OpportunityNoConfidence: 0,
            Rfqtype: null, RfqtypeConfidence: 0,
            DurationAgreement: null, DurationAgreementConfidence: 0,
            OverallConfidence: 0.95,
            // The conversational (email-body) path treats a result naming no buying
            // organisation as review-worthy; the manifest names one so the review reason a
            // journey observes is the one the scenario is actually about.
            CustomerCompanyName: BuyerName, CustomerCompanyNameConfidence: 0.9,
            CustomerBuyerEmail: BuyerEmail, CustomerBuyerEmailConfidence: BuyerEmail is null ? null : 0.9,
            Items: Lines.Select((line, index) => new LeadItemData(
                CompanyRef: null, CompanyRefConfidence: null,
                CustomerAccountPortalId: null, CustomerAccountPortalIdConfidence: null,
                CustomerRfqno: RfqNumber, CustomerRfqnoConfidence: 0.95,
                ItemMaterialCode: null, ItemMaterialCodeConfidence: null,
                CommodityProduct: null, CommodityProductConfidence: null,
                BuyerName: BuyerName, BuyerNameConfidence: 0.9,
                LineItemNo: (index + 1).ToString(), LineItemNoConfidence: 0.9,
                ProductShortName: line.Description, ProductShortNameConfidence: 0.95,
                Alternative: null, AlternativeConfidence: null,
                ProductShortDescription: line.Description, ProductShortDescriptionConfidence: 0.95,
                Currency: null, CurrencyConfidence: null,
                UnitOfMeasure: line.Uom, UnitOfMeasureConfidence: line.Uom is null ? null : 0.9,
                UnitPrice: null, UnitPriceConfidence: null,
                Quantity: line.Quantity, QuantityConfidence: 0.95,
                StorageLocation: null, StorageLocationConfidence: null,
                ManufacturerName: null, ManufacturerNameConfidence: null,
                ManufacturerPartNumber: line.PartNumber,
                ManufacturerPartNumberConfidence: line.PartNumber is null ? null : 0.95,
                AlternateProductName: null, AlternateProductNameConfidence: null,
                AlternatePartNumber: null, AlternatePartNumberConfidence: null,
                ItemText: null, ItemTextConfidence: null,
                MaterialPotext: null, MaterialPotextConfidence: null,
                LeadTime: null, LeadTimeConfidence: null,
                ReceivedDate: null, ReceivedDateConfidence: null,
                BidClosingDateLine: null, BidClosingDateLineConfidence: null,
                ItemConfidence: 0.95,
                // Conversational-path anchors: ProseAnchorVerifier drops any item whose
                // SourceSpan is not a verbatim quote of the submitted message, so the
                // manifest carries the exact quote for every body-borne line.
                SourceSpan: line.SourceSpan,
                QuantityToken: line.QuantityToken)).ToList());
}

public sealed class CorpusLlmLine
{
    [JsonPropertyName("partNumber")] public string? PartNumber { get; set; }
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("uom")] public string? Uom { get; set; }
    [JsonPropertyName("sourceSpan")] public string? SourceSpan { get; set; }
    [JsonPropertyName("quantityToken")] public string? QuantityToken { get; set; }
}

/// <summary>
/// Deterministic <see cref="ILLMService"/> that answers ONLY from the corpus manifest: the
/// document text is matched against the manifest's marker sets, so worker scheduling order
/// cannot change what any document extracts to. A text no manifest entry covers throws —
/// silence here would let a test pass while extracting garbage.
/// </summary>
public sealed class CorpusManifestLlm : ILLMService
{
    private readonly CorpusManifest _manifest = CorpusManifest.Load();
    private int _calls;

    public int CallCount => _calls;
    public AiProviderClass ProviderClass => AiProviderClass.Local;

    public Task<LeadExtractionResult?> ExtractLeadDataAsync(
        string fullText, AiCallContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var script = _manifest.ScriptFor(fullText)
            ?? throw new InvalidOperationException(
                "The corpus manifest has no LLM script for this document text: "
                + fullText[..Math.Min(fullText.Length, 200)]);
        return Task.FromResult<LeadExtractionResult?>(script.ToResult());
    }

    public Task<BoqDraftResult?> DraftServiceBoqAsync(
        string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<BoqDraftResult?>(null);
}
