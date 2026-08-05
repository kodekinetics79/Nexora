using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.CommercialIntelligence.Exceptions;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommercialExceptionType
{
    UnassignedLead,
    OverdueFollowUp
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommercialExceptionSeverity
{
    Low,
    Medium,
    High,
    Critical
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CommercialExceptionStatus
{
    Open,
    Acknowledged,
    Resolved,
    Dismissed
}

public static class CommercialAggregateType
{
    public const string Lead = "Lead";
    public const string Rfq = "Rfq";
    public const string Quote = "Quote";
    public const string Order = "Order";

    public static string Normalize(string value) => value.Trim().ToUpperInvariant() switch
    {
        "LEAD" => Lead,
        "RFQ" => Rfq,
        "QUOTE" => Quote,
        "ORDER" => Order,
        _ => throw new ArgumentException("Unsupported commercial aggregate type.", nameof(value))
    };
}

public sealed class CommercialExceptionCase
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialCaseId { get; set; }
    public string NexoraSerial { get; set; } = string.Empty;
    public CommercialExceptionType ExceptionType { get; set; }
    public string ExceptionKey { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public long SourceId { get; set; }
    public long SourceVersion { get; set; }
    public long? FollowUpTaskId { get; set; }
    public long? UnassignedWorkItemId { get; set; }
    public long? OwnerUserId { get; set; }
    public CommercialExceptionSeverity Severity { get; set; }
    public CommercialExceptionStatus Status { get; set; } = CommercialExceptionStatus.Open;
    public string ReasonCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string RecommendedActionCode { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "{}";
    public string RuleVersion { get; set; } = string.Empty;
    public DateTime FirstDetectedAtUtc { get; set; }
    public DateTime LastDetectedAtUtc { get; set; }
    public DateTime SlaDueAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public long Version { get; set; } = 1;

    public Models.CommercialCase CommercialCase { get; set; } = null!;
}

public sealed class CommercialExceptionEvent
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialExceptionCaseId { get; set; }
    public CommercialExceptionStatus? FromStatus { get; set; }
    public CommercialExceptionStatus ToStatus { get; set; }
    public long FromVersion { get; set; }
    public long ToVersion { get; set; }
    public string ActionCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;

    public CommercialExceptionCase CommercialExceptionCase { get; set; } = null!;
    public CommercialExceptionOutboxMessage OutboxMessage { get; set; } = null!;
}

public sealed class CommercialExceptionOutboxMessage
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CommercialExceptionEventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int AttemptCount { get; set; }

    public CommercialExceptionEvent CommercialExceptionEvent { get; set; } = null!;
}

public sealed class CommercialExceptionOperation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public long? CommercialExceptionCaseId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string ResultJson { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }

    public CommercialExceptionCase? CommercialExceptionCase { get; set; }
}
