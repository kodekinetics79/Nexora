using System.Data.Common;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// What one run produced. The invitation is carried IN MEMORY and never persisted — it is the
/// only thing in this record that a caller could not read back from the database, and the only
/// reason it exists is the synchronous compatibility path, which must hand the operator an
/// activation link in the same response the old endpoint did.
/// </summary>
public sealed record ProvisioningRunOutcome(
    ProvisioningExecution Execution,
    IssuedTenantAdminInvitation? Invitation,
    bool InvitationEmailSent);

/// <summary>
/// Drives one provisioning execution through its steps, persisting every transition.
///
/// <para><b>The journal and the work are on different connections, on purpose.</b> A step's
/// failure record written inside that step's own transaction is rolled back by the very failure
/// it records — which is exactly how the previous design ended up able to say nothing beyond
/// "Provisioning failed." So the <c>Running</c> marker and any <c>Failed</c> verdict are written
/// on a separate scope with its own context and its own transaction, and survive whatever the
/// step does. The <c>Succeeded</c> verdict is the mirror image and is written INSIDE the step's
/// transaction, so a step can never be marked done by work that then rolled back.</para>
///
/// <para><b>Leases, not locks.</b> Two runners on one execution would both write the same tenant
/// rows, and the steps are idempotent against themselves but not against a rival running at the
/// same instant. A lease expires rather than being released, so a runner whose process died does
/// not park an execution forever — the same reasoning as the finance outbox's claim.</para>
/// </summary>
public interface ITenantProvisioningRunner
{
    /// <summary>
    /// Claims and runs one execution as far as it will go. Returns null when it could not be
    /// claimed — already terminal, or already leased by another runner.
    /// </summary>
    Task<ProvisioningRunOutcome?> RunAsync(long executionId, CancellationToken ct = default);

    /// <summary>
    /// Claims and runs whatever is available, up to <paramref name="batchSize"/>. The worker's
    /// entry point, and the seam a test drives to get one deterministic pass.
    /// </summary>
    Task<int> RunAvailableAsync(int batchSize, CancellationToken ct = default);
}

