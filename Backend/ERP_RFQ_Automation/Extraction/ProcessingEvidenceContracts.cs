using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Extraction;

public sealed record ProcessingCostSummary(decimal? Amount, string? Currency, string Status);

public static class ProcessingCostAttribution
{
    public static ProcessingCostSummary Summarize(IReadOnlyCollection<AiRequest> externalRequests)
    {
        if (externalRequests.Count == 0)
            return new ProcessingCostSummary(null, null, "LocalComputeUnpriced");

        var priced = externalRequests.All(x => x.EstimatedCost.HasValue
            && !string.IsNullOrWhiteSpace(x.CostCurrency)
            && (string.Equals(x.CostStatus, AiCostStatuses.Priced, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.CostStatus, AiCostStatuses.EstimatedConfiguredRate, StringComparison.OrdinalIgnoreCase)));
        var currencies = externalRequests.Select(x => x.CostCurrency?.Trim().ToUpperInvariant())
            .Where(x => x is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (!priced || currencies.Length != 1)
            return new ProcessingCostSummary(null, null, "RateUnavailable");

        var status = externalRequests.All(x => string.Equals(
                x.CostStatus, AiCostStatuses.Priced, StringComparison.OrdinalIgnoreCase))
            ? AiCostStatuses.Priced
            : AiCostStatuses.EstimatedConfiguredRate;
        return new ProcessingCostSummary(externalRequests.Sum(x => x.EstimatedCost!.Value), currencies[0], status);
    }
}

public sealed record ProcessingEvidenceResponse(
    long LeadId,
    string? NexoraSerial,
    IReadOnlyCollection<ProcessingRfqEvidence> Rfqs,
    IReadOnlyCollection<ProcessingOccurrenceEvidence> Occurrences,
    IReadOnlyCollection<ProcessingJobEvidence> Jobs,
    IReadOnlyCollection<ProcessingRunEvidence> Runs,
    IReadOnlyCollection<ProcessingAiRequestEvidence> AiRequests,
    int LocalRequestCount,
    int ExternalRequestCount,
    decimal LocalRequestRate,
    decimal ExternalRequestRate,
    decimal? ExternalCostAmount,
    string? ExternalCostCurrency,
    string ExternalCostStatus);

public sealed record ProcessingRfqEvidence(long RfqId, string RfqNumber, DateTime CreatedOn);

public sealed record ProcessingOccurrenceEvidence(
    long OccurrenceId,
    long? SourceDocumentId,
    long? ExtractionJobId,
    string IntakeStatus,
    DateTimeOffset ReceivedOn,
    string? OriginalFileName,
    string? ContentHash,
    string? Classification,
    string? CorrelationId);

public sealed record ProcessingJobEvidence(
    long ExtractionJobId,
    long? SourceDocumentOccurrenceId,
    string SourceType,
    string Status,
    int Attempts,
    int MaxAttempts,
    string? Result,
    string? ErrorCode,
    DateTime CreatedOn,
    DateTime UpdatedOn);

public sealed record ProcessingRunEvidence(
    long ExtractionRunId,
    Guid RunId,
    long SourceDocumentId,
    long ExtractionJobId,
    int AttemptNumber,
    string Status,
    string ProcessingPath,
    string OcrStatus,
    int OcrPageCount,
    bool OcrTruncated,
    decimal? ProcessingCostAmount,
    string? ProcessingCostCurrency,
    string ProcessingCostStatus,
    string OcrCostStatus,
    string ParserVersion,
    string SchemaVersion,
    string? FailureReason,
    DateTimeOffset CreatedOn,
    DateTimeOffset? CompletedOn);

public sealed record ProcessingAiRequestEvidence(
    Guid RequestId,
    long? ExtractionJobId,
    long? SourceDocumentOccurrenceId,
    string Provider,
    string ProviderClass,
    string Model,
    string Version,
    string Reason,
    string Result,
    string CostStatus,
    decimal? EstimatedCost,
    string? CostCurrency,
    string? CostPricingVersion,
    bool BudgetWarning,
    long InputTokens,
    long OutputTokens,
    IReadOnlyCollection<ProcessingAiAttemptEvidence> Attempts);

public sealed record ProcessingAiAttemptEvidence(
    int AttemptNumber,
    string Provider,
    string Model,
    string Result,
    int? HttpStatus,
    string? ErrorCode,
    long InputTokens,
    long OutputTokens,
    long LatencyMilliseconds,
    DateTime StartedOn,
    DateTime CompletedOn);
