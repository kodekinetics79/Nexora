using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// Refusal of an offboarding operation, carrying the status code the API should answer with.
///
/// <para>The typed exception is the single source of the message and the status, mirroring
/// <c>TenantAccessDeniedException</c> (P2-A10): a controller that decides its own status codes
/// ends up disagreeing with the service about whether a stale confirmation is a 400 or a 409,
/// and the console builds retry logic on whichever it saw first.</para>
/// </summary>
public sealed class TenantOffboardingRefusedException(string message, int suggestedStatusCode)
    : Exception(message)
{
    /// <summary>The caller sent something wrong — a missing reason, a confirmation that does not
    /// match, a retention window outside the permitted range.</summary>
    public static TenantOffboardingRefusedException BadRequest(string message) => new(message, 400);

    /// <summary>The tenant is not in a state where this operation is legal, or the retention
    /// clock has not elapsed. Nothing about the request would fix it; time or another transition
    /// would.</summary>
    public static TenantOffboardingRefusedException Conflict(string message) => new(message, 409);

    /// <summary>The environment cannot perform this operation at all (wrong provider, no owner
    /// connection configured).</summary>
    public static TenantOffboardingRefusedException NotSupported(string message) => new(message, 501);

    public int SuggestedStatusCode { get; } = suggestedStatusCode;
}

public sealed class TenantOffboardingNotFoundException(long tenantId)
    : Exception($"Tenant {tenantId} does not exist.");

/// <summary>
/// Retention policy for the window between a scheduled deletion and the earliest purge.
/// Bound from configuration section <c>TenantLifecycle</c>.
/// </summary>
public sealed class TenantLifecycleOptions
{
    public const string SectionName = "TenantLifecycle";

    /// <summary>
    /// The floor the platform enforces regardless of configuration or caller. Seven days is the
    /// shortest window in which a deletion scheduled by mistake on a Friday can still be noticed
    /// by somebody who was not at work when it was scheduled.
    ///
    /// <para>A constant rather than a setting, on purpose. Every other number here can be tuned;
    /// this one is the guarantee that makes "there is a retention window" a true statement about
    /// the system rather than about its current configuration file.</para>
    /// </summary>
    public const int AbsoluteMinimumRetentionDays = 7;

    /// <summary>Sanity ceiling. A "retention window" of forty years is a deletion that will never
    /// happen, recorded as one that will — worse than refusing to schedule it.</summary>
    public const int AbsoluteMaximumRetentionDays = 3650;

    /// <summary>Applied when the caller does not name a window.</summary>
    public int DefaultRetentionDays { get; set; } = 30;

    /// <summary>
    /// The shortest window this deployment will accept, on top of
    /// <see cref="AbsoluteMinimumRetentionDays"/>. Raising it is a policy decision a jurisdiction
    /// or a contract can require; lowering it below the absolute floor has no effect.
    /// </summary>
    public int MinimumRetentionDays { get; set; } = AbsoluteMinimumRetentionDays;

    public int MaximumRetentionDays { get; set; } = AbsoluteMaximumRetentionDays;

    /// <summary>The effective floor: configuration may only make the window longer.</summary>
    public int EffectiveMinimumRetentionDays =>
        Math.Max(AbsoluteMinimumRetentionDays, MinimumRetentionDays);

    public int EffectiveMaximumRetentionDays =>
        Math.Clamp(MaximumRetentionDays, EffectiveMinimumRetentionDays, AbsoluteMaximumRetentionDays);

    public int EffectiveDefaultRetentionDays =>
        Math.Clamp(DefaultRetentionDays, EffectiveMinimumRetentionDays, EffectiveMaximumRetentionDays);
}

// ==== requests ===============================================================================

public sealed class ScheduleTenantDeletionRequest
{
    [Required]
    public string Reason { get; set; } = null!;

    /// <summary>Length of the retention window. Omitted means the platform default; a value
    /// outside the configured range is refused rather than clamped, because a caller who asked
    /// for three days and silently got thirty has been told something untrue.</summary>
    public int? RetentionDays { get; set; }
}

public sealed class CancelTenantDeletionRequest
{
    [Required]
    public string Reason { get; set; } = null!;
}

