namespace ERP_RFQ_Automation.AI;

public enum AiProviderClass
{
    Unknown,
    External,
    Local
}

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

public static class AiCostStatuses
{
    public const string LocalUnpriced = "LocalUnpriced";
    public const string RateUnavailable = "RateUnavailable";
    public const string EstimatedConfiguredRate = "EstimatedConfiguredRate";
    public const string Priced = "Priced";
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
    public long? MaxTokensPerDocument { get; set; }
    public decimal? ExternalInputCostPerMillionTokens { get; set; }
    public decimal? ExternalOutputCostPerMillionTokens { get; set; }
    public string? ExternalCostCurrency { get; set; }
    public string? ExternalPricingVersion { get; set; }
    /// <summary>
    /// Ceiling on UNAUTHORIZED external usage, as a percentage of the last 100 governed
    /// calls. A reservation whose endpoint holds a live per-tenant allow-list
    /// authorization (<see cref="AiExternalProviderAuthorization"/>) is exempt from this
    /// ratio — the allow-list is the precise control for authorized egress, and the
    /// exempting authorization id is recorded on the ledger row. Everything else
    /// (unauthorized external calls) remains subject to it unchanged, so tighter is
    /// better: the value is validated to 0..10 in AiTrustCenterService and that bound is
    /// deliberately not raised.
    /// </summary>
    public decimal ExternalDependencyCeilingPercent { get; set; } = 10m;
    public bool RedactionRequired { get; set; } = true;
    public string AllowedDataClassifications { get; set; } = "Public,Internal";
    public string EgressPolicy { get; set; } = "RedactedFieldsOnly";
    public string DataResidency { get; set; } = "TenantApprovedRegion";
    public int RetentionDays { get; set; } = 30;
    public bool InputOutputAuditAllowed { get; set; }
    public bool PrivacyReviewRequired { get; set; } = true;
    public decimal? LocalComputeCostPerHour { get; set; }
    public decimal? OcrCostPerPage { get; set; }
    public string? LocalCostCurrency { get; set; }
    public long Version { get; set; } = 1;
    public DateTime UpdatedOn { get; set; }
    public string UpdatedBy { get; set; } = null!;

    public static AiProcessingPolicy CreateSecureDefault(long businessUnitId, string actor, DateTime now) => new()
    {
        BusinessUnitId = businessUnitId,
        IsEnabled = true,
        ExternalProcessingAllowed = false,
        AllowedPurposes = "RfqExtraction,BoqDraft",
        ExternalDependencyCeilingPercent = 10m,
        RedactionRequired = true,
        AllowedDataClassifications = "Public,Internal",
        EgressPolicy = "RedactedFieldsOnly",
        DataResidency = "TenantApprovedRegion",
        RetentionDays = 30,
        InputOutputAuditAllowed = false,
        PrivacyReviewRequired = true,
        Version = 1,
        UpdatedOn = now,
        UpdatedBy = actor
    };
}

public sealed class AiRequest
{
    public Guid Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long? ExtractionJobId { get; set; }
    public long? SourceDocumentOccurrenceId { get; set; }
    public string Operation { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string PromptHash { get; set; } = null!;
    public string PromptVersion { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public AiProviderClass ProviderClass { get; set; }
    public string Model { get; set; } = null!;
    public string Status { get; set; } = AiCallStatuses.Reserved;
    public int InputCharacters { get; set; }
    public int OutputCharacters { get; set; }
    public string? InputHash { get; set; }
    public string? OutputHash { get; set; }
    public bool InjectionDetected { get; set; }
    public long EstimatedInputTokens { get; set; }
    public long ReservedTokens { get; set; }
    public bool BudgetWarning { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal? EstimatedCost { get; set; }
    public string? CostCurrency { get; set; }
    public string CostStatus { get; set; } = AiCostStatuses.RateUnavailable;
    public string? CostPricingVersion { get; set; }
    public string TokenSource { get; set; } = AiTokenSources.Estimated;
    public string? ErrorCode { get; set; }

    /// <summary>
    /// Set only when this reservation was EXEMPTED from the external-dependency ceiling:
    /// the id of the live <see cref="AiExternalProviderAuthorization"/> that covered the
    /// endpoint at reservation time. Lets the ledger answer "which calls went external
    /// under whose authorization". Null for local calls, for external calls under the
    /// ceiling (no exemption was needed), and for denied calls.
    /// </summary>
    public long? ExternalAuthorizationId { get; set; }

    /// <summary>
    /// The deployment's <see cref="AI.InferencePosture"/> at the moment of a ceiling
    /// exemption, recorded alongside <see cref="ExternalAuthorizationId"/> so the audit
    /// trail keeps the stance the deployment declared when the call egressed. Null
    /// whenever no exemption applied.
    /// </summary>
    public string? InferencePosture { get; set; }

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
