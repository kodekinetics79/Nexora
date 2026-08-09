namespace ERP_RFQ_Automation.Billing.Metering;

using System.Security.Cryptography;
using System.Text;

public enum UsageRatingStatus
{
    Pending,
    Ready,
    BlockedUncertifiedMeter,
    Rated,
    RatedZeroWithReason,
    ExcludedWithReason,
    Unrated,
    RatingFailed
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

public enum MeterCertificationStatus
{
    BillingCertified,
    ObservabilityOnly,
    NotImplemented,
    Blocked
}

public sealed record UsageMeterDefinition(
    string EventType, string BillingMeterKey, string Unit, MeterCertificationStatus Certification);

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

/// <summary>
/// Stable identity for a server-derived occurrence. A producer must be able to repeat the
/// same durable completion after an ambiguous commit without manufacturing a second UUID.
/// </summary>
public static class UsageEventIdentity
{
    public static Guid FromIdempotencyKey(long tenantId, string idempotencyKey)
    {
        if (tenantId <= 0) throw new ArgumentOutOfRangeException(nameof(tenantId));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("A usage idempotency key is required.", nameof(idempotencyKey));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"nexora:usage:v1:{tenantId}:{idempotencyKey.Trim()}"));
        return new Guid(digest.AsSpan(0, 16));
    }
}

public static class UsageMeterCatalog
{
    public static readonly IReadOnlyDictionary<string, UsageMeterDefinition> Definitions =
        new Dictionary<string, UsageMeterDefinition>(StringComparer.Ordinal)
        {
            ["processing.minutes"] = D("processing.minutes", "minute", MeterCertificationStatus.NotImplemented),
            ["documents"] = D("documents", "document", MeterCertificationStatus.BillingCertified),
            ["pages.processed"] = D("pages.processed", "page", MeterCertificationStatus.Blocked),
            ["rfqs"] = D("rfqs", "rfq", MeterCertificationStatus.NotImplemented),
            ["quotes"] = D("quotes", "quote", MeterCertificationStatus.NotImplemented),
            ["orders"] = D("orders", "order", MeterCertificationStatus.NotImplemented),
            ["emails"] = D("emails", "email", MeterCertificationStatus.NotImplemented),
            ["pages.ocr"] = D("pages.ocr", "page", MeterCertificationStatus.Blocked),
            ["ai.tokens"] = D(ERP_RFQ_Automation.Billing.BillingMeterKeys.AiTokensExternal, "token", MeterCertificationStatus.BillingCertified),
            ["api.calls"] = D("api.calls", "call", MeterCertificationStatus.NotImplemented),
            ["storage.gb-hours"] = D(ERP_RFQ_Automation.Billing.BillingMeterKeys.StorageGb, "gb-hour", MeterCertificationStatus.Blocked),
            ["supplier.searches"] = D("supplier.searches", "search", MeterCertificationStatus.NotImplemented),
            ["automation.runs"] = D("automation.runs", "run", MeterCertificationStatus.NotImplemented),
            ["base.subscription"] = D(ERP_RFQ_Automation.Billing.BillingMeterKeys.BaseSubscription, "subscription", MeterCertificationStatus.BillingCertified),
            ["users"] = D(ERP_RFQ_Automation.Billing.BillingMeterKeys.Seats, "user", MeterCertificationStatus.BillingCertified),
            ["dedicated.infrastructure"] = D("dedicated.infrastructure", "instance", MeterCertificationStatus.NotImplemented)
        };

    public static IReadOnlyDictionary<string, string> Units { get; } = Definitions
        .ToDictionary(x => x.Key, x => x.Value.Unit, StringComparer.Ordinal);

    public static bool IsBillingCertified(string eventType)
        => Definitions.TryGetValue(eventType, out var definition)
           && definition.Certification == MeterCertificationStatus.BillingCertified;

    public static UsageMeterDefinition? ForEvent(string eventType)
        => Definitions.TryGetValue(eventType, out var definition)
            ? definition with { EventType = eventType }
            : null;

    public static MeterCertificationStatus BillingCertification(string meterKey)
        => Definitions.Values.Where(x => x.BillingMeterKey == meterKey)
            .Select(x => x.Certification).DefaultIfEmpty(MeterCertificationStatus.NotImplemented).First();

    private static UsageMeterDefinition D(string meterKey, string unit, MeterCertificationStatus status)
        => new("", meterKey, unit, status);
}
