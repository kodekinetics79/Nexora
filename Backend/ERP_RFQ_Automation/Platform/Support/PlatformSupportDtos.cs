using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace ERP_RFQ_Automation.Platform.Support;

// ---- Support tickets: requests -------------------------------------------

/// <summary>
/// Raise a ticket against a tenant.
///
/// <para><see cref="Origin"/> is accepted but constrained: only
/// <see cref="SupportTicketOrigin.Operator"/> is honoured, and anything else is refused with a
/// message that says so. The field is on the wire rather than assumed because the day a
/// customer-facing channel exists, the difference between "the operator typed this" and "the
/// customer typed this" is the difference between a note the customer may read and one they may
/// not — and back-filling that distinction onto history is guesswork.</para>
/// </summary>
public class CreateSupportTicketRequest
{
    [Required]
    public long TenantId { get; set; }

    [Required, StringLength(200, MinimumLength = 1)]
    public string Subject { get; set; } = null!;

    [Required, StringLength(8000, MinimumLength = 1)]
    public string Body { get; set; } = null!;

    /// <summary>One of <see cref="SupportTicketSeverity"/>. Defaults to Normal when omitted.</summary>
    [StringLength(16)]
    public string? Severity { get; set; }

    /// <summary>One of <see cref="SupportTicketOrigin"/>. Defaults to Operator; see the type docs.</summary>
    [StringLength(16)]
    public string? Origin { get; set; }

    [StringLength(320), EmailAddress]
    public string? RequesterEmail { get; set; }

    public long? RequesterTenantUserId { get; set; }

    /// <summary>Assign at creation. Omit to leave the ticket in the unassigned queue.</summary>
    public long? AssignToPlatformUserId { get; set; }
}

public class AddSupportTicketNoteRequest
{
    [Required, StringLength(8000, MinimumLength = 1)]
    public string Body { get; set; } = null!;

    /// <summary>
    /// Defaults to TRUE. See <see cref="SupportTicketNote.IsInternal"/> for why the safe default
    /// is the internal one.
    /// </summary>
    public bool? IsInternal { get; set; }

    /// <summary>
    /// The <see cref="SupportTicket.Version"/> the console was showing. Optional; when supplied and
    /// stale the write is refused with 409 rather than silently landing on top of somebody else's
    /// edit.
    /// </summary>
    public long? ExpectedVersion { get; set; }
}

public class TransitionSupportTicketRequest
{
    /// <summary>Target <see cref="SupportTicketStatus"/>.</summary>
    [Required, StringLength(16)]
    public string Status { get; set; } = null!;

    /// <summary>
    /// Mandatory, exactly as it is for every tenant lifecycle transition. A status change with no
    /// stated reason is the audit record that answers "what" and refuses to answer "why", which is
    /// the only question anyone asks it six months later.
    /// </summary>
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = null!;

    /// <summary>Outcome text, recorded on the ticket and echoed into the thread. Resolve/close only.</summary>
    [StringLength(4000)]
    public string? Resolution { get; set; }

    public long? ExpectedVersion { get; set; }
}

public class AssignSupportTicketRequest
{
    /// <summary>Null unassigns, which is a real triage move and not a mistake.</summary>
    public long? AssignToPlatformUserId { get; set; }

    [StringLength(1000)]
    public string? Reason { get; set; }

    public long? ExpectedVersion { get; set; }
}

public class ChangeSupportTicketSeverityRequest
{
    [Required, StringLength(16)]
    public string Severity { get; set; } = null!;

    /// <summary>
    /// Mandatory. Severity drives who gets paged and what gets promised, so a silent downgrade is
    /// the change most worth being able to reconstruct.
    /// </summary>
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = null!;

    public long? ExpectedVersion { get; set; }
}

public class LinkSupportTicketRequest
{
    /// <summary>One of <see cref="SupportTicketLinkKind"/>.</summary>
    [Required, StringLength(24)]
    public string Kind { get; set; } = null!;

    /// <summary>Audit log id, or impersonation session jti. See <see cref="SupportTicketLink.TargetKey"/>.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public string TargetKey { get; set; } = null!;

    [StringLength(512)]
    public string? Note { get; set; }
}

// ---- Support tickets: responses ------------------------------------------

public class SupportTicketSummaryDto
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? TenantSlug { get; set; }

    /// <summary>
    /// The tenant's lifecycle state, carried on every ticket row. A queue that shows "Acme —
    /// cannot log in" without showing that Acme is Suspended sends the desk hunting for a bug that
    /// is actually an invoice.
    /// </summary>
    public string? TenantStatus { get; set; }

    public string Subject { get; set; } = null!;
    public string Severity { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string Origin { get; set; } = null!;

    public long? AssignedToPlatformUserId { get; set; }
    public string? AssignedToEmail { get; set; }
    public long? OpenedByPlatformUserId { get; set; }
    public string? OpenedByEmail { get; set; }
    public string? RequesterEmail { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? FirstRespondedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }

    public int NoteCount { get; set; }
    public int LinkCount { get; set; }

    public bool IsRedacted { get; set; }
    public long Version { get; set; }
}