public sealed class TenantProvisioningRunner : ITenantProvisioningRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ProvisioningOptions> _options;
    private readonly ILogger<TenantProvisioningRunner> _log;
    private readonly string _runnerId;

    public TenantProvisioningRunner(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ProvisioningOptions> options,
        ILogger<TenantProvisioningRunner> log)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _runnerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    public async Task<ProvisioningRunOutcome?> RunAsync(long executionId, CancellationToken ct = default)
    {
        var leaseToken = await TryClaimAsync(executionId, ct);
        return leaseToken is null ? null : await RunClaimedAsync(executionId, ct);
    }

    public async Task<int> RunAvailableAsync(int batchSize, CancellationToken ct = default)
    {
        var candidates = await FindAvailableAsync(batchSize, ct);
        var ran = 0;

        foreach (var executionId in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await RunAsync(executionId, ct) is not null)
                    ran++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // One execution's collapse must never stop the others. The execution itself has
                // already been journalled as failed by the step machinery; this catch exists for
                // the failure modes OUTSIDE a step — a dead connection while releasing a lease.
                _log.LogError(exception,
                    "Provisioning execution {ExecutionId} could not be driven; continuing with the next.",
                    executionId);
            }
        }

        return ran;
    }

    // ---- claiming ------------------------------------------------------------------------

    /// <summary>
    /// Everything a runner may legitimately pick up: queued work, work whose previous runner
    /// died and left an expired lease, and failures the runner has not yet exhausted its own
    /// attempt budget on.
    /// </summary>
    private async Task<IReadOnlyList<long>> FindAvailableAsync(int batchSize, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var now = DateTime.UtcNow;
        var failedRetryBefore = now - options.RetryBackoff;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // Projected to the id, not materialised as entities. Platform-plane grants are narrow by
        // design (see 20260805105320), and a claim query has no business reading a stored request
        // payload it is not going to use.
        return await db.Set<ProvisioningExecution>().AsNoTracking()
            .Where(e =>
                e.State == ProvisioningExecutionState.Pending
                || (e.State == ProvisioningExecutionState.Running
                    && (e.LeaseUntil == null || e.LeaseUntil <= now))
                || (e.State == ProvisioningExecutionState.Failed
                    && !e.FailureIsTerminal
                    && e.AttemptCount < options.MaxAutomaticAttempts
                    && (e.CompletedOn == null || e.CompletedOn <= failedRetryBefore)))
            .OrderBy(e => e.CreatedOn).ThenBy(e => e.Id)
            .Select(e => e.Id)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    private async Task<Guid?> TryClaimAsync(long executionId, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var leaseToken = Guid.NewGuid();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // The context enables EnableRetryOnFailure, so an explicit transaction must run inside
        // the execution strategy or EF throws — and every attempt must clear the change tracker,
        // because a rolled-back attempt can leave generated keys and post-SaveChanges states
        // tracked as though they had committed.
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<Guid?>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var now = DateTime.UtcNow;
            var execution = await db.Set<ProvisioningExecution>()
                .FirstOrDefaultAsync(e => e.Id == executionId, ct);
            if (execution is null || execution.IsTerminal || execution.IsLeasedAt(now))
                return null;

            execution.State = ProvisioningExecutionState.Running;
            execution.LeaseOwner = _runnerId;
            execution.LeaseToken = leaseToken;
            execution.LeaseUntil = now.Add(options.LeaseDuration);
            execution.AttemptCount++;
            execution.StartedOn ??= now;
            // Cleared on the way in rather than on the way out: an execution being retried must
            // not still be showing the previous attempt's failure while the new one is running.
            execution.FailedStep = null;
            execution.FailureReason = null;
            execution.FailureIsTerminal = false;
            execution.CompletedOn = null;
            execution.Version++;

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another runner claimed it between the read and the write. Losing this race is
                // the system working, not an error: exactly one runner ends up holding the lease.
                return null;
            }

            await tx.CommitAsync(ct);
            return leaseToken;
        });
    }

    // ---- the step machine ------------------------------------------------------------------

    private async Task<ProvisioningRunOutcome> RunClaimedAsync(long executionId, CancellationToken ct)
    {
        IssuedTenantAdminInvitation? issued = null;

        foreach (var stepCode in ProvisioningStepCodes.Ordered)
        {
            var snapshot = await SnapshotAsync(executionId, ct);

            // An operator cancelling mid-run is seen here, between steps. Cancellation cannot
            // interrupt a step in flight — a half-written transaction is worse than one extra
            // completed step — so it takes effect at the next boundary.
            if (snapshot.State == ProvisioningExecutionState.Cancelled)
            {
                _log.LogInformation(
                    "Provisioning execution {ExecutionId} was cancelled; stopping before step {Step}.",
                    executionId, stepCode);
                return new ProvisioningRunOutcome(await LoadAsync(executionId, ct), null, false);
            }

            var step = snapshot.Steps[stepCode];
            if (step.Status is ProvisioningStepStatus.Succeeded or ProvisioningStepStatus.Skipped)
                continue;

            // The invitation step does not run on the password path. Recorded as Skipped rather
            // than left Pending, so the console shows a decision instead of a step that looks
            // like it never started.
            if (stepCode == ProvisioningStepCodes.Invitation
                && snapshot.AdminActivation != AdminActivationMethods.Invite)
            {
                await MarkSkippedAsync(executionId, stepCode, ct);
                continue;
            }

            var isRetry = step.AttemptCount > 0;
            await MarkRunningAsync(executionId, stepCode, ct);

            try
            {
                issued = await ExecuteStepAsync(executionId, stepCode, isRetry, ct) ?? issued;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Process shutdown. The step is left Running with a live lease; when the lease
                // lapses another runner picks it up and re-attempts exactly this step. Nothing
                // is marked failed, because nothing about the request was wrong.
                throw;
            }
            catch (Exception exception)
            {
                await MarkFailedAsync(executionId, stepCode, exception, ct);
                return new ProvisioningRunOutcome(await LoadAsync(executionId, ct), null, false);
            }
        }

        await CompleteAsync(executionId, ct);

        // AFTER the commit, and only on a run that reached the end. A sent email cannot be
        // rolled back, and a live activation link for a tenant whose provisioning is still
        // broken invites the customer into a workspace that is not finished.
        var emailSent = issued is not null && await SendInvitationEmailAsync(issued, ct);

        return new ProvisioningRunOutcome(await LoadAsync(executionId, ct), issued, emailSent);
    }

    /// <summary>
    /// Runs one step inside its own transaction, on its own scope. The step's SUCCESS is written
    /// by the same transaction as its work, which is what makes "this step is done" a fact rather
    /// than a hope.
    /// </summary>
    private async Task<IssuedTenantAdminInvitation?> ExecuteStepAsync(
        long executionId, string stepCode, bool isRetry, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var executor = scope.ServiceProvider.GetRequiredService<IProvisioningStepExecutor>();

        // Overwritten by EVERY execution-strategy attempt, deliberately: the token that activates
        // the account is the one belonging to the attempt that COMMITTED. A token from an attempt
        // whose transaction rolled back hashes to a row that no longer exists and activates
        // nothing, so keeping the first one would email the customer a dead link.
        IssuedTenantAdminInvitation? issued = null;

        // Restarted per attempt, inside the delegate: DurationMs is the cost of the attempt an
        // operator is looking at, and a stopwatch that accumulated across execution-strategy
        // retries would report a 200ms step as having taken four seconds.
        var stopwatch = new Stopwatch();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // A failed execution-strategy attempt can leave generated keys and post-SaveChanges
            // states tracked even though its transaction rolled back. Every attempt must rebuild
            // its graph from the database.
            db.ChangeTracker.Clear();
            issued = null;
            stopwatch.Restart();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var execution = await db.Set<ProvisioningExecution>()
                .FirstAsync(e => e.Id == executionId, ct);
            var step = await db.Set<ProvisioningStep>()
                .FirstAsync(s => s.ExecutionId == executionId && s.StepCode == stepCode, ct);

            var request = ProvisioningRequestCanonicalizer.Rehydrate(execution.RequestPayload);
            var context = new ProvisioningStepContext(db, execution, request, execution.RequestedBy, isRetry);

            var detail = await executor.ExecuteAsync(stepCode, context, ct);

            step.Status = ProvisioningStepStatus.Succeeded;
            step.CompletedOn = DateTime.UtcNow;
            step.DurationMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
            step.Detail = detail;
            step.FailureCode = null;
            step.FailureReason = null;

            execution.CurrentStep = stepCode;
            execution.Version++;

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            issued = context.Issued;
        });

        return issued;
    }

    // ---- journal writes (always on their own scope, never inside a step transaction) ---------

    private async Task MarkRunningAsync(long executionId, string stepCode, CancellationToken ct)
        => await JournalAsync(executionId, ct, (execution, steps) =>
        {
            var step = steps[stepCode];
            step.Status = ProvisioningStepStatus.Running;
            step.AttemptCount++;
            step.StartedOn = DateTime.UtcNow;
            step.CompletedOn = null;
            step.DurationMs = null;

            execution.CurrentStep = stepCode;
            // Renewed per step rather than once per run: the baseline seed writes several hundred
            // rows, and a lease that lapses under a runner still working invites a second runner
            // onto the same tenant.
            execution.LeaseUntil = DateTime.UtcNow.Add(_options.CurrentValue.LeaseDuration);
        });

    private async Task MarkSkippedAsync(long executionId, string stepCode, CancellationToken ct)
        => await JournalAsync(executionId, ct, (_, steps) =>
        {
            var step = steps[stepCode];
            step.Status = ProvisioningStepStatus.Skipped;
            step.CompletedOn = DateTime.UtcNow;
            step.Detail = JsonSerializer.Serialize(new
            {
                reason = "Not applicable to this request's activation method."
            });
        });

    private async Task MarkFailedAsync(
        long executionId, string stepCode, Exception exception, CancellationToken ct)
    {
        var (code, reason, isTerminal) = Describe(exception);

        _log.LogError(exception,
            "Provisioning execution {ExecutionId} failed at step {Step} ({FailureCode}). Terminal: {Terminal}.",
            executionId, stepCode, code, isTerminal);

        await JournalAsync(executionId, ct, (execution, steps) =>
        {
            var now = DateTime.UtcNow;
            var step = steps[stepCode];
            step.Status = ProvisioningStepStatus.Failed;

            step.CompletedOn = now;
            step.DurationMs = step.StartedOn is { } started
                ? (int)Math.Min(int.MaxValue, (now - started).TotalMilliseconds)
                : null;
            step.FailureCode = code;
            step.FailureReason = reason;

            execution.State = ProvisioningExecutionState.Failed;
            execution.FailedStep = stepCode;
            execution.FailureReason = reason;
            execution.FailureIsTerminal = isTerminal;
            execution.CurrentStep = null;
            execution.CompletedOn = now;
            // Released, not held. A failure is a resting state an operator may act on
            // immediately, and a lease left behind would make the retry button do nothing.
            execution.LeaseOwner = null;
            execution.LeaseToken = null;
            execution.LeaseUntil = null;
        },
        // A failed provision is as much a privileged event as a successful one, and the operator
        // asking "what happened to this customer's account?" months later is asking the audit log,
        // not the application log.
        after: (scope, db, execution, token) => AuditTerminalAsync(
            scope, db, execution, "tenant.provision", PlatformAuditResults.Failure, token));
    }

    /// <summary>
    /// The execution's own completion transition: the tenant leaves <c>Provisioning</c> and
    /// becomes <c>Active</c>, and the result payload is frozen for replay.
    ///
    /// <para>Deliberately NOT part of any step. The tenant staying in <c>Provisioning</c> for the
    /// whole run is what makes a half-provisioned tenant read as half-provisioned on the tenants
    /// screen, instead of showing a green Active badge over a workspace with no currency, no
    /// units of measure and nobody who can sign in.</para>
    /// </summary>
    private async Task CompleteAsync(long executionId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var execution = await db.Set<ProvisioningExecution>()
                .Include(e => e.Steps)
                .FirstAsync(e => e.Id == executionId, ct);

            if (execution.TenantId is long tenantId)
            {
                var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
                    .FirstAsync(t => t.Id == tenantId, ct);
                tenant.PrimaryBusinessUnitId = execution.ProvisionedBusinessUnitId;
                tenant.Status = TenantStatus.Active;
                tenant.ModifiedBy = execution.RequestedBy;
                tenant.ModifiedOn = DateTime.UtcNow;
            }

            execution.State = ProvisioningExecutionState.Succeeded;
            execution.CurrentStep = null;
            execution.CompletedOn = DateTime.UtcNow;
            execution.LeaseOwner = null;
            execution.LeaseToken = null;
            execution.LeaseUntil = null;
            execution.ResultPayload = BuildResultPayload(execution);
            execution.Version++;

            await db.SaveChangesAsync(ct);

            // Written on THIS scope, so the audit row and the tenant's transition to Active
            // commit together. A tenant that went live without an audit trail, or an audit trail
            // for a transition that rolled back, are both worse than no record at all.
            await AuditTerminalAsync(scope, db, execution, "tenant.provision",
                PlatformAuditResults.Success, ct);

            await tx.CommitAsync(ct);
        });
    }

    /// <summary>
    /// Attributes a terminal transition to the OPERATOR who submitted it, not to the background
    /// process that carried it out.
    ///
    /// <para><c>PlatformAuditService</c> enforces a positive-actor invariant on success records —
    /// there is no anonymous privileged mutation — and the runner has no <c>HttpContext</c> and no
    /// principal. So the principal is reconstructed from the platform user id captured at submit,
    /// which is the honest answer: the human asked for this tenant, and the worker is their hands.
    /// When no id was captured (a caller whose token carried no <c>sub</c>) the row is skipped
    /// with a warning rather than fabricated, because an audit record naming the wrong actor is
    /// worse than a gap somebody can see.</para>
    /// </summary>
    private async Task AuditTerminalAsync(
        IServiceScope scope, ErpRfqAutomationContext db, ProvisioningExecution execution,
        string action, string result, CancellationToken ct)
    {
        if (execution.RequestedByPlatformUserId is not long actorId || actorId <= 0)
        {
            _log.LogWarning(
                "Provisioning execution {ExecutionId} completed with action {Action} but carried no " +
                "platform actor id, so no audit record was written. The submitting token had no " +
                "'sub' claim.", execution.Id, action);
            return;
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, actorId.ToString()),
            new Claim("email", execution.RequestedBy)
        ], "ProvisioningRunner"));

        // Constructed over THIS scope's context so its SaveChanges enlists in the ambient
        // transaction rather than opening a second one beside it.
        var audit = new PlatformAuditService(
            db, scope.ServiceProvider.GetRequiredService<ILogger<PlatformAuditService>>());

        // The administrator's EMAIL is audited; the password never is, generated or not, and
        // neither is the activation token. The step journal is summarised rather than repeated —
        // the full detail lives on platform."ProvisioningSteps" and is joinable by execution id.
        await audit.WriteAsync(principal, action, nameof(Tenant),
            execution.TenantId?.ToString() ?? execution.Id.ToString(),
            new
            {
                executionId = execution.Id,
                execution.CorrelationId,
                execution.Slug,
                execution.Name,
                execution.TenantId,
                execution.ProvisionedBusinessUnitId,
                adminEmail = execution.AdminEmail,
                adminUserId = execution.FoundingUserId,
                adminActivation = execution.AdminActivation,
                // The invitation's EXISTENCE is auditable; its token is not, and neither is the
                // URL that carries it.
                invitationIssued = execution.InvitationId is not null,
                attempts = execution.AttemptCount,
                execution.FailedStep,
                execution.FailureReason,
                steps = execution.Steps
                    .OrderBy(step => step.Ordinal)
                    .Select(step => new
                    {
                        step.StepCode, status = step.Status.ToString(),
                        step.AttemptCount, step.DurationMs
                    })
            },
            actAsTenantId: execution.TenantId, httpContext: null, result: result, ct: ct);
    }

    /// <summary>
    /// The frozen result a replayed submit returns.
    ///
    /// <para>Carries no one-time secret. The generated password and the activation link exist
    /// exactly once, in the body of the first response; storing them here so a replay could
    /// repeat them would turn an idempotency key into a credential-retrieval endpoint — the
    /// precise property the invitation design gives up convenience to avoid.</para>
    /// </summary>
    private static string BuildResultPayload(ProvisioningExecution execution)
        => JsonSerializer.Serialize(new
        {
            execution.TenantId,
            execution.ProvisionedBusinessUnitId,
            execution.FoundingRoleId,
            execution.FoundingUserId,
            execution.InvitationId,
            execution.Slug,
            steps = execution.Steps
                .OrderBy(s => s.Ordinal)
                .Select(s => new { s.StepCode, status = s.Status.ToString(), s.AttemptCount, s.DurationMs })
        });

    private async Task<bool> SendInvitationEmailAsync(
        IssuedTenantAdminInvitation issued, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var invitations = scope.ServiceProvider.GetRequiredService<ITenantAdminInvitationService>();

        // Never throws by contract: a mail outage must not turn a committed provision into a
        // failed execution, and the operator can always resend from the invitations screen.
        return await invitations.SendInvitationEmailAsync(issued, ct);
    }

    /// <summary>
    /// One journal write, on its own scope and its own transaction.
    ///
    /// <para>The scope is what makes a failure record survivable: written on the step's own
    /// connection it would be rolled back by the very failure it records, which is precisely how
    /// the previous design ended up able to say nothing beyond "Provisioning failed."</para>
    /// </summary>
    /// <param name="after">
    /// Runs inside the same transaction, after the mutation and its save. Used for the audit
    /// row, which must commit with the transition it describes or not at all.
    /// </param>
    private async Task JournalAsync(
        long executionId,
        CancellationToken ct,
        Action<ProvisioningExecution, IReadOnlyDictionary<string, ProvisioningStep>> mutate,
        Func<IServiceScope, ErpRfqAutomationContext, ProvisioningExecution, CancellationToken, Task>? after = null)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var execution = await db.Set<ProvisioningExecution>()
                .Include(e => e.Steps)
                .FirstAsync(e => e.Id == executionId, ct);
            var steps = execution.Steps.ToDictionary(s => s.StepCode, StringComparer.Ordinal);

            mutate(execution, steps);
            execution.Version++;

            await db.SaveChangesAsync(ct);

            if (after is not null)
                await after(scope, db, execution, ct);

            await tx.CommitAsync(ct);
        });
    }

    // ---- reads --------------------------------------------------------------------------

    private sealed record StepSnapshot(ProvisioningStepStatus Status, int AttemptCount);

    private sealed record ExecutionSnapshot(
        ProvisioningExecutionState State,
        string AdminActivation,
        IReadOnlyDictionary<string, StepSnapshot> Steps);

    /// <summary>
    /// The three facts the loop needs between steps, projected rather than materialised. The
    /// stored request payload is several kilobytes of company profile and there is no reason to
    /// pull it across the wire eight times per run.
    /// </summary>
    private async Task<ExecutionSnapshot> SnapshotAsync(long executionId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var header = await db.Set<ProvisioningExecution>().AsNoTracking()
            .Where(e => e.Id == executionId)
            .Select(e => new { e.State, e.AdminActivation })
            .FirstAsync(ct);

        var steps = await db.Set<ProvisioningStep>().AsNoTracking()
            .Where(s => s.ExecutionId == executionId)
            .Select(s => new { s.StepCode, s.Status, s.AttemptCount })
            .ToListAsync(ct);

        return new ExecutionSnapshot(
            header.State,
            header.AdminActivation,
            steps.ToDictionary(s => s.StepCode, s => new StepSnapshot(s.Status, s.AttemptCount),
                StringComparer.Ordinal));
    }

    private async Task<ProvisioningExecution> LoadAsync(long executionId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        return await db.Set<ProvisioningExecution>().AsNoTracking()
            .Include(e => e.Steps)
            .FirstAsync(e => e.Id == executionId, ct);
    }

    // ---- failure classification -------------------------------------------------------------

    /// <summary>
    /// Turns an exception into the three things the operator and the runner both need: a stable
    /// code, a sentence, and a verdict on whether trying again could ever help.
    ///
    /// <para>Getting the verdict wrong is expensive in both directions. A terminal condition
    /// retried is a hundred identical log lines burying the real message; a transient condition
    /// marked terminal is an operator told to edit a request that was perfectly correct.</para>
    /// </summary>
    private static (string Code, string Reason, bool IsTerminal) Describe(Exception exception)
    {
        if (exception is ProvisioningStepException step)
            return (step.FailureCode ?? nameof(ProvisioningStepException), Truncate(step.Message), step.IsTerminal);

        // DbException.SqlState is provider-agnostic since .NET 5, so the two SQLSTATEs that
        // matter operationally are readable without a Npgsql reference and without a
        // provider-specific catch that SQLite would never satisfy.
        var sqlState = FindSqlState(exception);
        return sqlState switch
        {
            // 23505 unique_violation: something already owns this slug, code or address. Every
            // step re-reads before it writes, so reaching a unique violation means the row was
            // created by somebody else — retrying loses the same race again.
            "23505" => ("23505", Truncate(
                "A uniqueness constraint refused this write: the workspace address, business unit " +
                "code or administrator email is already in use. " + exception.Message), true),

            // 42501 insufficient_privilege: the execution role lacks a grant. Real, and found in
            // production before — a table created after the point-in-time schema grant is
            // unreadable by every runtime role while SQLite tests stay green, because SQLite has
            // no roles. Terminal because no number of retries adds a GRANT.
            "42501" => ("42501", Truncate(
                "The database refused this write for lack of privilege (42501). A grant is missing " +
                "for the provisioning execution role; this cannot be fixed by retrying. " +
                exception.Message), true),

            // 23503 foreign_key_violation: something the step depends on was deleted underneath
            // it. Terminal for the same reason as a dangling recorded id.
            "23503" => ("23503", Truncate(
                "A referenced record no longer exists (23503). " + exception.Message), true),

            _ => (exception.GetType().Name, Truncate(exception.Message), false)
        };
    }

    private static string? FindSqlState(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is DbException { SqlState: { Length: > 0 } sqlState })
                return sqlState;

        return null;
    }

    private static string Truncate(string value)
        => value.Length <= 2000 ? value : value[..2000];
}
