using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Security;

/// <summary>Outcome of a pre-login throttle check.</summary>
public readonly record struct LoginLockoutState(bool IsLockedOut, TimeSpan RetryAfter)
{
    public static readonly LoginLockoutState Allowed = new(false, TimeSpan.Zero);
}

/// <summary>
/// SEC-H6: brute-force protection for the two login endpoints
/// (<c>POST /api/Auth/Login</c> and <c>POST /api/platform/auth/login</c>), which previously
/// had no lockout and no failed-attempt counter at all.
///
/// Counters are keyed by (plane, normalised email) and persisted, so the lockout survives a
/// process restart and is shared by every instance pointed at the same database. The tenant
/// and platform planes are separate key namespaces — see <see cref="LoginAttemptRecord"/>.
/// </summary>
public interface ILoginAttemptThrottle
{
    /// <summary>Called before credentials are verified. Fails open on infrastructure errors.</summary>
    Task<LoginLockoutState> CheckAsync(string plane, string? identifier, CancellationToken cancellationToken = default);

    /// <summary>Called after a failed credential check; advances the progressive lockout.</summary>
    Task RegisterFailureAsync(string plane, string? identifier, CancellationToken cancellationToken = default);

    /// <summary>Called after a successful login; clears the counter.</summary>
    Task RegisterSuccessAsync(string plane, string? identifier, CancellationToken cancellationToken = default);
}

/// <summary>
/// Progressive-lockout policy. Defaults are deliberately generous enough not to disrupt a
/// live pilot while still making online password guessing impractical; every value is
/// overridable under the <c>LoginThrottling:*</c> configuration section, mirroring the
/// <c>RateLimiting:*</c> convention already used by the platform rate limiter.
/// </summary>
public sealed class LoginThrottleOptions
{
    /// <summary>Consecutive failures tolerated before the first lockout. Default 5.</summary>
    public int FailureThreshold { get; init; } = 5;

    /// <summary>Failures older than this are forgotten (counter resets). Default 15 minutes.</summary>
    public TimeSpan FailureWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Duration of the first lockout. Doubles per additional failure. Default 1 minute.</summary>
    public TimeSpan BaseLockout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Ceiling for the progressive doubling. Default 60 minutes.</summary>
    public TimeSpan MaximumLockout { get; init; } = TimeSpan.FromMinutes(60);

    public static LoginThrottleOptions FromConfiguration(IConfiguration configuration)
    {
        var defaults = new LoginThrottleOptions();
        return new LoginThrottleOptions
        {
            FailureThreshold = GetInt(configuration, "LoginThrottling:FailureThreshold", defaults.FailureThreshold),
            FailureWindow = TimeSpan.FromSeconds(
                GetInt(configuration, "LoginThrottling:FailureWindowSeconds", (int)defaults.FailureWindow.TotalSeconds)),
            BaseLockout = TimeSpan.FromSeconds(
                GetInt(configuration, "LoginThrottling:BaseLockoutSeconds", (int)defaults.BaseLockout.TotalSeconds)),
            MaximumLockout = TimeSpan.FromSeconds(
                GetInt(configuration, "LoginThrottling:MaximumLockoutSeconds", (int)defaults.MaximumLockout.TotalSeconds))
        };
    }

    private static int GetInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}

public sealed class LoginAttemptThrottle : ILoginAttemptThrottle
{
    private readonly ErpRfqAutomationContext _context;
    private readonly LoginThrottleOptions _options;
    private readonly ILogger<LoginAttemptThrottle> _logger;
    private readonly TimeProvider _timeProvider;

