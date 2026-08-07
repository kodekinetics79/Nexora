namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// An explicit correlation between a ticket and something already in the operator plane: a
/// <c>platform.PlatformAuditLogs</c> row, or a <c>platform.ImpersonationSessions</c> row.
///
/// <para><b>What this buys over co-location in time.</b> Both the audit log and the impersonation
/// register are already keyed by tenant, so "what happened to this customer around 14:02" is
/// answerable without any link at all. What is NOT answerable is intent. "We impersonated into
/// Acme at 14:02 and there is a ticket open" and "we impersonated into Acme at 14:02 BECAUSE of
/// ticket 41" look identical in a timeline and are completely different answers to give a customer
/// who is asking why staff were in their account. A link is an operator asserting the second one,
/// and it is itself audited.</para>
///
/// <para><b>The cross-tenant guard is the point, not a nicety.</b> Linking resolves and then
/// RENDERS the target inside the ticket detail. Without a check that the target belongs to the same
/// tenant as the ticket, "attach audit row 998877 to my ticket" would be a general-purpose primitive
/// for reading any tenant's privileged-action metadata through a ticket the caller controls — a
/// read-across-a-boundary hole opened by a feature that looks like bookkeeping. Both link kinds are
/// therefore refused unless the target's tenant matches, and that refusal is a test.</para>
///
/// <para>Links are removable: a mis-attached audit row is a clerical error, not history, and the
/// removal is audited. The thing being pointed AT is append-only and unaffected either way.</para>
/// </summary>
public class SupportTicketLink
{
    public long Id { get; set; }

    public long SupportTicketId { get; set; }

    public SupportTicketLinkKind Kind { get; set; }

    /// <summary>
    /// Identity of the target within its kind: the decimal <c>PlatformAuditLogs.Id</c>, or the
    /// impersonation session's <c>jti</c>. Text rather than two nullable typed columns because the
    /// two keys have different types and a pair of mutually-exclusive nullable foreign keys is a
    /// constraint nobody writes and therefore a constraint that is never enforced.
    /// </summary>
    public string TargetKey { get; set; } = null!;

    /// <summary>Why the operator attached it. Optional; the kind and target are usually enough.</summary>
    public string? Note { get; set; }

    public long? LinkedByPlatformUserId { get; set; }

    /// <summary>Operator identity frozen at link time, for <see cref="SupportTicketNote.AuthorLabel"/>'s reason.</summary>
    public string LinkedByLabel { get; set; } = null!;

    public DateTime LinkedAtUtc { get; set; }

    public SupportTicket? Ticket { get; set; }
}

/// <summary>What a <see cref="SupportTicketLink"/> points at. Stored as its NAME.</summary>
public enum SupportTicketLinkKind
{
    /// <summary>A <c>platform.PlatformAuditLogs</c> row, keyed by its decimal id.</summary>
    AuditLog = 0,

    /// <summary>A <c>platform.ImpersonationSessions</c> row, keyed by its <c>jti</c>.</summary>
    ImpersonationSession = 1
}
