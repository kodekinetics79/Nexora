using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Provisioning;

public interface IProvisioningDiagnosticsService
{
    /// <summary>The latest attempt for a tenant. Null when no such tenant exists.</summary>
    Task<TenantProvisioningDiagnostics?> ForTenantAsync(long tenantId, CancellationToken ct = default);

    /// <summary>One named attempt, including one that never got as far as creating a tenant row.</summary>
    Task<TenantProvisioningDiagnostics?> ForExecutionAsync(long executionId, CancellationToken ct = default);
}

/// <summary>
/// Turns the provisioning journal into an answer, rather than a status.
///
/// <para><b>The defect this closes.</b> Every fact below was already persisted — the failed step,
/// its SQLSTATE, the sentence the runner wrote, the correlation id, the attempt counts — and none
/// of it reached a human. The console rendered "Provisioning failed." and the operator's only
/// remaining move was to ask an engineer to read the database. A read model is the whole fix: no
/// new state, no new writes, just the journal joined to the activation policy and phrased as the
/// four things somebody has to know before they can act.</para>
///
/// <para><b>It never mutates anything.</b> Diagnosing a broken tenant must be safe to do from a
/// read-only session, at any hour, without a reason prompt — otherwise the first thing an operator
/// does under pressure is the thing that changes state.</para>
/// </summary>
public sealed class ProvisioningDiagnosticsService(
    ErpRfqAutomationContext db,
    ITenantActivationPolicyService activation,
    IOptionsMonitor<ProvisioningOptions> options) : IProvisioningDiagnosticsService
{
    /// <summary>
    /// How long a Pending execution may sit untouched before "queued" becomes "nothing is running
    /// this". Comfortably past the worker's initial delay plus a poll interval, so a healthy
    /// deployment never reports itself broken during startup.
    /// </summary>
    private static readonly TimeSpan StalledPendingAfter = TimeSpan.FromMinutes(2);

    public async Task<TenantProvisioningDiagnostics?> ForTenantAsync(long tenantId, CancellationToken ct = default)
    {
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
        if (tenant is null) return null;

        // Newest first: a tenant whose first attempt failed and was resubmitted has two, and the
        // one an operator is looking at is always the latest.
        var execution = await db.Set<ProvisioningExecution>().AsNoTracking().Include(x => x.Steps)
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedOn).ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        return await BuildAsync(tenant, execution, ct);
    }

    public async Task<TenantProvisioningDiagnostics?> ForExecutionAsync(long executionId, CancellationToken ct = default)
    {
        var execution = await db.Set<ProvisioningExecution>().AsNoTracking().Include(x => x.Steps)
            .SingleOrDefaultAsync(x => x.Id == executionId, ct);
        if (execution is null) return null;

        var tenant = execution.TenantId is long tenantId
            ? await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            : null;

        return await BuildAsync(tenant, execution, ct);
    }

    private async Task<TenantProvisioningDiagnostics> BuildAsync(
        Tenant? tenant, ProvisioningExecution? execution, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var steps = (execution?.Steps ?? [])
            .OrderBy(step => step.Ordinal)
            .Select(step =>
            {
                ProvisioningStepCatalog.TryGet(step.StepCode, out var definition);
                return new ProvisioningStepDiagnostic(
                    step.StepCode, definition?.Label ?? step.StepCode, step.Ordinal,
                    step.Status.ToString(), step.AttemptCount, step.StartedOn, step.CompletedOn,
                    step.DurationMs, step.FailureCode, step.FailureReason);
            })
            .ToList();

        // Skipped counts as completed, matching ProvisioningProjection: a step that does not apply
        // to this request will never move again, and leaving it outstanding pins every
        // password-activation tenant at seven-eighths forever.
        var completed = steps
            .Where(x => x.Status is nameof(ProvisioningStepStatus.Succeeded)
                or nameof(ProvisioningStepStatus.Skipped))
            .Select(x => x.Step).ToList();

        var failed = execution?.FailedStep is { Length: > 0 } failedCode
            ? steps.FirstOrDefault(x => x.Step == failedCode)
            : steps.FirstOrDefault(x => x.Status == nameof(ProvisioningStepStatus.Failed));

        var status = execution is null ? "NotStarted" : execution.State.ToString();
        var stalled = IsStalledPending(execution, now);

        var (classification, classificationDetail, missing) =
            Classify(execution, failed, stalled);

        var failureReason = execution?.FailureReason
                            ?? failed?.FailureReason
                            ?? (stalled
                                ? "This request was accepted and journalled but no runner has ever picked it up."
                                : null);

        var (production, localTest, blockersUnavailable) = await BlockersAsync(tenant, ct);

        return new TenantProvisioningDiagnostics(
            TenantId: tenant?.Id ?? execution?.TenantId,
            TenantName: tenant?.Name ?? execution?.Name,
            TenantStatus: tenant?.Status.ToString(),
            DeploymentProfile: TenantDeploymentProfiles.ToWire(
                tenant?.DeploymentProfile ?? TenantDeploymentProfile.Production),
            ExecutionId: execution?.Id,
            Status: status,
            CurrentStep: execution?.CurrentStep,
            Steps: steps,
            CompletedSteps: completed,
            FailedStep: failed,
            FailureReason: failureReason,
            FailureCode: failed?.FailureCode,
            MissingPrerequisite: missing,
            Classification: classification,
            ClassificationDetail: classificationDetail,
            CorrelationId: execution?.CorrelationId,
            AttemptCount: execution?.AttemptCount ?? 0,
            CompletedStepCount: completed.Count,
            TotalStepCount: steps.Count,
            StartedOn: execution?.StartedOn,
            CompletedOn: execution?.CompletedOn,
            RecoveryActions: RecoveryActions(execution, failed, now),
            ProductionBlockers: production,
            LocalTestBlockers: localTest,
            BlockersUnavailableReason: blockersUnavailable,
            EvaluatedAtUtc: now);
    }

    /// <summary>
    /// Accepted, journalled, and never picked up. This is a deployment where
    /// <c>Platform:Provisioning:Enabled</c> is false or no host is running the worker at all, and
    /// it is indistinguishable from "slow" on every other surface — the wizard simply never moves.
    /// </summary>
    private static bool IsStalledPending(ProvisioningExecution? execution, DateTime now)
        => execution is { State: ProvisioningExecutionState.Pending, StartedOn: null }
           && execution.CreatedOn < now - StalledPendingAfter;

    /// <summary>
    /// Whose problem this is, and what is missing.
    ///
    /// <para>Keyed off the codes the runner already writes — <c>Describe</c> in
    /// <c>TenantProvisioningRunner</c> and the <c>failureCode</c> arguments in
    /// <c>ProvisioningStepExecutor</c> — rather than off the message text, so a reworded sentence
    /// never silently reclassifies a failure.</para>
    /// </summary>
    private (string Classification, string Detail, string? Missing) Classify(
        ProvisioningExecution? execution, ProvisioningStepDiagnostic? failed, bool stalled)
    {
        if (execution is null)
            return (ProvisioningIssueClassifications.PlatformConfiguration,
                "This tenant has no durable provisioning execution. It was created by the synchronous "
                + "compatibility path or by an earlier build, so there is no step journal to read.",
                "A durable provisioning execution. Re-provision through POST /api/platform/provisioning/tenants "
                + "if a step journal is needed.");

        if (stalled)
            return (ProvisioningIssueClassifications.PlatformConfiguration,
                "The request was accepted and journalled, but no runner has claimed it. Nothing has been "
                + "attempted, so nothing has failed — which is why no step shows an error.",
                options.CurrentValue.Enabled
                    ? "A running ProvisioningRunWorker. This process has the worker enabled, so either no "
                      + "host is running it or every host is failing to reach platform.\"ProvisioningExecutions\"."
                    : $"A running ProvisioningRunWorker. It is DISABLED in this deployment "
                      + $"({ProvisioningOptions.SectionName}:Enabled=false), so accepted work is never acted on.");

        if (execution.State == ProvisioningExecutionState.Cancelled)
            return (ProvisioningIssueClassifications.Cancelled,
                $"An operator abandoned this attempt{(execution.CancelledBy is { Length: > 0 } by ? $" ({by})" : "")}. "
                + "Anything earlier steps committed is still there, in Provisioning status.",
                null);

        if (execution.State == ProvisioningExecutionState.Succeeded)
            return (ProvisioningIssueClassifications.NoFailure,
                "All required steps succeeded; non-applicable steps were skipped. "
                + "Provisioning is complete. Tenant activation is evaluated separately.",
                null);

        if (failed is null && execution.FailureReason is null && !execution.FailureIsTerminal
            && execution.State is ProvisioningExecutionState.Pending or ProvisioningExecutionState.Running)
            return (ProvisioningIssueClassifications.NoFailure,
                execution.State == ProvisioningExecutionState.Running
                    ? "A runner holds this execution and is working through the steps."
                    : "The request is queued for provisioning. No step has recorded a failure.",
                null);

        // Step-specific codes first: they are the precise ones, and a SQLSTATE match on the same
        // failure would say something true but less useful.
        return failed?.FailureCode switch
        {
            "slug-taken" => (ProvisioningIssueClassifications.CustomerInput,
                "The workspace address now belongs to another tenant. Retrying loses the same race again.",
                "A workspace address nobody else owns. Edit the request and submit it again."),

            "email-taken" => (ProvisioningIssueClassifications.CustomerInput,
                "The founding administrator's email address is already held by another user. User email is "
                + "globally unique, so no retry can free it.",
                "A founding-administrator email address that is not already in use."),

            "plan-missing" or "plan-inactive" => (ProvisioningIssueClassifications.PlatformConfiguration,
                "The plan this request was submitted against was withdrawn between submit and execution. The "
                + "request was valid when it was made.",
                "An active plan. Reactivate it, or edit the request onto a different plan and resubmit."),

            "baseline-profile" or "baseline-target" => (ProvisioningIssueClassifications.PlatformConfiguration,
                "The workspace baseline seeder could not resolve what it was asked to seed. This is platform "
                + "reference data, not anything the customer supplied.",
                "A resolvable tenant baseline profile for this deployment."),

            "42501" => (ProvisioningIssueClassifications.PlatformConfiguration,
                "The database refused the write for lack of privilege (42501). A GRANT is missing for the "
                + "provisioning execution role — commonly a table created after the last grant migration.",
                "A GRANT on the tables this step writes for the provisioning execution role "
                + "(nexora_pipeline_app). Add the migration; no number of retries adds a GRANT."),

            "23505" => (ProvisioningIssueClassifications.CustomerInput,
                "A uniqueness constraint refused the write: the workspace address, business unit code or "
                + "administrator email is already in use. Every step re-reads before it writes, so reaching "
                + "this means somebody else created the row.",
                "A workspace address, business unit code and administrator email nobody else holds."),

            "23503" => (ProvisioningIssueClassifications.PlatformConfiguration,
                "A record this step depends on no longer exists (23503). Something it referenced was deleted "
                + "underneath the execution.",
                "The referenced record this step depends on."),

            var code when IsConnectivityCode(code) => (ProvisioningIssueClassifications.ExternalDependency,
                "Something outside this process refused or never answered. The request and this deployment's "
                + "configuration are both fine; the dependency is not.",
                "A reachable dependency. Retry once it is back — nothing here needs editing."),

            _ => execution.FailureIsTerminal
                ? (ProvisioningIssueClassifications.PlatformConfiguration,
                    "The runner judged this failure terminal: retrying it will produce the same result. The "
                    + "failure reason names what has to change.",
                    null)
                : (ProvisioningIssueClassifications.RetryableSystemFailure,
                    "An unclassified, non-terminal failure. Retrying is the correct first move; if it repeats "
                    + "with the same code, it is not transient.",
                    null)
        };
    }

    /// <summary>
    /// SQLSTATE class 08 is connection exception and 57P is operator intervention (admin shutdown,
    /// cannot connect now); the named exception types are what a mail host, an HTTP dependency or
    /// a socket timeout surfaces as.
    /// </summary>
    private static bool IsConnectivityCode(string? code)
        => code is { Length: > 0 }
           && (code.StartsWith("08", StringComparison.Ordinal)
               || code.StartsWith("57P", StringComparison.Ordinal)
               || code.Contains("Socket", StringComparison.OrdinalIgnoreCase)
               || code.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
               || code.Contains("Http", StringComparison.OrdinalIgnoreCase)
               || code.Contains("Smtp", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What the operator may press, and whether pressing it helps. Mirrors the refusals in
    /// <c>TenantProvisioningService.RetryAsync</c> so the console never offers a button that the
    /// server is certain to reject, and never disables one without saying why.
    /// </summary>
    private static IReadOnlyList<ProvisioningRecoveryAction> RecoveryActions(
        ProvisioningExecution? execution, ProvisioningStepDiagnostic? failed, DateTime now)
    {
        if (execution is null)
            return [new ProvisioningRecoveryAction("resume", null, false, false,
                "There is no durable execution to resume. Submit a new provisioning request instead.")];

        if (execution.State == ProvisioningExecutionState.Cancelled)
            return [new ProvisioningRecoveryAction("resume", null, false, false,
                "This execution was cancelled and will not run again by any route. Submit a new request.")];

        if (execution.IsLeasedAt(now))
            return [new ProvisioningRecoveryAction("resume", null, false, false,
                $"A runner holds the lease until {execution.LeaseUntil:O}. Wait for it to finish or fail.")];

        var actions = new List<ProvisioningRecoveryAction>();

        if (execution.State == ProvisioningExecutionState.Succeeded)
            actions.Add(new ProvisioningRecoveryAction("resume", null, false, false,
                "This execution already completed, so there is nothing to resume. Re-run a single step to "
                + "repair a specific gap."));
        else
            actions.Add(new ProvisioningRecoveryAction("resume", null, true,
                !execution.FailureIsTerminal,
                execution.FailureIsTerminal
                    ? "Accepted, but it cannot succeed as submitted: the runner judged this failure terminal, "
                      + "so a resume re-runs the same step and fails identically. Change what the failure "
                      + "names, or cancel and resubmit."
                    : "The runner picks up at the first step that has not succeeded and skips committed work."));

        if (failed is not null)
        {
            var retriable = ProvisioningStepCatalog.TryGet(failed.Step, out var definition)
                            && definition.IsRetriable;
            actions.Add(new ProvisioningRecoveryAction("retry-step", failed.Step, true,
                retriable && !execution.FailureIsTerminal,
                !retriable
                    ? $"Re-running '{failed.Label}' would duplicate rather than repair, so it is offered only "
                      + "as a deliberate act."
                    : execution.FailureIsTerminal
                        ? $"Accepted, but re-running '{failed.Label}' cannot succeed until what the failure "
                          + "names has changed."
                        : $"'{failed.Label}' is safe to run again: it establishes what already exists before "
                          + "it writes."));
        }

        return actions;
    }

    /// <summary>
    /// The two bars, computed independently of this tenant's own profile so they are always
    /// comparable.
    ///
    /// <para>PRODUCTION is every unsatisfied control, because production defers nothing.
    /// LOCAL_TEST is the unsatisfied controls that no deployment profile is entitled to defer —
    /// i.e. the ones outside <c>DeploymentPrerequisiteCatalog</c>, which are defects rather than
    /// absent third parties. Computing both from the same evaluation is what makes "six of these
    /// eight are the customer's estate and none of them stop you reproducing this locally" a fact
    /// on screen rather than something an engineer works out.</para>
    /// </summary>
    private async Task<(IReadOnlyList<ProvisioningBlocker> Production,
        IReadOnlyList<ProvisioningBlocker> LocalTest, string? Unavailable)> BlockersAsync(
        Tenant? tenant, CancellationToken ct)
    {
        if (tenant is null)
            return ([], [],
                "No tenant record exists yet, so the activation controls cannot be evaluated. "
                + "Provisioning did not get as far as creating one.");

        TenantActivationDecision? decision;
        try
        {
            decision = await activation.EvaluateAsync(tenant.Id, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A diagnostics screen that 500s because the thing it is diagnosing is broken is the
            // defect, not the diagnosis. The provisioning half of the payload is still worth
            // rendering, so the failure is reported in place of the blockers.
            return ([], [],
                $"The activation policy could not be evaluated ({exception.GetType().Name}), so the "
                + "production and local-test blocker lists are unknown rather than empty.");
        }

        if (decision is null)
            return ([], [], "The activation policy returned no decision for this tenant.");

        var production = new List<ProvisioningBlocker>();
        var localTest = new List<ProvisioningBlocker>();

        foreach (var control in decision.Controls.Where(x => !x.Satisfied))
        {
            var prerequisite = DeploymentPrerequisiteCatalog.ForControl(control.Code);
            production.Add(new ProvisioningBlocker(control.Code, TenantDeploymentProfiles.Production,
                control.Disposition, control.Detail,
                prerequisite?.ProductionRequirement ?? control.ProductionRequirement));

            if (prerequisite is null)
                localTest.Add(new ProvisioningBlocker(control.Code, TenantDeploymentProfiles.LocalTest,
                    ActivationControlDispositions.Blocking, control.Detail, null));
        }

        // Deployment prerequisites that never mapped to an activation control still belong on the
        // production list — that is the only place they are ever answered for.
        foreach (var prerequisite in decision.ProductionReadiness.Prerequisites
                     .Where(x => x.ControlCode is null && !x.Satisfied))
            production.Add(new ProvisioningBlocker(prerequisite.Key, TenantDeploymentProfiles.Production,
                prerequisite.Disposition, prerequisite.Detail, prerequisite.ProductionRequirement));

        return (production, localTest, null);
    }
}
