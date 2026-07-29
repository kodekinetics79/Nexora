namespace ERP_RFQ_Automation.CommercialIntelligence.Exceptions;

public sealed record CommercialExceptionQuery(
    CommercialExceptionStatus? Status = null,
    CommercialExceptionType? Type = null,
    CommercialExceptionSeverity? MinimumSeverity = null,
    bool OverdueOnly = false,
    int PageNumber = 1,
    int PageSize = 25);

public sealed record CommercialExceptionItem(
    long Id,
    long CommercialCaseId,
    string NexoraSerial,
    CommercialExceptionType ExceptionType,
    CommercialExceptionSeverity Severity,
    CommercialExceptionStatus Status,
    string Title,
    string Summary,
    string ReasonCode,
    string RecommendedActionCode,
    string SourceType,
    long SourceId,
    long SourceVersion,
    long? OwnerUserId,
    string? OwnerName,
    DateTime FirstDetectedAtUtc,
    DateTime LastDetectedAtUtc,
    DateTime SlaDueAtUtc,
    bool IsOverdue,
    string EvidenceJson,
    string RuleVersion,
    long Version);

public sealed record CommercialExceptionSourceCoverage(
    string SourceType,
    bool IsAvailable,
    string Status,
    string Detail);

public sealed record CommercialExceptionMetricDefinitions(
    string Total,
    string Active,
    string Critical,
    string Overdue);

public sealed record CommercialExceptionPage(
    IReadOnlyList<CommercialExceptionItem> Items,
    int Total,
    int Active,
    int Critical,
    int Overdue,
    DateTime GeneratedAtUtc,
    string RuleVersion,
    string Scope,
    int PageNumber,
    int PageSize,
    string CoverageStatus,
    IReadOnlyList<CommercialExceptionSourceCoverage> SourceCoverage,
    CommercialExceptionMetricDefinitions MetricDefinitions);

public sealed record RefreshCommercialExceptionsCommand(
    string CorrelationId,
    string IdempotencyKey,
    string ActorId);

public sealed record RefreshCommercialExceptionsResult(
    int Detected,
    int Reopened,
    int Refreshed,
    int Resolved,
    DateTime ReconciledAtUtc,
    string RuleVersion);

public sealed record TransitionCommercialExceptionCommand(
    long ExpectedVersion,
    CommercialExceptionStatus TargetStatus,
    string ActionCode,
    string Reason,
    string CorrelationId,
    string IdempotencyKey,
    string ActorId);

public sealed class CommercialExceptionNotFoundException(string message) : Exception(message);
public sealed class CommercialExceptionConflictException(string message) : Exception(message);