public class SupportTicketDetailDto : SupportTicketSummaryDto
{
    public string? Body { get; set; }
    public string? Resolution { get; set; }
    public long? RequesterTenantUserId { get; set; }
    public string? RedactedReason { get; set; }
    public DateTime? RedactedAtUtc { get; set; }

    public IReadOnlyList<SupportTicketNoteDto> Notes { get; set; } = [];
    public IReadOnlyList<SupportTicketLinkDto> Links { get; set; } = [];

    /// <summary>
    /// Where this ticket may go next, served from <see cref="SupportTicketLifecycle"/>. The console
    /// renders buttons from this instead of hard-coding the graph, so the two can never disagree
    /// about whether a Closed ticket can be reopened.
    /// </summary>
    public IReadOnlyList<string> AllowedTransitions { get; set; } = [];
}

public class SupportTicketNoteDto
{
    public long Id { get; set; }
    public long? AuthorPlatformUserId { get; set; }
    public string AuthorKind { get; set; } = null!;
    public string AuthorLabel { get; set; } = null!;
    public string Body { get; set; } = null!;
    public bool IsInternal { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class SupportTicketLinkDto
{
    public long Id { get; set; }
    public string Kind { get; set; } = null!;
    public string TargetKey { get; set; } = null!;
    public string? Note { get; set; }
    public string LinkedByLabel { get; set; } = null!;
    public DateTime LinkedAtUtc { get; set; }

    /// <summary>
    /// A one-line rendering of what was linked, resolved at read time: the audit row's action and
    /// timestamp, or the impersonation session's reason and window. Null when the target no longer
    /// resolves, which is information rather than an error — the link stays visible and says so.
    /// </summary>
    public string? TargetSummary { get; set; }

    public DateTime? TargetOccurredAtUtc { get; set; }
}

/// <summary>
/// A ticket's thread and its privileged-action trail, merged and ordered. The state changes are not
/// stored twice: they are read back out of <c>platform.PlatformAuditLogs</c> by target, which is
/// what makes the ticket timeline and the audit explorer agree by construction rather than by
/// discipline.
/// </summary>
public class SupportTicketTimelineDto
{
    public long TicketId { get; set; }
    public long TenantId { get; set; }
    public IReadOnlyList<SupportTicketTimelineEntryDto> Entries { get; set; } = [];
}

public class SupportTicketTimelineEntryDto
{
    /// <summary>"note" or "audit".</summary>
    public string Kind { get; set; } = null!;

    public string Id { get; set; } = null!;
    public DateTime OccurredAtUtc { get; set; }

    /// <summary>Audit action verb, or "note" for a thread entry.</summary>
    public string Action { get; set; } = null!;

    public string? Actor { get; set; }
    public string? Body { get; set; }
    public string? Result { get; set; }

    /// <summary>
    /// Structured audit metadata, when the entry has any and this caller may see it. A ReadOnlyOps
    /// operator reading a ticket's thread sees the notes (which are the support conversation) and the
    /// shape of every transition, but not the transition payloads — support mutations are
    /// TenantAdmin-gated, so their payloads are too.
    /// </summary>
    public JsonElement? Metadata { get; set; }

    public bool MetadataDisclosed { get; set; } = true;

    public string? MetadataPolicy { get; set; }
}

// ---- Audit explorer ------------------------------------------------------

public class PlatformAuditEntryDto
{
    public long Id { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public long ActorPlatformUserId { get; set; }

    /// <summary>Display name, else email, else "system" for the reserved actor 0.</summary>
    public string Actor { get; set; } = null!;

    public string? ActorEmail { get; set; }
    public string Action { get; set; } = null!;
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public long? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? Ip { get; set; }
    public string Result { get; set; } = null!;

    /// <summary>
    /// The audit row's context, as structured JSON rather than a string containing JSON. The
    /// existing <c>/api/platform/audit</c> hands the console a string and leaves it to parse; a
    /// console that has to <c>JSON.parse</c> a field is a console that renders "[object Object]"
    /// the first time somebody forgets.
    ///
    /// <para>NULL when <see cref="MetadataDisclosed"/> is false — see
    /// <see cref="PlatformAuditDisclosure"/> for why a payload is only served to a caller who could
    /// have written it.</para>
    /// </summary>
    public JsonElement? Metadata { get; set; }

    /// <summary>
    /// False when this caller may see that the action happened but not what it carried. Explicit
    /// rather than implied by a null <see cref="Metadata"/>: a console has to be able to tell
    /// "restricted" from "this action recorded nothing", and those two are very different answers
    /// to give someone reconstructing an incident.
    /// </summary>
    public bool MetadataDisclosed { get; set; } = true;

    /// <summary>The policy that would unlock the payload. Rendered by the console next to the withholding.</summary>
    public string? MetadataPolicy { get; set; }
}

/// <summary>
/// One audit entry with its before/after decoded. This is the "what actually changed in a
/// privileged action" view: <see cref="Changes"/> is derived from the metadata that the writing
/// endpoint already records, and is EMPTY rather than invented when the action recorded no
/// before/after pair.
///
/// <para>When <see cref="PlatformAuditEntryDto.MetadataDisclosed"/> is false, <see cref="Before"/>
/// and <see cref="After"/> are null and <see cref="Changes"/> carries field NAMES with null values —
/// "the commercial terms changed, and these are the terms that changed" without the terms
/// themselves.</para>
/// </summary>
public class PlatformAuditEntryDetailDto : PlatformAuditEntryDto
{
    public JsonElement? Before { get; set; }
    public JsonElement? After { get; set; }
    public IReadOnlyList<PlatformAuditFieldChangeDto> Changes { get; set; } = [];
}

public class PlatformAuditFieldChangeDto
{
    public string Field { get; set; } = null!;

    /// <summary>Null both when the field genuinely had no value and when the payload is withheld.</summary>
    public string? Before { get; set; }

    public string? After { get; set; }
}

public class PagedResultDto<T>
{
    public IReadOnlyList<T> Items { get; set; } = [];
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public bool HasMore => (long)Page * PageSize < TotalCount;
}

/// <summary>
/// One row of a tenant's operational history, whatever plane it came from. The console renders a
/// single ordered list; the <see cref="Kind"/> discriminator is what lets it choose an icon without
/// needing three separate fetches that it then has to merge itself and get the ordering wrong.
/// </summary>
public class TenantTimelineEntryDto
{
    /// <summary>"audit", "impersonation" or "ticket".</summary>
    public string Kind { get; set; } = null!;

    public string Id { get; set; } = null!;
    public DateTime OccurredAtUtc { get; set; }
    public string Action { get; set; } = null!;
    public string? Actor { get; set; }
    public string? Summary { get; set; }
    public string? Result { get; set; }

    /// <summary>Withheld on the same terms as everywhere else — see <see cref="PlatformAuditDisclosure"/>.</summary>
    public JsonElement? Metadata { get; set; }

    public bool MetadataDisclosed { get; set; } = true;

    public string? MetadataPolicy { get; set; }
}

/// <summary>Distinct action verbs present in the audit log, with how often each occurs.</summary>
public class PlatformAuditActionDto
{
    public string Action { get; set; } = null!;
    public int Count { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}

// ---- Tenant operations summary -------------------------------------------

/// <summary>
/// Everything a tenant's page in the operator console needs about how that customer is being
/// operated, in ONE round trip. Assembled server-side because the alternative — six fetches the
/// console stitches together — is six chances to disagree about which tenant is being looked at,
/// and the whole point of the page is that it is about exactly one.
/// </summary>
public class TenantOperationsSummaryDto
{
    public DateTime GeneratedAtUtc { get; set; }
    public TenantLifecycleSnapshotDto Lifecycle { get; set; } = null!;
    public TenantSupportSnapshotDto Support { get; set; } = null!;
    public TenantAuditSnapshotDto Audit { get; set; } = null!;
    public TenantImpersonationSnapshotDto Impersonation { get; set; } = null!;
}

public class TenantLifecycleSnapshotDto
{
    public long TenantId { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string? StatusReason { get; set; }
    public long? PlanId { get; set; }
    public string? PlanCode { get; set; }
    public long? PrimaryBusinessUnitId { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
}

public class TenantSupportSnapshotDto
{
    public int OpenTicketCount { get; set; }
    public int UnassignedOpenTicketCount { get; set; }

    /// <summary>Live ticket counts keyed by <see cref="SupportTicketStatus"/> name.</summary>
    public IReadOnlyDictionary<string, int> OpenByStatus { get; set; } = new Dictionary<string, int>();

    /// <summary>Live ticket counts keyed by <see cref="SupportTicketSeverity"/> name.</summary>
    public IReadOnlyDictionary<string, int> OpenBySeverity { get; set; } = new Dictionary<string, int>();

    /// <summary>The oldest still-untouched live ticket, which is the one that embarrasses the desk.</summary>
    public DateTime? OldestOpenTicketCreatedAtUtc { get; set; }

    public IReadOnlyList<SupportTicketSummaryDto> RecentTickets { get; set; } = [];
}

public class TenantAuditSnapshotDto
{
    public int EntryCountLast30Days { get; set; }
    public int FailureCountLast30Days { get; set; }
    public DateTime? LastActionAtUtc { get; set; }
    public IReadOnlyList<PlatformAuditEntryDto> RecentEntries { get; set; } = [];
}

public class TenantImpersonationSnapshotDto
{
    public int ActiveSessionCount { get; set; }
    public int SessionCountLast30Days { get; set; }
    public IReadOnlyList<TenantImpersonationSessionDto> Sessions { get; set; } = [];
}

public class TenantImpersonationSessionDto
{
    public string Jti { get; set; } = null!;
    public long ActorPlatformUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string Reason { get; set; } = null!;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }

    /// <summary>"active", "expired" or "revoked" — the same vocabulary <c>ImpersonationController</c> serves.</summary>
    public string Status { get; set; } = null!;

    /// <summary>
    /// Tickets that claim this session. Empty is a meaningful answer: it says an operator entered
    /// this customer's account without recording which piece of work sent them there.
    /// </summary>
    public IReadOnlyList<long> LinkedTicketIds { get; set; } = [];
}
