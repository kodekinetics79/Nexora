namespace ERP_RFQ_Automation.Billing.Metering;

public enum UsageRatingStatus
{
    Pending,
    Ready,
    BlockedUncertifiedMeter,
    Rated
}

public enum UsageEventKind
{
    Consumption,
    Adjustment
}

/// <summary>An immutable, tenant-qualified occurrence of billable or attributable usage.</summary>
public sealed class UsageEvent
{
    public Guid UsageEventId { get; set; }
    public long TenantId { get; set; }
    public UsageEventKind Kind { get; set; }
    public string EventType { get; set; } = null!;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = null!;
    public DateTime OccurredAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
    public string SourceRecordType { get; set; } = null!;
    public string SourceRecordId { get; set; } = null!;
    public string SourceSystem { get; set; } = null!;
    public string? ActorId { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string CorrelationId { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public decimal CostAmount { get; set; }
    public string Currency { get; set; } = null!;
    public string EvidenceSha256 { get; set; } = null!;
    public UsageRatingStatus RatingStatus { get; set; }
    public Guid? AdjustsUsageEventId { get; set; }
    public long? RateCardId { get; set; }
    public long? RateCardLineId { get; set; }
    public long? RateCardVersion { get; set; }
    public decimal AllowanceApplied { get; set; }
    public decimal OverageQuantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? RatedAmount { get; set; }
}

/// <summary>Rebuildable minute-level projection. Source events remain authoritative.</summary>
public sealed class UsageMinuteAggregate
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string EventType { get; set; } = null!;
    public string Unit { get; set; } = null!;
    public DateTime MinuteUtc { get; set; }
    public decimal Quantity { get; set; }
    public decimal CostAmount { get; set; }
    public int EventCount { get; set; }
    public DateTime RefreshedAtUtc { get; set; }
}

public sealed record RecordUsageEvent(
    Guid UsageEventId,
    long TenantId,
    string EventType,
    decimal Quantity,
    string Unit,
    DateTime OccurredAtUtc,
    string SourceRecordType,
    string SourceRecordId,
    string SourceSystem,
    string? ActorId,
    string? Provider,
    string? Model,
    string CorrelationId,
    string IdempotencyKey,
    decimal CostAmount,
    string Currency,
    string EvidenceSha256,
    Guid? AdjustsUsageEventId = null,
    long? RateCardId = null,
    long? RateCardLineId = null,
    long? RateCardVersion = null,
    decimal AllowanceApplied = 0,
    decimal? UnitPrice = null);

public static class UsageMeterCatalog
{
    public static readonly IReadOnlyDictionary<string, string> Units =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["processing.minutes"] = "minute", ["documents"] = "document",
            ["pages.processed"] = "page", ["rfqs"] = "rfq", ["quotes"] = "quote",
            ["orders"] = "order", ["emails"] = "email", ["pages.ocr"] = "page",
            ["ai.tokens"] = "token", ["api.calls"] = "call", ["storage.gb-hours"] = "gb-hour",
            ["supplier.searches"] = "search", ["automation.runs"] = "run",
            ["base.subscription"] = "subscription", ["users"] = "user",
            ["dedicated.infrastructure"] = "instance"
        };

    // These meters cannot become invoiceable until their instrumentation is certified.
    public static bool IsBillingCertified(string eventType) => eventType is not
        ("pages.processed" or "pages.ocr" or "storage.gb-hours");
}
