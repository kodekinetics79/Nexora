using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.Extensions.Logging;

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
    public int CallCount { get; private set; }

    public StubLlm(params LeadExtractionResult?[] responses)
        => _responses = new Queue<LeadExtractionResult?>(responses);

    public Task<LeadExtractionResult?> ExtractLeadDataAsync(string fullText)
    {
        CallCount++;
        Prompts.Add(fullText);
        var result = _responses.Count > 0 ? _responses.Dequeue() : null;
        return Task.FromResult(result);
    }
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
