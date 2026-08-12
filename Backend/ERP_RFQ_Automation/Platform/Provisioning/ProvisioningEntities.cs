namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// Where a provisioning attempt has got to, as a whole.
///
/// <para><b>Names, not ordinals.</b> Stored as strings for the same reason
/// <c>Tenant.BillingMode</c> is: an execution row is read months later during a support
/// investigation, and reordering this enum must not silently reclassify a historical failure
/// into a success.</para>
/// </summary>
public enum ProvisioningExecutionState
{
    /// <summary>Accepted and journalled; no step has been attempted yet.</summary>
    Pending,

    /// <summary>A runner holds the lease and is working through the steps.</summary>
    Running,

    /// <summary>Every step reached a terminal success (or was legitimately skipped) and the
    /// tenant has been moved out of <c>Provisioning</c> into <c>Active</c>.</summary>
    Succeeded,

    /// <summary>A step failed. The execution keeps everything earlier steps committed and is
    /// retriable — this is a resting state, not a dead one.</summary>
    Failed,

    /// <summary>An operator abandoned the attempt. Nothing further will run.</summary>
    Cancelled
}

/// <summary>
/// Where one step of a provisioning attempt has got to.
///
/// <para><see cref="Skipped"/> is a first-class outcome rather than an absence: the invitation
/// step does not run on the password activation path, and a console that showed a blank row
/// there would look identical to a step that silently never started.</para>
/// </summary>
public enum ProvisioningStepStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,

    /// <summary>Deliberately not applicable to this request — recorded so it reads as a decision.</summary>
    Skipped,

    /// <summary>Never attempted because the execution was cancelled first.</summary>
    Cancelled
}

/// <summary>
/// The nine things provisioning does, in the order it does them.
///
/// <para><b>Why constants and not an enum.</b> These codes are the contract between the server
/// and the console's progress list, and they appear verbatim in audit metadata and in the
/// operator's retry request. A string that a human can read in a log line without a lookup table
/// is worth more here than the compiler's help, and the set is closed by
/// <see cref="Ordered"/> rather than by the type system.</para>
/// </summary>
public static class ProvisioningStepCodes
{
    /// <summary>The <c>platform.Tenants</c> row itself, created in <c>Provisioning</c> status.</summary>
    public const string Tenant = "tenant";

    /// <summary>The primary <c>BusinessUnits</c> row — the tenant's actual data boundary.</summary>
    public const string BusinessUnit = "business-unit";

    /// <summary>The lead/RFQ/quote/order status catalogue every commercial screen reads.</summary>
    public const string LifecycleStatuses = "lifecycle-statuses";

    /// <summary>The tenant's AI processing policy. On PostgreSQL a trigger writes this row.</summary>
    public const string AiPolicy = "ai-policy";

    /// <summary>The SUPER_ADMIN role the founding administrator holds.</summary>
    public const string FoundingRole = "founding-role";

    /// <summary>The founding administrator's <c>Users</c> row.</summary>
    public const string FoundingAdmin = "founding-admin";

    /// <summary>Currencies, units of measure, quote configuration, starter roles and grants.</summary>
    public const string BaselineSeed = "baseline-seed";

    /// <summary>
    /// The tenant's data boundaries, registered from the platform manifest and — for the primary
    /// PostgreSQL scope only — verified against a recorded probe of the running system.
    ///
    /// <para>A no-op on a deployment that declares no manifest, which is how the manual
    /// register-then-verify path survives untouched where nobody has described their estate.</para>
    /// </summary>
    public const string DataBoundaries = "data-boundaries";

    /// <summary>The single-use activation link. Skipped on the password path.</summary>
    public const string Invitation = "invitation";

    /// <summary>
    /// Execution order. The runner walks this list and stops at the first step that does not
    /// reach a terminal success, so the order here IS the dependency graph: nothing later can
    /// run before everything earlier has committed.
    /// </summary>
    public static readonly IReadOnlyList<string> Ordered =
    [
        Tenant, BusinessUnit, LifecycleStatuses, AiPolicy,
        FoundingRole, FoundingAdmin, BaselineSeed, DataBoundaries, Invitation
    ];

    public static bool IsKnown(string? code)
        => code is not null && Ordered.Contains(code, StringComparer.Ordinal);
}

