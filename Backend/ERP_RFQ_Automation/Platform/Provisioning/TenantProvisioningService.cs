using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>How a submit was resolved. Maps one-for-one onto the controller's status codes.</summary>
public enum ProvisioningSubmitOutcome
{
    /// <summary>A new execution was created and queued. 202.</summary>
    Created,

    /// <summary>The same key and the same payload had already been accepted; this is the
    /// ORIGINAL execution, unchanged and un-rerun. 200.</summary>
    Replayed,

    /// <summary>The request itself is wrong. 400.</summary>
    Invalid,

    /// <summary>The workspace address is refused. 400, with the reason named.</summary>
    SlugRefused,

    /// <summary>Something already owns this key, address or administrator. 409.</summary>
    Conflict
}

/// <param name="GeneratedPassword">
/// Present only on <see cref="ProvisioningSubmitOutcome.Created"/>, on the password path, when
/// the operator supplied none of their own. Never on a replay: only a BCrypt hash is stored, so
/// there is nothing to return — and if there were, an idempotency key would have become a
/// credential-retrieval endpoint.
/// </param>
public sealed record ProvisioningSubmitResult(
    ProvisioningSubmitOutcome Outcome,
    ProvisioningExecution? Execution,
    string? GeneratedPassword = null,
    string? Error = null,
    SlugRefusalReason SlugReason = SlugRefusalReason.None)
{
    /// <summary>
    /// A record's compiler-generated ToString prints EVERY property, so the single most natural
    /// line anyone could write — <c>_logger.LogInformation("provisioned {Result}", result)</c> —
    /// would put a live founding-administrator password into the log aggregator, which is exactly
    /// the place the invite path exists to keep credentials out of. Redacted at the source rather
    /// than by trusting every future call site to remember, matching the same override on
    /// <c>IssuedTenantAdminInvitation</c>. No call site logs this today; the point is that one can
    /// be added without anybody noticing.
    /// </summary>
    public override string ToString() =>
        $"ProvisioningSubmitResult {{ Outcome = {Outcome}, ExecutionId = {Execution?.Id}, "
        + $"GeneratedPassword = {(GeneratedPassword is null ? "none" : "[redacted]")}, "
        + $"Error = {Error}, SlugReason = {SlugReason} }}";
}

/// <summary>Why a retry or a cancel was refused, so the controller can pick a status code.</summary>
public enum ProvisioningCommandOutcome
{
    Accepted,
    NotFound,

    /// <summary>The execution is in a state this command does not apply to. 409.</summary>
    InvalidState,

    /// <summary>A runner currently holds the lease. 409 — wait, or wait for the lease to lapse.</summary>
    Busy,

    /// <summary>The named step is not one this execution has. 400.</summary>
    UnknownStep
}

public sealed record ProvisioningCommandResult(
    ProvisioningCommandOutcome Outcome, ProvisioningExecution? Execution, string? Error = null);

/// <summary>
/// Accepting, replaying, retrying and abandoning provisioning work. Everything that decides
/// WHETHER work happens; <see cref="ITenantProvisioningRunner"/> decides how.
/// </summary>
public interface ITenantProvisioningService
{
    Task<ProvisioningSubmitResult> SubmitAsync(
        ProvisionTenantRequest request, string? idempotencyKey, string actor,
        long? actorPlatformUserId, CancellationToken ct = default);

    Task<ProvisioningExecution?> GetAsync(long executionId, CancellationToken ct = default);

    Task<IReadOnlyList<ProvisioningExecution>> ListAsync(
        ProvisioningExecutionState? state, int take, CancellationToken ct = default);

    /// <summary>
    /// Puts a failed execution back in the queue, optionally rewinding a single step.
    ///
    /// <para>Rewinding one step does NOT rewind the ones after it: an execution that failed at the
    /// baseline seed has nothing after it that succeeded, and an execution where an operator
    /// wants to re-run an EARLIER step is asking for a repair, not a replay. The runner still
    /// walks every step in order and skips the ones already done, so a rewound step is re-run and
    /// everything behind it is left exactly as it is.</para>
    /// </summary>
    Task<ProvisioningCommandResult> RetryAsync(
        long executionId, string? stepCode, string actor, CancellationToken ct = default);

    Task<ProvisioningCommandResult> CancelAsync(
        long executionId, string reason, string actor, CancellationToken ct = default);

