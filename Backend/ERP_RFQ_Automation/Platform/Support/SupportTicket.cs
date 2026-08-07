namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// One unit of support work about a tenant.
///
/// <para><b>Why the platform schema and no query filter.</b> Like
/// <c>platform.ImpersonationSessions</c> and <c>platform.TenantAdminInvitations</c>, a ticket is a
/// control-plane record ABOUT a tenant rather than a record BELONGING to one: it is written by an
/// operator who holds no tenant scope, and the request that reads it carries no
/// <c>nexora.business_unit_id</c>. Carrying a global query filter would additionally enrol the
/// table in the RLS-policy expectation asserted by
/// <c>PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase</c>, which
/// only applies to the public schema and which no operator-plane table can satisfy — there is no
/// business unit in scope to write a policy against. <see cref="TenantId"/> is deliberately named
/// TenantId rather than BusinessUnitId for the same reason: the isolation sweep in
/// <c>TenantIsolationTests</c> keys on the tenant-data column names, and this is not tenant data.</para>
///
/// <para><b>Suspension is when support matters most.</b> Nothing here consults
/// <c>Tenant.Status</c>. A suspended tenant is precisely the customer who is on the phone asking
/// why they are locked out, and an ops desk that refuses to open a ticket for them is an ops desk
/// that fails at the only moment it was bought for. The single precondition on every write is that
/// the tenant row EXISTS.</para>
///
/// <para><b>Nothing here says "operator-only".</b> <see cref="Origin"/> records which channel the
/// ticket arrived through and <see cref="RequesterEmail"/>/<see cref="RequesterTenantUserId"/>
/// record who it is FOR, both of which are meaningless in a desk that only operators can reach and
/// both of which a customer-facing channel needs on day one. Today the controller accepts only
/// <see cref="SupportTicketOrigin.Operator"/>; the columns exist so that admitting a second channel
/// is an authorization change rather than a migration against live support history.</para>
///
/// <para><b>Purge.</b> The tenant foreign key is RESTRICT, so deleting a tenant row out from under
/// its support history fails loudly instead of silently vacuuming the record of what was done for
/// that customer. Erasure runs through <see cref="ISupportTicketRedactionService"/>, which strips
/// the customer-derived text and stamps <see cref="RedactedAtUtc"/> while leaving the ticket,
/// its lifecycle timestamps and its audit trail standing.</para>
/// </summary>
public class SupportTicket
{
    public long Id { get; set; }

    /// <summary>The tenant this ticket is about. Never null: a ticket with no customer is a note.</summary>
    public long TenantId { get; set; }

    public string Subject { get; set; } = null!;

    /// <summary>
    /// The opening description. Nullable in the SCHEMA and required by the create request: erasure
    /// has to be able to empty it, and a column that cannot be emptied is a column that turns a
    /// contractual delete obligation into a schema change under time pressure.
    /// </summary>
    public string? Body { get; set; }

    public SupportTicketSeverity Severity { get; set; } = SupportTicketSeverity.Normal;

    public SupportTicketStatus Status { get; set; } = SupportTicketStatus.New;

    /// <summary>Channel the ticket arrived through. See the type docs for why this is not implied.</summary>
    public SupportTicketOrigin Origin { get; set; } = SupportTicketOrigin.Operator;

    /// <summary>
    /// The operator who raised it. Nullable because a customer-raised ticket has no platform
    /// actor — the same reason <see cref="SupportTicketNote.AuthorPlatformUserId"/> is nullable.
    /// </summary>
    public long? OpenedByPlatformUserId { get; set; }

    /// <summary>
    /// The operator currently responsible. Null means the ticket is in the unassigned queue, which
    /// is a real state an ops desk needs to filter on, not a defect.
    /// </summary>
    public long? AssignedToPlatformUserId { get; set; }

    /// <summary>
    /// Who at the customer this is for, recorded as free text because the operator is usually
    /// looking at an email thread rather than a <c>Users</c> row. Stripped by redaction.
    /// </summary>
    public string? RequesterEmail { get; set; }

    /// <summary>
    /// The tenant <c>Users</c> row behind the request, when one is known. Deliberately carries NO
    /// foreign key: it points across the schema boundary into tenant-owned data that a purge
    /// deletes, and the record of who asked for help must survive that account's removal — the
    /// same reason <c>IamAuditEvent.TargetId</c> carries none.
    /// </summary>
    public long? RequesterTenantUserId { get; set; }