/// <summary>
/// One attempt to provision one tenant, and the record that makes that attempt survivable.
///
/// <para><b>The state this replaces.</b> Provisioning was a single HTTP request wrapping a single
/// database transaction that did seven things. A failure anywhere rolled all seven back and
/// surfaced as a bare 500, so nobody could say which step failed or why; a timeout left the
/// operator unable to tell a slow success from a silent failure; there was no way to retry the
/// one step that broke; and a double-click attempted a second, complete provision that only the
/// slug and email unique indexes happened to stop — by accident of schema rather than by
/// design.</para>
///
/// <para><b>Each step now owns its own transaction, deliberately.</b> That is a weaker atomicity
/// guarantee than one big transaction and a much stronger operational one: partial progress is
/// real, is visible, and is resumable. The honesty comes from <c>TenantStatus.Provisioning</c> —
/// the tenant stays in that status until the LAST step succeeds, so a half-provisioned tenant
/// reads as half-provisioned on the tenants screen instead of showing a green Active badge over
/// an empty workspace, which is exactly what the old all-or-nothing design produced whenever the
/// transaction committed the tenant and something downstream was later found missing.</para>
///
/// <para><b>Why the platform schema and no query filter.</b> Like <c>ImpersonationSessions</c> and
/// <c>TenantAdminInvitations</c>, this is a control-plane record ABOUT a tenant, written before
/// the tenant exists at all — there is no business unit to scope it to for most of its life.
/// Carrying no global query filter also keeps it outside the RLS-policy expectation asserted by
/// <c>PostgreSqlProductionDialectTests</c>, which enumerates filtered <c>public</c>-schema tables
/// only.</para>
/// </summary>
public class ProvisioningExecution
{
    public long Id { get; set; }

    /// <summary>
    /// The caller's replay guard. Unique, so a repeated submit is stopped by the database rather
    /// than by a read-then-write race in application code.
    ///
    /// <para>Always populated: when a caller supplies none, the server mints one so every
    /// execution is addressable by key. A caller that omits it gets no replay protection from
    /// this column, which is why <see cref="Slug"/> carries a second, narrower guard.</para>
    /// </summary>
    public string IdempotencyKey { get; set; } = null!;

    /// <summary>
    /// SHA-256 over the canonicalised submitted payload. The point of the key is that the SAME
    /// request may be sent twice; sending a DIFFERENT request under a key that has already been
    /// used is a client bug or a confused operator, and returning the first request's result
    /// would silently provision something nobody asked for. Compared on every replay.
    /// </summary>
    public string RequestFingerprint { get; set; } = null!;

    /// <summary>
    /// The submitted request, verbatim except that <c>adminPassword</c> is removed before it is
    /// written. A resume that happens minutes or hours later needs the whole request to rebuild
    /// its graph, and this is the only copy of it — but a stored plaintext credential would
    /// outlive every guarantee the invitation flow exists to provide. The credential survives as
    /// <see cref="AdminPasswordHash"/> instead, which is what the <c>Users</c> row needs anyway.
    /// </summary>
    public string RequestPayload { get; set; } = null!;

    /// <summary>
    /// BCrypt hash of whatever credential this request settles on — an operator-supplied
    /// password, a generated one, or (on the invite path) the hash of a discarded random value
    /// that no plaintext exists for.
    ///
    /// <para>Computed ONCE, at submit, and reused by every attempt. BCrypt mints a fresh salt per
    /// call, so hashing inside the retriable step would give two attempts two different stored
    /// hashes — and if the customer had already activated against the first, the retry would
    /// invalidate a credential they were successfully using.</para>
    /// </summary>
    public string AdminPasswordHash { get; set; } = null!;

    /// <summary>
    /// The validated, reserved-word-checked slug. Also the natural key of the second replay
    /// guard: a unique index over live executions makes two concurrent attempts on one slug
    /// impossible even when the caller sends no idempotency key.
    /// </summary>
    public string Slug { get; set; } = null!;

    /// <summary>Tenant display name, denormalised so the console's in-flight list needs no join
    /// to a <c>Tenants</c> row that may not exist yet.</summary>
    public string Name { get; set; } = null!;

    /// <summary>Founding administrator's address, for the same reason — and because a failed
    /// execution's most common cause is an address already in use, which the operator has to be
    /// able to see without opening the payload.</summary>
    public string AdminEmail { get; set; } = null!;

