using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>A logger that discards everything — keeps the service-under-test decoupled
/// from any logging framework/package in the test project.</summary>
public sealed class NoopLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) { }
}

/// <summary>
/// Deterministic <see cref="ILLMService"/> stub. Returns a scripted result for each
/// successive call (one call == one chunk), recording every prompt so tests can assert
/// how many chunks the service produced. A null scripted entry models a chunk failure.
/// </summary>
public sealed class StubLlm : ILLMService
{
    private readonly Queue<LeadExtractionResult?> _responses;
    public List<string> Prompts { get; } = new();

    /// <summary>Items the extractor declared it packed into each successive call.</summary>
    public List<int?> RequestedItemCounts { get; } = new();
    public int CallCount { get; private set; }
    public AiProviderClass ProviderClass { get; }

    /// <summary>
    /// Output-token ceiling this stub advertises. Chunk sizing is derived from it, so
    /// tests that care about chunk boundaries set it explicitly rather than depending on
    /// whatever the interface default happens to be.
    /// </summary>
    public int MaxOutputTokens { get; init; } = 4096;

    public StubLlm(params LeadExtractionResult?[] responses)
        : this(AiProviderClass.Local, responses) { }

    public StubLlm(AiProviderClass providerClass, params LeadExtractionResult?[] responses)
    {
        ProviderClass = providerClass;
        _responses = new Queue<LeadExtractionResult?>(responses);
    }

    public Task<LeadExtractionResult?> ExtractLeadDataAsync(
        string fullText, AiCallContext context, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Prompts.Add(fullText);
        RequestedItemCounts.Add(context.ItemsInPayload);
        var result = _responses.Count > 0 ? _responses.Dequeue() : null;
        return Task.FromResult(result);
    }

    // WP-BOQ member of ILLMService — not exercised by the extraction tests; returns
    // null ("model produced nothing usable") so any accidental call degrades safely.
    public Task<BoqDraftResult?> DraftServiceBoqAsync(
        string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<BoqDraftResult?>(null);
}

/// <summary>
/// An LLM fake with a REAL output-token ceiling. It answers a request only when the number
/// of line items the caller packed into it stays at or below <see cref="TruncateAboveItems"/>;
/// anything larger comes back the way ollama.com/deepseek-v4-pro actually came back on a
/// 40-line RFQ — <c>done_reason="length"</c>, no parseable JSON, and
/// <see cref="AiErrorCodes.OutputTruncated"/>.
///
/// The advertised <see cref="MaxOutputTokens"/> and the point at which it truncates are
/// SEPARATE knobs on purpose: that is what lets a test model the case the estimate cannot
/// cover — an unusually verbose document that overflows a correctly-planned chunk — and
/// prove the extractor responds by asking for LESS rather than by asking again.
/// </summary>
public sealed class BudgetedStubLlm : ILLMService
{
    /// <summary>Item count for each successive call, in order. The proof of what was asked for.</summary>
    public List<int> RequestedItemCounts { get; } = new();
    public List<string> Prompts { get; } = new();
    public int CallCount => RequestedItemCounts.Count;
    public AiProviderClass ProviderClass => AiProviderClass.Local;
    public int MaxOutputTokens { get; }

    /// <summary>Largest item count this fake can answer. Above it, output truncates.</summary>
    public int TruncateAboveItems { get; }

    /// <summary>
    /// When set, ANY chunk whose text contains this marker truncates no matter how small it
    /// is — the "one pathological line item" case, where halving eventually isolates a
    /// single row that still cannot fit and the extractor has nothing left to shrink.
    /// </summary>
    public string? AlwaysTruncatesMarker { get; init; }

    public BudgetedStubLlm(int maxOutputTokens, int truncateAboveItems)
    {
        MaxOutputTokens = maxOutputTokens;
        TruncateAboveItems = truncateAboveItems;
    }

    public Task<LlmExtractionOutcome> ExtractLeadDataDetailedAsync(
        string fullText, AiCallContext context, CancellationToken cancellationToken = default)
    {
        var items = context.ItemsInPayload ?? 1;
        RequestedItemCounts.Add(items);
        Prompts.Add(fullText);
        var truncates = items > TruncateAboveItems
            || (AlwaysTruncatesMarker is not null
                && fullText.Contains(AlwaysTruncatesMarker, StringComparison.Ordinal));
        return Task.FromResult(truncates
            ? new LlmExtractionOutcome(null, AiErrorCodes.OutputTruncated)
            : new LlmExtractionOutcome(Ext.Result(Ext.Items(items, 0.9), 0.9), null));
    }