    /// <summary>
    /// The outcome, written when the ticket reaches <see cref="SupportTicketStatus.Resolved"/> or
    /// <see cref="SupportTicketStatus.Closed"/>. Held on the ticket so a queue list can show what
    /// happened without fetching every ticket's thread; the transition that set it is in the audit
    /// trail with the same text, which is what the timeline renders in order.
    /// </summary>
    public string? Resolution { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Last time anything happened — a note, a transition, an assignment, a severity change. This
    /// is the column the queue sorts on, because "oldest untouched ticket first" is the only
    /// ordering that surfaces the ticket everybody forgot about.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// First operator note on the ticket. Recorded because "did anyone answer this customer at
    /// all" is a different question from "is it fixed", and the second one cannot answer it.
    /// </summary>
    public DateTime? FirstRespondedAtUtc { get; set; }

    /// <summary>
    /// Cleared on reopen, along with <see cref="ClosedAtUtc"/>. A reopened ticket is genuinely not
    /// resolved, and leaving a stale resolution timestamp behind would quietly inflate every
    /// time-to-resolution figure ever computed from this table. The history of the earlier
    /// resolution is not lost: it is in the append-only audit trail as a
    /// <c>support.ticket.transition</c> record.
    /// </summary>
    public DateTime? ResolvedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>
    /// Set by <see cref="ISupportTicketRedactionService"/> when the tenant is purged. A redacted
    /// ticket keeps its identity, lifecycle and audit trail and loses every field that could carry
    /// customer content.
    /// </summary>
    public DateTime? RedactedAtUtc { get; set; }

    public string? RedactedReason { get; set; }

    /// <summary>
    /// Optimistic concurrency. Two operators triaging the same ticket from two open browser tabs is
    /// the ordinary case, not an edge case: without this, the second write silently overwrites the
    /// first and the audit trail records two transitions from the same starting state. Configured
    /// as an EF concurrency token so the guard is in the UPDATE's WHERE clause rather than in a
    /// read-then-write window; callers may additionally send the version they were shown, which
    /// turns a stale console into a clean 409 instead of a lost edit.
    /// </summary>
    public long Version { get; set; } = 1;

    public ICollection<SupportTicketNote> Notes { get; set; } = new List<SupportTicketNote>();

    public ICollection<SupportTicketLink> Links { get; set; } = new List<SupportTicketLink>();
}

/// <summary>
/// Operator-facing urgency. Stored as its NAME rather than its ordinal for the reason
/// <c>Tenant.BillingMode</c> is: a ticket closed two years ago has to stay explainable, and
/// inserting a level into the middle of this enum must not silently reclassify history.
/// </summary>
public enum SupportTicketSeverity
{
    /// <summary>Customer is down or data is at risk. Someone is working it now.</summary>
    Critical = 0,

    /// <summary>Major function unusable with no workaround.</summary>
    High = 1,

    /// <summary>The default. Degraded or confusing, workaround exists.</summary>
    Normal = 2,

    /// <summary>Question, cosmetic issue, or a request with no deadline.</summary>
    Low = 3
}

/// <summary>
/// Lifecycle state. The permitted moves between these are defined once in
/// <see cref="SupportTicketLifecycle"/> rather than scattered through the controller, so the graph
/// can be read — and tested — without reading an endpoint.
/// </summary>
public enum SupportTicketStatus
{
    /// <summary>Raised, not yet triaged. The only state a ticket can be created in.</summary>
    New = 0,

    /// <summary>Being worked by the desk.</summary>
    Open = 1,

    /// <summary>Blocked on someone outside the desk — the customer, a vendor, a release.</summary>
    Pending = 2,

    /// <summary>Fixed as far as the desk is concerned; awaiting confirmation.</summary>
    Resolved = 3,

    /// <summary>
    /// Finished. NOT terminal: customers come back, and forcing a duplicate ticket for a returning
    /// problem destroys the one thread that holds its history. Reopening is an audited transition.
    /// </summary>
    Closed = 4
}

/// <summary>
/// Which channel a ticket came in through. Only <see cref="Operator"/> is accepted today — see the
/// <see cref="SupportTicket"/> docs for why the other two exist in the model anyway.
/// </summary>
public enum SupportTicketOrigin
{
    /// <summary>Raised by a platform operator on the customer's behalf.</summary>
    Operator = 0,

    /// <summary>Raised by the customer through a customer-facing surface.</summary>
    Customer = 1,

    /// <summary>Created from an inbound message by an email-to-ticket pipeline.</summary>
    Email = 2
}