    /// <summary>Resolved once at submit: <c>invite</c> or <c>password</c>. Decides whether the
    /// invitation step runs or is recorded as <see cref="ProvisioningStepStatus.Skipped"/>.</summary>
    public string AdminActivation { get; set; } = null!;

    public ProvisioningExecutionState State { get; set; } = ProvisioningExecutionState.Pending;

    /// <summary>
    /// The step the runner is on. Written before the work starts, so an execution that dies with
    /// the process still names the step it died in — the single most useful fact for the operator
    /// staring at a stalled wizard.
    /// </summary>
    public string? CurrentStep { get; set; }

    /// <summary>Set when a step fails; cleared when a retry starts. Names the step, not just the error.</summary>
    public string? FailedStep { get; set; }

    /// <summary>Operator-facing failure summary. Never a raw stack trace and never a secret.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// True when the failure cannot be fixed by retrying — a slug or an email that another
    /// tenant now owns, a plan that was deactivated. The console must offer "edit and resubmit"
    /// rather than a retry button that is guaranteed to fail again.
    /// </summary>
    public bool FailureIsTerminal { get; set; }

    // ---- what the steps produced -------------------------------------------------------
    // Each id is written INSIDE its own step's transaction. That is what makes a retry safe:
    // "did this step commit?" is answered by a column that could only have been set by the same
    // transaction that wrote the row, so there is no window in which the row exists and the
    // execution does not know about it.

    public long? TenantId { get; set; }

    /// <summary>
    /// The business unit this execution CREATED — an outcome, not a scope.
    ///
    /// <para>Deliberately not named <c>BusinessUnitId</c>. That name is the tenant-scope column
    /// throughout this codebase, and <c>TenantIsolationTests</c> sweeps the model for it and fails
    /// any entity carrying one without a global query filter — a guardrail whose opt-out list is
    /// empty and should stay that way. This row is a control-plane record that exists BEFORE the
    /// business unit it names, written by an operator with no tenant scope at all, so there is
    /// nothing to scope it to; filtering it would hide a provisioning attempt from the very
    /// console that has to watch it. The name says which of the two it is.</para>
    /// </summary>
    public long? ProvisionedBusinessUnitId { get; set; }
    public long? FoundingRoleId { get; set; }
    public long? FoundingUserId { get; set; }
    public long? InvitationId { get; set; }

    /// <summary>
    /// The original response, minus every one-time secret. A replayed submit returns THIS rather
    /// than re-running anything.
    ///
    /// <para>The generated password and the activation URL are deliberately absent. They exist
    /// exactly once, in the body of the first response, and a replay that handed them out again
    /// would turn an idempotency key into a credential-retrieval endpoint — the precise property
    /// the invitation design gives up storage for.</para>
    /// </summary>
    public string? ResultPayload { get; set; }

    /// <summary>Ties every audit row, log line and step of this attempt together.</summary>
    public string CorrelationId { get; set; } = null!;

    /// <summary>Platform operator who submitted it, by email.</summary>
    public string RequestedBy { get; set; } = null!;

    /// <summary>Their <c>platform.PlatformUsers</c> id, when the token carried a resolvable one.</summary>
    public long? RequestedByPlatformUserId { get; set; }

    /// <summary>Operator who cancelled, and why. Cancellation is a privileged, audited act.</summary>
    public string? CancelledBy { get; set; }

    public string? CancellationReason { get; set; }

    public DateTime CreatedOn { get; set; }

    /// <summary>First time any step was attempted. Null while the execution is still queued.</summary>
    public DateTime? StartedOn { get; set; }

    /// <summary>When the execution reached a terminal state, whatever that state is.</summary>
    public DateTime? CompletedOn { get; set; }

    /// <summary>How many times a runner has picked this execution up. Distinct from a step's own
    /// attempt count: one execution attempt can advance several steps.</summary>
    public int AttemptCount { get; set; }

    // ---- lease ---------------------------------------------------------------------------
    // Two runners must never work the same execution: the steps are idempotent against
    // themselves but not against a rival writing the same rows at the same instant.

    public string? LeaseOwner { get; set; }

    public Guid? LeaseToken { get; set; }

    /// <summary>
    /// When the lease lapses. A runner whose process died holds a lease nobody can release, so
    /// the lease EXPIRES rather than being owned forever — an execution stuck in
    /// <see cref="ProvisioningExecutionState.Running"/> past this instant is abandoned and may be
    /// claimed by anyone.
    /// </summary>
    public DateTime? LeaseUntil { get; set; }

