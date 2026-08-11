namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// What KIND of problem this is, which is the only question that decides who has to act.
///
/// <para>The console previously had one sentence for every outcome — "Provisioning failed." — and
/// that sentence sent the same person to look at four unrelated things. An operator cannot tell a
/// mistyped email address from a missing database GRANT from a mail host that is down by staring
/// at the same red box, so every one of them became a support ticket routed by guesswork.</para>
/// </summary>
public static class ProvisioningIssueClassifications
{
    /// <summary>
    /// Something in the submitted request has to change: an address somebody else now owns, a
    /// workspace slug that has been taken, a plan that no longer exists. Nobody can fix this by
    /// retrying, and the console must offer "edit and resubmit" rather than a retry button.
    /// </summary>
    public const string CustomerInput = "CUSTOMER_INPUT";

    /// <summary>
    /// This deployment is wired wrong: a missing GRANT for the provisioning role, a disabled
    /// provisioning worker, a baseline template the seeder cannot resolve. The customer did
    /// nothing; a platform engineer has to change configuration or schema.
    /// </summary>
    public const string PlatformConfiguration = "PLATFORM_CONFIGURATION";

    /// <summary>
    /// Something outside this process refused or never answered — the database was unreachable,
    /// the mail host rejected the connection, an HTTP dependency timed out. Retrying is
    /// reasonable, but only once the dependency is back.
    /// </summary>
    public const string ExternalDependency = "EXTERNAL_DEPENDENCY";

    /// <summary>
    /// An unclassified, non-terminal failure. Retrying is the correct first move. This is the
    /// default precisely because the alternatives all make a claim, and a wrong claim about who
    /// has to act is worse than an honest "try it again and tell us if it persists".
    /// </summary>
    public const string RetryableSystemFailure = "RETRYABLE_SYSTEM_FAILURE";
}

/// <summary>One step, flattened for the diagnostics surface.</summary>
public sealed record ProvisioningStepDiagnostic(
    string Step,
    string Label,
    int Ordinal,
    string Status,
    int AttemptCount,
    DateTime? StartedOn,
    DateTime? CompletedOn,
    int? DurationMs,
    string? FailureCode,
    string? FailureReason);

/// <summary>
/// A recovery action, and whether taking it is safe.
/// </summary>
/// <param name="Action">"resume" or "retry-step".</param>
/// <param name="Available">
/// False when the server would refuse the call outright — a cancelled execution, a lease another
/// runner is holding. A console rendering a control for an unavailable action must say why.
/// </param>
/// <param name="Safe">
/// False when the server WOULD accept it but it cannot succeed or would not help: a terminal
/// failure re-fails identically, and a step marked non-retriable would duplicate rather than
/// repair. Available-but-unsafe is a real state and is deliberately not collapsed into either
/// neighbour.
/// </param>
public sealed record ProvisioningRecoveryAction(
    string Action, string? Step, bool Available, bool Safe, string Detail);

/// <summary>
/// One thing standing between this tenant and a profile it is not yet fit for.
/// </summary>
/// <param name="Scope">"PRODUCTION" or "LOCAL_TEST" — which bar this blocker belongs to.</param>
public sealed record ProvisioningBlocker(
    string Code, string Scope, string Disposition, string Detail, string? ProductionRequirement);

/// <summary>
/// Everything an operator needs in order to answer "what happened to this tenant, whose problem is
/// it, and what may I safely press?" — in one payload, from persisted state.
///
/// <para><b>Why the blockers are two lists and not one.</b> "This tenant cannot go live" and "this
/// tenant cannot even be brought up on a laptop" are different sentences with different owners,
/// and a single merged list makes the second invisible behind the first. An engineer trying to
/// reproduce a defect locally needs to know that six of the eight blockers are the customer's ERP
/// and their tax authority and are irrelevant to them — and the platform owner needs to know those
/// six have not gone away.</para>
/// </summary>
/// <param name="Status">The execution state, or "NotStarted" when this tenant has no durable execution at all.</param>
/// <param name="FailureReason">A sentence an operator can act on. Never a stack trace, never "Provisioning failed."</param>
/// <param name="FailureCode">The stable failure code — a SQLSTATE, a step-specific code, or an exception type name.</param>
/// <param name="MissingPrerequisite">The one thing that has to exist before this can succeed, named. Null when nothing is missing.</param>
/// <param name="Classification">One of <see cref="ProvisioningIssueClassifications"/>.</param>
/// <param name="CorrelationId">Ties this attempt to every audit row and log line it produced.</param>
/// <param name="BlockersUnavailableReason">
/// Why the two blocker lists are empty when they are empty for a reason other than "there are
/// none" — no tenant row exists yet, or the activation policy could not be evaluated. Without it
/// an empty list reads as a clean bill of health.
/// </param>
public sealed record TenantProvisioningDiagnostics(
    long? TenantId,
    string? TenantName,
    string? TenantStatus,
    string DeploymentProfile,
    long? ExecutionId,
    string Status,
    string? CurrentStep,
    IReadOnlyList<ProvisioningStepDiagnostic> Steps,
    IReadOnlyList<string> CompletedSteps,
    ProvisioningStepDiagnostic? FailedStep,
    string? FailureReason,
    string? FailureCode,
    string? MissingPrerequisite,
    string Classification,
    string ClassificationDetail,
    string? CorrelationId,
    int AttemptCount,
    int CompletedStepCount,
    int TotalStepCount,
    DateTime? StartedOn,
    DateTime? CompletedOn,
    IReadOnlyList<ProvisioningRecoveryAction> RecoveryActions,
    IReadOnlyList<ProvisioningBlocker> ProductionBlockers,
    IReadOnlyList<ProvisioningBlocker> LocalTestBlockers,
    string? BlockersUnavailableReason,
    DateTime EvaluatedAtUtc);
