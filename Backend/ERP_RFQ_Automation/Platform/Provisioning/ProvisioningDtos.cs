using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// A submit. The tenant request itself is unchanged — it is the SAME
/// <see cref="ProvisionTenantRequest"/> the synchronous endpoint takes, deliberately, so the
/// console's wizard, its validation and its tests carry over field for field.
/// </summary>
public sealed class SubmitProvisioningRequest
{
    [Required]
    public ProvisionTenantRequest Tenant { get; set; } = null!;

    /// <summary>
    /// The caller's replay guard. May also be supplied as the <c>Idempotency-Key</c> header,
    /// which is what an HTTP client library will do automatically; the header wins when both are
    /// present and disagree is impossible because the header is copied here before validation.
    ///
    /// <para>Optional, but strongly recommended and the console always sends one. Without it the
    /// only protection against a double submit is the live-slug guard, which catches the same
    /// tenant twice but cannot recognise a retry as a retry — so the caller gets a 409 where they
    /// should have got their original result back.</para>
    /// </summary>
    [StringLength(200, MinimumLength = 8)]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// The draft this was submitted from, when the operator came through the wizard's save/resume
    /// path. Stamped onto the draft so the console can retire it without losing what was typed.
    /// </summary>
    public long? FromDraftId { get; set; }
}

/// <summary>
/// One step, as the console's progress list renders it. Everything an operator needs to answer
/// "what is it doing, what broke, and can I do anything about it?".
/// </summary>
public sealed class ProvisioningStepDto
{
    public string Step { get; set; } = null!;

    /// <summary>Sentence-case label. Server-side so every client says the same thing.</summary>
    public string Label { get; set; } = null!;

    public int Ordinal { get; set; }

    public string Status { get; set; } = null!;

    public int AttemptCount { get; set; }

    public DateTime? StartedOn { get; set; }

    public DateTime? CompletedOn { get; set; }

    public int? DurationMs { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>What the step produced, as raw JSON. Row counts, ids, expiries — the evidence a
    /// green tick is backed by real rows.</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// False when re-running this step would duplicate something rather than repair it. The
    /// console must not offer a retry button that makes the situation worse; see
    /// <c>ProvisioningStepCatalog</c> for which step this is and what it does instead.
    /// </summary>
    public bool IsRetriable { get; set; }
}

/// <summary>The whole attempt, as the console polls it.</summary>
public sealed class ProvisioningExecutionDto
{
    public long Id { get; set; }

    public string State { get; set; } = null!;

    public string Slug { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string AdminEmail { get; set; } = null!;

    public string AdminActivation { get; set; } = null!;

    /// <summary>The step in flight right now. Null when nothing is running.</summary>
    public string? CurrentStep { get; set; }

    public string? FailedStep { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>
    /// True when retrying is pointless — the slug or the address now belongs to somebody else,
    /// the plan was deactivated. The console offers "edit and resubmit", not "try again".
    /// </summary>
    public bool FailureIsTerminal { get; set; }

    public long? TenantId { get; set; }

    public long? ProvisionedBusinessUnitId { get; set; }

    public long? FoundingUserId { get; set; }

    public string CorrelationId { get; set; } = null!;

    public string RequestedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public DateTime? StartedOn { get; set; }

    public DateTime? CompletedOn { get; set; }

    public int AttemptCount { get; set; }

    public string? CancelledBy { get; set; }

    public string? CancellationReason { get; set; }

    public List<ProvisioningStepDto> Steps { get; set; } = [];

    /// <summary>Steps finished, over steps that will run. Drives the progress bar without the
    /// client having to know which steps are skipped on which activation path.</summary>
    public int CompletedStepCount { get; set; }

    public int TotalStepCount { get; set; }
}

/// <summary>
/// What submit returns. The execution plus — exactly once, in this response and nowhere else —
/// the one-time secrets.
/// </summary>
public sealed class SubmitProvisioningResponse
{
    public ProvisioningExecutionDto Execution { get; set; } = null!;

    /// <summary>
    /// True when this call created the execution; false when an identical request had already
    /// been accepted under the same key and this is the original result being replayed.
    ///
    /// <para>The console shows the same screen either way — that is the entire point — but the
    /// distinction matters for a script that wants to know whether it actually did anything.</para>
    /// </summary>
    public bool Created { get; set; }

    /// <summary>
    /// The generated initial password, on the password path with no supplied credential. Present
    /// ONLY on the call that created the execution.
    ///
    /// <para>A replay returns null here even for the same key, and that is deliberate rather than
    /// an omission: only a BCrypt hash is stored, so there is nothing to return, and if there
    /// were, an idempotency key would have become a credential-retrieval endpoint.</para>
    /// </summary>
    public string? GeneratedPassword { get; set; }

    /// <summary>
    /// Explains a null <see cref="GeneratedPassword"/> or activation link on a replay, so the
    /// operator reads "already issued, not retrievable" instead of assuming it failed.
    /// </summary>
    public string? SecretNotice { get; set; }
}

/// <summary>Retry one step, or resume the whole execution.</summary>
public sealed class RetryProvisioningRequest
{
    /// <summary>
    /// A single step code to retry. Omitted means "resume": the runner picks up at the first
    /// step that has not succeeded and carries on, which is what an operator wants after fixing
    /// whatever the environment was doing wrong.
    /// </summary>
    [StringLength(64)]
    public string? Step { get; set; }

    /// <summary>
    /// Why. Required, and audited. A retry re-executes privileged writes against a customer's
    /// data, and "somebody clicked it again" is not an acceptable answer three months later.
    /// </summary>
    [Required, StringLength(1000, MinimumLength = 5)]
    public string Reason { get; set; } = null!;
}

/// <summary>Abandon an attempt.</summary>
public sealed class CancelProvisioningRequest
{
    [Required, StringLength(1000, MinimumLength = 5)]
    public string Reason { get; set; } = null!;
}

/// <summary>Save a half-finished wizard.</summary>
public sealed class SaveProvisioningDraftRequest
{
    /// <summary>The operator's label. Defaults to the tenant name in the payload when omitted.</summary>
    [StringLength(256)]
    public string? Name { get; set; }

    /// <summary>The partially completed request. Every field is optional at this stage — that is
    /// what makes it a draft.</summary>
    [Required]
    public ProvisionTenantRequest Payload { get; set; } = null!;

    /// <summary>
    /// The version last loaded. Required on update so two tabs open on one draft cannot silently
    /// overwrite each other; omit it only when creating.
    /// </summary>
    public long? Version { get; set; }
}

/// <summary>A saved draft, as the console lists and loads it.</summary>
public sealed class ProvisioningDraftDto
{
    public long Id { get; set; }

    public string Name { get; set; } = null!;

    public string OwnerEmail { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public DateTime UpdatedOn { get; set; }

    public long Version { get; set; }

    public long? SubmittedExecutionId { get; set; }

    /// <summary>Populated on load, omitted on list — a list of twenty drafts should not carry
    /// twenty full company profiles.</summary>
    public ProvisionTenantRequest? Payload { get; set; }
}

/// <summary>
/// The wizard's live address check. Answers "may I have this?" before the operator has filled in
/// anything else, which is the only moment changing it is free.
/// </summary>
public sealed class SlugAvailabilityDto
{
    /// <summary>The normalised address the request would actually get.</summary>
    public string? Slug { get; set; }

    public bool IsAvailable { get; set; }

    /// <summary>One of <see cref="SlugRefusalReason"/>, as a name. "None" when available.</summary>
    public string Reason { get; set; } = null!;

    /// <summary>Operator-facing explanation. Null when available.</summary>
    public string? Message { get; set; }
}