    /// <summary>
    /// The last instant the holding runner proved it was still alive: written when the lease is
    /// taken and re-written at every step boundary, on the SAME journal transaction that renews
    /// <see cref="LeaseUntil"/>.
    ///
    /// <para><b>Why a separate column when <see cref="LeaseUntil"/> already moves.</b>
    /// <c>LeaseUntil</c> is a deadline derived from configuration, so "how long has this been
    /// quiet?" cannot be read off it without also knowing which <c>LeaseDuration</c> was in force
    /// when it was written — and that value is editable at runtime through
    /// <c>IOptionsMonitor</c>. A recovery decision that hinges on a number somebody may have
    /// changed since is not evidence. This column is an OBSERVATION: a runner was demonstrably
    /// executing at this instant.</para>
    ///
    /// <para><b>Its resolution is one step, not one second.</b> The heartbeat is written between
    /// steps rather than by a timer, so a runner inside a long step looks silent for the length of
    /// that step. That is why the staleness rule (<see cref="ProvisioningLeaseStaleness"/>)
    /// demands BOTH a lapsed lease and a heartbeat older than a grace that can never be shorter
    /// than the configured lease.</para>
    /// </summary>
    public DateTime? LeaseHeartbeatAt { get; set; }

    /// <summary>
    /// How many times ownership of this execution has been taken away from an abandoned attempt
    /// by <c>IProvisioningLeaseRecovery</c>. Distinct from <see cref="AttemptCount"/>: an
    /// execution on its fourth attempt is ordinary, and an execution on its fourth FORCED recovery
    /// is a runner that keeps dying in the same place, which is a different problem with a
    /// different fix.
    /// </summary>
    public int RecoveredAttemptCount { get; set; }

    /// <summary>When ownership was last force-recovered.</summary>
    public DateTime? LastRecoveredOn { get; set; }

    /// <summary>Who recovered it, by email. Recovery is a privileged act and is never anonymous.</summary>
    public string? LastRecoveredBy { get; set; }

    /// <summary>
    /// Why. Demanded rather than optional, and kept on the row as well as in the audit log — the
    /// operator who finds this execution next is reading the execution, not the audit trail.
    /// </summary>
    public string? LastRecoveryReason { get; set; }

    /// <summary>
    /// Optimistic concurrency over the whole row. The lease is the coarse guard; this stops the
    /// finer race where a runner and an operator's cancel land on the row in the same instant.
    /// </summary>
    public long Version { get; set; }

    public virtual ICollection<ProvisioningStep> Steps { get; set; } = new List<ProvisioningStep>();

    /// <summary>Terminal states never run again without an explicit operator action.</summary>
    public bool IsTerminal => State is ProvisioningExecutionState.Succeeded
        or ProvisioningExecutionState.Cancelled;

    /// <summary>
    /// A lease is only meaningful while it is unexpired AND the execution is actually running.
    /// Both halves matter: a Failed execution can carry a stale lease from the attempt that
    /// failed, and treating that as "someone is working on it" would make retry impossible.
    /// </summary>
    public bool IsLeasedAt(DateTime utcNow)
        => State == ProvisioningExecutionState.Running
           && LeaseUntil is { } until && until > utcNow;

    /// <summary>
    /// The most recent instant at which SOMETHING is known to have been happening to this
    /// execution, in descending order of how directly it evidences a live runner.
    ///
    /// <para><see cref="LeaseHeartbeatAt"/> first, because it is the only value written for the
    /// express purpose of saying "still here". <see cref="LeaseUntil"/> next, so rows written
    /// before the heartbeat column existed are still assessable rather than instantly stale — a
    /// backfilled null must never read as "abandoned since 1970". Then the attempt's start, then
    /// the row's creation, so the fallback chain always terminates on a non-null column.</para>
    /// </summary>
    public DateTime LastProgressAt
        => LeaseHeartbeatAt ?? LeaseUntil ?? StartedOn ?? CreatedOn;
}