    /// <summary>Answers the wizard's live "may I have this address?" without writing anything.</summary>
    Task<SlugAvailabilityDto> CheckSlugAsync(
        string? requestedSlug, string? tenantName, CancellationToken ct = default);
}

public sealed class TenantProvisioningService : ITenantProvisioningService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly ProvisioningRunSignal _signal;
    private readonly ILogger<TenantProvisioningService> _log;

    public TenantProvisioningService(
        ErpRfqAutomationContext db, ProvisioningRunSignal signal,
        ILogger<TenantProvisioningService> log)
    {
        _db = db;
        _signal = signal;
        _log = log;
    }

    public async Task<ProvisioningSubmitResult> SubmitAsync(
        ProvisionTenantRequest request, string? idempotencyKey, string actor,
        long? actorPlatformUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = DateTime.UtcNow;
        var fingerprint = ProvisioningRequestCanonicalizer.Fingerprint(request);

        // The replay check comes FIRST, before validation. A caller retrying a request that was
        // already accepted must get their original result even if the rules have tightened since
        // — refusing a replay on a rule that did not exist when the work was accepted would leave
        // them with a tenant they cannot see and an error saying it was never created.
        var key = Normalize(idempotencyKey);
        if (key is not null
            && await FindByKeyAsync(key, ct) is ProvisioningExecution existing)
        {
            if (!string.Equals(existing.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                return new ProvisioningSubmitResult(
                    ProvisioningSubmitOutcome.Conflict, existing,
                    Error: $"Idempotency key '{key}' was already used for a DIFFERENT request " +
                           $"(execution {existing.Id}, workspace address '{existing.Slug}'). Reusing " +
                           "a key with changed content would either silently discard the change or " +
                           "silently provision the wrong tenant, so it is refused. Send a new key.");

            return new ProvisioningSubmitResult(ProvisioningSubmitOutcome.Replayed, existing);
        }

        if (ProvisioningRequestValidator.Validate(request, now) is string validationError)
            return new ProvisioningSubmitResult(
                ProvisioningSubmitOutcome.Invalid, null, Error: validationError);

        var verdict = ReservedTenantSlugs.Evaluate(request.Slug, request.Name);
        if (!verdict.IsAccepted)
            return new ProvisioningSubmitResult(
                ProvisioningSubmitOutcome.SlugRefused, null,
                Error: verdict.Message, SlugReason: verdict.Reason);

        var slug = verdict.Slug!;
        var adminEmail = request.AdminEmail.Trim();

        // Pre-checks for the ERROR MESSAGE, not for the guarantee. The guarantees are the unique
        // indexes below and in platform."Tenants" / "Users"; these reads exist so the operator is
        // told which field is the problem instead of receiving a rolled-back transaction.
        if (await _db.Set<ProvisioningExecution>().AsNoTracking()
                .Where(e => e.Slug == slug
                            && (e.State == ProvisioningExecutionState.Pending
                                || e.State == ProvisioningExecutionState.Running
                                || e.State == ProvisioningExecutionState.Failed))
                .Select(e => new { e.Id, e.State })
                .FirstOrDefaultAsync(ct) is { } live)
            return new ProvisioningSubmitResult(
                ProvisioningSubmitOutcome.Conflict, null,
                Error: $"Provisioning for '{slug}' is already in progress (execution {live.Id}, " +
                       $"state {live.State}). Open it to see where it is; retry or cancel it before " +
                       "submitting the same workspace address again.");

        if (await _db.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, ct))
            return new ProvisioningSubmitResult(
                ProvisioningSubmitOutcome.Conflict, null,
                Error: $"A tenant with workspace address '{slug}' already exists.");

        // Users.Email is GLOBALLY unique — one address, one account, one tenant.
        if (await _db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == adminEmail, ct))
            return new ProvisioningSubmitResult(
                ProvisioningSubmitOutcome.Conflict, null,
                Error: $"A user with email '{adminEmail}' already exists. One email address maps to " +
                       "one account on one tenant; use a different address for this tenant's " +
                       "administrator.");

        var activation = ProvisioningRequestValidator.ResolveActivation(request);
        var invited = activation == AdminActivationMethods.Invite;

        // On the invite path NOBODY holds a credential for this account — not the customer, who
        // has not chosen one yet, and deliberately not the operator either. The column is
        // non-nullable, so it is filled with the hash of a discarded random value: unusable by
        // construction, and it cannot be brute-forced into something that logs in because no
        // plaintext for it exists anywhere.
        string? generatedPassword = null;
        string passwordToHash;
        if (invited)
            passwordToHash = ProvisioningInitialCredential.Generate() + ProvisioningInitialCredential.Generate();
        else if (!string.IsNullOrEmpty(request.AdminPassword))
            passwordToHash = request.AdminPassword;
        else
        {
            generatedPassword = ProvisioningInitialCredential.Generate();
            passwordToHash = generatedPassword;
        }

        // Hashed ONCE, here, and stored. BCrypt mints a fresh salt per call, so hashing inside a
        // retriable step would give two attempts two different stored hashes — and a customer who
        // had already activated against the first would find their credential silently
        // invalidated by a retry that was supposed to be a repair.
        var passwordHash = BCrypt.Net.BCrypt.HashPassword(passwordToHash);

        var execution = new ProvisioningExecution
        {
            // Minted when the caller sends none, so every execution is addressable by key even
            // though a caller who omitted one gets no replay protection from it.
            IdempotencyKey = key ?? $"srv-{Guid.NewGuid():N}",
            RequestFingerprint = fingerprint,
            RequestPayload = ProvisioningRequestCanonicalizer.Redact(request),
            AdminPasswordHash = passwordHash,
            Slug = slug,
            Name = request.Name.Trim(),
            AdminEmail = adminEmail,
            AdminActivation = activation,
            State = ProvisioningExecutionState.Pending,
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedBy = actor,
            RequestedByPlatformUserId = actorPlatformUserId,
            CreatedOn = now
        };

        foreach (var definition in ProvisioningStepCatalog.All)
            execution.Steps.Add(new ProvisioningStep
            {
                StepCode = definition.Code,
                Ordinal = definition.Ordinal,
                Status = ProvisioningStepStatus.Pending
            });

        var strategy = _db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                // A failed attempt can leave generated keys tracked even though its transaction
                // rolled back; the graph is small and rebuilt from the same detached instance,
                // so clearing is safe and re-adding restores the whole aggregate.
                _db.ChangeTracker.Clear();
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                _db.Set<ProvisioningExecution>().Add(execution);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (DbUpdateException exception)
        {
            // The unique indexes are the real guard, and this is what it feels like when they
            // fire: two identical submits raced past the reads above. The loser re-reads the
            // winner's execution and replays it, which is exactly what the caller wanted.
            _log.LogWarning(exception,
                "Provisioning submit for '{Slug}' lost a uniqueness race; resolving to the winner.", slug);

            _db.ChangeTracker.Clear();
            if (key is not null && await FindByKeyAsync(key, ct) is ProvisioningExecution winner)
                return string.Equals(winner.RequestFingerprint, fingerprint, StringComparison.Ordinal)
                    ? new ProvisioningSubmitResult(ProvisioningSubmitOutcome.Replayed, winner)
                    : new ProvisioningSubmitResult(
                        ProvisioningSubmitOutcome.Conflict, winner,
                        Error: $"Idempotency key '{key}' was already used for a different request.");

            return new ProvisioningSubmitResult(
                ProvisioningSubmitOutcome.Conflict, null,
                Error: $"Provisioning for '{slug}' is already in progress. Open the existing " +
                       "execution rather than submitting again.");
        }

        // Wake the runner so the operator's first poll already shows a step in flight. A missed
        // signal costs one poll interval and nothing else — the queue is the table, not this.
        _signal.Poke();

        return new ProvisioningSubmitResult(
            ProvisioningSubmitOutcome.Created, execution, generatedPassword);
    }

    public async Task<ProvisioningExecution?> GetAsync(long executionId, CancellationToken ct = default)
        => await _db.Set<ProvisioningExecution>().AsNoTracking()
            .Include(e => e.Steps)
            .FirstOrDefaultAsync(e => e.Id == executionId, ct);

    public async Task<IReadOnlyList<ProvisioningExecution>> ListAsync(
        ProvisioningExecutionState? state, int take, CancellationToken ct = default)
    {
        var query = _db.Set<ProvisioningExecution>().AsNoTracking().Include(e => e.Steps).AsQueryable();
        if (state is { } wanted)
            query = query.Where(e => e.State == wanted);

        return await query
            .OrderByDescending(e => e.CreatedOn).ThenByDescending(e => e.Id)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);
    }

    public async Task<ProvisioningCommandResult> RetryAsync(
        long executionId, string? stepCode, string actor, CancellationToken ct = default)
    {
        var normalizedStep = Normalize(stepCode);
        if (normalizedStep is not null && !ProvisioningStepCodes.IsKnown(normalizedStep))
            return new ProvisioningCommandResult(
                ProvisioningCommandOutcome.UnknownStep, null,
                $"'{normalizedStep}' is not a provisioning step. Valid steps: " +
                $"{string.Join(", ", ProvisioningStepCodes.Ordered)}.");

        var strategy = _db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var execution = await _db.Set<ProvisioningExecution>()
                .Include(e => e.Steps)
                .FirstOrDefaultAsync(e => e.Id == executionId, ct);
            if (execution is null)
                return new ProvisioningCommandResult(ProvisioningCommandOutcome.NotFound, null);

            var now = DateTime.UtcNow;
            if (execution.IsLeasedAt(now))
                return new ProvisioningCommandResult(
                    ProvisioningCommandOutcome.Busy, execution,
                    $"A runner is working on execution {execution.Id} right now (lease held until " +
                    $"{execution.LeaseUntil:O}). Wait for it to finish or fail before retrying.");

            // A cancelled execution never runs again by any route: an operator decided the attempt
            // was abandoned, and quietly resurrecting it under a retry button would undo that.
            if (execution.State == ProvisioningExecutionState.Cancelled)
                return new ProvisioningCommandResult(
                    ProvisioningCommandOutcome.InvalidState, execution,
                    $"Execution {execution.Id} was cancelled and will not run again. Submit a new " +
                    "request instead.");

            // A SUCCEEDED execution may have ONE step re-run, but not the whole thing.
            //
            // The distinction is the difference between a repair and a nonsense. Re-running the
            // whole provision of a live tenant means nothing — everything already exists. Re-running
            // one step is the operational reality: the customer never received their activation
            // email, or somebody deleted a baseline row and the workspace needs its gap filled.
            // Every step was written to be safe to run against a tenant that already exists, which
            // is what makes this an offer rather than a hazard.
            if (execution.State == ProvisioningExecutionState.Succeeded && normalizedStep is null)
                return new ProvisioningCommandResult(
                    ProvisioningCommandOutcome.InvalidState, execution,
                    $"Execution {execution.Id} already completed and the tenant it created is active, " +
                    "so there is nothing to resume. Name a single step to re-run it as a repair.");

            if (normalizedStep is not null)
            {
                var target = execution.Steps.FirstOrDefault(s => s.StepCode == normalizedStep);
                if (target is null)
                    return new ProvisioningCommandResult(
                        ProvisioningCommandOutcome.UnknownStep, execution,
                        $"Execution {execution.Id} has no step '{normalizedStep}'.");

                // Rewound to Pending, keeping AttemptCount. The count is what tells the runner
                // this is a RE-run, which is what makes the invitation step compensate instead of
                // minting a second live activation link.
                target.Status = ProvisioningStepStatus.Pending;
                target.FailureCode = null;
                target.FailureReason = null;
                target.CompletedOn = null;
            }

            // The reset is the whole retry: the runner walks every step in order and skips the
            // ones that already succeeded, so an execution put back to Pending resumes exactly
            // where it stopped without re-doing committed work.
            execution.State = ProvisioningExecutionState.Pending;
            execution.FailedStep = null;
            execution.FailureReason = null;
            execution.FailureIsTerminal = false;
            execution.CompletedOn = null;
            // The runner's own attempt budget is reset by a human deciding the underlying cause
            // is fixed. Without this, an execution that exhausted MaxAutomaticAttempts could
            // never be picked up again no matter how many times the operator pressed retry.
            execution.AttemptCount = 0;
            execution.LeaseOwner = null;
            execution.LeaseToken = null;
            execution.LeaseUntil = null;
            execution.Version++;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogInformation(
                "Provisioning execution {ExecutionId} was retried by {Actor}{Step}.",
                executionId, actor, normalizedStep is null ? string.Empty : $" from step {normalizedStep}");

            return new ProvisioningCommandResult(ProvisioningCommandOutcome.Accepted, execution);
        });

        if (result.Outcome == ProvisioningCommandOutcome.Accepted)
            _signal.Poke();

        return result;
    }

    public async Task<ProvisioningCommandResult> CancelAsync(
        long executionId, string reason, string actor, CancellationToken ct = default)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var execution = await _db.Set<ProvisioningExecution>()
                .Include(e => e.Steps)
                .FirstOrDefaultAsync(e => e.Id == executionId, ct);
            if (execution is null)
                return new ProvisioningCommandResult(ProvisioningCommandOutcome.NotFound, null);

            if (execution.State == ProvisioningExecutionState.Succeeded)
                return new ProvisioningCommandResult(
                    ProvisioningCommandOutcome.InvalidState, execution,
                    "This execution has already completed; the tenant exists and is active. Use the " +
                    "tenant lifecycle endpoints to suspend or archive it.");

            if (execution.State == ProvisioningExecutionState.Cancelled)
                return new ProvisioningCommandResult(
                    ProvisioningCommandOutcome.InvalidState, execution,
                    "This execution was already cancelled.");

            // A live lease is refused rather than raced. Cancelling under a runner that is
            // mid-step would leave the step's transaction to commit into an execution the
            // database says is finished, and the runner checks for cancellation between steps
            // anyway — so the honest answer is "in a moment", not a silent overwrite.
            if (execution.IsLeasedAt(DateTime.UtcNow))
                return new ProvisioningCommandResult(
                    ProvisioningCommandOutcome.Busy, execution,
                    $"A runner holds execution {execution.Id} until {execution.LeaseUntil:O}. It " +
                    "stops at the next step boundary; try again in a moment.");

            var now = DateTime.UtcNow;
            execution.State = ProvisioningExecutionState.Cancelled;
            execution.CancelledBy = actor;
            execution.CancellationReason = reason.Trim();
            execution.CompletedOn = now;
            execution.CurrentStep = null;
            execution.LeaseOwner = null;
            execution.LeaseToken = null;
            execution.LeaseUntil = null;
            execution.Version++;

            // Steps that never ran are marked Cancelled rather than left Pending, so the console
            // shows "this was abandoned" instead of a queue that looks like it is still moving.
            // Steps that SUCCEEDED keep their status: cancellation does not undo committed work
            // and must not claim to.
            foreach (var step in execution.Steps.Where(s =>
                         s.Status is ProvisioningStepStatus.Pending or ProvisioningStepStatus.Failed))
                step.Status = ProvisioningStepStatus.Cancelled;

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogWarning(
                "Provisioning execution {ExecutionId} for '{Slug}' was cancelled by {Actor}: {Reason}. " +
                "Tenant {TenantId} and business unit {ProvisionedBusinessUnitId} (where created) remain and stay " +
                "in Provisioning status; cancellation abandons the attempt, it does not delete rows.",
                executionId, execution.Slug, actor, execution.CancellationReason,
                execution.TenantId, execution.ProvisionedBusinessUnitId);

            return new ProvisioningCommandResult(ProvisioningCommandOutcome.Accepted, execution);
        });
    }

    public async Task<SlugAvailabilityDto> CheckSlugAsync(
        string? requestedSlug, string? tenantName, CancellationToken ct = default)
    {
        var verdict = ReservedTenantSlugs.Evaluate(requestedSlug, tenantName);
        if (!verdict.IsAccepted)
            return new SlugAvailabilityDto
            {
                Slug = ReservedTenantSlugs.Slugify(
                    string.IsNullOrWhiteSpace(requestedSlug) ? tenantName : requestedSlug),
                IsAvailable = false,
                Reason = verdict.Reason.ToString(),
                Message = verdict.Message
            };

        var slug = verdict.Slug!;

        if (await _db.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, ct))
            return new SlugAvailabilityDto
            {
                Slug = slug,
                IsAvailable = false,
                Reason = nameof(SlugRefusalReason.None),
                Message = $"'{slug}' is already taken by another tenant."
            };

        if (await _db.Set<ProvisioningExecution>().AsNoTracking()
                .AnyAsync(e => e.Slug == slug
                               && (e.State == ProvisioningExecutionState.Pending
                                   || e.State == ProvisioningExecutionState.Running
                                   || e.State == ProvisioningExecutionState.Failed), ct))
            return new SlugAvailabilityDto
            {
                Slug = slug,
                IsAvailable = false,
                Reason = nameof(SlugRefusalReason.None),
                Message = $"'{slug}' is claimed by a provisioning attempt that has not finished."
            };

        return new SlugAvailabilityDto
        {
            Slug = slug,
            IsAvailable = true,
            Reason = nameof(SlugRefusalReason.None)
        };
    }

    private Task<ProvisioningExecution?> FindByKeyAsync(string key, CancellationToken ct)
        => _db.Set<ProvisioningExecution>().AsNoTracking()
            .Include(e => e.Steps)
            .FirstOrDefaultAsync(e => e.IdempotencyKey == key, ct);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
