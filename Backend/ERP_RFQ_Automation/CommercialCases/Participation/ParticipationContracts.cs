namespace ERP_RFQ_Automation.CommercialCases.Participation;

public sealed record LeadFitCriterionCommand(string Code, string Decision, string? Note);

public sealed record RecordLeadFitAssessmentCommand(
    long ExpectedLeadRevisionId,
    int ExpectedDecisionVersion,
    int? ExpectedFitVersion,
    string OverallDecision,
    string Rationale,
    IReadOnlyList<LeadFitCriterionCommand> Criteria,
    string IdempotencyKey,
    string Actor);

public sealed record LeadLineParticipationCommand(
    long LeadItemRevisionId,
    LeadLineParticipationChoice Choice,
    string? ReasonCode = null,
    string? ReasonNotes = null,
    long? ProductId = null,
    int? Quantity = null,
    string? UnitOfMeasure = null,
    string? Currency = null);

public sealed record CommitLeadParticipationCommand(
    long ExpectedLeadRevisionId,
    int ExpectedDecisionVersion,
    int? ExpectedParticipationVersion,
    bool Commit,
    long? FitAssessmentId,
    IReadOnlyList<LeadLineParticipationCommand> Lines,
    string IdempotencyKey,
    string Actor,
    string? ReasonCode = null,
    string? Notes = null);

public sealed record LeadFitAssessmentResult(
    long Id,
    long LeadId,
    long LeadRevisionId,
    int Sequence,
    string PolicyVersion,
    string Recommendation,
    bool IsActionable,
    string AssessmentJson,
    DateTimeOffset AssessedAtUtc);

public sealed record LeadLineParticipationResult(
    long LeadItemRevisionId,
    LeadLineParticipationChoice Choice,
    string? ReasonCode,
    string? ReasonNotes,
    long? ProductId,
    int? Quantity,
    string? UnitOfMeasure,
    int? UomId,
    string? Currency,
    long? CurrencyId,
    string CatalogPolicyVersion,
    string WarningSnapshotJson);

public sealed record LeadParticipationResult(
    long Id,
    long LeadId,
    long LeadRevisionId,
    long FitAssessmentId,
    int Sequence,
    bool IsCommitted,
    LeadParticipationOutcome Outcome,
    string? ReasonCode,
    string? Notes,
    DateTimeOffset DecidedAtUtc,
    IReadOnlyList<LeadLineParticipationResult> Lines);

public interface ILeadParticipationService
{
    Task<LeadFitAssessmentResult> RecordFitAssessmentAsync(
        long businessUnitId, long leadId, RecordLeadFitAssessmentCommand command, CancellationToken ct = default);

    Task<LeadParticipationResult> CommitDecisionAsync(
        long businessUnitId, long leadId, CommitLeadParticipationCommand command, CancellationToken ct = default);

    Task<LeadParticipationResult?> GetCurrentDecisionAsync(
        long businessUnitId, long leadId, CancellationToken ct = default);
}