/// <summary>
/// One step of one execution: what it is, how it went, how long it took and — when it went
/// wrong — why.
///
/// <para><b>Written by two different transactions, on purpose.</b> The <c>Running</c> marker and
/// any <c>Failed</c> verdict are written on a SEPARATE connection from the step's own work. A
/// failure record written inside the step's transaction would be rolled back by the very failure
/// it is recording, which is how the old design ended up with nothing but "Provisioning failed."
/// The success verdict is the opposite case and is written INSIDE the step transaction, so a step
/// can never be marked done by a transaction that then rolled back.</para>
/// </summary>
public class ProvisioningStep
{
    public long Id { get; set; }

    public long ExecutionId { get; set; }

    /// <summary>One of <see cref="ProvisioningStepCodes"/>. Unique within an execution.</summary>
    public string StepCode { get; set; } = null!;

    /// <summary>Position in <see cref="ProvisioningStepCodes.Ordered"/>, denormalised so the
    /// console can render the list in order without knowing the catalogue.</summary>
    public int Ordinal { get; set; }

    public ProvisioningStepStatus Status { get; set; } = ProvisioningStepStatus.Pending;

    /// <summary>
    /// How many times this step has been attempted, across every execution attempt. A step on its
    /// fourth attempt is a different operational picture from an execution on its fourth attempt,
    /// and only this column can tell them apart.
    /// </summary>
    public int AttemptCount { get; set; }

    public DateTime? StartedOn { get; set; }

    public DateTime? CompletedOn { get; set; }

    /// <summary>
    /// Wall-clock duration of the LAST attempt, in milliseconds. Stored rather than derived,
    /// because <see cref="StartedOn"/> is overwritten by each retry and the console's "this step
    /// normally takes 200ms and has been running for 40 seconds" judgement needs the number that
    /// belongs to the attempt in front of it.
    /// </summary>
    public int? DurationMs { get; set; }

    /// <summary>
    /// A short, stable classification of the failure — the exception type, or the PostgreSQL
    /// SQLSTATE where one is available. Machine-readable so the console can special-case the two
    /// that operators actually hit (23505 unique violation, 42501 insufficient privilege).
    /// </summary>
    public string? FailureCode { get; set; }

    /// <summary>Human-readable failure text, truncated. Never carries a token or a password.</summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// What the step produced, as JSON — the baseline seeder's row counts, the invitation's
    /// expiry, the ids created. This is the evidence that a green tick means real rows exist,
    /// which is the claim the old "tenant created successfully" toast could not back up.
    /// </summary>
    public string? Detail { get; set; }

    public virtual ProvisioningExecution Execution { get; set; } = null!;
}

/// <summary>
/// A provisioning wizard saved half-finished.
///
/// <para><b>Why server-side and not browser storage.</b> An operator gathers a customer's legal
/// name, tax number, contract dates and billing terms across more than one sitting, often from
/// more than one machine, and often after a call in which the customer promised to send the
/// missing detail. A draft held in <c>localStorage</c> is lost by a browser profile switch, a
/// cleared cache, or the colleague who picks the account up.</para>
///
/// <para><b>Deliberately holds no secret.</b> <c>adminPassword</c> is REFUSED on the way in
/// rather than stripped, because a caller who sent one believes it was saved. The credential path
/// is separate and stays separate: on the invite path no credential exists at all until the
/// customer chooses one, and on the password path it is supplied at submit and hashed
/// immediately.</para>
/// </summary>
public class ProvisioningDraft
{
    public long Id { get; set; }

    /// <summary>Operator's own label for the draft — usually the prospective customer's name.</summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Email of the operator the draft belongs to. Drafts are private to their author: a
    /// half-filled commercial negotiation is not something every support engineer should be
    /// reading, and a draft nobody owns is a draft nobody maintains.
    /// </summary>
    public string OwnerEmail { get; set; } = null!;

    public long? OwnerPlatformUserId { get; set; }

    /// <summary>The partially completed <c>ProvisionTenantRequest</c>, as JSON.</summary>
    public string Payload { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    /// <summary>Last save. Drives the console's "saved 4 minutes ago" and the pruning of drafts
    /// nobody has touched in months.</summary>
    public DateTime UpdatedOn { get; set; }

    /// <summary>
    /// Set when the draft was submitted, so the console can stop offering a draft whose tenant
    /// already exists without deleting the operator's record of what they filled in.
    /// </summary>
    public long? SubmittedExecutionId { get; set; }

    /// <summary>Optimistic concurrency: two tabs open on one draft must not silently overwrite
    /// each other's fields.</summary>
    public long Version { get; set; }
}