/// <summary>
/// An export produces the tenant's ENTIRE commercial history as a file, so it carries the same
/// contract as the destructive verbs: a reason somebody can read later, and the tenant's name
/// typed back.
///
/// <para>It is not destructive, and it is guarded like it is. A red-team audit put it plainly:
/// under the previous policy a SupportAdmin could not COUNT the tenant's rows — the purge preview
/// is Owner-only — but could TAKE all of them, with a one-character reason and no confirmation.
/// Disclosure is not reversible either; once the file exists there is no un-sending it.</para>
/// </summary>
public sealed class ExportTenantDataRequest
{
    [Required]
    public string Reason { get; set; } = null!;

    [Required]
    public string Confirmation { get; set; } = null!;
}

/// <summary>
/// The typed-confirmation contract for the two irreversible operations.
///
/// <para><see cref="Confirmation"/> must be the tenant's exact <c>Name</c>, for the reason
/// <c>TenantDataReset</c> already established: a generated token can be echoed back by a script
/// without anyone reading it, whereas typing the customer's name requires knowing which customer
/// is about to be destroyed. <c>GET .../offboarding</c> returns the required string so a console
/// can display it — it is a proof of attention, not a secret.</para>
/// </summary>
public sealed class ConfirmTenantDestructionRequest
{
    [Required]
    public string Reason { get; set; } = null!;

    [Required]
    public string Confirmation { get; set; } = null!;
}

// ==== responses ==============================================================================

public sealed record TenantLifecycleEventDto(
    long Id,
    string Action,
    string? FromStage,
    string? ToStage,
    string TenantStatus,
    string Reason,
    string ActorEmail,
    string? Detail,
    DateTime OccurredOn);

public sealed record TenantExportReceiptDto(
    long Id,
    DateTime RequestedOn,
    DateTime CompletedOn,
    string RequestedBy,
    long TotalRows,
    long SizeBytes,
    string ContentSha256,
    string Format,
    string Sections);

/// <summary>
/// Everything the offboarding console needs for one tenant in a single read: where it is on both
/// lifecycle axes, what the clock says, what it must type to destroy it, and the complete history.
/// </summary>
public sealed record TenantOffboardingStatusDto(
    long TenantId,
    string TenantName,
    string TenantSlug,
    string TenantStatus,
    string Stage,

    // the retention clock
    int? RetentionDays,
    DateTime? DeletionScheduledOn,
    DateTime? PurgeEligibleOn,
    bool IsPurgeEligible,
    int? DaysUntilPurgeEligible,
    string? DeletionReason,
    string? DeletionScheduledBy,

    // irreversible acts already performed
    DateTime? PurgedOn,
    string? PurgedBy,
    long? PurgedRowCount,
    DateTime? PersonalDataErasedOn,
    string? PersonalDataErasedBy,
    long? ErasedIdentityCount,
    DateTime? LastExportedOn,
    string? LastExportedBy,

    // what the caller may do next, resolved server-side so the console cannot invent a button
    bool CanScheduleDeletion,
    bool CanCancelDeletion,
    bool CanPurge,
    bool CanErasePersonalData,

    /// <summary>The exact string a caller must echo to purge or erase. See
    /// <see cref="ConfirmTenantDestructionRequest"/>.</summary>
    string ConfirmationRequired,

    IReadOnlyList<TenantLifecycleEventDto> History,
    IReadOnlyList<TenantExportReceiptDto> Exports,
    IReadOnlyList<string> Disclosures);

/// <summary>A tenant on the purge work queue.</summary>
public sealed record PendingTenantDeletionDto(
    long TenantId,
    string TenantName,
    string TenantSlug,
    DateTime? DeletionScheduledOn,
    DateTime? PurgeEligibleOn,
    bool IsPurgeEligible,
    int? DaysUntilPurgeEligible,
    string? DeletionReason,
    string? DeletionScheduledBy);

public sealed record TenantPurgeResultDto(
    long TenantId,
    string TenantSlug,
    long RowsDeleted,
    int TablesTouched,
    IReadOnlyList<TenantPurgeTableCount> Tables,
    long LifecycleEventsRetained,
    long PlatformAuditRecordsRetained,
    int SupportTicketsRedacted,
    int SupportNotesErased,
    string Summary,
    IReadOnlyList<string> Disclosures);

public sealed record TenantErasureResultDto(
    long TenantId,
    long IdentitiesErased,
    IReadOnlyList<TenantErasureTarget> Targets,
    string Summary,
    IReadOnlyList<string> Disclosures);
