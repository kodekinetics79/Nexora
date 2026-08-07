namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// One entry in a ticket's thread.
///
/// <para><b>Notes cannot be edited.</b> The thread is the evidence of what was said to a customer
/// and what an operator claimed to have done; a desk where yesterday's note can be quietly reworded
/// after an incident is a desk whose thread proves nothing. The guarantee is enforced the same way
/// the platform audit log's is — by privilege, not by hope: the migration REVOKEs UPDATE on this
/// table from <c>nexora_pipeline_app</c>, the role every <c>/api/platform</c> request executes
/// under. No application path mutates a loaded note, and a future one that tried would fail with
/// 42501 in production. That asymmetry (green on SQLite, denied on PostgreSQL) is the reason
/// <c>PlatformSupportPostgreSqlTests</c> exercises it against a real server.</para>
///
/// <para><b>DELETE is deliberately still granted.</b> Immutability that also forbids erasure would
/// leave a contractual "delete this customer's data" obligation with no lawful implementation, so
/// the line drawn here is: you cannot REWRITE history, you can ERASE it, and the erasure is itself
/// an audited platform action (<c>support.ticket.redact</c>). Only
/// <see cref="ISupportTicketRedactionService"/> deletes notes.</para>
///
/// <para><b>Why the author is denormalised.</b> <see cref="AuthorLabel"/> stores the address as it
/// was when the note was written. Resolving the author through
/// <see cref="AuthorPlatformUserId"/> at read time makes an old thread change its attribution when
/// an operator's display name changes, and go blank entirely if that operator's row is ever
/// removed — the same reason <c>ImpersonationSession.RevokedBy</c> stores an email rather than an
/// id.</para>
/// </summary>
public class SupportTicketNote
{
    public long Id { get; set; }

    public long SupportTicketId { get; set; }

    /// <summary>
    /// Null for a <see cref="SupportTicketAuthorKind.System"/> entry and for anything a
    /// customer-facing channel writes later. Present for every operator note today.
    /// </summary>
    public long? AuthorPlatformUserId { get; set; }

    public SupportTicketAuthorKind AuthorKind { get; set; } = SupportTicketAuthorKind.Operator;

    /// <summary>Author identity frozen at write time. See the type docs.</summary>
    public string AuthorLabel { get; set; } = null!;

    public string Body { get; set; } = null!;

    /// <summary>
    /// Whether this note would be hidden from the customer if they could see the thread. Defaults
    /// to TRUE, which is the only safe default: an operator-only desk that later grows a customer
    /// view must not retroactively publish two years of internal commentary because the column was
    /// added with a permissive default. Nothing reads it today; it exists so that turning the
    /// customer view on is a filter, not a data-classification project.
    /// </summary>
    public bool IsInternal { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public SupportTicket? Ticket { get; set; }
}

/// <summary>Who wrote a note. Stored as its NAME, for <see cref="SupportTicketSeverity"/>'s reason.</summary>
public enum SupportTicketAuthorKind
{
    /// <summary>A platform operator.</summary>
    Operator = 0,

    /// <summary>Someone at the customer, through a channel that does not exist yet.</summary>
    Customer = 1,

    /// <summary>
    /// Nexora itself. Reserved for entries the desk did not type — today only the tombstone the
    /// redaction service leaves behind when a purge erases a thread, so that a ticket whose notes
    /// vanished does not read as a ticket nobody ever worked.
    /// </summary>
    System = 2
}
