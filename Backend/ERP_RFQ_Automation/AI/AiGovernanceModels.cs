namespace ERP_RFQ_Automation.AI;

public static class AiCallStatuses
{
    public const string Reserved = "Reserved";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Denied = "Denied";
    public const string Failed = "Failed";
    public const string Unknown = "Unknown";
}

public static class AiTokenSources
{
    public const string ProviderExact = "ProviderExact";
    public const string ProviderApproximate = "ProviderApproximate";
    public const string Estimated = "Estimated";
}

public sealed class AiProcessingPolicy
{
    public long BusinessUnitId { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool ExternalProcessingAllowed { get; set; }
    public string AllowedPurposes { get; set; } = "RfqExtraction,BoqDraft";
    public string? AllowedProvider { get; set; }
    public string? AllowedModel { get; set; }
    public long? MonthlySoftTokenLimit { get; set; }
    public long? MonthlyHardTokenLimit { get; set; }
    public long Version { get; set; } = 1;
    public DateTime UpdatedOn { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

public sealed class AiRequest
{
    public Guid Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string Operation { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string PromptHash { get; set; } = null!;
    public string PromptVersion { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string Model { get; set; } = null!;
    public string Status { get; set; } = AiCallStatuses.Reserved;
    public int InputCharacters { get; set; }
    public int OutputCharacters { get; set; }
    public string? InputHash { get; set; }
    public string? OutputHash { get; set; }
    public bool InjectionDetected { get; set; }
    public long EstimatedInputTokens { get; set; }
    public long ReservedTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string TokenSource { get; set; } = AiTokenSources.Estimated;
    public string? ErrorCode { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? StartedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
    public ICollection<AiCallAttempt> Attempts { get; set; } = new List<AiCallAttempt>();
}

public sealed class AiCallAttempt
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public long BusinessUnitId { get; set; }
    public int AttemptNumber { get; set; }
    public string Provider { get; set; } = null!;
    public string Model { get; set; } = null!;
    public string Status { get; set; } = null!;
    public int? HttpStatus { get; set; }
    public string? ProviderRequestId { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public string TokenSource { get; set; } = AiTokenSources.Estimated;
    public long LatencyMilliseconds { get; set; }
    public long? ProviderDurationNanoseconds { get; set; }
    public string? ResponseHash { get; set; }
    public string? ErrorCode { get; set; }
    public DateTime StartedOn { get; set; }
    public DateTime CompletedOn { get; set; }
    public AiRequest Request { get; set; } = null!;
}

public sealed class AiBudgetPeriod
{
    public long BusinessUnitId { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public long? SoftTokenLimit { get; set; }
    public long? HardTokenLimit { get; set; }
    public long ReservedTokens { get; set; }
    public long SettledTokens { get; set; }
    public long Version { get; set; } = 1;
    public DateTime UpdatedOn { get; set; }
}
