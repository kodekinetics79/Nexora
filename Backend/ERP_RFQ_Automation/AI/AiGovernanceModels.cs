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

/// <summary>
/// Canonical provider-attempt error codes written to the AI ledger. These used to be
/// scattered string literals; they are constants because operators triage on them and
/// because <see cref="OutputTruncated"/> in particular has to be distinguishable from
/// <see cref="InvalidOutput"/> — the two look identical at the parser (unparseable JSON)
/// but mean completely different things and have completely different remedies.
/// </summary>
public static class AiErrorCodes
{
    /// <summary>Provider returned a non-success HTTP status.</summary>
    public const string ProviderHttpError = "provider_http_error";

    /// <summary>Provider returned HTTP 200 with no assistant content at all.</summary>
    public const string EmptyResponse = "empty_response";

    /// <summary>
    /// The model produced a COMPLETE response that is not valid/trustworthy JSON.
    /// A genuine model or prompt-quality failure — retrying the same request is pointless.
    /// </summary>
    public const string InvalidOutput = "invalid_output";

    /// <summary>
    /// The model hit its output-token ceiling (<c>done_reason == "length"</c>) and the JSON
    /// was cut mid-object. NOT a model-quality failure: the request asked for more output
    /// than the budget allows. The only useful retry is a SMALLER request (fewer line
    /// items), which is why this code is retryable and is surfaced to the chunking caller.
    /// </summary>
    public const string OutputTruncated = "output_truncated";

    /// <summary>All provider attempts were spent without a usable result.</summary>
    public const string AttemptsExhausted = "attempts_exhausted";

    /// <summary>The provider signalled truncation for a payload that is already one line item.</summary>
    public const string SingleItemExceedsOutputBudget = "single_item_exceeds_output_budget";
}

/// <summary>
/// The recognised values of <see cref="AiProcessingPolicy.EgressPolicy"/>.
///
/// <para>The column shipped as free text with a default of <c>RedactedFieldsOnly</c>, was
/// validated on write to be non-blank, and was read by nothing at all — so a tenant whose
/// policy said "redacted fields only" had whole unstructured documents egress anyway. A knob
/// that does nothing is worse than no knob, which is the argument
/// <see cref="AiGovernanceService"/> already makes about the dependency ceiling.</para>
///
/// <para>It now decides one thing, precisely: whether whole unstructured document text may
/// leave for an external provider at all. It is a SECOND lock, independent of
/// <see cref="AiExternalProviderAuthorization.UnstructuredDocumentsAllowed"/> — the policy
/// says what this tenant's data may ever become, the authorization says what one named
/// destination may receive. Both must agree. Anything unrecognised reads as the strict
/// value: fail closed.</para>
/// </summary>
public static class AiEgressPolicies
{
    /// <summary>Only reduced field/row payloads may egress. The secure default.</summary>
    public const string RedactedFieldsOnly = "RedactedFieldsOnly";

    /// <summary>Whole unstructured document text may egress to an authorized destination.</summary>
    public const string FullDocument = "FullDocument";

    public static bool IsRecognised(string? value) =>
        PermitsWholeDocument(value)
        || string.Equals(value?.Trim(), RedactedFieldsOnly, StringComparison.OrdinalIgnoreCase);

    /// <summary>True only for the one value that opts in. Unrecognised values do not.</summary>
    public static bool PermitsWholeDocument(string? value) =>
        string.Equals(value?.Trim(), FullDocument, StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Per-document token budget for ONE extraction pass. Enforcement (see
    /// <c>AiGovernanceService.ReserveAsync</c>) allows
    /// <c>AiGovernanceService.DocumentBudgetRetryCycles</c> full passes over the
    /// document's lifetime, because the usage ledger accumulates across queue retry
    /// attempts and a retry is a genuinely new set of governed calls.
    /// </summary>
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
