using System.Security.Claims;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>Canonical IAM audit action names. String constants, not an enum, because the
    /// column is queried directly by compliance exports and must stay readable in SQL.</summary>
    public static class IamAuditActions
    {
        public const string UserCreated = "USER_CREATED";
        public const string UserUpdated = "USER_UPDATED";
        public const string UserRoleChanged = "USER_ROLE_CHANGED";
        public const string UserDeactivated = "USER_DEACTIVATED";
        public const string UserDeleted = "USER_DELETED";
        public const string PasswordChanged = "PASSWORD_CHANGED";
        public const string PermissionGranted = "PERMISSION_GRANTED";
        public const string PermissionModified = "PERMISSION_MODIFIED";
        public const string PermissionRevoked = "PERMISSION_REVOKED";
        public const string PermissionGrantDenied = "PERMISSION_GRANT_DENIED";
        public const string RoleCreated = "ROLE_CREATED";
        public const string RoleRenamed = "ROLE_RENAMED";
        /// <summary>A role's authority tier (Setup_Master.RoleRank) moved — the privilege-bearing
        /// role edit, kept distinct from a cosmetic rename.</summary>
        public const string RoleRankChanged = "ROLE_RANK_CHANGED";
        public const string RoleDeleted = "ROLE_DELETED";

        // Mailbox administration. These belong in the IAM trail rather than an application log:
        // a mailbox row carries a stored credential and decides which server this system logs
        // into and which address customer-facing mail leaves from. Whoever changes one is
        // exercising privilege, and "who repointed the RFQ inbox, and when" has to be answerable
        // months later.
        public const string MailboxCreated = "MAILBOX_CREATED";
        public const string MailboxUpdated = "MAILBOX_UPDATED";
        public const string MailboxDeleted = "MAILBOX_DELETED";
        /// <summary>The stored password was replaced. Separate from <see cref="MailboxUpdated"/>
        /// because a credential rotation is the event a reviewer looks for, and it must not be
        /// buried inside a diff of host/port edits.</summary>
        public const string MailboxCredentialChanged = "MAILBOX_CREDENTIAL_CHANGED";
        /// <summary>A connection test was run. Recorded because it causes this server to open an
        /// outbound socket to an operator-supplied host — the audit trail is what makes that
        /// attributable.</summary>
        public const string MailboxTested = "MAILBOX_TESTED";
        /// <summary>Every outbound SMTP configuration was deactivated at once, halting
        /// customer-facing mail. A containment action, and a deliberate one.</summary>
        public const string OutboundMailPaused = "OUTBOUND_MAIL_PAUSED";

        /// <summary>Every action this system emits, in the order they appear above.</summary>
        public static readonly string[] All =
        {
            UserCreated, UserUpdated, UserRoleChanged, UserDeactivated, UserDeleted,
            PasswordChanged, PermissionGranted, PermissionModified, PermissionRevoked,
            PermissionGrantDenied, RoleCreated, RoleRenamed, RoleRankChanged, RoleDeleted,
            MailboxCreated, MailboxUpdated, MailboxDeleted, MailboxCredentialChanged,
            MailboxTested, OutboundMailPaused
        };
    }

    public static class IamAuditTargets
    {
        public const string User = "User";
        public const string Role = "Role";
        public const string RolePermission = "RolePermission";
        public const string Mailbox = "Mailbox";
    }

    /// <summary>The mutation being recorded. Actor and tenant are NOT part of this record —
    /// they are resolved server-side by the writer from the caller's token.</summary>
    public sealed record IamAuditEntry(
        string Action,
        string TargetType,
        long? TargetId = null,
        string? TargetLabel = null,
        object? Before = null,
        object? After = null,
        string? Reason = null);

    /// <summary>
    /// Writes <see cref="IamAuditEvent"/> rows through the CALLER'S request-scoped
    /// <c>ErpRfqAutomationContext</c>, so the audit row participates in whatever transaction the
    /// mutation is already running in: it commits with the change or rolls back with it. There is
    /// no separate connection and no fire-and-forget path — a mutation that fails must leave no
    /// audit trace, and an audit write that fails must fail the mutation.
    /// </summary>
    public interface IIamAuditWriter
    {
        /// <summary>Adds the event to the shared change tracker WITHOUT saving. Use when the very
        /// next call is a repository method that performs its own <c>SaveChangesAsync</c>: both
        /// rows then land in that single implicit transaction.</summary>
        IamAuditEvent Enlist(ClaimsPrincipal? principal, IamAuditEntry entry);

        /// <summary>Enlists and immediately saves. Call inside an explicit
        /// <c>IDbContextTransaction</c> when the mutation was persisted by an earlier
        /// <c>SaveChangesAsync</c> (e.g. to capture a server-generated target id).</summary>
        Task<IamAuditEvent> WriteAsync(
            ClaimsPrincipal? principal, IamAuditEntry entry, CancellationToken cancellationToken = default);

        /// <summary>
        /// Opens a transaction on the SHARED request-scoped <c>DbContext</c> — the same instance the
        /// repositories write through — so a mutation and its audit event commit or roll back as
        /// one. Returns null when a transaction is already ambient (the caller is nested and the
        /// outer scope already provides the guarantee) or the provider does not support them.
        /// </summary>
        Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAtomicAsync(
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Runs <paramref name="work"/> inside a transaction that is safe under the retrying
        /// execution strategy, so the mutation and its audit event commit or roll back as one.
        ///
        /// <para><b>Why this exists.</b> Callers used to open the transaction themselves via
        /// <see cref="BeginAtomicAsync"/> and then call <c>SaveChangesAsync</c>. The DbContext is
        /// registered with <c>EnableRetryOnFailure</c>, and EF refuses to execute inside a
        /// user-initiated transaction under a retrying strategy — it throws
        /// <c>"The configured execution strategy 'NpgsqlRetryingExecutionStrategy' does not
        /// support user-initiated transactions"</c>. Crucially it throws at SAVE time, not at
        /// BeginTransaction, so the try/catch guarding BeginAtomicAsync never saw it and every
        /// affected action returned a 500. User creation and both role-permission mutations
        /// failed 100% of the time.</para>
        ///
        /// <para>The whole unit must be retriable, which means the strategy has to own the
        /// transaction — hence this method rather than a fix at each call site.</para>
        ///
        /// <para><b>WARNING — <paramref name="work"/> MAY RUN MORE THAN ONCE.</b> That is the
        /// point of a retrying strategy: on a transient fault the transaction is rolled back and
        /// the delegate is invoked again. Two rules follow, and violating either reintroduces the
        /// duplicate-write bug this method was added to avoid:</para>
        /// <list type="number">
        /// <item><description>Construct and <c>Add</c> entities INSIDE the delegate. An entity
        /// built outside is still tracked as <c>Added</c> after a failed attempt; re-adding the
        /// same instance on retry can insert the row twice.</description></item>
        /// <item><description>Reset any accumulator the delegate mutates (counters, output lists)
        /// on entry, and re-read any state it decides from — a retry that appends to totals from
        /// the failed attempt reports numbers that never happened.</description></item>
        /// </list>
        /// <para>Non-transactional side effects — file writes, password hashing, outbound calls —
        /// belong OUTSIDE the delegate so they happen exactly once.</para>
        /// </summary>
        Task ExecuteAtomicAsync(Func<Task> work, CancellationToken cancellationToken = default);
    }
}