    public LoginAttemptThrottle(
        ErpRfqAutomationContext context,
        LoginThrottleOptions options,
        ILogger<LoginAttemptThrottle> logger,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LoginLockoutState> CheckAsync(
        string plane, string? identifier, CancellationToken cancellationToken = default)
    {
        var key = Normalise(identifier);
        if (key is null) return LoginLockoutState.Allowed;

        try
        {
            var record = await FindAsync(plane, key, cancellationToken);
            if (record?.LockedUntilUtc is null) return LoginLockoutState.Allowed;

            var now = UtcNow();
            if (record.LockedUntilUtc <= now) return LoginLockoutState.Allowed;

            return new LoginLockoutState(true, record.LockedUntilUtc.Value - now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail open on infrastructure faults: a broken counter table must not lock every
            // user out of the product. The failure is loud so it is noticed.
            _logger.LogError(exception, "Login throttle check failed for plane {Plane}; allowing the attempt.", plane);
            return LoginLockoutState.Allowed;
        }
    }

    public async Task RegisterFailureAsync(
        string plane, string? identifier, CancellationToken cancellationToken = default)
    {
        var key = Normalise(identifier);
        if (key is null) return;

        var now = UtcNow();

        // One retry: two instances can race to INSERT the first row for a key; the unique
        // index turns the loser into a DbUpdateException, and the retry then finds the row.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var record = await FindAsync(plane, key, cancellationToken);

                if (record is null)
                {
                    _context.LoginAttempts.Add(new LoginAttemptRecord
                    {
                        Plane = plane,
                        AttemptKey = key,
                        FailedCount = 1,
                        FirstFailedOnUtc = now,
                        LastFailedOnUtc = now,
                        LockedUntilUtc = LockoutFor(1, now)
                    });
                }
                else
                {
                    // A stale burst outside the window starts a fresh count.
                    var expired = record.LockedUntilUtc is null
                                  && now - record.LastFailedOnUtc > _options.FailureWindow;

                    record.FailedCount = expired ? 1 : record.FailedCount + 1;
                    if (expired) record.FirstFailedOnUtc = now;
                    record.LastFailedOnUtc = now;
                    record.LockedUntilUtc = LockoutFor(record.FailedCount, now);
                }

                await _context.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException exception)
            {
                foreach (var entry in _context.ChangeTracker.Entries<LoginAttemptRecord>())
                    entry.State = EntityState.Detached;

                if (attempt == 1)
                {
                    _logger.LogError(exception,
                        "Login throttle could not record a failure for plane {Plane}.", plane);
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception,
                    "Login throttle could not record a failure for plane {Plane}.", plane);
                return;
            }
        }
    }

    public async Task RegisterSuccessAsync(
        string plane, string? identifier, CancellationToken cancellationToken = default)
    {
        var key = Normalise(identifier);
        if (key is null) return;

        try
        {
            var record = await FindAsync(plane, key, cancellationToken);
            if (record is null) return;

            _context.LoginAttempts.Remove(record);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception,
                "Login throttle could not clear the counter for plane {Plane}.", plane);
        }
    }

    /// <summary>
    /// null once below the threshold; otherwise the progressive window
    /// (base, base*2, base*4, ... capped at <see cref="LoginThrottleOptions.MaximumLockout"/>).
    /// </summary>
    private DateTime? LockoutFor(int failedCount, DateTime now)
    {
        if (failedCount < _options.FailureThreshold) return null;

        var steps = Math.Min(failedCount - _options.FailureThreshold, 16); // guard the shift
        var multiplier = 1L << steps;
        var ticks = Math.Min(_options.BaseLockout.Ticks * multiplier, _options.MaximumLockout.Ticks);
        return now.AddTicks(ticks);
    }

    private Task<LoginAttemptRecord?> FindAsync(string plane, string key, CancellationToken cancellationToken) =>
        _context.LoginAttempts.FirstOrDefaultAsync(
            x => x.Plane == plane && x.AttemptKey == key, cancellationToken);

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private static string? Normalise(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return null;
        var trimmed = identifier.Trim().ToLowerInvariant();
        return trimmed.Length > 256 ? trimmed[..256] : trimmed;
    }
}

public static class LoginThrottlingServiceCollectionExtensions
{
    /// <summary>SEC-H6: registers the shared, database-backed login lockout.</summary>
    public static IServiceCollection AddLoginThrottling(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(LoginThrottleOptions.FromConfiguration(configuration));
        services.AddScoped<ILoginAttemptThrottle, LoginAttemptThrottle>();
        return services;
    }
}
