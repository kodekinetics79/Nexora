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
    IReadOnlyList<LifecycleTransitionOption> AllowedTransitions,
    /// <summary>
    /// Whether <c>POST .../reopen</c> would be accepted from the CURRENT state.
    ///
    /// <para>Terminal is not the same question. A lead that was disqualified, lost or cancelled can
    /// come back — "we passed on it, the customer returned" is ordinary trade — while one that
    /// COMPLETED or was merged as a DUPLICATE cannot, and both sets are terminal. A client that
    /// offered reopen on <see cref="IsTerminal"/> would advertise a verb the server refuses, and a
    /// client that hardcoded the reopenable set would drift from
    /// <c>LifecyclePolicy.IsReopenable</c> the first time it changed. So the policy answers here,
    /// once, and the screen only has to read a boolean.</para>
    ///
    /// <para>This says nothing about the CALLER: the reopen endpoints additionally carry
    /// <c>[RequireManagerRole]</c>, which is the caller's rank and is resolved from the token.</para>
    /// </summary>
    bool CanReopen);

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