    public async Task<LeadExtractionResult?> ExtractLeadDataAsync(
        string fullText, AiCallContext context, CancellationToken cancellationToken = default)
        => (await ExtractLeadDataDetailedAsync(fullText, context, cancellationToken)).Result;

    public Task<BoqDraftResult?> DraftServiceBoqAsync(
        string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
        => Task.FromResult<BoqDraftResult?>(null);
}

/// <summary>Builders for the verbose positional records used by the extraction pipeline.</summary>
public static class Ext
{
    /// <summary>One line item carrying only the fields that matter to conservation /
    /// confidence: a name, a quantity and a per-item confidence.</summary>
    public static LeadItemData Item(double itemConfidence, string? name = "Item", int quantity = 1)
        => new(
            CompanyRef: null, CompanyRefConfidence: 0,
            CustomerAccountPortalId: null, CustomerAccountPortalIdConfidence: 0,
            CustomerRfqno: null, CustomerRfqnoConfidence: 0,
            ItemMaterialCode: null, ItemMaterialCodeConfidence: 0,
            CommodityProduct: null, CommodityProductConfidence: 0,
            BuyerName: null, BuyerNameConfidence: 0,
            LineItemNo: null, LineItemNoConfidence: 0,
            ProductShortName: name, ProductShortNameConfidence: itemConfidence,
            Alternative: null, AlternativeConfidence: 0,
            ProductShortDescription: null, ProductShortDescriptionConfidence: 0,
            Currency: null, CurrencyConfidence: 0,
            UnitOfMeasure: null, UnitOfMeasureConfidence: 0,
            UnitPrice: null, UnitPriceConfidence: 0,
            Quantity: quantity, QuantityConfidence: itemConfidence,
            StorageLocation: null, StorageLocationConfidence: 0,
            ManufacturerName: null, ManufacturerNameConfidence: 0,
            ManufacturerPartNumber: null, ManufacturerPartNumberConfidence: 0,
            AlternateProductName: null, AlternateProductNameConfidence: 0,
            AlternatePartNumber: null, AlternatePartNumberConfidence: 0,
            ItemText: null, ItemTextConfidence: 0,
            MaterialPotext: null, MaterialPotextConfidence: 0,
            LeadTime: null, LeadTimeConfidence: 0,
            ReceivedDate: null, ReceivedDateConfidence: 0,
            BidClosingDateLine: null, BidClosingDateLineConfidence: 0,
            ItemConfidence: itemConfidence);

    /// <summary><paramref name="count"/> items, each at <paramref name="itemConfidence"/>.</summary>
    public static List<LeadItemData> Items(int count, double itemConfidence)
        => Enumerable.Range(0, count).Select(i => Item(itemConfidence, $"Item {i}")).ToList();

    /// <summary>A chunk/document result. <paramref name="headerConfidence"/> is applied to the
    /// four header fields the service averages (Rfqno / BuyersName / RecDate / BidClosingDate).</summary>
    public static LeadExtractionResult Result(List<LeadItemData> items, double headerConfidence)
        => new(
            Rfqno: "RFQ-1", RfqnoConfidence: headerConfidence,
            BuyersName: "Buyer", BuyersNameConfidence: headerConfidence,
            RecDate: "2026-07-14", RecDateConfidence: headerConfidence,
            BidClosingDate: "2026-07-30", BidClosingDateConfidence: headerConfidence,
            BiddingDecision: null, BiddingDecisionConfidence: 0,
            AcknowledgmentDate: null, AcknowledgmentDateConfidence: 0,
            SubDate: null, SubDateConfidence: 0,
            HeaderRemarks: null, HeaderRemarksConfidence: 0,
            OpportunityNo: null, OpportunityNoConfidence: 0,
            Rfqtype: null, RfqtypeConfidence: 0,
            DurationAgreement: null, DurationAgreementConfidence: 0,
            OverallConfidence: headerConfidence,
            Items: items);
}
