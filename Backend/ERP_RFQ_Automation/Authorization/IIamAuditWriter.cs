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
        public const string RoleDeleted = "ROLE_DELETED";

        /// <summary>Every action this system emits, in the order they appear above.</summary>
        public static readonly string[] All =
        {
            UserCreated, UserUpdated, UserRoleChanged, UserDeactivated, UserDeleted,
            PasswordChanged, PermissionGranted, PermissionModified, PermissionRevoked,
            PermissionGrantDenied, RoleCreated, RoleRenamed, RoleDeleted
        };
    }

    public static class IamAuditTargets
    {
        public const string User = "User";
        public const string Role = "Role";
        public const string RolePermission = "RolePermission";
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
    }
}
