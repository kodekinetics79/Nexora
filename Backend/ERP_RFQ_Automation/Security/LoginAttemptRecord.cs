using System;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// SEC-H6: durable failed-login counter backing <see cref="ILoginAttemptThrottle"/>.
///
/// Deliberately NOT tenant-scoped and NOT covered by an EF global query filter: the row is
/// written before the caller is authenticated, so at that point there is no businessUnitId
/// claim to scope by. <see cref="Plane"/> keeps the tenant and platform counters in separate
/// namespaces so the two-plane boundary is preserved — locking out a tenant login can never
/// lock out a platform operator, and vice versa.
///
/// Persisting to the database (rather than an in-process counter) is what makes the lockout
/// survive restarts and hold across every instance sharing the database.
/// </summary>
public class LoginAttemptRecord
{
    public long Id { get; set; }

    /// <summary>Either <c>tenant</c> or <c>platform</c> — see <see cref="LoginPlane"/>.</summary>
    public string Plane { get; set; } = null!;

    /// <summary>Normalised (trimmed, lower-cased) login identifier — the email address.</summary>
    public string AttemptKey { get; set; } = null!;

    /// <summary>Consecutive failures inside the current window.</summary>
    public int FailedCount { get; set; }

    public DateTime FirstFailedOnUtc { get; set; }

    public DateTime LastFailedOnUtc { get; set; }

    /// <summary>Set once <see cref="FailedCount"/> crosses the threshold; null while unlocked.</summary>
    public DateTime? LockedUntilUtc { get; set; }
}

/// <summary>The two authentication planes tracked independently by the throttle.</summary>
public static class LoginPlane
{
    public const string Tenant = "tenant";
    public const string Platform = "platform";
}
