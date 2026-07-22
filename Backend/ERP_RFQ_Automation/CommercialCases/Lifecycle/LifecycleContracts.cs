namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public sealed record LifecycleTransitionCommand(
    string TargetStatusCode,
    int ExpectedVersion,
    string? ReasonCode,
    string? ReasonNotes,
    string Source,
    string CorrelationId,
    string RequestReference,
    string IdempotencyKey);

public sealed record LifecycleActor(string ActorId, string ActorSource);

public sealed record LifecycleTransitionResult(
    long EventId,
    long CommercialCaseId,
    string CommercialCaseReference,
    string AggregateType,
    long AggregateId,
    long? PreviousStatusId,
    string PreviousStatusCode,
    long NewStatusId,
    string NewStatusCode,
    int Version,
    string EventType,
    string ActorId,
    DateTime OccurredOn,
    string? ReasonCode,
    string? ReasonNotes,
    string CorrelationId,
    string RequestReference,
    bool Replayed);

public sealed record LifecycleTransitionOption(long StatusId, string StatusCode, string Label, bool RequiresReason);

public sealed record LifecycleStateView(
    string AggregateType,
    long AggregateId,
    long CommercialCaseId,
    string CommercialCaseReference,
    long? CurrentStatusId,
    string CurrentStatusCode,
    int Version,
    bool IsTerminal,
    IReadOnlyList<LifecycleTransitionOption> AllowedTransitions);

public sealed class LifecycleValidationException : Exception
{
    public LifecycleValidationException(string message) : base(message) { }
}

public sealed class LifecycleConflictException : Exception
{
    public LifecycleConflictException(string message) : base(message) { }
}

public sealed class LifecycleNotFoundException : Exception
{
    public LifecycleNotFoundException(string message) : base(message) { }
}
