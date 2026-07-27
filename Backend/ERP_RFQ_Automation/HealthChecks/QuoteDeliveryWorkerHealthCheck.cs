using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

public interface IQuoteDeliveryWorkerHeartbeat
{
    DateTimeOffset? LastSeen { get; }
    int ConsecutiveFailures { get; }
    void Beat();
    void RecordSuccess();
    void RecordFailure();
}

public sealed class QuoteDeliveryWorkerHeartbeat : IQuoteDeliveryWorkerHeartbeat
{
    private long _lastSeenTicks;
    private int _consecutiveFailures;

    public DateTimeOffset? LastSeen
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSeenTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    public void Beat() => Interlocked.Exchange(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
    public void RecordSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);
    public void RecordFailure() => Interlocked.Increment(ref _consecutiveFailures);
}

public sealed class QuoteDeliveryWorkerHealthCheck(IQuoteDeliveryWorkerHeartbeat heartbeat) : IHealthCheck
{
    private static readonly TimeSpan MaximumSilence = TimeSpan.FromSeconds(30);

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var lastSeen = heartbeat.LastSeen;
        if (!lastSeen.HasValue)
            return Task.FromResult(HealthCheckResult.Unhealthy("Quote delivery worker has not started."));
        if (DateTimeOffset.UtcNow - lastSeen.Value > MaximumSilence)
            return Task.FromResult(HealthCheckResult.Unhealthy("Quote delivery worker heartbeat is stale."));
        if (heartbeat.ConsecutiveFailures >= 3)
            return Task.FromResult(HealthCheckResult.Unhealthy("Quote delivery worker is repeatedly failing."));
        return Task.FromResult(HealthCheckResult.Healthy("Quote delivery claim loop is active."));
    }
}
