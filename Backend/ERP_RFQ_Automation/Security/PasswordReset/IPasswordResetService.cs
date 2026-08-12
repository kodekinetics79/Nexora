namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// Self-service password recovery for tenant users.
///
/// <para><b>Read <see cref="RequestResetAsync"/>'s return type before anything else.</b> It is
/// <c>Task</c>, not <c>Task&lt;something&gt;</c>, and that is the single most important design
/// decision in this module. The enumeration-safety requirement — the response must be identical
/// whether or not the address belongs to an account — is not enforced by the controller
/// remembering to discard a result. It is enforced by there being no result to discard.</para>
/// </summary>
public interface IPasswordResetService
{
    /// <summary>
    /// Mint and send a reset link IF the address belongs to an account that can use one, and
    /// tell the caller nothing about whether it did.
    ///
    /// <para>Never throws for an unknown address, an inactive account, a missing tenant or a
    /// mail outage: every one of those is a normal outcome that must be indistinguishable from
    /// success at the boundary. Infrastructure faults still propagate — a database that is down
    /// is a 500 for everybody, which discloses nothing.</para>
    /// </summary>
    Task RequestResetAsync(string? email, string? clientIp, CancellationToken ct = default);

    /// <summary>Read what a presented token is worth, without spending it.</summary>
    Task<PasswordResetChallenge> InspectAsync(string? token, CancellationToken ct = default);

    /// <summary>
    /// Spend a token and set the account's password. Single-use by construction: the liveness
    /// rule is the WHERE clause of the UPDATE that spends the row, so two concurrent callers
    /// produce exactly one success.
    /// </summary>
    Task<PasswordResetResult> CompleteAsync(
        string? token, string? newPassword, string? clientIp, CancellationToken ct = default);

    /// <summary>The link that goes in the email, for tests and for diagnostics. Never logged.</summary>
    string BuildResetUrl(string token);
}
